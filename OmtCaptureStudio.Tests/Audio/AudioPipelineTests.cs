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
    /// Exercises the audio/video clock-lock resampler directly, headless.
    ///
    /// The invariant that keeps A/V locked is: output audio frames ==
    /// videoFrames * (sampleRate / frameRate). Both cases assert the resampler tracks
    /// that target (within the un-flushed final-chunk tail) and that its ratio
    /// converges to trueFps / nominalFps.
    ///
    /// Case A (drift): source reports 30 fps but delivers ~29.226 fps (the vMix defect).
    /// Case B (lockstep): video and audio clocks already agree.
    /// </summary>
    static void TestAudioClockResampler()
    {
        Console.WriteLine("\n[Step 0.6] Verifying AudioClockResampler (drift correction + passthrough)...");
        // --- Case A: 30 fps nominal, ~29.226 fps true delivery (vMix defect) ---
        {
            const int sampleRate = 48000, channels = 2;
            const double nominalFps = 30.0, trueFps = 29.226;
            const double seconds = 30.0;
            var (ratio, inputFrames, outputFrames, videoFrames) =
                SimulateResampler(sampleRate, nominalFps, trueFps, channels, seconds);

            double expectedRatio = trueFps / nominalFps; // 0.9742
            double target = videoFrames * (sampleRate / nominalFps); // what keeps A/V locked

            Console.WriteLine($" -> Drift case: video at {trueFps} fps vs nominal {nominalFps}; " +
                              $"ratio {ratio:F4} (expected {expectedRatio:F4}), " +
                              $"out {outputFrames} vs target {target:F0} (diff {outputFrames - target:F0} frames)");

            if (Math.Abs(ratio - expectedRatio) > 0.003)
            {
                throw new Exception(
                    $"AudioClockResampler drift test failed: converged ratio {ratio:F4} vs expected {expectedRatio:F4}");
            }
            // Must track the video clock closely; the last ~1 chunk (≤ ~2k frames) is
            // legitimately un-flushed at the recording edge.
            if (Math.Abs(outputFrames - target) > 2000)
            {
                throw new Exception(
                    $"AudioClockResampler drift test failed: output {outputFrames} vs A/V-lock target {target:F0}");
            }
        }

        // --- Case B: clocks already agree -> ratio 1.0, output tracks video clock ---
        {
            const int sampleRate = 48000, channels = 2;
            const double fps = 60.0;
            const double seconds = 20.0;
            var (ratio, inputFrames, outputFrames, videoFrames) =
                SimulateResampler(sampleRate, fps, fps, channels, seconds);

            double target = videoFrames * (sampleRate / fps);

            Console.WriteLine($" -> Lockstep case: ratio {ratio:F4}, " +
                              $"out {outputFrames} vs target {target:F0} (diff {outputFrames - target:F0} frames)");

            if (Math.Abs(ratio - 1.0) > 0.001)
            {
                throw new Exception($"AudioClockResampler lockstep test failed: ratio {ratio:F4} != 1.0");
            }
            if (Math.Abs(outputFrames - target) > 2000)
            {
                throw new Exception(
                    $"AudioClockResampler lockstep test failed: output {outputFrames} vs A/V-lock target {target:F0}");
            }
        }

        // --- Case C: near-Nyquist amplitude preserved (music transparency) ---
        {
            const int sampleRate = 48000, channels = 1;
            const double nominalFps = 30.0, trueFps = 29.226; // drift ratio ~0.974
            const double freq = 19000.0, amp = 0.5;

            var resampler = new AudioClockResampler();
            resampler.Reset(sampleRate, nominalFps, channels);

            const int chunkFrames = 1024;
            const double seconds = 5.0;
            int totalChunks = (int)(seconds * sampleRate / chunkFrames);

            var collected = new List<float>(totalChunks * chunkFrames);
            double videoClock = 0.0, phase = 0.0;
            var chunk = new byte[chunkFrames * 4];

            for (int i = 0; i < totalChunks; i++)
            {
                videoClock += (double)chunkFrames / sampleRate * trueFps;
                while (resampler.VideoFramesSeen < (long)videoClock)
                {
                    resampler.OnVideoFrame();
                }

                for (int f = 0; f < chunkFrames; f++)
                {
                    float s = (float)(amp * Math.Sin(phase));
                    phase += 2.0 * Math.PI * freq / sampleRate;
                    Buffer.BlockCopy(BitConverter.GetBytes(s), 0, chunk, f * 4, 4);
                }

                byte[] outBuf = resampler.Process(chunk, chunk.Length, out int outBytes);
                int outFrames = outBytes / 4;
                for (int f = 0; f < outFrames; f++)
                {
                    collected.Add(BitConverter.ToSingle(outBuf, f * 4));
                }
            }

            // Measure RMS over the steady-state tail (skip ~1s: FIR priming + PLL settle).
            int skip = sampleRate; // 1 second of output samples
            if (collected.Count <= skip + 1000)
            {
                throw new Exception("AudioClockResampler quality test: too few output samples.");
            }
            double sumSq = 0.0;
            for (int i = skip; i < collected.Count; i++)
            {
                sumSq += collected[i] * collected[i];
            }
            double rms = Math.Sqrt(sumSq / (collected.Count - skip));
            double inputRms = amp / Math.Sqrt(2.0);
            double gainDb = 20.0 * Math.Log10(rms / inputRms);

            Console.WriteLine($" -> Quality case: {freq / 1000.0:F0} kHz sine resampled at ratio {resampler.Ratio:F4}; " +
                              $"gain {gainDb:F3} dB (windowed-sinc ≈ 0 dB, linear would be ≈ -4.7 dB)");

            // Windowed-sinc keeps near-Nyquist content to ~unity; a linear kernel would
            // land ~-4.7 dB. Allow a comfortable margin above that.
            if (gainDb < -0.5)
            {
                throw new Exception(
                    $"AudioClockResampler quality test failed: {freq / 1000.0:F0} kHz gain {gainDb:F3} dB (windowed-sinc should be ~0 dB)");
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> AudioClockResampler drift correction and lockstep verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// ADR-0003 Slice 1: the stereo capture ring is a pure in-memory module now, so its whole policy
    /// (fill/drain, wrap, latency cap, overflow, underrun→silence, exactly-once handoff) is testable
    /// with no hardware. Previously the ring existed only inside two live capture devices.
    /// </summary>
    static void TestStereoCaptureRing()
    {
        Console.WriteLine("\n[Step 0.676] Verifying StereoCaptureRing (fill/drain, wrap, latency cap, underrun)...");

        // --- Defaults and argument guards. ---
        var defaults = new StereoCaptureRing();
        if (defaults.CapacitySamples != 48_000 || defaults.MaxLatencySamples != 5_760)
        {
            throw new Exception($"Ring defaults mismatch: capacity={defaults.CapacitySamples}, maxLatency={defaults.MaxLatencySamples}");
        }
        bool threw = false;
        try { _ = new StereoCaptureRing(100, 500); } catch (ArgumentOutOfRangeException) { threw = true; }
        if (!threw) throw new Exception("A latency cap larger than the capacity must be rejected.");

        // --- Fill / drain, exactly once, in order (cap == capacity so nothing is dropped). ---
        var ring = new StereoCaptureRing(1000, 1000);
        if (ring.AvailableSamples != 0) throw new Exception("A new ring must be empty.");

        // Sized for the largest single write below (1200 in the overflow case).
        var left = new float[1200];
        var right = new float[1200];
        for (int i = 0; i < 1000; i++) { left[i] = i; right[i] = -i; }
        ring.Write(left, right, 1000);
        if (ring.AvailableSamples != 1000) throw new Exception($"Expected 1000 available, got {ring.AvailableSamples}");

        var outL = new float[1000];
        var outR = new float[1000];
        // Read in uneven chunks: every frame must arrive exactly once, in order.
        int got = 0;
        foreach (int chunk in new[] { 30, 30, 40, 500, 400 })
        {
            ring.Read(outL.AsSpan(got, chunk), outR.AsSpan(got, chunk), chunk);
            got += chunk;
        }
        for (int i = 0; i < 1000; i++)
        {
            if (outL[i] != i || outR[i] != -i)
            {
                throw new Exception($"Frame {i} came back as L={outL[i]}/R={outR[i]} (expected L={i}/R={-i}).");
            }
        }
        if (ring.AvailableSamples != 0) throw new Exception("Draining the ring must leave it empty.");

        // --- Wrap-around across the ring boundary. ---
        for (int i = 0; i < 600; i++) { left[i] = 2000 + i; right[i] = 2000 + i; }
        ring.Write(left, right, 600);
        ring.Read(outL.AsSpan(0, 600), outR.AsSpan(0, 600), 600);
        for (int i = 0; i < 600; i++)
        {
            if (outL[i] != 2000 + i) throw new Exception($"Wrap read mismatch at {i}: {outL[i]} != {2000 + i}");
        }

        // --- Overflow: the oldest frames are overwritten, the newest window is retained. ---
        for (int i = 0; i < 1200; i++) { left[i] = 5000 + i; right[i] = 5000 + i; }
        ring.Write(left, right, 1200);
        if (ring.AvailableSamples != 1000) throw new Exception($"A full ring must hold exactly its capacity, got {ring.AvailableSamples}");
        ring.Read(outL, outR, 1000);
        for (int i = 0; i < 1000; i++)
        {
            if (outL[i] != 5000 + 200 + i)
            {
                throw new Exception($"Overflow window mismatch at {i}: {outL[i]} != {5000 + 200 + i}");
            }
        }

        // --- Underrun → silence: the shortfall is zero-filled, never stale. ---
        ring.Write(left, right, 10);                      // 10 frames available
        for (int i = 0; i < 200; i++) outL[i] = 7f;       // sentinel
        for (int i = 0; i < 200; i++) outR[i] = 7f;
        ring.Read(outL.AsSpan(0, 200), outR.AsSpan(0, 200), 200);
        for (int i = 0; i < 10; i++)
        {
            if (outL[i] != 5000 + i) throw new Exception($"Underrun prefix mismatch at {i}: {outL[i]}");
        }
        for (int i = 10; i < 200; i++)
        {
            if (outL[i] != 0f || outR[i] != 0f) throw new Exception($"Underrun shortfall at {i} must be silence, got {outL[i]}/{outR[i]}");
        }
        if (ring.AvailableSamples != 0) throw new Exception("An underrun read must consume everything available.");

        // --- ReadUpTo: reads exactly min(available, max) and does NOT zero-fill. ---
        ring.Write(left, right, 300);
        for (int i = 0; i < 500; i++) outL[i] = 9f;
        int read = ring.ReadUpTo(outL.AsSpan(0, 500), outR.AsSpan(0, 500), 500);
        if (read != 300) throw new Exception($"ReadUpTo should report 300, got {read}");
        for (int i = 300; i < 500; i++)
        {
            if (outL[i] != 9f) throw new Exception($"ReadUpTo must not touch the destination beyond the frames read (index {i}).");
        }
        if (ring.AvailableSamples != 0) throw new Exception("ReadUpTo must consume what it reported.");

        // --- Latency cap: past the cap the ring jumps to half the window and keeps the NEWEST frames. ---
        var capped = new StereoCaptureRing(1000, 100);
        for (int i = 0; i < 100; i++) { left[i] = i; right[i] = i; }
        capped.Write(left, right, 100);
        if (capped.AvailableSamples != 100) throw new Exception($"At the cap the window must be intact, got {capped.AvailableSamples}");

        // The 101st frame trips the cap. Drop arithmetic is `available - cap/2` = 101 - 50 = 51, so the
        // retained window is ring indices 51..100: the newest 49 written frames plus this new one.
        left[0] = 1234f;
        capped.Write(left, right, 1);
        if (capped.AvailableSamples != 50)
        {
            throw new Exception($"Exceeding the cap must drop to half the window (50), got {capped.AvailableSamples}");
        }
        capped.Read(outL.AsSpan(0, 50), outR.AsSpan(0, 50), 50);
        for (int i = 0; i < 49; i++)
        {
            if (outL[i] != 51 + i)
            {
                throw new Exception($"The cap must retain the NEWEST frames; index {i} was {outL[i]}, expected {51 + i}");
            }
        }
        if (outL[49] != 1234f)
        {
            throw new Exception($"The just-written frame must survive the cap drop; index 49 was {outL[49]}, expected 1234");
        }

        // --- Reset clears everything (used when a capture stream is torn down). ---
        capped.Write(left, right, 10);
        capped.Reset();
        if (capped.AvailableSamples != 0) throw new Exception("Reset must empty the ring.");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> StereoCaptureRing fill/drain, wrap, overflow, underrun, ReadUpTo and latency cap verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// ADR-0003 Slice 1: the negotiated-state members live on <see cref="IHostAudioSource"/>, so no
    /// caller needs to downcast to a concrete driver adapter. Exercised through the interface type
    /// only — if a member were missing, this would not compile.
    /// </summary>
    static void TestHostAudioSourceInterface()
    {
        Console.WriteLine("\n[Step 0.677] Verifying IHostAudioSource widening (no downcasts) + shared format constants...");

        if (HostAudioFormat.TargetSampleRate != 48_000 || HostAudioFormat.TargetChannels != 2)
        {
            throw new Exception($"Shared host-audio vocabulary mismatch: {HostAudioFormat.TargetSampleRate}/{HostAudioFormat.TargetChannels}");
        }

        // The silent clock: a source with no device behind it.
        using (IHostAudioSource silent = HostAudioSourceFactory.Create(HostAudioCaptureMode.Mute))
        {
            if (silent.IsHardwareActive) throw new Exception("The silent source must not claim hardware.");
            if (silent.DeviceSampleRate != 48_000 || silent.IsResamplingRequired) throw new Exception("The silent source negotiates nothing.");
            if (silent.RateNegotiationNote != null || silent.InputLatencyFrames != 0 || silent.LastError != null)
            {
                throw new Exception("The silent source must report no note, no latency and no error.");
            }
            var planar = new float[2 * 64];
            silent.FillPlanarAudio(planar, 64);
        }

        // A real adapter, reached ONLY through the interface (this is the AC5 assertion: no downcast).
        // Hardware may or may not be present on a headless box, so is-state is asserted, not its value.
        using (IHostAudioSource system = HostAudioSourceFactory.Create(HostAudioCaptureMode.SystemAudio))
        {
            int rate = system.DeviceSampleRate;
            if (rate <= 0) throw new Exception($"DeviceSampleRate must be positive, got {rate}");
            if (system.IsResamplingRequired != (rate != HostAudioFormat.TargetSampleRate))
            {
                throw new Exception("IsResamplingRequired must agree with the negotiated device rate.");
            }
            string summary = $"{system.SampleRate}Hz -> {rate}Hz, resample={system.IsResamplingRequired}, active={system.IsHardwareActive}, err={system.LastError ?? "none"}";
            Console.WriteLine($" -> SystemAudio source via IHostAudioSource only: {summary}");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Interface widening verified (silent + system sources, zero concrete-type downcasts)!");
        Console.ResetColor();
    }

    /// <summary>
    /// ADR-0003 Slice 2: the rate-conversion policy is one shared module now, so it is testable
    /// headlessly — bit-exact identity at matched rates, and sinc-vs-linear accuracy at 44.1→48 kHz.
    /// </summary>
    static void TestFixedRatioStereoResampler()
    {
        Console.WriteLine("\n[Step 0.678] Verifying FixedRatioStereoResampler (passthrough identity, sinc vs linear)...");

        // --- Matched rates: bit-exact identity, no delay, channels independent. ---
        var identity = new FixedRatioStereoResampler(48000, 48000);
        if (!identity.IsPassthrough || identity.Step != 1.0)
        {
            throw new Exception($"A matched-rate resampler must report passthrough (step={identity.Step}).");
        }

        int n = 2048;
        var inL = new float[n + 256];
        var inR = new float[n + 256];
        for (int i = 0; i < inL.Length; i++)
        {
            inL[i] = (float)Math.Sin(2.0 * Math.PI * 997.0 * i / 48000.0);
            inR[i] = inL[i] * -0.5f;
        }
        identity.Append(inL, inR, inL.Length);

        var idOutL = new float[n];
        var idOutR = new float[n];
        int idProduced = identity.Process(idOutL, idOutR, n);
        if (idProduced != n) throw new Exception($"Matched-rate resampler produced {idProduced}/{n} frames.");

        // Phase 0 of the kernel is a unit impulse, so the output must be the input with NO delay and NO
        // filtering — to within float rounding of the (float-quantised, DC-normalised) filter table.
        float maxDev = 0f;
        for (int i = 0; i < n; i++)
        {
            maxDev = Math.Max(maxDev, Math.Abs(idOutL[i] - inL[i]));
            maxDev = Math.Max(maxDev, Math.Abs(idOutR[i] - inR[i]));
        }
        Console.WriteLine($" -> 48000->48000 identity: max sample deviation {maxDev:E2} over {n} frames (no delay, no filtering)");
        if (maxDev > 1e-6f)
        {
            throw new Exception($"Matched-rate passthrough must be identity to float rounding; max deviation {maxDev:E3}");
        }

        // Delay-free: the very first sample must already be the first input sample, not silence.
        if (Math.Abs(idOutL[0] - inL[0]) > 1e-6f)
        {
            throw new Exception($"Pass-through must not introduce delay; output[0]={idOutL[0]} vs input[0]={inL[0]}");
        }

        // --- 44.1 -> 48 kHz on a 200 Hz-8 kHz sweep: sinc must beat the linear policy it replaced. ---
        const int inRate = 44100;
        const int outRate = 48000;
        const int inFrames = 44100;                 // 1 s
        const double f0 = 200.0, f1 = 8000.0;
        var sweep = new float[inFrames];
        for (int i = 0; i < inFrames; i++)
        {
            double t = (double)i / inRate;
            sweep[i] = (float)Math.Sin(SweepPhase(t, f0, f1));
        }

        var sinc = new FixedRatioStereoResampler(inRate, outRate);
        sinc.Append(sweep, sweep, inFrames);
        const int wantedOutputs = 24096;
        var sincOut = new float[wantedOutputs];
        var sincOutR = new float[wantedOutputs];
        int sincProduced = sinc.Process(sincOut, sincOutR, wantedOutputs);
        if (sincProduced < 20000) throw new Exception($"Sinc resampler produced only {sincProduced} frames.");

        // Linear-interpolation reference: exactly the per-sample lerp the WASAPI callback used to do.
        double step = (double)inRate / outRate;
        var linearOut = new float[sincProduced];
        double pos = 0.0;
        for (int k = 0; k < sincProduced; k++)
        {
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, inFrames - 1);
            float frac = (float)(pos - i0);
            linearOut[k] = sweep[i0] + (sweep[i1] - sweep[i0]) * frac;
            pos += step;
        }

        // Signal-to-error against the ideal tone at the OUTPUT rate (the resampler preserves time:
        // output k corresponds to input time k/outRate), measured over the same span for both.
        const int measureFrom = 1000;
        const int measureCount = 18000;
        double idealEnergy = 0.0, sincErr = 0.0, linearErr = 0.0;
        for (int k = measureFrom; k < measureFrom + measureCount; k++)
        {
            double ideal = Math.Sin(SweepPhase((double)k / outRate, f0, f1));
            idealEnergy += ideal * ideal;
            sincErr += Math.Pow(sincOut[k] - ideal, 2);
            linearErr += Math.Pow(linearOut[k] - ideal, 2);
        }
        double sincDb = 10.0 * Math.Log10(idealEnergy / Math.Max(sincErr, 1e-30));
        double linearDb = 10.0 * Math.Log10(idealEnergy / Math.Max(linearErr, 1e-30));
        Console.WriteLine($" -> 44100->48000 sine sweep (200 Hz-8 kHz) SER: sinc {sincDb:F1} dB vs linear {linearDb:F1} dB");

        if (sincDb < 40.0) throw new Exception($"Sinc SER {sincDb:F1} dB is below the 40 dB floor.");
        if (sincDb < linearDb + 10.0)
        {
            throw new Exception($"Sinc must beat the linear policy by >=10 dB; got sinc {sincDb:F1} vs linear {linearDb:F1} dB.");
        }

        // Right channel was fed the same sweep, so it must match the left bit-for-bit.
        for (int k = 0; k < sincProduced; k++)
        {
            if (sincOut[k] != sincOutR[k]) throw new Exception($"Channels diverged at frame {k}.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Resampler passthrough identity + sinc superiority over linear verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// ADR-0003 Slice 2, structural assertion: rate conversion must sit on the CONSUMER side of both
    /// adapters, and the resampler must exist in exactly ONE place. This is a source-shape check —
    /// the behavioural half is covered by the resampler step above plus the live GUI steps.
    /// </summary>
    static void TestConversionPlacement()
    {
        Console.WriteLine("\n[Step 0.679] Verifying rate conversion sits on the consumer side (structure)...");

        string? wasapi = FindRepoSource("OmtCaptureStudio/Services/WasapiHostAudioSource.cs");
        string? asio = FindRepoSource("OmtCaptureStudio/Services/AsioHostAudioSource.cs");
        string? module = FindRepoSource("OmtCaptureStudio/Services/Audio/FixedRatioStereoResampler.cs");
        if (wasapi == null || asio == null || module == null)
        {
            Console.WriteLine($" -> [SKIP] source tree not reachable from {AppContext.BaseDirectory}");
            return;
        }

        string wasapiText = File.ReadAllText(wasapi);
        string asioText = File.ReadAllText(asio);

        // The WASAPI capture callback must no longer carry any conversion state or math.
        foreach (string forbidden in new[] { "resampleStep", "resamplePos" })
        {
            if (wasapiText.Contains(forbidden))
            {
                throw new Exception($"WASAPI still carries capture-callback conversion state: '{forbidden}'");
            }
        }

        int ingestStart = wasapiText.IndexOf("private void IngestPacket(", StringComparison.Ordinal);
        int ingestEnd = wasapiText.IndexOf("private static void ReadFrameStereo(", StringComparison.Ordinal);
        if (ingestStart < 0 || ingestEnd <= ingestStart) throw new Exception("Could not locate IngestPacket to inspect.");
        string ingestBody = wasapiText.Substring(ingestStart, ingestEnd - ingestStart);
        if (ingestBody.Contains("l1 - l0") || ingestBody.Contains("frac"))
        {
            throw new Exception("IngestPacket still interpolates — conversion has not left the capture callback.");
        }
        if (!ingestBody.Contains("_ring.WriteSampleLocked"))
        {
            throw new Exception("IngestPacket no longer feeds the ring; the refactor did not land as expected.");
        }

        // Conversion must now live on the consumer read path of BOTH adapters.
        if (!wasapiText.Contains("FillResampled"))
        {
            throw new Exception("WASAPI has no consumer-side FillResampled path.");
        }
        if (!wasapiText.Contains("FixedRatioStereoResampler") || !asioText.Contains("FixedRatioStereoResampler"))
        {
            throw new Exception("Both adapters must share FixedRatioStereoResampler.");
        }

        // Exactly one definition of the resampler, and not as a nested private class any more.
        if (asioText.Contains("private sealed class FixedRatioStereoResampler"))
        {
            throw new Exception("ASIO still declares its own nested resampler — the module was not promoted.");
        }
        if (!File.ReadAllText(module).Contains("public sealed class FixedRatioStereoResampler"))
        {
            throw new Exception("The promoted resampler module is not where it should be.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Conversion placement verified (callback pure, consumer-side resampler shared by both adapters)!");
        Console.ResetColor();
    }

    /// <summary>Phase of a linear frequency sweep f0→f1 over one second (T = 1 s).</summary>
    static double SweepPhase(double t, double f0, double f1)
        => 2.0 * Math.PI * (f0 * t + (f1 - f0) / 2.0 * t * t);

    /// <summary>
    /// Feeds ~<paramref name="seconds"/> of audio in 1024-frame chunks, advancing the
    /// video clock at <paramref name="trueFps"/> between chunks, and returns the
    /// resampler's converged ratio plus total input/output frame counts and video frames.
    /// </summary>
    static (double ratio, long inputFrames, long outputFrames, long videoFrames) SimulateResampler(
        int sampleRate, double nominalFps, double trueFps, int channels, double seconds)
    {
        var resampler = new AudioClockResampler();
        resampler.Reset(sampleRate, nominalFps, channels);

        const int chunkFrames = 1024;
        var chunk = new byte[chunkFrames * channels * 4];
        int totalChunks = (int)(seconds * sampleRate / chunkFrames);

        long inputFrames = 0, outputFrames = 0;
        double videoClock = 0.0; // fractional video frames elapsed

        for (int i = 0; i < totalChunks; i++)
        {
            // Advance the video clock by this audio chunk's wall-clock duration.
            double audioSeconds = (double)chunkFrames / sampleRate;
            videoClock += audioSeconds * trueFps;
            while (resampler.VideoFramesSeen < (long)videoClock)
            {
                resampler.OnVideoFrame();
            }

            resampler.Process(chunk, chunk.Length, out int outBytes);
            inputFrames += chunkFrames;
            outputFrames += outBytes / (channels * 4);
        }

        return (resampler.Ratio, inputFrames, outputFrames, resampler.VideoFramesSeen);
    }

}

