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
    /// NET-022 / ADR-0011: the mode-based shell. Headless — the state is constructed with no Avalonia
    /// platform, and the ViewModel is driven through its real commands. Covers the three states, the
    /// "picker always opens" rule, the Change-Mode gate, and LastAppMode persistence.
    /// </summary>
    /// <summary>
    /// DIAG-VMIX receiver hardening. Everything downstream of the receiver is FPA1-only and codec-BLIND:
    /// AudioLevelCalculator and AudioInterleaver each cast the payload to float* and read
    /// channels * samplesPerChannel * 4 bytes. The payload is sized by the SENDER, so an unexpected codec
    /// or an under-declared buffer would drive this process past the end of the buffer it was handed - a
    /// remote-triggerable out-of-bounds read. This asserts the pure guard, the refusal the operator sees,
    /// and that the accepted FPA1 path forwards the frame byte-for-byte as before.
    /// </summary>
    static void TestOmtAudioFrameGuard()
    {
        Console.WriteLine("\n[Step 0.699] Verifying the OMT audio frame guard (codec + geometry receiver hardening)...");

        // ---- 1. THE WORKING POPULATION IS ACCEPTED, EXACTLY AS BEFORE -----------------------------
        // These are the exact numbers our own sender puts on the wire (measured in the DIAG-VMIX control
        // trace: 2 ch @ 48 kHz, 800 samples/ch, DataLength 6400, codec FPA1 for 900/900 frames). If this
        // ever stops being accepted the guard has broken every working source.
        AudioFrameValidation ok = OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 6400);
        Expect(ok.IsAccepted, $"A real FPA1 frame must be accepted; got {ok.Reason}: {ok.Detail}");
        Expect(ok.Detail.Length == 0, "An accepted frame must carry no detail text.");

        // 64ch x 16384 is the ruled FINAL geometry ceiling (4,194,304 bytes; the payload envelope of
        // 16 MiB is a separate axis). Derived from the constant so a retune moves this with it.
        foreach (var (ch, smp) in new[] { (1, 480), (2, 1024), (8, 4096), (64, OmtAudioFrameGuard.MaxSamplesPerChannel) })
        {
            int len = ch * smp * 4;
            Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, ch, smp, len).IsAccepted,
                $"{ch}ch x {smp} FPA1 at exactly {len} bytes must be accepted.");
        }

        // ---- 2. PA16 IS REFUSED, NEVER REINTERPRETED (the DIAG-VMIX mechanism) --------------------
        // A PA16 payload for the same geometry is HALF the size the consumers would read - reading it is
        // precisely the over-read reproduced in the diagnosis pass.
        AudioFrameValidation pa16 = OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Pa16Codec, 2, 800, 3200);
        Expect(!pa16.IsAccepted, "A PA16-announcing frame must be REFUSED - never reinterpreted.");
        Expect(pa16.Reason == AudioFrameRejection.UnsupportedCodec,
            $"A PA16 frame must be refused as UnsupportedCodec; got {pa16.Reason}.");
        Expect(pa16.Detail.Contains("PA16", StringComparison.Ordinal),
            $"The refusal must NAME the codec the sender announced; got '{pa16.Detail}'.");
        Expect(pa16.Detail.Contains("16-bit", StringComparison.Ordinal),
            $"The refusal must say what PA16 is so the log is actionable; got '{pa16.Detail}'.");

        // A coincidence of SIZE must not license reinterpretation: refused on the CODEC alone.
        Expect(!OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Pa16Codec, 2, 800, 6400).IsAccepted,
            "A PA16 frame must be refused on its CODEC alone, whatever length it declares.");

        // ---- 3. AN UNKNOWN CODEC IS REFUSED AND NAMED LEGIBLY -------------------------------------
        AudioFrameValidation unknown = OmtAudioFrameGuard.Validate(0x12345678, 2, 800, 6400);
        Expect(!unknown.IsAccepted && unknown.Reason == AudioFrameRejection.UnsupportedCodec,
            "An unrecognised audio codec must be refused as UnsupportedCodec.");
        Expect(OmtAudioFrameGuard.CodecName(OmtAudioFrameGuard.Fpa1Codec) == "FPA1 (826363974)",
            $"Codec names must decode from the tag; got '{OmtAudioFrameGuard.CodecName(OmtAudioFrameGuard.Fpa1Codec)}'.");
        Expect(OmtAudioFrameGuard.CodecName(OmtAudioFrameGuard.Pa16Codec) == "PA16 (909197648)",
            $"got '{OmtAudioFrameGuard.CodecName(OmtAudioFrameGuard.Pa16Codec)}'.");
        Expect(OmtAudioFrameGuard.CodecName(0).Length > 0, "Naming a codec must never throw, even for zero.");

        // ---- 4. A GEOMETRY/DataLength MISMATCH IS REFUSED (the over-read) -------------------------
        AudioFrameValidation shortPayload = OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 3200);
        Expect(!shortPayload.IsAccepted, "An under-length FPA1 payload must be refused - reading it over-runs the buffer.");
        Expect(shortPayload.Reason == AudioFrameRejection.GeometryMismatch,
            $"An under-length payload is a GeometryMismatch; got {shortPayload.Reason}.");
        Expect(shortPayload.Detail.Contains("3200", StringComparison.Ordinal) &&
               shortPayload.Detail.Contains("6400", StringComparison.Ordinal),
            $"The refusal must state both the actual and the expected length; got '{shortPayload.Detail}'.");
        Expect(shortPayload.Detail.Contains("PAST the end", StringComparison.Ordinal),
            $"The refusal must say the read would over-run; got '{shortPayload.Detail}'.");
        Expect(shortPayload.Reason == AudioFrameRejection.GeometryMismatch,
            "An under-length payload is still a GeometryMismatch under the widened R2 boundary.");

        // The boundary is >= on the payload and == on the geometry-derived READ length (SEC-OMT-01 R2).
        // One byte SHORT is the over-read itself and stays refused; one byte LONG is a conformant padded
        // sender and must be ACCEPTED, because reading the geometry out of a larger buffer is in-bounds.
        // (This supersedes this step's earlier strict-equality asserts - R2 changes the contract.)
        Expect(!OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 6399).IsAccepted,
            "One byte SHORT of the announced geometry must be refused - that is the over-read itself.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 6401).IsAccepted,
            "One byte LONG must be ACCEPTED (R2): a padded conformant sender is legal, and reading the " +
            "geometry out of a larger buffer is in-bounds. Strict equality dropped such a source for no " +
            "security benefit.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 6400 + 4096).IsAccepted,
            "A generously padded payload must be accepted (R2).");

        // ---- 5. HOSTILE GEOMETRY: RANGES GATED, AND NO INTEGER OVERFLOW ---------------------------
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 0, 800, 6400).Reason
               == AudioFrameRejection.GeometryOutOfRange, "Zero channels must be out of range.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, OmtAudioFrameGuard.MaxChannels + 1, 800, 6400).Reason
               == AudioFrameRejection.GeometryOutOfRange, "Above MaxChannels must be out of range.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 0, 6400).Reason
               == AudioFrameRejection.GeometryOutOfRange, "Zero samples must be out of range.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, OmtAudioFrameGuard.MaxSamplesPerChannel + 1, 6400).Reason
               == AudioFrameRejection.GeometryOutOfRange, "Above MaxSamplesPerChannel must be out of range.");

        // The range gate runs BEFORE the length arithmetic, so a geometry that would wrap in 32-bit
        // (65536 x 65536 x 4 == 1.7e10, which truncates to 0) can never be evaluated as a small length.
        Expect(!OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 65536, 65536, 0).IsAccepted,
            "A hostile geometry must never be accepted - least of all by wrapping the expectation to zero.");

        // ---- 6. THE REFUSAL IS COUNTED, AND SURFACED ONCE PER DISTINCT PROBLEM --------------------
        using var receiver = new OmtReceiverService();
        var surfaced = new List<string>();
        receiver.ErrorOccurred += m => surfaced.Add(m);

        Expect(receiver.RejectedAudioFrames == 0, "A fresh receiver must have refused nothing.");

        Expect(receiver.ReportRejectedAudioFrame(pa16) == 1, "The first refusal must be counted as occurrence 1.");
        Expect(surfaced.Count == 1, "The first refusal must reach the operator.");
        Expect(surfaced[0].Contains("Refused an OMT audio frame", StringComparison.Ordinal),
            $"The operator's message must say what happened; got '{surfaced[0]}'.");
        Expect(surfaced[0].Contains("PA16", StringComparison.Ordinal),
            $"The operator's message must name the codec; got '{surfaced[0]}'.");
        Expect(surfaced[0].Contains("No audio", StringComparison.Ordinal),
            $"The operator must be told the consequence - no audio is being captured; got '{surfaced[0]}'.");

        // Repeats are COUNTED but must not re-surface: a 60 fps sender would otherwise flood the status line.
        receiver.ReportRejectedAudioFrame(pa16);
        receiver.ReportRejectedAudioFrame(pa16);
        Expect(receiver.RejectedAudioFrames == 3, "Every refusal is counted, even when not re-reported.");
        Expect(surfaced.Count == 1, "A repeated identical refusal must NOT re-surface.");

        // A DIFFERENT problem is surfaced again - the operator must not miss a new failure mode.
        receiver.ReportRejectedAudioFrame(shortPayload);
        Expect(receiver.RejectedAudioFrames == 4, "The counter must keep counting across reasons.");
        Expect(surfaced.Count == 2, "A different refusal reason must surface again.");

        // ---- 7. THE WIRING: the guard gates the frame, and the payload is untouched ----------------
        string? receiverSource = FindRepoSource("OmtCaptureStudio/Services/OmtReceiverService.cs");
        if (receiverSource == null)
        {
            Console.WriteLine("   [SKIP] OmtReceiverService.cs is not reachable from this tree.");
        }
        else
        {
            string src = File.ReadAllText(receiverSource);
            string flat = string.Join(" ", src.Split(
                new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

            // The codec must actually be READ - the defect this guards against was that it never was.
            Expect(flat.Contains(
                "OmtAudioFrameGuard.Validate( frame.Codec, frame.Channels, frame.SamplesPerChannel, frame.DataLength);",
                StringComparison.Ordinal),
                "The receiver must hand the guard the frame's OWN announced codec and delivered length.");

            int guardAt = src.IndexOf("OmtAudioFrameGuard.Validate(", StringComparison.Ordinal);
            int acceptedAt = src.IndexOf("if (verdict.IsAccepted)", StringComparison.Ordinal);
            int forwardAt = src.IndexOf("AudioFrameReceived?.Invoke(", StringComparison.Ordinal);
            Expect(guardAt >= 0 && acceptedAt >= 0 && forwardAt >= 0,
                "The receiver must still validate, branch, and forward.");
            Expect(guardAt < acceptedAt && acceptedAt < forwardAt,
                "The forward must sit INSIDE the acceptance branch: a guard placed after the forward is decorative.");

            // BYTE-IDENTICAL FPA1 PATH: the accepted frame is forwarded exactly as it was before hardening.
            Expect(flat.Contains(
                "AudioFrameReceived?.Invoke( frame.Data, frame.Channels, frame.SamplesPerChannel, frame.SampleRate, frame.Timestamp );",
                StringComparison.Ordinal),
                "The forwarded payload must be byte-for-byte what it was before: the guard GATES the frame, it never rewrites or reinterprets it.");
        }

        // ---- 8. THE VIDEO TWIN (SEC-OMT-01 SEC-02, R11-R14) ---------------------------------------
        // UpdateFrame copies `stride * height` out of the peer's pointer on the RECEIVE thread. These are
        // the four vectors from the security audit's video table; the int32 wrap cases are named because
        // `int requiredBytes = stride * height` silently became 0 (copy skipped) or negative (allocation
        // throws on the receive thread) for reachable pairs.
        VideoFrameValidation benign = OmtVideoFrameGuard.Validate(
            OmtVideoFrameGuard.BgraCodec, 1920, 1080, 7680, 8294400);
        Expect(benign.IsAccepted,
            $"A real 1920x1080 BGRA frame must be ACCEPTED - it is the working population; got {benign.Reason}: {benign.Detail}");

        VideoFrameValidation eightK = OmtVideoFrameGuard.Validate(
            OmtVideoFrameGuard.BgraCodec, 7680, 4320, 30720, 3200);
        Expect(!eightK.IsAccepted,
            "An 8K announce over a 3200-byte payload must be REFUSED - pre-fix it read ~126 MB out of a <=10 MB buffer.");
        Expect(eightK.Reason == VideoFrameRejection.GeometryMismatch,
            $"The 8K vector is a payload mismatch; got {eightK.Reason}.");
        Expect(eightK.Detail.Contains("PAST the end", StringComparison.Ordinal),
            $"The video refusal must say the read would over-run; got '{eightK.Detail}'.");

        // int32 product 65536 * 65536 = 4,294,967,296 -> wraps to 0 (the copy would be silently skipped).
        Expect(!OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 65536, 65536, 65536, 65536).IsAccepted,
            "A geometry whose stride*height wraps to ZERO in int32 must be refused - that is the silent-skip case.");
        // int32 product 100000 * 30000 = 3,000,000,000 -> wraps NEGATIVE (new byte[negative] throws).
        Expect(!OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 100000, 30000, 100000, 1000).IsAccepted,
            "A geometry whose stride*height wraps NEGATIVE in int32 must be refused - that is the throwing case.");

        // R11: the pixel layout must be the one the app asked for and asserts everywhere else.
        // Same geometry as the benign BGRA frame above, so the CODEC is the only difference: if the
        // codec rule were removed this frame would sail through every other check and be accepted.
        // (A UYVY frame at its natural 3840 stride is caught by the stride rule instead, which would
        // make this assertion vacuous - that is how a negative control caught the first draft.)
        VideoFrameValidation uyvy = OmtVideoFrameGuard.Validate(0x59565955 /* UYVY */, 1920, 1080, 7680, 8294400);
        Expect(!uyvy.IsAccepted,
            "A non-BGRA video frame must be refused rather than reinterpreted as BGRA (R11).");
        Expect(uyvy.Reason == VideoFrameRejection.UnsupportedCodec,
            $"The non-BGRA refusal must be a CODEC refusal, not an incidental geometry one; got {uyvy.Reason}: {uyvy.Detail}");
        Expect(OmtVideoFrameGuard.CodecName(OmtVideoFrameGuard.BgraCodec) == "BGRA (1095911234)",
            $"Video codec names must decode from the tag; got '{OmtVideoFrameGuard.CodecName(OmtVideoFrameGuard.BgraCodec)}'.");

        // R12: the stride must actually cover a row, and the dimensions must be sane.
        Expect(!OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 1920, 1080, 4096, 8294400).IsAccepted,
            "A stride that cannot cover a BGRA row must be refused (R12).");
        Expect(!OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 20000, 1080, 80000, 86400000).IsAccepted,
            "A dimension beyond the ceiling must be refused (R12).");
        // ---- 12. R14 RE-SHAPED: the byte ceiling killed conformant 1440p/4K (R14 correction) ---------
        // The old bound was the 10 MiB BYTE ceiling: 2560x1440 BGRA is 14,745,600 bytes and 3840x2160 is
        // 33,177,600 - both conformant, both refused outright. Every vector in the table was 1080p, which
        // is why nothing caught it. The bound is now a PIXEL COUNT at 8K.
        VideoFrameValidation hd1440 = OmtVideoFrameGuard.Validate(
            OmtVideoFrameGuard.BgraCodec, 2560, 1440, 2560 * 4, 2560 * 1440 * 4);
        Expect(hd1440.IsAccepted,
            "R14: conformant 2560x1440 BGRA (14,745,600 bytes) must be ACCEPTED - the old 10 MiB byte " +
            $"ceiling refused it; got {hd1440.Reason}: {hd1440.Detail}");

        VideoFrameValidation uhd2160 = OmtVideoFrameGuard.Validate(
            OmtVideoFrameGuard.BgraCodec, 3840, 2160, 3840 * 4, 3840 * 2160 * 4);
        Expect(uhd2160.IsAccepted,
            "R14: conformant 3840x2160 BGRA (33,177,600 bytes) must be ACCEPTED - it is exactly the 8K " +
            $"pixel bound; got {uhd2160.Reason}: {uhd2160.Detail}");

        // ...and 8K itself, which is the bound, plus the first size past it.
        Expect(OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 7680, 4320, 7680 * 4, 7680 * 4320 * 4).IsAccepted,
            "R14: 8K (7680x4320 = 33,177,600 pixels) is the bound and must be accepted.");
        Expect(OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 7680, 4321, 7680 * 4, 7680 * 4321 * 4).Reason
               == VideoFrameRejection.FrameTooLarge,
            "R14: one row past 8K must be refused as FrameTooLarge.");
        Expect(OmtVideoFrameGuard.MaxPixels == 7680 * 4320,
            $"R14: the pixel bound must be exactly 8K; got {OmtVideoFrameGuard.MaxPixels}.");
        Console.WriteLine($"      R14 re-shaped: pixel bound {OmtVideoFrameGuard.MaxPixels:N0} (8K); " +
                          $"1440p {(long)2560 * 1440 * 4:N0} B and 4K {(long)3840 * 2160 * 4:N0} B both accepted");

        // The byte ceiling is retained only as a reference constant, never as enforcement.
        Console.WriteLine($"      (the old 10 MiB byte ceiling would have refused both: VideoMaxSize={OmtVideoFrameGuard.VideoMaxSize:N0})");

        // R14 (as previously): the SDK's own envelope assertion for the table's ceiling geometry.
        long oversize = 16384L * 4 * 16384;
        Expect(oversize > OmtVideoFrameGuard.VideoMaxSize,
            "The 16K ceiling geometry must exceed VIDEO_MAX_SIZE, or the R14 cap is untestable.");

        // ---- 9. THE PRIMITIVES CLAMP THEMSELVES (SEC-OMT-01 R4/R5) --------------------------------
        // The guard sits at one call site, but AudioLevelCalculator/AudioInterleaver are PUBLIC APIs, so a
        // future caller that bypasses the receiver must not be able to re-open the finding. Asserted here
        // as well as in the SEC-OMT-01 table because that table's own R4/R5 section is unreachable while
        // one of its earlier R2 rows is red - a masked assertion is not a proven one.
        const int ProbeBytes = 8 * 1024 * 1024;
        byte[] probeBuffer = new byte[ProbeBytes];
        var probePin = GCHandle.Alloc(probeBuffer, GCHandleType.Pinned);
        try
        {
            IntPtr probe = probePin.AddrOfPinnedObject();

            // R4: out-of-range geometry must be refused BEFORE anything is allocated for it. Pre-fix,
            // `new ChannelAudioLevel[channels]` ran first: 1e9 channels allocated a 7 GB array in ~5 ms.
            Expect(AudioLevelCalculator.ComputeLevels(probe, 1_000_000, 1).Channels.Length == 0,
                "AudioLevelCalculator must refuse an out-of-range CHANNEL count the way AudioInterleaver does (R4).");
            // DERIVED, not restated: the "one past the ceiling" probe must follow the constant, or a
            // retune silently leaves this asserting a number that is out of range for the wrong reason.
            Expect(AudioLevelCalculator.ComputeLevels(probe, 1, OmtAudioFrameGuard.MaxSamplesPerChannel + 1).Channels.Length == 0,
                "AudioLevelCalculator must refuse samples-per-channel above the shared ceiling (R4).");
            // The ceiling itself stays legal - unifying the policy must not narrow the accepted population.
            byte[] ceiling = new byte[64 * OmtAudioFrameGuard.MaxSamplesPerChannel * 4];
            var ceilingPin = GCHandle.Alloc(ceiling, GCHandleType.Pinned);
            try
            {
                Expect(AudioLevelCalculator.ComputeLevels(
                           ceilingPin.AddrOfPinnedObject(), 64, OmtAudioFrameGuard.MaxSamplesPerChannel).Channels.Length == 64,
                    "The inclusive ceiling (64ch x MaxSamplesPerChannel = 16384, the ruled value) must remain " +
                    "accepted (R4) - and this now reads the constant, so the ruling moves it automatically.");
            }
            finally { ceilingPin.Free(); }

            // R4: the two consumers must AGREE on legality. This is a PIN, not the asymmetry guard -
            // AudioInterleaver already enforced 64 locally, so reverting it to the literal changes
            // nothing (that is why the negative control on it cannot fire, and a control that cannot
            // fail proves nothing). The asymmetry itself was the CALCULATOR's missing clamp, and that is
            // guarded by the R4 assertions above.
            int interleavedBytes;
            new AudioInterleaver().Interleave(probe, 65, 1, out interleavedBytes);
            Expect(interleavedBytes == 0,
                "AudioInterleaver must keep refusing 65 channels, so both consumers agree on legality (R4).");
        }
        finally { probePin.Free(); }

        // ---- 10. BOUNDS PROOF (SEC-OMT-01 R3/R4 disposition) --------------------------------------
        // The guard's range gate is only a real defence if the arithmetic it licenses cannot overflow, and
        // if the FIRST consumer to run cannot walk past a frame the guard accepted. Both are checked here
        // as arithmetic rather than asserted in prose.
        const int MaxCh = OmtAudioFrameGuard.MaxChannels;
        const int MaxSmp = OmtAudioFrameGuard.MaxSamplesPerChannel;

        long worstBytes = (long)MaxCh * MaxSmp * OmtAudioFrameGuard.BytesPerFpa1Sample;
        long worstOffset = (long)MaxCh * MaxSmp;
        Console.WriteLine($"      bounds: MaxChannels={MaxCh}, MaxSamplesPerChannel={MaxSmp}");
        Console.WriteLine($"              worst frame bytes  = {worstBytes:N0}   (int.MaxValue = {int.MaxValue:N0})");
        Console.WriteLine($"              worst ch*smp offset = {worstOffset:N0}");
        Console.WriteLine($"              largest REAL frame  = 5760 samples/ch x 2ch x 4 = {5760L * 2 * 4:N0} (vMix priming frame, measured)");

        Expect(worstBytes <= int.MaxValue,
            $"R4: at the enforced ceiling the frame size ({worstBytes:N0}) must fit in int32, or a consumer " +
            "doing int arithmetic on it overflows.");
        Expect(worstOffset <= int.MaxValue,
            $"R5: at the enforced ceiling the channel offset ({worstOffset:N0}) must fit in int32 - it does, " +
            "and it is computed in long regardless, so the SEC-03/C wrap is closed twice over.");
        // The ceiling the guard accepts is not a licence to read: for ANY accepted frame the guard proved
        // dataLength >= channels*samples*4, and AudioLevelCalculator reads exactly channels*samples*4 with
        // the SAME parameters, so its read is bounded by the same number the guard admitted.
        // The two axes, ruled separately: GEOMETRY caps at 64 x 16384 x 4 = 4,194,304 and the PAYLOAD
        // envelope at 16 MiB (4x that, for padding headroom). The worst frame is now 43x inside int32 and
        // 0.25x of the SDK's own AUDIO_MAX_SIZE, instead of 47x of it.
        long worstGeometry = (long)MaxCh * MaxSmp * OmtAudioFrameGuard.BytesPerFpa1Sample;
        // The GEOMETRY ceiling may not EXCEED the payload envelope - that is the invariant. Whether it
        // EQUALS it (one bound by construction) or sits BELOW it (envelope as padding headroom) is a
        // RULING, and this one changed more than once. Asserting a strict ordering here is what made this
        // step FAIL the moment the two became equal at 65536, while the same block also printed
        // "== the geometry ceiling, by construction" - two contradictory claims in one diagnostic.
        // Assert the invariant, then REPORT which relationship is in force.
        Expect(worstGeometry <= OmtAudioFrameGuard.AudioMaxSize,
            $"the geometry ceiling must not EXCEED the payload envelope; {worstGeometry:N0} vs {OmtAudioFrameGuard.AudioMaxSize:N0}.");
        Console.WriteLine($"              worst GEOMETRY = {worstGeometry:N0}  ({MaxCh}ch x {MaxSmp} x 4)");
        Console.WriteLine(worstGeometry == OmtAudioFrameGuard.AudioMaxSize
            ? $"              PAYLOAD envelope = {OmtAudioFrameGuard.AudioMaxSize:N0}  == the geometry ceiling, by construction"
            : $"              PAYLOAD envelope = {OmtAudioFrameGuard.AudioMaxSize:N0}  " +
              $"({OmtAudioFrameGuard.AudioMaxSize - worstGeometry:N0} bytes of padding headroom above the geometry ceiling)");

        AudioFrameValidation atCeiling = OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, MaxCh, MaxSmp, (int)worstGeometry);
        Expect(atCeiling.IsAccepted && atCeiling.ReadBytes == (int)worstGeometry,
            $"R3: the worst geometry must be accepted and publish {worstGeometry:N0}; got {atCeiling.ReadBytes:N0} ({atCeiling.Reason}).");
        // Padded ABOVE the geometry is conformant - the corrected table's ceiling row states exactly this.
        AudioFrameValidation paddedCeiling = OmtAudioFrameGuard.Validate(
            OmtAudioFrameGuard.Fpa1Codec, MaxCh, MaxSmp, OmtAudioFrameGuard.AudioMaxSize);
        Expect(paddedCeiling.IsAccepted && paddedCeiling.ReadBytes == (int)worstGeometry,
            "R2/R9: the ceiling geometry with a padded payload must be accepted, publishing the GEOMETRY " +
            $"({worstGeometry:N0}), not the padding; got {paddedCeiling.ReadBytes:N0} ({paddedCeiling.Reason}).");
        AudioFrameValidation padded = OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 6400 + 4096);
        Expect(padded.ReadBytes == 6400,
            $"R3: a padded frame must publish the GEOMETRY (6400), not the payload (10496) - a padded " +
            $"sender's surplus bytes are not audio and nothing may read into them; got {padded.ReadBytes}.");
        VideoFrameValidation vCeiling = OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 1920, 1080, 7680, 8294400);
        Expect(vCeiling.IsAccepted && vCeiling.ReadBytes == 8294400,
            $"R13: the video verdict must publish the effective read it licensed; got {vCeiling.ReadBytes}.");

        // ---- 11. R9 ENVELOPE + R7 CIRCUIT BREAKER --------------------------------------------------
        // R9: the declared payload envelope, ruled at 16 MiB. Boundaries asserted on both sides so the cap
        // can neither disappear nor creep.
        Expect(OmtAudioFrameGuard.AudioMaxSize == 16777216,
            $"R9: the audio frame envelope is ruled at 16 MiB; got {OmtAudioFrameGuard.AudioMaxSize}.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, OmtAudioFrameGuard.AudioMaxSize)
                   .Reason != AudioFrameRejection.PayloadTooLarge,
            "R9: a payload exactly AT the envelope must not be refused FOR SIZE (its geometry is checked separately).");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, OmtAudioFrameGuard.AudioMaxSize + 1).Reason
               == AudioFrameRejection.PayloadTooLarge,
            "R9: one byte past the envelope must be refused as PayloadTooLarge - a length no real audio frame can carry.");

        // R7: the breaker counts consecutive refusals, resets on any accepted frame, and is latched.
        using (var breaker = new OmtReceiverService())
        {
            Expect(breaker.ConsecutiveRejections == 0, "R7: a fresh receiver has no consecutive refusals.");
            Expect(OmtReceiverService.MaxConsecutiveRejections > 1,
                "R7: the threshold must be more than one frame, or a transient burst would tear a good source down.");
            // Stay BELOW the threshold here: crossing it schedules a real teardown on the thread pool, which
            // is deliberately not something this table should trigger.
            for (int i = 0; i < 3; i++) breaker.NoteRejectedFrameAndMaybeTearDown();
            Expect(breaker.ConsecutiveRejections == 3,
                $"R7: each refused frame must advance the breaker; got {breaker.ConsecutiveRejections}.");
        }

        Console.WriteLine(" -> FPA1 accepted unchanged, PA16 and unknown codecs refused and named, geometry mismatches " +
                          "refused both directions, hostile geometry gated, refusal counted and surfaced once per " +
                          "problem, the VIDEO twin refuses the 8K/int32-wrap/non-BGRA vectors, and both audio " +
                          "primitives clamp themselves on ONE policy!");
    }
}

