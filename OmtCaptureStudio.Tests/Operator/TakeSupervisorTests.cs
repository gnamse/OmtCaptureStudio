using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Xml.Linq;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Audio;
using OmtCaptureStudio.Services.Sinks;
using OmtCaptureStudio.Services.Sources;
using OmtCaptureStudio.Services.Forensics;
using OmtCaptureStudio.ViewModels;

namespace OmtCaptureStudio.Tests;

partial class Program
{
    /// <summary>
    /// NET-018: the extracted TakeSupervisor driven IN ISOLATION — a fake recorder, a fake watchable
    /// feed and the injected clock. No AudioEngine, no sink, no CaptureSession event plumbing: this is
    /// the ADR-0002 policy on its own, which is the whole reason the state machine was extracted.
    /// </summary>
    static void TestTakeSupervisor()
    {
        Console.WriteLine("\n[Step 0.695] Verifying TakeSupervisor in isolation (fake recorder + injected clock)...");

        var config = new RecordingConfig
        {
            InputLossThresholdSeconds = 5.0,
            InputRecoveryGraceSeconds = 30.0,
            MaxDurationMinutes = 30
        };
        var format = new StreamFormat { Width = 1920, Height = 1080, FrameRate = 30, SampleRate = 48000, Channels = 2 };

        // ---- 1. TAKE LIFECYCLE + WATCHDOG ARMING -------------------------------------------
        {
            long now = 1_000_000;
            Func<long> clock = () => now;
            var recorder = new FakeTakeRecorder();
            var feed = new FakeWatchableFeed();
            var watchdog = new InputHealthWatchdog(thresholdMs: 5000, clock: clock);

            using var supervisor = new TakeSupervisor(recorder, watchdog, () => feed, clock);

            Expect(!supervisor.TakeInProgress, "A supervisor with no take must not report one in progress.");
            Expect(!watchdog.IsArmed, "The watchdog must start disarmed.");

            recorder.IsRecording = true;
            supervisor.BeginTake(config, format);

            Expect(supervisor.TakeInProgress, "BeginTake must put the supervisor into the take state.");
            Expect(watchdog.IsArmed, "A take over a watchable (network) feed must arm the watchdog.");
            Expect(supervisor.InputLossThreshold == TimeSpan.FromSeconds(5),
                $"BeginTake must apply the config's input-loss threshold; got {supervisor.InputLossThreshold}.");
            Expect(supervisor.InputRecoveryGracePeriod == TimeSpan.FromSeconds(30),
                $"BeginTake must apply the config's grace period; got {supervisor.InputRecoveryGracePeriod}.");

            supervisor.EndTake();
            Expect(!supervisor.TakeInProgress, "EndTake must leave the take state.");
            Expect(!watchdog.IsArmed, "EndTake must disarm the watchdog.");

            // An in-process source is NEVER watched, even mid-take (ADR-0002 decision 2).
            feed.RequiresInputHealthWatch = false;
            recorder.IsRecording = true;
            supervisor.BeginTake(config, format);
            Expect(!watchdog.IsArmed,
                "A take over an in-process feed (test signal / host capture) must never arm the watchdog.");
            supervisor.EndTake();
        }

        // ---- 2. BOUNDARY EVALUATION ORDERING -----------------------------------------------
        // Boundaries are evaluated BEFORE the watchdog edge. A take that ends on a boundary this tick
        // must be disarmed before the poll can report a false input loss.
        {
            long now = 2_000_000;
            Func<long> clock = () => now;
            var recorder = new FakeTakeRecorder();
            var feed = new FakeWatchableFeed();
            var watchdog = new InputHealthWatchdog(thresholdMs: 5000, clock: clock);

            using var supervisor = new TakeSupervisor(recorder, watchdog, () => feed, clock);

            bool lostFired = false;
            bool maxFired = false;
            supervisor.InputLost += () => lostFired = true;
            supervisor.MaxDurationReached += () => maxFired = true;

            recorder.IsRecording = true;
            supervisor.BeginTake(config, format);
            Expect(watchdog.IsArmed, "Precondition: the watchdog must be armed for the ordering test.");

            // Silence past the loss threshold AND a due max-duration boundary on the SAME tick.
            recorder.NextBoundary = RecordingBoundary.MaxDuration;
            now += 6000;
            supervisor.Tick();

            Expect(maxFired, "The max-duration boundary must be reported.");
            Expect(!lostFired,
                "The boundary is evaluated BEFORE the watchdog edge, so a take that just ended must not " +
                "also report an input loss on the same tick.");
            Expect(!supervisor.TakeInProgress, "A max-duration boundary must end the take.");
            Expect(!watchdog.IsArmed, "Ending the take on a boundary must disarm the watchdog before the poll.");
            Expect(recorder.Stops.Count == 1 && recorder.Stops[0] == RecordingStopReason.MaxDuration,
                $"The boundary stop must route through the recorder (the arm's single finalize path); " +
                $"got [{string.Join(",", recorder.Stops)}].");
        }

        // ---- 3. OUTAGE -> RECONNECT BACKOFF -> GRACE EXPIRY --------------------------------
        {
            long now = 3_000_000;
            Func<long> clock = () => now;
            var recorder = new FakeTakeRecorder();
            var feed = new FakeWatchableFeed();
            var watchdog = new InputHealthWatchdog(thresholdMs: 5000, clock: clock);

            using var supervisor = new TakeSupervisor(recorder, watchdog, () => feed, clock);

            int lostCount = 0;
            int recoveredCount = 0;
            bool failed = false;
            supervisor.InputLost += () => lostCount++;
            supervisor.InputRecovered += () => recoveredCount++;
            supervisor.InputRecoveryFailed += () => failed = true;

            recorder.IsRecording = true;
            supervisor.BeginTake(config, format);

            // Outage boundary: silence past the threshold.
            now += 5001;
            supervisor.Tick();

            Expect(lostCount == 1, $"Exactly one InputLost must be raised at the outage boundary; got {lostCount}.");
            Expect(!recorder.IsRecording,
                "The current segment must be finalized AT the outage boundary (stop routed through the recorder).");
            Expect(recorder.Stops.Count == 1 && recorder.Stops[0] == RecordingStopReason.InputLoss,
                $"The outage stop must be recorded as InputLoss; got [{string.Join(",", recorder.Stops)}].");
            Expect(supervisor.TakeInProgress,
                "The TAKE must stay in progress across an outage (only the segment ended), so the watchdog " +
                "keeps watching for restored frames.");

            // Backoff: attempt 1 fires on the outage tick, then 250 ms, then 500 ms (ReconnectBackoff).
            WaitFor(() => feed.ReconnectCount >= 1, "first auto-reconnect attempt");
            now += 249;
            supervisor.Tick();
            Thread.Sleep(40);
            Expect(feed.ReconnectCount == 1,
                $"No reconnect may fire before the 250 ms backoff window elapses; got {feed.ReconnectCount}.");

            now += 1; // exactly 250 ms
            supervisor.Tick();
            WaitFor(() => feed.ReconnectCount >= 2, "second auto-reconnect attempt");

            // Grace expiry: still lost past the 30 s window -> the take ends and the alarm persists.
            now += 31_000;
            supervisor.Tick();

            Expect(failed, "The bounded grace period expiring must raise InputRecoveryFailed.");
            Expect(!supervisor.TakeInProgress, "Grace expiry must end the take.");
            Expect(recoveredCount == 0, "A never-restored feed must not report a recovery.");
        }

        // ---- 4. RECOVERY RESUMES THE TAKE INTO THE NEXT SEGMENT ----------------------------
        {
            long now = 4_000_000;
            Func<long> clock = () => now;
            var recorder = new FakeTakeRecorder();
            var feed = new FakeWatchableFeed();
            var watchdog = new InputHealthWatchdog(thresholdMs: 5000, clock: clock);

            using var supervisor = new TakeSupervisor(recorder, watchdog, () => feed, clock);

            int recoveredCount = 0;
            supervisor.InputRecovered += () => recoveredCount++;

            recorder.IsRecording = true;
            supervisor.BeginTake(config, format);

            now += 5001;
            supervisor.Tick();
            Expect(recorder.Stops.Count == 1, "Precondition: the outage must finalize the first segment.");
            int segmentAfterOutage = recorder.CurrentSegmentIndex;

            // Frames return within grace: the watchdog edge flips on the next poll.
            watchdog.NotifyVideoFrame();
            now += 100;
            supervisor.Tick();

            Expect(recoveredCount == 1, $"Recovery must raise InputRecovered exactly once; got {recoveredCount}.");
            Expect(supervisor.TakeInProgress, "A recovered take must still be in progress.");
            Expect(recorder.IsRecording, "Recovery must resume recording through the recorder.");
            Expect(recorder.ResumeCount == 1,
                $"Recovery must resume into the next segment via the recorder; got {recorder.ResumeCount}.");
            Expect(recorder.CurrentSegmentIndex == segmentAfterOutage + 1,
                $"Resume must open the NEXT segment; went {segmentAfterOutage} -> {recorder.CurrentSegmentIndex}.");
        }

        Console.WriteLine(" -> Take lifecycle, watchdog arming (watchable only), boundary-before-watchdog ordering, " +
                          "outage -> backoff -> grace expiry, and segment resume all verified with NO audio engine!");
    }

