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

    /// <summary>
    /// Yields a fixed number of frames, then goes idle. <see cref="HealthyWhenIdle"/> distinguishes a
    /// static desktop (duplication alive, timeout) from a lost session.
    /// </summary>
    sealed class ScriptedFrameSource : IFrameSource
    {
        private readonly GCHandle _pin;
        private int _remaining;
        public ScriptedFrameSource(int yields, bool healthyWhenIdle)
        {
            Width = 64; Height = 64;
            _remaining = yields;
            HealthyWhenIdle = healthyWhenIdle;
            _pin = GCHandle.Alloc(new byte[Width * Height * 4], GCHandleType.Pinned);
        }
        public bool HealthyWhenIdle { get; set; }
        public int Width { get; }
        public int Height { get; }
        public int Stride => Width * 4;
        public IntPtr BufferPtr => _pin.AddrOfPinnedObject();
        public bool IsCaptureSessionHealthy => _remaining > 0 || HealthyWhenIdle;
        public bool TryCaptureFrame()
        {
            if (_remaining <= 0) return false;
            _remaining--;
            return true;
        }
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

        // HRESULT classification first: it does not need a GPU or OMT, and the send-loop cases below do.
        Expect(DxgiFrameAcquire.Classify(DxgiFrameAcquire.Ok) == DxgiAcquireKind.Success, "S_OK is success.");
        Expect(DxgiFrameAcquire.Classify(DxgiFrameAcquire.WaitTimeout) == DxgiAcquireKind.Timeout,
            "DXGI_ERROR_WAIT_TIMEOUT is a static desktop, not a lost session.");
        Expect(DxgiFrameAcquire.Classify(DxgiFrameAcquire.AccessLost) == DxgiAcquireKind.AccessLost,
            "DXGI_ERROR_ACCESS_LOST must be classified as access-lost.");
        Expect(DxgiFrameAcquire.Classify(DxgiFrameAcquire.DeviceRemoved) == DxgiAcquireKind.DeviceLost,
            "DXGI_ERROR_DEVICE_REMOVED must be classified as device-lost.");
        Expect(DxgiFrameAcquire.Classify(DxgiFrameAcquire.DeviceReset) == DxgiAcquireKind.DeviceLost,
            "DXGI_ERROR_DEVICE_RESET must be classified as device-lost.");
        Expect(!DxgiFrameAcquire.NeedsDuplicationReset(DxgiAcquireKind.Timeout),
            "A timeout must not recreate the duplication.");
        Expect(DxgiFrameAcquire.NeedsDuplicationReset(DxgiAcquireKind.AccessLost),
            "Access-lost must recreate the duplication.");
        Expect(DxgiFrameAcquire.NeedsDuplicationReset(DxgiAcquireKind.DeviceLost),
            "Device-lost must recreate the duplication (and the device).");
        Expect(DxgiFrameAcquire.Classify(unchecked((int)0x887A0001)) == DxgiAcquireKind.OtherFailure,
            "An unexpected DXGI failure must not be mistaken for a timeout.");
        Console.WriteLine(" -> DXGI acquire classification: timeout is not a reset; access-lost and device-lost are.");

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

        // ---- POST-FRAME: a session that dies AFTER frames arrived must alarm, even though a buffer exists.
        var lost = new ScriptedFrameSource(yields: 3, healthyWhenIdle: false);
        var lostAudio = new CountingAudioSource();
        var lostMessages = new List<string>();
        var lostFired = new ManualResetEventSlim(false);
        using (var lostGen = new OmtDesktopCaptureGenerator(lost, sourceName: "OMT_TEST_LOST", fps: 30,
                                                            audioSource: lostAudio, firstFrameTimeoutMs: 5_000,
                                                            captureStallTimeoutMs: 350))
        {
            lostGen.CaptureStalled += m => { lock (lostMessages) lostMessages.Add(m); lostFired.Set(); };
            lostGen.Start();
            bool lostOk = lostFired.Wait(TimeSpan.FromSeconds(8));
            lostGen.Stop();
            Expect(lostOk, "A session that goes unhealthy after frames arrived must raise the stall alarm.");
        }
        lost.Dispose();
        lock (lostMessages)
        {
            Expect(lostMessages.Count == 1, $"A post-frame stall must be reported once per outage; got {lostMessages.Count}.");
            string lm = lostMessages[0];
            Expect(lm.Contains("stalled") && lm.Contains("unhealthy"),
                $"The post-frame message must say the session is stalled and unhealthy; got '{lm}'.");
            Expect(lm.Contains(nameof(ScriptedFrameSource)), $"The post-frame message must name the provider; got '{lm}'.");
            Expect(!lm.Contains("NO audio is being sent"),
                "After frames have arrived the send loop still ships audio; the message must not claim otherwise.");
        }
        Expect(lostAudio.FillCalls > 0,
            $"Audio must keep flowing on the frozen picture; FillPlanarAudio was called {lostAudio.FillCalls} time(s).");

        // ---- NEGATIVE: a static desktop (idle, but the session stays healthy) must NOT alarm.
        var still = new ScriptedFrameSource(yields: 2, healthyWhenIdle: true);
        int stillReports = 0;
        using (var stillGen = new OmtDesktopCaptureGenerator(still, sourceName: "OMT_TEST_STILL", fps: 30,
                                                             audioSource: new CountingAudioSource(),
                                                             firstFrameTimeoutMs: 5_000,
                                                             captureStallTimeoutMs: 300))
        {
            stillGen.CaptureStalled += _ => Interlocked.Increment(ref stillReports);
            stillGen.Start();
            Thread.Sleep(900);
            stillGen.Stop();
        }
        still.Dispose();
        Expect(stillReports == 0,
            $"A healthy timeout (static desktop) must not raise a stall; it fired {stillReports} time(s).");

        Console.WriteLine($" -> stall reported once with a message naming the provider, the advertised source and the audio consequence;");
        Console.WriteLine($"    audio never sent (0 pulls); a never-yielding provider trips it, and the detector is armed before the window!");
    }
}

