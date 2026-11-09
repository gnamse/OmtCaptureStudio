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
    /// <summary>DIAG-BROADCAST: a capture provider that NEVER yields a frame - the failing DXGI/GDI state.</summary>
    sealed class StallingFrameSource : IFrameSource
    {
        private readonly GCHandle _pin;
        private readonly bool _yields;
        public StallingFrameSource(int w = 1920, int h = 1080, bool yields = false, bool degraded = false)
        {
            Width = w; Height = h; _yields = yields; IsUsingFallbackCapture = degraded;
            _pin = GCHandle.Alloc(new byte[w * 4 * h], GCHandleType.Pinned);
        }
        public bool IsUsingFallbackCapture { get; }
        public int Width { get; }
        public int Height { get; }
        public int Stride => Width * 4;
        public IntPtr BufferPtr => _pin.AddrOfPinnedObject();
        public bool TryCaptureFrame() => _yields;   // false = the failing DXGI/GDI state
        public void Dispose() { if (_pin.IsAllocated) _pin.Free(); }
    }

    /// <summary>Counts audio pulls: proves the send loop skipped audio while no frame had arrived.</summary>
    sealed class CountingAudioSource : IHostAudioSource
    {
        public int FillCalls;
        public int SampleRate => 48000;
        public int Channels => 2;
        public bool IsHardwareActive => false;
        public int DeviceSampleRate => 48000;
        public bool IsResamplingRequired => false;
        public string? RateNegotiationNote => null;
        public int InputLatencyFrames => 0;
        public string? LastError => null;
        public void FillPlanarAudio(Span<float> planarDestination, int samplesPerChannel)
        {
            Interlocked.Increment(ref FillCalls);
            planarDestination.Clear();
        }
        public void Dispose() { }
    }

    static void TestBroadcastCaptureStall()
    {
        Console.WriteLine("\n[Step 0.702] Verifying a capture provider that never yields is REPORTED, not silent (DIAG-BROADCAST)...");

        var capturer = new StallingFrameSource();
        var audio = new CountingAudioSource();
        var stalledMessages = new List<string>();
        var stalled = new ManualResetEventSlim(false);

        // Short window so the suite does not wait 10 s; production uses DefaultFirstFrameTimeoutMs.
        using var generator = new OmtDesktopCaptureGenerator(
            capturer,
            sourceName: "OMT_TEST_STALL",
            fps: 30,
            audioSource: audio,
            firstFrameTimeoutMs: 400);
        generator.CaptureStalled += m => { lock (stalledMessages) stalledMessages.Add(m); stalled.Set(); };

        generator.Start();

        bool fired = stalled.Wait(TimeSpan.FromSeconds(15));
        generator.Stop();

        Expect(fired, "A capture provider that never yields a frame must trip the stall report (it was silent before).");
        lock (stalledMessages)
        {
            Expect(stalledMessages.Count == 1, $"The stall must be reported exactly ONCE; got {stalledMessages.Count}.");
            string msg = stalledMessages[0];
            // verify the MESSAGE, not just that something fired - a bare 'error' would be useless to an operator
            Expect(msg.Contains("no frames are being captured"), $"The message must say what is wrong; got '{msg}'");
            Expect(msg.Contains(nameof(StallingFrameSource)), $"The message must NAME the provider; got '{msg}'");
            Expect(msg.Contains("NO audio is being sent"), $"The message must state that audio is skipped too; got '{msg}'");
            Expect(msg.Contains("OMT_TEST_STALL"), $"The message must name the advertised source; got '{msg}'");
        }

        // The consequence the message claims must actually hold: no audio was ever pulled.
        Expect(audio.FillCalls == 0, $"Audio must NOT be sent while no frame has arrived; FillPlanarAudio was called {audio.FillCalls} time(s).");
        Expect(!generator.HasProducedFrames, "HasProducedFrames must stay false for a provider that never yields.");

        capturer.Dispose();

        // ---- THE OWNER'S LIKELY CASE: stalled AND degraded (DXGI unavailable, GDI fallback) ----------
        // A plain "no frames" message tells the operator something is wrong; naming the DEGRADED PATH is
        // what tells them why, which is the whole reason this clause exists.
        var degradedCapturer = new StallingFrameSource(degraded: true);
        var degradedMessages = new List<string>();
        var degradedFired = new ManualResetEventSlim(false);
        using (var dg = new OmtDesktopCaptureGenerator(degradedCapturer, sourceName: "OMT_TEST_DEGRADED",
                                                       fps: 30, audioSource: new CountingAudioSource(),
                                                       firstFrameTimeoutMs: 400))
        {
            dg.CaptureStalled += m => { lock (degradedMessages) degradedMessages.Add(m); degradedFired.Set(); };
            dg.Start();
            degradedFired.Wait(TimeSpan.FromSeconds(15));
            dg.Stop();
        }
        degradedCapturer.Dispose();

        lock (degradedMessages)
        {
            Expect(degradedMessages.Count == 1, $"A degraded stall must report once; got {degradedMessages.Count}.");
            string dm = degradedMessages[0];
            Expect(dm.Contains("DXGI Desktop Duplication unavailable, using GDI fallback"),
                $"The message must name the DEGRADED PATH when the capturer reports one; got '{dm}'");
        }

        // and a NON-degraded stall must NOT claim a fallback it is not using (no crying wolf)
        lock (stalledMessages)
        {
            Expect(!stalledMessages[0].Contains("GDI fallback"),
                "A stall on a non-degraded provider must not claim the GDI fallback wording.");
        }

        // NEGATIVE CONTROL OF THE DETECTOR ITSELF: a provider that DOES yield must NOT trip it. Without
        // this, the step would pass even if the detector fired unconditionally on every broadcast.
        var goodCapturer = new StallingFrameSource(yields: true);
        var goodAudio = new CountingAudioSource();
        int falseReports = 0;
        using (var ok = new OmtDesktopCaptureGenerator(goodCapturer, sourceName: "OMT_TEST_OK", fps: 30,
                                                        audioSource: goodAudio, firstFrameTimeoutMs: 400))
        {
            ok.CaptureStalled += _ => Interlocked.Increment(ref falseReports);
            ok.Start();
            Thread.Sleep(1200);
            ok.Stop();
        }
        goodCapturer.Dispose();
        Expect(falseReports == 0, $"A provider that DOES yield must never trip the stall report; it fired {falseReports} time(s).");

        Console.WriteLine($" -> stall reported once with a message naming the provider, the advertised source and the audio consequence;");
        Console.WriteLine($"    audio never sent (0 pulls); a never-yielding provider trips it, and the detector is armed before the window!");
    }
}