    /// <summary>
    /// NET-018: stands in for StreamRecorderService in the supervisor's isolation test. Records the
    /// effect calls so their SEQUENCE can be asserted, and lets the test decide what the arm reports.
    /// No sink, no transport, no audio engine.
    /// </summary>
    private sealed class FakeTakeRecorder : ITakeRecorder
    {
        private readonly List<RecordingStopReason> _stops = new();

        public bool IsRecording { get; set; }

        public int CurrentSegmentIndex { get; set; } = 1;

        public string? CurrentRecordingPath { get; set; } = "/fake/seg01.mp4";

        /// <summary>The boundary the fake arm reports on the NEXT evaluation, then clears.</summary>
        public RecordingBoundary NextBoundary { get; set; } = RecordingBoundary.None;

        public IReadOnlyList<RecordingStopReason> Stops => _stops;

        public int ResumeCount { get; private set; }

        public RecordingBoundary EvaluateBoundary(RecordingConfig config)
        {
            RecordingBoundary boundary = NextBoundary;
            NextBoundary = RecordingBoundary.None;   // a real boundary fires once
            return boundary;
        }

        public bool RollSegment(RecordingConfig config, StreamFormat format, RecordingStopReason reason)
        {
            CurrentSegmentIndex++;
            CurrentRecordingPath = $"/fake/seg{CurrentSegmentIndex:00}.mp4";
            return true;
        }

