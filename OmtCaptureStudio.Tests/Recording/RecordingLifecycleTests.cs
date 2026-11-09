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
    /// A network-like test source: wraps a real <see cref="SyntheticPatternSource"/> (so frames carry
    /// genuine buffers the video/audio pipeline can consume) but presents itself as a recoverable
    /// remote feed whose frames can be suspended, and counts auto-reconnect attempts.
    /// </summary>
    private sealed class RecoverableTestSource : IMediaSource, IRecoverableMediaSource
    {
        private readonly SyntheticPatternSource _inner = new();
        private volatile bool _forwarding = true;
        private int _reconnectCount;

        public RecoverableTestSource()
        {
            _inner.VideoFrameReceived += (p, l, w, h, s, fps, ts) =>
            {
                if (_forwarding) VideoFrameReceived?.Invoke(p, l, w, h, s, fps, ts);
            };
            _inner.AudioFrameReceived += (p, ch, smp, rate, ts) =>
            {
                if (_forwarding) AudioFrameReceived?.Invoke(p, ch, smp, rate, ts);
            };
            _inner.TelemetryUpdated += t => TelemetryUpdated?.Invoke(t);
            _inner.StatusChanged += s => StatusChanged?.Invoke(s);
            _inner.ErrorOccurred += e => ErrorOccurred?.Invoke(e);
        }

        public string Name => "Recoverable Test Source";
        public string Address => "test://recoverable";

        /// <summary>The socket stays up during an outage — only the frames stop (the ADR-0002 failure mode).</summary>
        public bool IsConnected => true;

        public bool RequiresInputHealthWatch => true;

        public int ReconnectCount => Volatile.Read(ref _reconnectCount);

        public event Action<IntPtr, int, int, int, int, double, long>? VideoFrameReceived;
        public event Action<IntPtr, int, int, int, long>? AudioFrameReceived;
        public event Action<StreamTelemetry>? TelemetryUpdated;
        public event Action<string>? StatusChanged;
        public event Action<string>? ErrorOccurred;

        public void Connect() => _inner.Connect();

        public void Disconnect() => _inner.Disconnect();

        /// <summary>Counts the attempt; deliberately does NOT resume frames (the test drives Resume).</summary>
        public void EnsureConnected() => Interlocked.Increment(ref _reconnectCount);

        public void Suspend() => _forwarding = false;

        public void Resume() => _forwarding = true;

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>
    /// A sink that records the ORDER of arm/disarm operations and lets the test observe the arm's gate
    /// from inside the sink callbacks — which is exactly how the sequencing becomes observable at all.
    /// </summary>
    private sealed class SignalRecordingSink : OmtCaptureStudio.Services.Sinks.IRecordingSink
    {
        public readonly List<string> Steps = new();
        public Action<string>? OnStep;
        public System.Threading.ManualResetEventSlim? BlockVideoWrite;
        /// <summary>When set, <see cref="FinalizeSink"/> blocks here until the gate is set (DEF-02 ordering test).</summary>
        public System.Threading.ManualResetEventSlim? BlockFinalize;
        /// <summary>Non-zero once a finalize has entered the sink — the DEF-02 test waits on this, not on a sleep.</summary>
        public int FinalizeEntered;
        public bool ThrowOnFinalize;

        private long _video, _audio;

        public long VideoFramesReceived => _video;
        public long AudioBytesReceived => _audio;
        public long VideoFramesDropped => 0;
        public long AudioChunksDropped => 0;
        public string ResolvedEncoderName => "SignalSink";
        public string DiagnosticTail => "diag-tail";
        public bool RequiresDurableStorage => false;

        public event Action<string>? SinkError;
        public void RaiseSinkError(string msg) => SinkError?.Invoke(msg);

        private void Step(string name) { Steps.Add(name); OnStep?.Invoke(name); }

        public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
        {
            Step("init");
            return true;
        }

        public void WriteVideo(IntPtr pData, int dataLength)
        {
            Step("video");
            _video++;
            BlockVideoWrite?.Wait(2000);
        }

        public void WriteAudio(byte[] buffer, int offset, int count)
        {
            Step("audio");
            _audio += count;
        }

        public void FinalizeSink(out long totalBytesWritten)
        {
            Step("finalize");
            System.Threading.Interlocked.Increment(ref FinalizeEntered);
            BlockFinalize?.Wait(5000);
            totalBytesWritten = _audio + 4096;
            if (ThrowOnFinalize) throw new InvalidOperationException("transport already dead");
        }

        public void Dispose() { }
    }

    /// <summary>Count of a substring, for source-shape (locality) assertions.</summary>
    static int CountIn(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>Verifies sequential segment naming (ADR-0002 segment-per-outage).</summary>
    static void TestRecordingSegments()
    {
        Console.WriteLine("\n[Step 0.672] Verifying RecordingSegments _partNN naming...");

        string basePath = Path.Combine(Path.GetTempPath(), "OMT_Capture_2026-10-15_13-08-43.mp4");
        int[] indices = { 1, 2, 3, 10 };
        string[] suffixes = { "", "_part02", "_part03", "_part10" };
        string stem = Path.Combine(Path.GetTempPath(), "OMT_Capture_2026-10-15_13-08-43");

        for (int i = 0; i < indices.Length; i++)
        {
            string expected = $"{stem}{suffixes[i]}.mp4";
            string actual = RecordingSegments.SegmentFilePath(basePath, indices[i]);
            if (actual != expected)
            {
                throw new Exception($"Segment {indices[i]}: expected '{expected}', got '{actual}'");
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Segment naming verified: base, _part02, _part03, _part10!");
        Console.ResetColor();
    }

    /// <summary>
    /// Headless verification of the ADR-0002 outage policy at the CaptureSession level: the watchdog
    /// arms only for a recording over a recoverable (network) source, the current segment is finalized
    /// at the outage boundary before anything else, frames returning within grace resume recording into
    /// the next _partNN segment, and reconnect attempts follow the backoff schedule. Clock-driven — no
    /// real 5 s sleeps.
    /// </summary>
    static void TestRecordingOutageRecovery()
    {
        Console.WriteLine("\n[Step 0.673] Verifying recording input-loss watchdog, auto-reconnect & segment-per-outage (headless)...");

        long now = 1_000_000;
        Func<long> clock = () => now;

        string outDir = Path.Combine(Path.GetTempPath(), "OMT_Outage_Test");
        if (Directory.Exists(outDir))
        {
            try { Directory.Delete(outDir, true); } catch { }
        }
        Directory.CreateDirectory(outDir);

        var config = new RecordingConfig
        {
            OutputDirectory = outDir,
            FilenamePrefix = "Outage_Test",
            ContainerFormat = OutputContainerFormat.MP4,
            EncoderChoice = VideoEncoderChoice.CpuX264,
            Quality = QualityPreset.High,
            InputLossThresholdSeconds = 5.0,
            InputRecoveryGraceSeconds = 30.0
        };

        // --- (a) An in-process source (test signal / synthetic) is never watched, even while recording. ---
        using (var sink = new NullRecordingSink())
        using (var recorder = new StreamRecorderService(sink))
        using (var audio = new AudioEngine())
        using (var session = new CaptureSession(audio, recorder, ownsDependencies: false, healthClock: clock))
        {
            session.Connect(new SyntheticPatternSource());
            WaitForFormat(session, "synthetic source");

            if (!session.StartRecording(config))
            {
                throw new Exception("Failed to start recording on the synthetic source.");
            }

            bool lostFired = false;
            session.InputLost += () => lostFired = true;

            now += 60_000; // a full minute of simulated silence
            session.CheckInputHealth();

            if (lostFired || session.IsInputLost || !session.IsRecording)
            {
                throw new Exception("The watchdog must not watch in-process sources (test signal / synthetic).");
            }

            session.StopRecording();
        }

        Console.WriteLine(" -> In-process sources are correctly excluded from the input-health watchdog.");

        // --- (b) A recoverable (network-like) source: outage -> finalize -> reconnect -> resume segment. ---
        using (var sink = new NullRecordingSink())
        using (var recorder = new StreamRecorderService(sink))
        using (var audio = new AudioEngine())
        using (var session = new CaptureSession(audio, recorder, ownsDependencies: false, healthClock: clock))
        {
            var source = new RecoverableTestSource();

            long frames = 0;
            session.VideoFrameAvailable += (p, l, w, h, s, fps, ts) => Interlocked.Increment(ref frames);

            var order = new List<string>();
            RecordingStopSummary? outageStopSummary = null;
            session.RecordingStopped += summary => { outageStopSummary = summary; order.Add("finalize"); };
            session.InputLost += () => order.Add("input-lost");
            session.InputRecovered += () => order.Add("input-recovered");

            session.Connect(source);
            WaitForFormat(session, "recoverable source");

            // Not recording => not watched, even with the feed dead.
            source.Suspend();
            Thread.Sleep(80);
            now += 60_000;
            session.CheckInputHealth();
            if (session.IsInputLost || session.IsInputHealthWatched)
            {
                throw new Exception("The watchdog must not arm unless a recording is in progress.");
            }

            // Connect the take.
            source.Resume();
            long framesBeforeTake = Interlocked.Read(ref frames);
            WaitFor(() => Interlocked.Read(ref frames) > framesBeforeTake, "frames before starting the take");
            now += 1000;
            session.CheckInputHealth();

            if (!session.StartRecording(config))
            {
                throw new Exception("Failed to start recording on the recoverable source.");
            }
            if (session.RecordingSegmentIndex != 1 || !session.IsInputHealthWatched)
            {
                throw new Exception($"Take must open segment 1 and arm the watchdog: segment={session.RecordingSegmentIndex}, watched={session.IsInputHealthWatched}");
            }
            string? firstSegment = session.CurrentRecordingPath;

            if (session.IsInputLost)
            {
                throw new Exception("Watchdog reported loss while frames were flowing.");
            }

            // --- Outage boundary: silence past the threshold. ---
            source.Suspend();
            Thread.Sleep(80);
            now += 6000; // > 5 s threshold
            session.CheckInputHealth();

            if (session.IsRecording)
            {
                throw new Exception("Recording must be stopped (segment finalized) at the outage boundary.");
            }
            if (order.Count < 2 || order[0] != "finalize" || order[1] != "input-lost")
            {
                throw new Exception($"Segment must be finalized BEFORE the alarm is raised: [{string.Join(",", order)}]");
            }
            if (outageStopSummary == null || outageStopSummary.SegmentIndex != 1 || outageStopSummary.MediaDuration <= TimeSpan.Zero)
            {
                throw new Exception(
                    $"Boundary stop must carry a segment-1 summary with a positive media duration: " +
                    $"{(outageStopSummary == null ? "null" : $"seg={outageStopSummary.SegmentIndex}, media={outageStopSummary.MediaDuration}")}");
            }

            // --- Reconnect attempts follow the backoff schedule (250 ms, then 500 ms). ---
            WaitFor(() => source.ReconnectCount >= 1, "first auto-reconnect attempt");
            now += 250;
            session.CheckInputHealth();
            WaitFor(() => source.ReconnectCount >= 2, "second auto-reconnect attempt");

            now += 100; // only 100 ms of the 500 ms window elapsed
            session.CheckInputHealth();
            Thread.Sleep(80);
            if (source.ReconnectCount != 2)
            {
                throw new Exception($"Reconnect attempts must honour the backoff schedule; got {source.ReconnectCount} after 350 ms.");
            }

            // The second attempt (at T+250) schedules the third at T+750; advance to exactly that.
            now += 400;
            session.CheckInputHealth();
            WaitFor(() => source.ReconnectCount >= 3, "third auto-reconnect attempt");

            // --- Frames return within the grace period: resume into the next _partNN segment. ---
            long framesBeforeResume = Interlocked.Read(ref frames);
            source.Resume();
            WaitFor(() => Interlocked.Read(ref frames) > framesBeforeResume, "frames after resume");
            now += 100;
            session.CheckInputHealth();

            if (!session.IsRecording)
            {
                throw new Exception("Recording must resume when frames return within the grace period.");
            }
            if (session.RecordingSegmentIndex != 2)
            {
                throw new Exception($"Resume must open segment 2, got {session.RecordingSegmentIndex}.");
            }
            string? second = session.CurrentRecordingPath;
            if (second == null || !second.EndsWith("_part02" + Path.GetExtension(firstSegment ?? ".mp4"), StringComparison.Ordinal))
            {
                throw new Exception($"Resumed segment must be named …_part02: got '{second}' (from '{firstSegment}')");
            }
            if (!order.SequenceEqual(new[] { "finalize", "input-lost", "input-recovered" }))
            {
                throw new Exception($"Unexpected transition order: [{string.Join(",", order)}]");
            }

            session.StopRecording();
        }

        Console.WriteLine(" -> Outage finalize-before-alarm, backoff reconnect, and _part02 resume verified!");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Input-loss watchdog, auto-reconnect, and segment-per-outage verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// DEF-01: a stop that follows dead air must report MEDIA duration (frames / stamped fps), never
    /// wall clock — the watchdog-triggered stop used to count the input-loss window and disagreed with
    /// the container (QA: 11.05 s reported vs 5.92 s on disk). Real (not injected) time here, so the
    /// wall clock genuinely accrues dead air while no frames arrive.
    /// </summary>
    static void TestRecordingStopReportsMediaDuration()
    {
        Console.WriteLine("\n[Step 0.674] Verifying DEF-01: stop reports MEDIA duration, not wall clock (dead air excluded)...");

        string outDir = Path.Combine(Path.GetTempPath(), "OMT_Duration_Test");
        if (Directory.Exists(outDir))
        {
            try { Directory.Delete(outDir, true); } catch { }
        }
        Directory.CreateDirectory(outDir);

        using var sink = new NullRecordingSink();
        using var recorder = new StreamRecorderService(sink);
        using var audio = new AudioEngine();
        using var session = new CaptureSession(audio, recorder);
        var source = new RecoverableTestSource();

        RecordingStopSummary? summary = null;
        session.RecordingStopped += s => summary = s;

        session.Connect(source);
        WaitForFormat(session, "recoverable source");

        var config = new RecordingConfig
        {
            OutputDirectory = outDir,
            FilenamePrefix = "Duration_Test",
            ContainerFormat = OutputContainerFormat.MP4,
            EncoderChoice = VideoEncoderChoice.CpuX264,
            Quality = QualityPreset.High,
            // Far above the simulated gap, so the watchdog does not interfere with this test.
            InputLossThresholdSeconds = 999,
            InputRecoveryGraceSeconds = 999
        };

        if (!session.StartRecording(config))
        {
            throw new Exception("StartRecording failed for the DEF-01 duration test.");
        }

        // ~0.6 s of live frames, then 1.2 s of dead air (frames stop, nothing is written).
        Thread.Sleep(600);
        source.Suspend();
        Thread.Sleep(1200);

        long framesWritten = session.RecordingFramesWritten;
        TimeSpan wallAtStop = session.RecordingElapsedTime;
        session.StopRecording();

        if (summary == null)
        {
            throw new Exception("RecordingStopped did not fire for the DEF-01 duration test.");
        }

        // Wall clock really did span the dead air...
        if (summary.WallClockDuration.TotalSeconds < 1.5)
        {
            throw new Exception($"Wall-clock duration should span the dead air (>=1.5s), got {summary.WallClockDuration.TotalSeconds:F3}s");
        }

        // ...and the reported duration must NOT be that wall clock: dead air is excluded.
        if (summary.MediaDuration.TotalSeconds >= summary.WallClockDuration.TotalSeconds - 1.0)
        {
            throw new Exception(
                $"DEF-01 regression: reported duration {summary.MediaDuration.TotalSeconds:F3}s tracks wall clock " +
                $"{summary.WallClockDuration.TotalSeconds:F3}s (dead air not excluded).");
        }

        // Media duration is frames / stamped fps — SyntheticPatternSource stamps 60 fps.
        double expectedMediaSeconds = framesWritten / 60.0;
        if (framesWritten <= 0 || Math.Abs(summary.MediaDuration.TotalSeconds - expectedMediaSeconds) > 0.35)
        {
            throw new Exception(
                $"Media duration {summary.MediaDuration.TotalSeconds:F3}s does not match frames/fps " +
                $"({framesWritten} frames -> {expectedMediaSeconds:F3}s).");
        }

        if (summary.MediaDuration > wallAtStop)
        {
            throw new Exception($"Media duration {summary.MediaDuration} cannot exceed the captured span {wallAtStop}.");
        }
        if (summary.AudioDuration <= TimeSpan.Zero)
        {
            throw new Exception("Audio duration should be positive (audio flowed before the gap).");
        }

        Console.WriteLine(
            $" -> media {summary.MediaDuration.TotalSeconds:F3}s (frames {framesWritten}) vs wall-clock " +
            $"{summary.WallClockDuration.TotalSeconds:F3}s; dead air excluded.");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> DEF-01 verified: reported duration is media duration, wall clock logged separately.");
        Console.ResetColor();
    }

    /// <summary>
    /// FEAT-07: pure recording-health analysis with synthetic telemetry — verdict computation, drop
    /// thresholds and the A/V clamp warning. No FFmpeg, no UI, no I/O.
    /// </summary>
    static void TestRecordingHealthModel()
    {
        Console.WriteLine("\n[Step 0.675] Verifying RecordingHealth model (verdict, drop thresholds, clamp warning)...");

        var healthy = new RecordingHealthSnapshot
        {
            IsRecording = true,
            HasTake = true,
            SegmentIndex = 1,
            EncoderName = RecordingHealth.EncoderDisplayName(VideoEncoderChoice.CpuX264),
            AchievedBitrateMbps = 6.2,
            VideoFramesWritten = 360,
            VideoFramesDropped = 0,
            AudioChunksDropped = 0,
            MediaDuration = TimeSpan.FromSeconds(6.0),
            AudioDuration = TimeSpan.FromSeconds(6.02),
            WallClockDuration = TimeSpan.FromSeconds(6.1),
            AvDriftRatio = 1.0,
            AvDriftClamped = false
        };

        if (RecordingHealth.IsDropWarning(healthy) || RecordingHealth.IsDriftWarning(healthy) || !RecordingHealth.IsAvParityOk(healthy))
        {
            throw new Exception("A healthy snapshot must not raise any warning.");
        }
        if (RecordingHealth.BitrateText(healthy) != "6.2 Mbps" || RecordingHealth.EncoderText(healthy) != "x264 (CPU)")
        {
            throw new Exception($"Health text mismatch: bitrate='{RecordingHealth.BitrateText(healthy)}', encoder='{RecordingHealth.EncoderText(healthy)}'");
        }

        // Drop threshold: warns AT the threshold, not below.
        if (RecordingHealth.IsDropWarning(healthy with { VideoFramesDropped = RecordingHealth.DropWarnThreshold - 1 }))
        {
            throw new Exception("Drops below the threshold must not warn.");
        }
        if (!RecordingHealth.IsDropWarning(healthy with { VideoFramesDropped = RecordingHealth.DropWarnThreshold }))
        {
            throw new Exception("Drops at the threshold must warn.");
        }

        // Drift band: |ratio - 1| beyond the band warns; a pinned clamp always warns.
        // (Strictly inside: 1.0 + 0.02 is not representable exactly in binary, so use a margin.)
        if (RecordingHealth.IsDriftWarning(healthy with { AvDriftRatio = 1.0 + RecordingHealth.DriftWarnBand - 0.001 }))
        {
            throw new Exception("Drift inside the band must not warn.");
        }
        if (!RecordingHealth.IsDriftWarning(healthy with { AvDriftRatio = 1.0 + RecordingHealth.DriftWarnBand + 0.001 }))
        {
            throw new Exception("Drift beyond the band must warn.");
        }
        if (!RecordingHealth.IsDriftWarning(healthy with { AvDriftRatio = 1.0, AvDriftClamped = true }))
        {
            throw new Exception("A pinned clamp must warn even at ratio 1.0.");
        }
        if (!RecordingHealth.DriftText(healthy with { AvDriftRatio = 1.1, AvDriftClamped = true }).Contains("clamped", StringComparison.Ordinal))
        {
            throw new Exception("DriftText must surface the clamp.");
        }

        // Duration parity: within tolerance is OK, beyond it is not, and a missing track cannot mismatch.
        if (!RecordingHealth.IsAvParityOk(healthy with { AudioDuration = TimeSpan.FromSeconds(6.0 + RecordingHealth.AvParityToleranceSeconds) }))
        {
            throw new Exception("Parity exactly at the tolerance edge must be OK.");
        }
        if (RecordingHealth.IsAvParityOk(healthy with { AudioDuration = TimeSpan.FromSeconds(6.0 + RecordingHealth.AvParityToleranceSeconds + 0.5) }))
        {
            throw new Exception("Parity beyond the tolerance must be reported.");
        }
        if (!RecordingHealth.IsAvParityOk(healthy with { AudioDuration = TimeSpan.Zero }))
        {
            throw new Exception("A missing audio track must not be called a parity mismatch.");
        }

        // Stop verdict: reports MEDIA duration + drops + finalize + segment + encoder, never wall clock.
        var summary = new RecordingStopSummary(
            Path: @"C:\captures\Take.mp4",
            MediaDuration: TimeSpan.FromSeconds(5.91),
            WallClockDuration: TimeSpan.FromSeconds(11.06), // includes ~5 s of dead air
            AudioDuration: TimeSpan.FromSeconds(5.93),
            FinalizeDuration: TimeSpan.FromMilliseconds(412),
            FileSizeBytes: 486_548,
            VideoFramesWritten: 355,
            VideoFramesDropped: 2,
            AudioChunksDropped: 0,
            SegmentIndex: 1,
            EncoderName: "x264 (CPU)",
            DiagnosticTail: "frame=1 fps=0 q=0.0");

        string verdict = RecordingHealth.BuildStopVerdict(summary);
        Console.WriteLine($" -> {verdict}");

        foreach (string expected in new[] { "video 5.91s", "audio 5.93s", "parity OK", "drops 2 video", "finalize 412 ms", "segment 1", "x264 (CPU)" })
        {
            if (!verdict.Contains(expected, StringComparison.Ordinal))
            {
                throw new Exception($"Stop verdict is missing '{expected}': {verdict}");
            }
        }
        if (verdict.Contains("11.06", StringComparison.Ordinal))
        {
            throw new Exception($"DEF-01 regression: the stop verdict must not report wall clock: {verdict}");
        }

        string wallNote = RecordingHealth.WallClockNote(summary);
        if (!wallNote.Contains("11.06", StringComparison.Ordinal) || !wallNote.Contains("dead air", StringComparison.Ordinal))
        {
            throw new Exception($"Wall-clock must be reported separately and labelled: {wallNote}");
        }

        // The summary derives its own snapshot: achieved bitrate comes from file size / MEDIA duration.
        double expectedMbps = 486_548 * 8.0 / 1_000_000.0 / 5.91;
        if (Math.Abs(summary.ToHealthSnapshot().AchievedBitrateMbps - expectedMbps) > 0.01)
        {
            throw new Exception($"Derived bitrate mismatch: {summary.ToHealthSnapshot().AchievedBitrateMbps:F3} vs {expectedMbps:F3}");
        }
        if (summary.ToHealthSnapshot().DiagnosticTail != "frame=1 fps=0 q=0.0")
        {
            throw new Exception("The summary must carry its diagnostic tail through to the snapshot.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> RecordingHealth verdict, drop thresholds, clamp warning and parity verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// ADR-0005: disk-storage policy is a pure module now, so preflight and low-water decisions are
    /// tested as functions of drive facts — no real volume, no mocks, no disk I/O.
    /// </summary>
    static void TestRecordingStorageGuard()
    {
        Console.WriteLine("\n[Step 0.681] Verifying RecordingStorageGuard (preflight, low-water, size estimate)...");

        // --- File-system guard: FAT/FAT32 carry a 4 GB single-file limit; exFAT/NTFS do not. ---
        if (!RecordingStorageGuard.IsFattishFileSystem("FAT32")) throw new Exception("FAT32 must be flagged.");
        if (!RecordingStorageGuard.IsFattishFileSystem("fat")) throw new Exception("The file-system check must be case-insensitive.");
        if (RecordingStorageGuard.IsFattishFileSystem("NTFS")) throw new Exception("NTFS must not be flagged.");
        if (RecordingStorageGuard.IsFattishFileSystem("exFAT")) throw new Exception("exFAT has no 4 GB limit and must not be flagged.");
        if (RecordingStorageGuard.IsFattishFileSystem(null) || RecordingStorageGuard.IsFattishFileSystem("  ")) throw new Exception("A blank format must not be flagged.");

        // --- Preflight verdicts, as pure functions of the drive facts. ---
        var fat = RecordingStorageGuard.Classify("D:\\", "FAT32", 100L * 1024 * 1024 * 1024, 1_000_000_000);
        if (fat.Outcome != StoragePreflightOutcome.RefusedFatFileSystem || fat.CanRecord || !fat.IsRefusal)
        {
            throw new Exception($"FAT32 must be refused outright; got {fat.Outcome}.");
        }
        if (fat.Message == null || !fat.Message.Contains("4 GB")) throw new Exception("The FAT32 refusal must explain the 4 GB limit.");

        // Estimated 9 GB at Medium/1080p60 → required floor is 4x = 36 GB, so 8 GB free is refused.
        var tight = RecordingStorageGuard.Classify("D:\\", "NTFS", 8L * 1024 * 1024 * 1024, 9_000_000_000);
        if (tight.Outcome != StoragePreflightOutcome.RefusedInsufficientSpace || tight.CanRecord || !tight.IsRefusal)
        {
            throw new Exception($"Insufficient space must be refused; got {tight.Outcome}.");
        }
        if (tight.RequiredBytes != 36_000_000_000L)
        {
            throw new Exception($"Required headroom must be 4x the estimate (36000000000); got {tight.RequiredBytes}.");
        }
        if (tight.Message == null || !tight.Message.Contains("60-minute"))
        {
            throw new Exception($"The space refusal must state the preflight window; got: {tight.Message}");
        }

        var ok = RecordingStorageGuard.Classify("D:\\", "NTFS", 100L * 1024 * 1024 * 1024, 9_000_000_000);
        if (ok.Outcome != StoragePreflightOutcome.Ok || !ok.CanRecord || ok.IsRefusal)
        {
            throw new Exception($"A roomy NTFS volume must pass; got {ok.Outcome}.");
        }

        // The absolute floor dominates a tiny estimate.
        var floor = RecordingStorageGuard.Classify("D:\\", "NTFS", 3L * 1024 * 1024 * 1024, 1_000);
        if (floor.RequiredBytes != RecordingStorageGuard.MinimumPreflightFreeBytes)
        {
            throw new Exception($"A tiny estimate must fall back to the 2 GB floor; got {floor.RequiredBytes}.");
        }
        if (RecordingStorageGuard.RequiredPreflightBytes(1_000_000_000) != 4_000_000_000L)
        {
            throw new Exception("An above-floor estimate must require 4x.");
        }

        // An unreadable volume proceeds rather than faking a verdict.
        var unknown = RecordingStorageGuard.Undetermined();
        if (!unknown.CanRecord || unknown.IsRefusal) throw new Exception("An undetermined volume must proceed.");

        // --- Low-water verdict. ---
        if (!RecordingStorageGuard.IsLowOnSpace("NTFS", 500L * 1024 * 1024)) throw new Exception("500 MB free on NTFS is below the 1 GB low-water mark.");
        if (RecordingStorageGuard.IsLowOnSpace("NTFS", 5L * 1024 * 1024 * 1024)) throw new Exception("5 GB free on NTFS is above the low-water mark.");
        if (RecordingStorageGuard.IsLowOnSpace("FAT32", 10L * 1024 * 1024)) throw new Exception("FAT32 must be exempt from the low-water mark (preflight refuses it).");
        if (RecordingStorageGuard.LowWaterMarkBytes != 1024L * 1024 * 1024) throw new Exception("The documented 1 GB low-water mark changed.");

        // --- Session-size estimate: locked to the documented baseline. ---
        var medium = new RecordingConfig { Quality = QualityPreset.Medium, EncoderChoice = VideoEncoderChoice.CpuX264 };
        var high = new RecordingConfig { Quality = QualityPreset.High, EncoderChoice = VideoEncoderChoice.CpuX264 };
        var prores = new RecordingConfig { Quality = QualityPreset.Medium, EncoderChoice = VideoEncoderChoice.ProRes };
        var hd60 = new StreamFormat { Width = 1920, Height = 1080, FrameRate = 60.0 };

        long mediumHd60 = RecordingStorageGuard.EstimateSessionSize(medium, hd60);
        if (mediumHd60 != 9_000_000_000L)
        {
            throw new Exception($"Medium 1080p60 must estimate 9 GB (20 Mbps x 1 h); got {mediumHd60}.");
        }
        if (RecordingStorageGuard.EstimateSessionSize(high, hd60) != 13_500_000_000L)
        {
            throw new Exception("High quality must scale the estimate by 1.5x.");
        }
        if (RecordingStorageGuard.EstimateSessionSize(prores, hd60) != 54_000_000_000L)
        {
            throw new Exception("ProRes must scale the estimate by 6x.");
        }
        if (RecordingStorageGuard.EstimateSessionSize(medium, hd60 with { Width = 3840, Height = 2160 }) != 36_000_000_000L)
        {
            throw new Exception("4x the pixels must estimate 4x the bytes.");
        }
        if (RecordingStorageGuard.EstimateSessionSize(medium, hd60 with { FrameRate = 30.0 }) != 4_500_000_000L)
        {
            throw new Exception("Half the frame rate must halve the estimate.");
        }
        // A zero/absent frame rate falls back to 60 rather than dividing by zero.
        if (RecordingStorageGuard.EstimateSessionSize(medium, hd60 with { FrameRate = 0.0 }) != 9_000_000_000L)
        {
            throw new Exception("A zero frame rate must fall back to the 60 fps baseline.");
        }

        // --- Byte formatting used in operator-facing messages. ---
        if (RecordingStorageGuard.FormatBytes(3L * 1024 * 1024 * 1024) != "3.0 GB") throw new Exception("GB formatting changed.");
        if (RecordingStorageGuard.FormatBytes(500L * 1024 * 1024) != "500 MB") throw new Exception("MB formatting changed.");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Storage guard verified (FAT32, headroom, low-water, estimate scaling, formatting)!");
        Console.ResetColor();
    }

    /// <summary>
    /// ADR-0005: the arm/disarm sequence is one deep module with one authoritative gate and one
    /// finalize path. These assertions are deliberately observable-order assertions (what the SINK saw
    /// and when, versus the gate) rather than implementation poking.
    /// </summary>
    static void TestRecordingArm()
    {
        Console.WriteLine("\n[Step 0.682] Verifying RecordingArm (arm order, single finalize, drain, convergence)...");

        var config = new RecordingConfig { OutputDirectory = Path.GetTempPath(), FilenamePrefix = "ArmTest" };
        var format = new StreamFormat { Width = 320, Height = 240, FrameRate = 10.0, SampleRate = 48000, Channels = 2 };

        // ---- Arm order: the sink must be initialized while the gate is still OPEN, then the gate flips.
        var sink = new SignalRecordingSink();
        var arm = new RecordingArm(sink);
        bool? gateDuringInit = null;
        sink.OnStep = step => { if (step == "init") gateDuringInit = arm.IsRecording; };

        if (!arm.Arm(config, format))
        {
            Console.WriteLine(" -> [SKIP] storage preflight refused the temp volume in this environment; arm-path assertions skipped (policy covered by Step 0.681).");
            return;
        }

        if (gateDuringInit != false)
        {
            throw new Exception($"The gate must still be CLOSED while the sink initializes; saw {gateDuringInit}.");
        }
        if (!arm.IsRecording) throw new Exception("Arm must leave the gate open.");
        if (arm.CurrentSegmentIndex != 1) throw new Exception("A fresh take must start at segment 1.");

        // Arm reset the A/V clock as its FIRST step: a freshly reset clock reports unity drift.
        double ratioAfterArm = arm.AvDriftRatio;
        Console.WriteLine($" -> A/V clock ratio immediately after Arm: {ratioAfterArm:F6} (reset inside the arm sequence)");
        if (ratioAfterArm <= 0 || Math.Abs(ratioAfterArm - 1.0) > 1e-6)
        {
            throw new Exception($"Arm must reset the A/V clock to unity before flipping the gate; ratio was {ratioAfterArm}.");
        }

        // ---- Writes reach the sink while armed.
        var frame = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try
        {
            arm.WriteVideoFrame(frame, 16);
            arm.WriteAudioData(new byte[8], 0, 8);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(frame); }

        if (sink.VideoFramesReceived != 1) throw new Exception("An armed write must reach the sink.");
        if (sink.AudioBytesReceived != 8) throw new Exception("An armed audio write must reach the sink.");

        // ---- Disarm: gate closes BEFORE finalize, exactly one finalize, and the summary is published.
        bool? gateDuringFinalize = null;
        sink.OnStep = step => { if (step == "finalize") gateDuringFinalize = arm.IsRecording; };
        RecordingStopSummary? summary = null;
        arm.RecordingStopped += s => summary = s;

        if (!arm.Disarm(RecordingStopReason.Manual, out var direct)) throw new Exception("Disarm must report that it stopped an active take.");
        if (gateDuringFinalize != false) throw new Exception($"The gate must be CLOSED before finalize; saw {gateDuringFinalize}.");
        if (CountIn(string.Join(",", sink.Steps), "finalize") != 1) throw new Exception($"Exactly one finalize expected; steps: {string.Join(" ", sink.Steps)}");
        if (summary == null || direct == null) throw new Exception("Disarm must publish a stop summary.");
        if (summary.Reason != RecordingStopReason.Manual) throw new Exception($"Manual stop must report Manual; got {summary.Reason}.");
        if (summary.VideoFramesWritten != 1) throw new Exception($"The summary must carry the frame count; got {summary.VideoFramesWritten}.");
        if (summary.MediaDuration.TotalSeconds <= 0) throw new Exception("The summary must carry a media duration.");
        if (RecordingHealth.ReasonText(summary.Reason) != "stopped by operator") throw new Exception("The verdict reason text is wrong.");
        if (!RecordingHealth.BuildStopVerdict(summary).StartsWith("Stop verdict (stopped by operator)")) throw new Exception("The stop verdict must name the reason.");

        // ---- Disarm is idempotent, and the gate now blocks writes.
        sink.Steps.Clear();
        if (arm.Disarm(RecordingStopReason.Manual, out _)) throw new Exception("A second Disarm must be a no-op.");
        var frame2 = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try { arm.WriteVideoFrame(frame2, 16); } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(frame2); }
        if (sink.VideoFramesReceived != 1) throw new Exception("A write after Disarm must not reach the sink.");

        // ---- Bounded drain: a writer stuck inside the sink must NOT hang the caller. The three
        //      low-disk/UI paths depend on this; an unbounded wait would freeze the UI tick thread.
        var block = new System.Threading.ManualResetEventSlim(false);
        sink.BlockVideoWrite = block;
        if (!arm.Arm(config, format)) throw new Exception("Re-arm for the drain test failed.");
        sink.OnStep = null;
        var stuck = System.Threading.Tasks.Task.Run(() =>
        {
            var f = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
            try { arm.WriteVideoFrame(f, 16); } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(f); }
        });
        System.Threading.Thread.Sleep(20); // let the writer get in and block
        var drainWatch = System.Diagnostics.Stopwatch.StartNew();
        bool drained = arm.Disarm(RecordingStopReason.InputLoss, out var drainSummary);
        drainWatch.Stop();
        block.Set();
        stuck.Wait(2000);

        if (!drained) throw new Exception("Disarm must complete while a writer is stuck in the sink.");
        // The cap is DrainTimeoutMs of real time, plus at most one Sleep(1) overshoot (Windows
        // quantises a 1 ms sleep to ~15 ms), plus scheduling slack.
        long drainBudget = RecordingArm.DrainTimeoutMs + 100;
        if (drainWatch.ElapsedMilliseconds > drainBudget)
        {
            throw new Exception($"Disarm must be bounded (~{RecordingArm.DrainTimeoutMs} ms), took {drainWatch.ElapsedMilliseconds} ms (budget {drainBudget}).");
        }
        if (drainSummary == null || drainSummary.Reason != RecordingStopReason.InputLoss)
        {
            throw new Exception("The bounded-drain disarm must still publish an InputLoss summary.");
        }
        Console.WriteLine($" -> Bounded drain: Disarm returned in {drainWatch.ElapsedMilliseconds} ms with a writer stuck in the sink (cap {RecordingArm.DrainTimeoutMs} ms).");

        // ---- Convergence: every trigger routes through the one path, differing only in the reason.
        foreach (var (reason, viaFacade) in new[]
        {
            (RecordingStopReason.Manual, "StopRecording()"),
            (RecordingStopReason.InputLoss, "StopRecording(InputLoss)"),
            (RecordingStopReason.LowDisk, "StopRecording(LowDisk)")
        })
        {
            var s2 = new SignalRecordingSink();
            using var svc = new StreamRecorderService(s2);
            if (!svc.StartRecording(config, format)) throw new Exception($"Facade start failed for {viaFacade}.");
            s2.Steps.Clear();
            svc.StopRecording(reason);
            int finalizes = CountIn(string.Join(",", s2.Steps), "finalize");
            if (finalizes != 1) throw new Exception($"{viaFacade} must finalize exactly once; saw {finalizes}.");
            if (!string.IsNullOrEmpty(svc.CurrentRecordingPath) && svc.IsRecording) throw new Exception($"{viaFacade} must close the gate.");
        }

        // ---- Disposal routes through the same path rather than tearing down around it.
        var s3 = new SignalRecordingSink();
        var arm3 = new RecordingArm(s3);
        RecordingStopSummary? disposeSummary = null;
        arm3.RecordingStopped += s => disposeSummary = s;
        if (!arm3.Arm(config, format)) throw new Exception("Arm for the dispose test failed.");
        s3.Steps.Clear();
        arm3.Dispose();
        if (CountIn(string.Join(",", s3.Steps), "finalize") != 1) throw new Exception("Dispose must finalize through the arm.");
        if (disposeSummary == null || disposeSummary.Reason != RecordingStopReason.Dispose)
        {
            throw new Exception("Dispose must publish a Dispose-reason summary.");
        }

        arm3.Dispose(); // idempotent

        // ---- Secondary failure: the transport is already dead, so finalize throws. The original error
        //      must survive, a summary must still be published, and nothing may escape.
        var dead = new SignalRecordingSink { ThrowOnFinalize = true };
        var arm4 = new RecordingArm(dead);
        RecordingStopSummary? errSummary = null;
        var errors = new List<string>();
        arm4.RecordingStopped += s => errSummary = s;
        arm4.RecordingError += e => errors.Add(e);

        if (!arm4.Arm(config, format)) throw new Exception("Arm for the sink-failure test failed.");
        var f3 = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try { arm4.WriteVideoFrame(f3, 16); arm4.WriteVideoFrame(f3, 16); } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(f3); }
        dead.RaiseSinkError("FFmpeg audio pipe disconnected unexpectedly.");

        if (arm4.IsRecording) throw new Exception("A sink error must close the gate.");
        if (errSummary == null) throw new Exception("A sink error must still publish a summary (best-effort finalize).");
        if (errSummary.Reason != RecordingStopReason.SinkError) throw new Exception($"Sink failure must report SinkError; got {errSummary.Reason}.");
        if (errSummary.VideoFramesWritten != 2) throw new Exception($"The summary must carry what reached the transport; got {errSummary.VideoFramesWritten}.");
        if (errors.Count == 0) throw new Exception("The original sink error must be surfaced.");
        if (errors[errors.Count - 1] != "FFmpeg audio pipe disconnected unexpectedly.")
        {
            throw new Exception($"The ORIGINAL sink error must be reported last, unmasked; last was: {errors[errors.Count - 1]}");
        }
        if (!errors.Exists(e => e.Contains("may be incomplete")))
        {
            throw new Exception("The failed finalize must be reported as its own diagnostic.");
        }
        Console.WriteLine($" -> Sink failure: {errSummary.VideoFramesWritten} frames summarized, finalize failure reported without masking the original error.");

        arm4.Dispose();
        arm.Dispose();
        arm3.Dispose();

        // ---- DEF-02 / OBS-03: the low-disk error must be raised AFTER Disarm(LowDisk) completes, not
        //      synchronously at T+0. Raised inline it flashed "Recording Error: Low disk space…" first,
        //      and the stop summary then overwrote it ~500 ms later — masking the reason in the bottom
        //      bar (the drawer verdict was already correct). Mirrors OnSinkError, whose Disarm runs
        //      synchronously first so its error stays last.
        {
            var diskSink = new SignalRecordingSink();
            var diskArm = new RecordingArm(diskSink);
            var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
            diskArm.RecordingStopped += _ => order.Enqueue("stopped");
            diskArm.RecordingError += _ => order.Enqueue("error");

            if (!diskArm.Arm(config, format))
            {
                Console.WriteLine(" -> [SKIP] low-disk ordering: storage preflight refused the temp volume in this environment (policy covered by Step 0.681).");
            }
            else
            {
                // Hold the finalize open so the T+0-vs-after-disarm distinction is observable.
                var finalizeGate = new System.Threading.ManualResetEventSlim(false);
                diskSink.BlockFinalize = finalizeGate;

                diskArm.StopLowDisk("Recording stopped: low disk space on the destination volume.");

                // Wait for the background disarm to reach (and block inside) FinalizeSink.
                var spin = System.Diagnostics.Stopwatch.StartNew();
                while (System.Threading.Volatile.Read(ref diskSink.FinalizeEntered) == 0 && spin.ElapsedMilliseconds < 3000)
                {
                    System.Threading.Thread.Sleep(5);
                }
                if (System.Threading.Volatile.Read(ref diskSink.FinalizeEntered) == 0)
                {
                    throw new Exception("DEF-02: the background low-disk disarm never reached FinalizeSink.");
                }

                // THE discriminating assertion: while the finalize is still blocked, the low-disk error
                // must NOT have been raised. Raising it at T+0 is exactly the bug.
                if (!order.IsEmpty)
                {
                    throw new Exception($"DEF-02: the low-disk error must wait for the disarm to complete; saw [{string.Join(",", order)}] while finalize was still blocked.");
                }

                finalizeGate.Set(); // let the finalize finish and the continuation run

                spin.Restart();
                while (order.Count < 2 && spin.ElapsedMilliseconds < 3000)
                {
                    System.Threading.Thread.Sleep(5);
                }

                var seq = order.ToArray();
                if (seq.Length != 2 || seq[0] != "stopped" || seq[1] != "error")
                {
                    throw new Exception($"DEF-02: expected [stopped,error] (low-disk error AFTER the stop summary); got [{string.Join(",", seq)}].");
                }
                if (diskArm.IsRecording) throw new Exception("DEF-02: the low-disk stop must close the gate.");
                Console.WriteLine(" -> Low-disk ordering: error raised only AFTER Disarm(LowDisk) completed; the stop summary is not masked.");
            }

            diskArm.Dispose();
        }

        // ---- Locality: ONE finalize call site in the whole app outside the sink implementations, and
        //      no Task.Run finalize left in the orchestration classes (AC3).
        string? armSrc = FindRepoSource("OmtCaptureStudio/Services/RecordingArm.cs");
        string? svcSrc = FindRepoSource("OmtCaptureStudio/Services/StreamRecorderService.cs");
        string? sesSrc = FindRepoSource("OmtCaptureStudio/Services/CaptureSession.cs");
        string? supSrc = FindRepoSource("OmtCaptureStudio/Services/TakeSupervisor.cs");
        if (armSrc == null || svcSrc == null || sesSrc == null || supSrc == null)
        {
            Console.WriteLine($" -> [SKIP] source tree not reachable from {AppContext.BaseDirectory}; locality assertions skipped.");
        }
        else
        {
            string armText = File.ReadAllText(armSrc);
            string svcText = File.ReadAllText(svcSrc);
            string sesText = File.ReadAllText(sesSrc);
            string supText = File.ReadAllText(supSrc);

            if (CountIn(armText, "FinalizeSink(") != 1) throw new Exception("The arm must have exactly one FinalizeSink call site.");
            if (CountIn(svcText, "FinalizeSink(") != 0) throw new Exception("StreamRecorderService must not finalize — it is a facade now.");
            if (CountIn(sesText, "FinalizeSink(") != 0) throw new Exception("CaptureSession must not finalize — it goes through the arm.");
            if (CountIn(svcText, "Task.Run") != 0) throw new Exception("StreamRecorderService must not own a thread hop.");
            if (CountIn(sesText, "Task.Run(") != 0) throw new Exception("CaptureSession must not own a thread hop — the reconnect hop moved to TakeSupervisor (NET-018).");
            if (CountIn(supText, "Task.Run(") != 1) throw new Exception("TakeSupervisor should only Task.Run the reconnect hop, nothing else.");
            if (CountIn(armText, "Task.Run") != 1) throw new Exception("The arm must own the single off-thread disarm.");
            if (sesText.Contains("_audioResampler")) throw new Exception("CaptureSession must no longer own the A/V clock.");
            // The arm is the only file that assigns the gate, and it does so exactly three times:
            // open in Arm, close in Disarm, and close again in Arm's failure rollback (an event
            // subscriber throwing out of RecordingStarted must not leave the gate open).
            if (CountIn(armText, "_isRecording = true") != 1 ||
                CountIn(armText, "_isRecording = false") != 2 ||
                CountIn(svcText, "_isRecording") != 0 ||
                CountIn(sesText, "_isRecording") != 0)
            {
                throw new Exception("The arm must be the ONLY place the gate is assigned (1 open, 2 closes) and no other class may touch it.");
            }
            Console.WriteLine(" -> Finalize locality: 1 FinalizeSink site (the arm), 0 in the facade/session, 1 thread hop (now in TakeSupervisor — NET-018); the gate is assigned in NO other class (the arm itself has 1 open + 2 closes: Arm, Disarm, and Arm's failure rollback).");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> RecordingArm sequencing, single finalize, bounded drain, convergence and best-effort sink-failure verified!");
        Console.ResetColor();
    }
}