        public bool StartNextSegment(RecordingConfig config, StreamFormat format)
        {
            ResumeCount++;
            CurrentSegmentIndex++;
            CurrentRecordingPath = $"/fake/seg{CurrentSegmentIndex:00}.mp4";
            IsRecording = true;
            return true;
        }

        public void StopRecording(RecordingStopReason reason)
        {
            _stops.Add(reason);
            IsRecording = false;
        }
    }

    /// <summary>
    /// NET-018: a watchable feed with no transport behind it. The supervisor only asks two questions —
    /// "must this be watched?" and "please reconnect" — so that is all this answers.
    /// </summary>
    private sealed class FakeWatchableFeed : IMediaSource, IRecoverableMediaSource
    {
        private int _reconnectCount;

        public string Name => "Fake Watchable Feed";
        public string Address => "fake://watchable";
        public bool IsConnected => true;

        public bool RequiresInputHealthWatch { get; set; } = true;

        public int ReconnectCount => Volatile.Read(ref _reconnectCount);

        public event Action<IntPtr, int, int, int, int, double, long>? VideoFrameReceived { add { } remove { } }
        public event Action<IntPtr, int, int, int, long>? AudioFrameReceived { add { } remove { } }
        public event Action<StreamTelemetry>? TelemetryUpdated { add { } remove { } }
        public event Action<string>? StatusChanged { add { } remove { } }
        public event Action<string>? ErrorOccurred { add { } remove { } }

        public void Connect() { }
        public void Disconnect() { }

        public void EnsureConnected() => Interlocked.Increment(ref _reconnectCount);

        public void Dispose() { }
    }

}
