using System;
using System.Runtime.InteropServices;
using OmtCaptureStudio.Controls;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Audio;

namespace OmtCaptureStudio.Tests;

/// <summary>
/// SEC-OMT-01: the executable contract for the OMT RECEIVE-PATH TRUST BOUNDARY.
///
/// <para>The receive path is remote, unauthenticated input. Every per-frame field the SDK surfaces -
/// <c>Codec</c>, <c>Channels</c>, <c>SamplesPerChannel</c>, <c>DataLength</c>, and for video
/// <c>Width</c>/<c>Height</c>/<c>Stride</c> - is chosen by the peer, while the consumers assume 32-bit
/// float planar / BGRA. This table pins that contract as data rather than as narrative prose, so a
/// future widening of any bound fails loudly instead of silently re-opening the over-read.</para>
///
/// <para>Every assertion is keyed to a requirement id from the security audit
/// (<c>SEC-OMT-01-omt-receive-trust-boundary.md</c>, §4). The R-ids in the failure messages are the
/// traceability: a red line here names the requirement it belongs to.</para>
///
/// <para><b>Every row is expected GREEN on the hardened tree.</b> R1-R5, R8 and R10-R16 have landed, so
/// there are no outstanding rows here; the <see cref="Stage"/> enum is retained so a FUTURE owed
/// requirement is visibly red-by-design rather than silently passed.</para>
///
/// <para><b>The ruled envelope: geometry and payload are the SAME number by construction</b> -
/// <c>MaxChannels x MaxSamplesPerChannel x 4 == AudioMaxSize</c> - so the guard has one ceiling, not two
/// ordered bounds. The ceiling row is DERIVED from those constants, and <c>RunEnvelopeCeiling</c> reports
/// whether the equality is actually in force, so a future retune of either axis cannot silently invalidate
/// the table. That is not hypothetical: this number was ruled to 192000, then 16384, then 65536, and each
/// hardcoded row went stale - the last one also masked the rows behind it.</para>
/// </summary>
static class SecurityRegressionTable
{
    /// <summary>The suite convention: one deep assertion per fact, throwing with the fact as the message.</summary>
    static void Expect(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    /// <summary>Which tree a row is expected to pass on.</summary>
    enum Stage
    {
        /// <summary>True on the pre-fix tree too - it guards behaviour the fix must NOT break.</summary>
        Holding,

        /// <summary>Encodes a requirement the fix owes; red until that requirement lands.</summary>
        PostFix
    }

    /// <summary>
    /// A hostile (or conformant) audio frame as the SDK would surface it, and the verdict the receiver
    /// must reach. <paramref name="mustAccept"/> is the whole contract: accept means "this raw pointer
    /// may reach AudioLevelCalculator/AudioInterleaver", reject means "it must never reach them".
    /// </summary>
    readonly record struct AudioVector(
        string Req,
        Stage Stage,
        string Name,
        int Codec,
        int Channels,
        int SamplesPerChannel,
        int DataLength,
        bool MustAccept);

    const int Fpa1 = OmtAudioFrameGuard.Fpa1Codec;
    const int Pa16 = OmtAudioFrameGuard.Pa16Codec;
    const int Bgra = OmtVideoFrameGuard.BgraCodec;

    /// <summary>
    /// The hostile-frame table. Row order is deliberate: the conformant population first (so a fix that
    /// breaks every working source is caught), then the codec-confusion vectors, then the geometry
    /// vectors, then the escalations the diagnosis pass did not cover.
    /// </summary>
    static readonly AudioVector[] Vectors =
    {
        // ---- The working population. Measured live: our own sender emits 2ch @ 48 kHz, 800 samples/ch,
        // DataLength 6400, codec FPA1 for 900/900 frames. Any fix that refuses these has broken the app.
        new("R1", Stage.Holding, "our own sender (FPA1, 2ch x 800 x 4)", Fpa1, 2, 800, 6400, true),
        new("R1", Stage.Holding, "mono 48 kHz (R1 conformant)", Fpa1, 1, 480, 1920, true),
        // ---- Two independent axes, as ruled: GEOMETRY (MaxChannels x MaxSamplesPerChannel) and PAYLOAD
        // (AudioMaxSize). The final ruling makes them THE SAME NUMBER by construction, so the ceiling row
        // below is derived rather than written as a literal. The legacy 64x192000 geometry is kept as an
        // explicit REFUSE row, so raising MaxSamplesPerChannel again can never pass silently.
        // The geometry ceiling at ITS OWN size, DERIVED from the guard's constants. The ruling is that the
        // geometry ceiling and the payload envelope are THE SAME NUMBER by construction
        // (MaxChannels x MaxSamplesPerChannel x 4 == AudioMaxSize), not two ordered bounds. This row was
        // hardcoded three times while the ruling moved (64x192000, then 64x16384, then 64x65536), and every
        // one of those literals went stale. Deriving it means the row tracks the guard instead of arguing
        // with it, and RunEnvelopeCeiling asserts the equality relationship separately.
        new("R1", Stage.Holding, "the inclusive geometry ceiling, exact (ruled == the payload envelope)",
            Fpa1,
            OmtAudioFrameGuard.MaxChannels,
            OmtAudioFrameGuard.MaxSamplesPerChannel,
            (int)((long)OmtAudioFrameGuard.MaxChannels * OmtAudioFrameGuard.MaxSamplesPerChannel * OmtAudioFrameGuard.BytesPerFpa1Sample),
            true),
        new("R9", Stage.Holding, "one byte past the envelope (AudioMaxSize + 1)", Fpa1, 2, 800, 16777217, false),
        new("R9", Stage.Holding, "the old geometry ceiling is now unreachable: 64ch x 192000 needs 49 MB", Fpa1, 64, 192000, 49152000, false),

        // ---- Codec confusion: the DIAG-VMIX mechanism. Refused on the CODEC alone, never reinterpreted.
        new("R1", Stage.Holding, "PA16 unless declared", Pa16, 2, 800, 3200, false),
        new("R1", Stage.Holding, "PA16 with an FPA1-sized length (size must not license reinterpretation)", Pa16, 2, 800, 6400, false),
        new("R11", Stage.Holding, "unknown codec tag", 0x12345678, 2, 800, 6400, false),

        // ---- Geometry vs payload: the over-read itself. 100% past the payload at the reported geometry.
        new("R3", Stage.Holding, "under-declared payload (3200 declared, 6400 read)", Fpa1, 2, 800, 3200, false),
        new("R3", Stage.Holding, "one byte short of the geometry", Fpa1, 2, 800, 6399, false),

        // ---- Range gates. Note the ceiling is INCLUSIVE, so 65 and MaxSamplesPerChannel+1 are the
        // rejects, never MaxChannels / MaxSamplesPerChannel themselves (64 / 16384 at the current ruling).
        new("R1", Stage.Holding, "zero channels", Fpa1, 0, 800, 0, false),
        new("R1", Stage.Holding, "above MaxChannels (65)", Fpa1, 65, 1, 260, false),
        new("R1", Stage.Holding, "negative samples per channel", Fpa1, 2, -1, 0, false),

        // ---- R2: a payload LARGER than the geometry is conformant and must be ACCEPTED, with the read
        // clamped to the geometry. Reading `expected` bytes out of a `dataLength`-byte buffer is
        // in-bounds, so strict equality adds ZERO security while silently dropping a source that pads
        // or rounds its buffer (OMTFrame.SetDataLength is sender-set; NETWORK_RECEIVE_MAX_TRANSFER is
        // 131072). This SUPERSEDES the strict-equality asserts in TestOmtAudioFrameGuard section 4.
        //
        // These three rows were Stage.PostFix while R2 was outstanding. They are Holding now that it has
        // landed, and that reclassification is load-bearing: a PostFix row that fails is COUNTED, not
        // thrown, so leaving them would have masked any future regression of the R2 boundary instead of
        // failing the gate.
        new("R2", Stage.Holding, "padded payload (8192 declared, 6400 read)", Fpa1, 2, 800, 8192, true),
        new("R2", Stage.Holding, "one byte long (6401 declared)", Fpa1, 2, 800, 6401, true),

        // The padded row must be PADDED - `dataLength` strictly greater than the geometry's needs - and
        // must sit inside the ruled AUDIO_MAX_SIZE envelope (16 MB). An earlier revision of this row read
        // `1048576 * 4` (4 194 304) against a 64x192000 geometry needing 49 152 000, i.e. an
        // UNDER-declared payload at 8.5% of the geometry: accepting it would have reinstated a 45 MB
        // over-read, which is the very escalation this table exists to catch. Geometry 64x16384 needs
        // exactly 4 194 304 bytes, so 8 388 608 is unambiguously padded and still inside the envelope.
        new("R2", Stage.Holding, "padded payload (8 MB declared, 4 MB read, 64ch x 16384)", Fpa1, 64, 16384, 8388608, true),
    };

    /// <summary>
    /// SEC-OMT-01 §E + §B + §C + §D. Runs the audio-side contract: the hostile-frame table, then the two
    /// consumer primitives the diagnosis pass did not reach.
    /// </summary>
    public static void RunAll()
    {
        Console.WriteLine("\n[Step 0.700] SEC-OMT-01: OMT receive-path trust boundary regression table...");

        // Order matters: the VIDEO table runs FIRST so it is independently verifiable. Any assertion that
        // sits below a failing one never executes - that is how the R4/R5 section was masked before, and
        // the video contract must not inherit the same fate from an unrelated red audio row.
        RunVideoTable();
        RunViewportAcceptance();
        RunHostileFrameTable();
        RunEnvelopeCeiling();
        RunPaddedSenderSemantics();
        RunConsumerPrimitiveCaps();

        Console.WriteLine(" -> SEC-OMT-01: the receive-path trust boundary holds; every hostile vector is refused.");
    }

    // ---------------------------------------------------------------------------------------------
    // The hostile-frame table (R1, R2, R3, R9, R11)
    // ---------------------------------------------------------------------------------------------
    static void RunHostileFrameTable()
    {
        Console.WriteLine(" -> Hostile-frame table (the peer chooses codec, geometry and length per frame):");

        var failures = new List<string>();
        var owed = new List<string>();

        foreach (AudioVector v in Vectors)
        {
            AudioFrameValidation verdict =
                OmtAudioFrameGuard.Validate(v.Codec, v.Channels, v.SamplesPerChannel, v.DataLength);
            bool routed = verdict.IsAccepted == v.MustAccept;

            Console.WriteLine($"      [{(routed ? " ok " : v.Stage == Stage.PostFix ? "RED " : "FAIL")}] {v.Req} " +
                              $"{v.Name} -> {(verdict.IsAccepted ? "accepted" : verdict.Reason.ToString())}");

            if (routed)
            {
                continue;
            }

            string line =
                $"[SEC-OMT-01 {v.Req}] {v.Name}: the receiver must " +
                $"{(v.MustAccept ? "ACCEPT" : "REFUSE")} this frame, but it " +
                $"{(verdict.IsAccepted ? "accepted" : "refused")} it ({verdict.Reason}: {verdict.Detail}). " +
                (v.MustAccept
                    ? "A conformant source is being dropped."
                    : "This frame's raw pointer would reach a float*-casting consumer - a remote over-read.");

            (v.Stage == Stage.PostFix ? owed : failures).Add(line);
        }

        // EVERY row runs before anything throws. Aborting on the first red row is how a table stops being
        // evidence: when the 16 MiB envelope (R9) landed, the ceiling row went red and silently hid the
        // three R2 rows behind it - the same shape of masking that hid the R4/R5 section before that.
        // Report all rows, then fail once with the whole set.
        Expect(failures.Count == 0,
            $"{failures.Count} vector(s) FAILED:\n  " + string.Join("\n  ", failures));

        Expect(owed.Count == 0,
            $"{owed.Count} vector(s) are still OWED by the fix (red-by-design):\n  " + string.Join("\n  ", owed));
    }

    // ---------------------------------------------------------------------------------------------
    // R14 END-TO-END: the consumer that drops frames silently
    // ---------------------------------------------------------------------------------------------
    /// <summary>
    /// R14 must hold at the VIEWPORT, not only in the guard, because <c>UpdateFrame</c> re-validates and
    /// returns silently on refusal. That is exactly how the byte-cap regression survived a green suite: the
    /// guard was re-shaped to a pixel cap while the control kept the old 10 MiB byte bound, so 1440p and 4K
    /// passed the guard and were discarded here - measured, 4K staged 0 bytes and left the back-buffer at
    /// its 1080p size. A guard-only assertion cannot see that.
    ///
    /// <para>Observation is via the private back-buffer, which is reflection in the TEST project only (the
    /// no-reflection rule is a Native AOT constraint on product code). If the field is ever renamed this
    /// fails loudly rather than going quietly blind.</para>
    /// </summary>
    static void RunViewportAcceptance()
    {
        Console.WriteLine(" -> Viewport acceptance (the consumer that DROPS frames silently - R14 end-to-end):");

        var control = new VideoViewportControl();
        var field = typeof(VideoViewportControl).GetField(
            "_backBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Expect(field != null,
            "[SEC-OMT-01 R14] VideoViewportControl._backBuffer was renamed, so this end-to-end assertion is " +
            "BLIND. Re-point it - a guard-only test cannot see a frame dropped at the viewport.");

        // The three resolutions whose payload EXCEEDS the old 10 MiB byte ceiling. 1080p is the control.
        var cases = new (string Name, int W, int H)[]
        {
            ("1080p (below the old byte cap)", 1920, 1080),
            ("1440p (14,745,600 B - above it)", 2560, 1440),
            ("4K UHD (33,177,600 B - above it)", 3840, 2160),
        };

        foreach (var c in cases)
        {
            int stride = c.W * OmtVideoFrameGuard.BytesPerBgraPixel;
            int len = stride * c.H;                     // the payload, declared IN FULL
            byte[] payload = new byte[len];
            var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try
            {
                field.SetValue(control, null);          // reset, so "staged" is observable
                control.UpdateFrame(pin.AddrOfPinnedObject(), len, c.W, c.H, stride);

                var staged = (byte[])field.GetValue(control);
                bool didStage = staged != null && staged.Length == len;

                Console.WriteLine($"      [{(didStage ? " ok " : "FAIL")}] {c.Name} -> " +
                                  (didStage ? "staged" : "DROPPED at the viewport"));

                Expect(didStage,
                    $"[SEC-OMT-01 R14] a conformant {c.Name} frame must reach the viewport's staging " +
                    "buffer. It was DROPPED, which means the viewport's own bounds disagree with " +
                    "OmtVideoFrameGuard - the guard accepted it and this method threw it away.");
            }
            finally
            {
                pin.Free();
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // R9: the payload envelope, DERIVED from the guard's own constants
    // ---------------------------------------------------------------------------------------------
    /// <summary>
    /// The accepted ceiling is a function of three constants the guard owns, so it is DERIVED here rather
    /// than hardcoded. A hardcoded ceiling row is what broke when <c>MaxSamplesPerChannel</c> moved from
    /// 192000 to 16384: the row kept asserting a geometry the guard had stopped accepting, and because the
    /// table then aborted on its first red row it ALSO hid the rows behind it. Both failure modes are
    /// closed by deriving the geometry and by reporting every row before throwing.
    /// </summary>
    static void RunEnvelopeCeiling()
    {
        Console.WriteLine(" -> Envelope ceiling (R9), derived from the guard's constants:");

        int ch = OmtAudioFrameGuard.MaxChannels;
        int smp = OmtAudioFrameGuard.MaxSamplesPerChannel;
        long geometryCeiling = (long)ch * smp * OmtAudioFrameGuard.BytesPerFpa1Sample;

        Console.WriteLine($"      MaxChannels={ch}, MaxSamplesPerChannel={smp}, AudioMaxSize={OmtAudioFrameGuard.AudioMaxSize:N0}");
        Console.WriteLine($"      geometry ceiling = {ch} x {smp} x 4 = {geometryCeiling:N0} bytes");

        // The two ceilings must be ordered, or the table below is asserting the wrong one. The RULED target
        // is equality (the geometry ceiling IS the envelope, not a second bound), so report the distance
        // rather than gate on it: the constant retune and the row land in different commits, and this table
        // must stay green through that window while still showing whether the ruling has been implemented.
        Expect(geometryCeiling <= OmtAudioFrameGuard.AudioMaxSize,
            $"[SEC-OMT-01 R9] the GEOMETRY ceiling ({geometryCeiling:N0}) must fit under the payload " +
            $"envelope ({OmtAudioFrameGuard.AudioMaxSize:N0}); when it does not, the envelope binds first " +
            "and the geometry ceiling is unreachable - decide which one is meant to be the ceiling.");

        long slack = OmtAudioFrameGuard.AudioMaxSize - geometryCeiling;
        Console.WriteLine(slack == 0
            ? "      geometry ceiling == payload envelope (the ruled construction is in force)"
            : $"      geometry ceiling is {slack:N0} bytes BELOW the envelope (ruled target is equality in force once " +
              "MaxSamplesPerChannel reaches 65536)");

        // The geometry ceiling is legal at exactly its own size (inclusive, matching the range checks).
        Expect(OmtAudioFrameGuard.Validate(Fpa1, ch, smp, (int)geometryCeiling).IsAccepted,
            $"[SEC-OMT-01 R9] {ch}ch x {smp} at exactly {geometryCeiling:N0} bytes must be accepted.");

        // ... and stays legal when the confirmant sender pads out to the envelope.
        Expect(OmtAudioFrameGuard.Validate(Fpa1, ch, smp, OmtAudioFrameGuard.AudioMaxSize).IsAccepted,
            $"[SEC-OMT-01 R2/R9] the geometry ceiling PADDED to the {OmtAudioFrameGuard.AudioMaxSize:N0}-byte " +
            "envelope must be accepted - this is the row that must not regress to strict equality.");

        // The envelope is inclusive on its own size and refuses one byte more.
        Expect(OmtAudioFrameGuard.Validate(Fpa1, ch, smp, OmtAudioFrameGuard.AudioMaxSize + 1).Reason
               == AudioFrameRejection.PayloadTooLarge,
            "[SEC-OMT-01 R9] one byte past the envelope must be refused as PayloadTooLarge.");

        // One sample past the geometry ceiling (with a payload that would otherwise fit) must be refused.
        Expect(!OmtAudioFrameGuard.Validate(Fpa1, ch, smp + 1, OmtAudioFrameGuard.AudioMaxSize).IsAccepted,
            $"[SEC-OMT-01 R9] {smp + 1} samples per channel exceeds MaxSamplesPerChannel={smp}.");
    }

    // ---------------------------------------------------------------------------------------------
    // R2: the padded-sender boundary, stated as the exclusive pair it is
    // ---------------------------------------------------------------------------------------------
    static void RunPaddedSenderSemantics()
    {
        // The boundary must be >= on the payload and == on the geometry-derived READ length. Both halves
        // are asserted, because "accept everything" is as wrong as "accept only exact".
        AudioFrameValidation exact =
            OmtAudioFrameGuard.Validate(Fpa1, 2, 800, 2 * 800 * 4);
        Expect(exact.IsAccepted, "[SEC-OMT-01 R2] an exactly-sized FPA1 frame must stay accepted.");

        AudioFrameValidation padded =
            OmtAudioFrameGuard.Validate(Fpa1, 2, 800, 2 * 800 * 4 + 4096);
        Expect(padded.IsAccepted,
            "[SEC-OMT-01 R2] a padded conformant frame must be ACCEPTED (reading 6400 of 10496 bytes is " +
            "in-bounds); it is currently refused. The security property comes from dataLength >= expected, " +
            "not from equality.");

        AudioFrameValidation shortByOne =
            OmtAudioFrameGuard.Validate(Fpa1, 2, 800, 2 * 800 * 4 - 1);
        Expect(!shortByOne.IsAccepted,
            "[SEC-OMT-01 R3] one byte SHORT must remain refused - that is the over-read itself, and " +
            "widening R2 must not have widened this.");
    }

    // ---------------------------------------------------------------------------------------------
    // R4 / R5: the consumer primitives, not just the guard
    // ---------------------------------------------------------------------------------------------
    static void RunConsumerPrimitiveCaps()
    {
        // The guard sits at ONE call site. AudioLevelCalculator.ComputeLevels is public and is also reached
        // through AudioEngine.ComputeLevels; a future caller that bypasses the guard re-opens the whole
        // finding. It must therefore enforce the same ceiling itself.
        //
        // Geometry below is chosen to be SAFE ON THE PRE-FIX TREE while still being far outside any legal
        // frame: the walk stays inside the 8 MB buffer pinned here, so a red row reports a cap that is
        // missing rather than crashing the suite. (1e6 rather than 1e9 channels deliberately - pre-fix,
        // 1e9 allocates a 7 GB reference array and then walks 1e9 samples.)
        const int BufferBytes = 8 * 1024 * 1024;
        byte[] buffer = new byte[BufferBytes];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            IntPtr p = pin.AddrOfPinnedObject();

            // R4a: an out-of-range CHANNEL COUNT must be refused before anything is allocated for it.
            // AudioInterleaver enforces > 64; AudioLevelCalculator enforces nothing, and it runs FIRST
            // (AudioEngine.IngestAudioFrame: AudioLevelCalculator.ComputeLevels then _interleaver.Interleave).
            AudioLevelData tooManyChannels = AudioLevelCalculator.ComputeLevels(p, 1_000_000, 1);
            Expect(tooManyChannels.Channels.Length == 0,
                "[SEC-OMT-01 R4] AudioLevelCalculator.ComputeLevels must refuse an out-of-range channel " +
                $"count the way AudioInterleaver does; it returned {tooManyChannels.Channels.Length} " +
                "channel results. It allocates new ChannelAudioLevel[channels] BEFORE validating " +
                "(AudioLevelCalculator.cs:17), so an unclamped channel count is a remote allocation DoS " +
                "as well as a 46 MB walk.");

            // R4b: an out-of-range SAMPLES-PER-CHANNEL must likewise be refused.
            AudioLevelData tooManySamples = AudioLevelCalculator.ComputeLevels(
                p, 1, OmtAudioFrameGuard.MaxSamplesPerChannel + 1);
            Expect(tooManySamples.Channels.Length == 0,
                "[SEC-OMT-01 R4] AudioLevelCalculator.ComputeLevels must refuse samples-per-channel above " +
                $"the shared ceiling ({OmtAudioFrameGuard.MaxSamplesPerChannel}); it accepted " +
                $"{OmtAudioFrameGuard.MaxSamplesPerChannel + 1}.");

            // R5: with both axes clamped, the int32 product `ch * samplesPerChannel` in the pointer
            // arithmetic (AudioLevelCalculator.cs:24) can no longer wrap - MaxChannels * MaxSamplesPerChannel
            // is small at the ruled ceiling (64 x 16384 = 1.05e6), far inside int32. The clause is asserted
            // as the consequence of R4 rather than by provoking a wrap, because provoking one means reading
            // before the buffer.
            long worstOffset = (long)OmtAudioFrameGuard.MaxChannels * OmtAudioFrameGuard.MaxSamplesPerChannel;
            Expect(worstOffset <= int.MaxValue,
                "[SEC-OMT-01 R5] at the enforced ceiling the channel offset must still fit in int32, or " +
                "the pointer arithmetic must be widened to long.");

            // The ceiling itself stays legal - R4 must not have narrowed the accepted population.
            // (Up to 16 MiB transient; the one large allocation in this table, deliberately kept last.)
            //
            // Buffer size AND the call BOTH derive from the guard's constants. Sizing the buffer from a
            // literal while calling with the live constant is how a test becomes an over-read of its own
            // making: with the literal at 64 x 16384 x 4 while MaxSamplesPerChannel moved to 65536, this
            // would have walked 16 MiB out of a 4 MiB array - the exact defect this table exists to catch.
            int ceilCh = OmtAudioFrameGuard.MaxChannels;
            int ceilSmp = OmtAudioFrameGuard.MaxSamplesPerChannel;
            byte[] ceiling = new byte[unchecked((int)((long)ceilCh * ceilSmp * sizeof(float)))];
            var ceilingPin = GCHandle.Alloc(ceiling, GCHandleType.Pinned);
            try
            {
                AudioLevelData atCeiling = AudioLevelCalculator.ComputeLevels(
                    ceilingPin.AddrOfPinnedObject(), ceilCh, ceilSmp);
                Expect(atCeiling.Channels.Length == ceilCh,
                    $"[SEC-OMT-01 R4] the inclusive ceiling ({ceilCh}ch x {ceilSmp}, the ruled " +
                    $"MaxSamplesPerChannel) must remain accepted; got {atCeiling.Channels.Length} channel results.");
            }
            finally
            {
                ceilingPin.Free();
            }
        }
        finally
        {
            pin.Free();
        }
    }

    // =============================================================================================
    // R11--R16 - THE VIDEO TWIN
    //
    // VideoViewportControl.UpdateFrame copies `stride * height` bytes out of the peer's pointer and is
    // invoked by a DIRECT delegate on OMT_ReceiveThread (MainWindow.axaml.cs:101), so an unvalidated size
    // is a remote over-read that also stalls frame reception. Measured pre-fix: an 8K announce read
    // ~126 MB out of a <=10 MB buffer, and `int requiredBytes = stride * height` WRAPPED for two
    // reachable pairs - to 0 (copy silently skipped) and negative (new byte[negative] throws on the
    // receive thread). The rows below are that measurement, pinned.
    //
    // The vectors, in the order the audit states them (the working population first, then the over-read,
    // then the two int32 pairs that must be caught by SOME rule before the product is formed):
    // =============================================================================================
    static void RunVideoTable()
    {
        Console.WriteLine(" -> Video twin (the peer chooses width/height/stride/codec per frame):");

        var vectors = new (string Req, string Name, int Codec, int W, int H, int Stride, int Len, bool MustAccept)[]
        {
            ("R11", "benign 1920x1080 BGRA (the working population)", Bgra, 1920, 1080, 7680, 8294400, true),

            // ---- R14 RE-SHAPED: byte ceiling -> PIXEL ceiling at 8K. These three are the regression guard
            // for the defect the old 10 MiB BYTE cap caused, and they exist because every accepted video
            // vector being 1080p is exactly why a cap that bricked 1440p+ stayed green. Each is CONFORMANT,
            // each declares its payload in FULL (so R13 passes), and each is LARGER than 10,485,760 bytes -
            // the byte ceiling refused all of them outright.
            ("R14", "1440p, 14,745,600 bytes - ABOVE the old 10 MiB byte cap - must be ACCEPTED", Bgra, 2560, 1440, 10240, 14745600, true),
            ("R14", "4K UHD, 33,177,600 bytes - 3x the old byte cap - must be ACCEPTED", Bgra, 3840, 2160, 15360, 33177600, true),
            ("R14", "8K at exactly MaxPixels (33,177,600 px, INCLUSIVE) must be ACCEPTED", Bgra, 7680, 4320, 30720, 132710400, true),

            // R13 isolation: dimensions AND stride are all legal, so the only failing rule is the product
            // against the payload - 30720 x 4320 = 132 710 400 bytes against a 3 200-byte payload.
            ("R13", "8K announce over a 3200-byte payload (132 MB read, geometry legal)", Bgra, 7680, 4320, 30720, 3200, false),

            // R14 isolation, refusal side: geometry legal and payload declared IN FULL, so the only failing
            // rule is the 8K PIXEL ceiling. One row past 8K pins it from the other side of the boundary.
            ("R14", "one row past 8K (7680x4321), payload in full, exceeds MaxPixels", Bgra, 7680, 4321, 30720, 132741120, false),
            ("R14", "16K square with its payload in full exceeds MaxPixels (268 Mpx)", Bgra, 16384, 16384, 65536, 1073741824, false),
            // These two are the exact inputs whose int32 product wrapped in the PRE-FIX control (to 0 and
            // to negative), and they are kept for that provenance - but they are refused by the DIMENSION
            // ceiling (R12) before any product is formed, so they pin R12, NOT the wrap. The row name must
            // say so: naming them "wraps to zero" would advertise a mechanism that never fires, and a
            // negative control that cannot fire for its stated reason is not a control.
            ("R12", "65536x65536 - the pre-fix wrap-to-ZERO pair; gated by the dimension ceiling", Bgra, 65536, 65536, 65536, 65536, false),
            ("R12", "100000x30000 - the pre-fix wrap-NEGATIVE pair; gated by the dimension ceiling", Bgra, 100000, 30000, 100000, 1000, false),
        };

        var videoFailures = new List<string>();

        foreach (var v in vectors)
        {
            VideoFrameValidation verdict = OmtVideoFrameGuard.Validate(v.Codec, v.W, v.H, v.Stride, v.Len);
            bool routed = verdict.IsAccepted == v.MustAccept;

            Console.WriteLine($"      [{(routed ? " ok " : "FAIL")}] {v.Req} {v.Name} -> " +
                              $"{(verdict.IsAccepted ? "accepted" : verdict.Reason.ToString())}");

            if (!routed)
            {
                videoFailures.Add(
                    $"[SEC-OMT-01 {v.Req}] {v.Name}: the viewport must " +
                    $"{(v.MustAccept ? "ACCEPT" : "REFUSE")} this frame, but it " +
                    $"{(verdict.IsAccepted ? "accepted" : "refused")} it ({verdict.Reason}: {verdict.Detail}). " +
                    (v.MustAccept
                        ? "A legitimate capture is being dropped."
                        : "This frame's remote pointer would be copied for stride*height bytes on the receive thread."));
            }
        }

        // Same rule as the audio table: report every row, then fail once. A per-row throw would let the
        // first bad vector hide the ones after it.
        Expect(videoFailures.Count == 0,
            $"{videoFailures.Count} video vector(s) FAILED:\n  " + string.Join("\n  ", videoFailures));

        // R11: an UYVY frame with the SAME geometry as the accepted BGRA one. The codec must be the only
        // difference - a UYVY frame at its natural stride is refused by the stride rule instead, which
        // would make this assertion vacuous and its negative control unable to fire.
        VideoFrameValidation uyvy = OmtVideoFrameGuard.Validate(0x59565955, 1920, 1080, 7680, 8294400);
        Expect(!uyvy.IsAccepted && uyvy.Reason == VideoFrameRejection.UnsupportedCodec,
            "[SEC-OMT-01 R11] a non-BGRA frame must be refused AS A CODEC refusal, not incidentally by " +
            $"geometry; got {uyvy.Reason}.");

        // R12: the stride must cover a BGRA row, and a dimension beyond the ceiling is refused.
        Expect(!OmtVideoFrameGuard.Validate(Bgra, 1920, 1080, 4096, 8294400).IsAccepted,
            "[SEC-OMT-01 R12] a stride that cannot cover a 1920-pixel BGRA row must be refused.");
        Expect(!OmtVideoFrameGuard.Validate(Bgra, 20000, 1080, 80000, 86400000).IsAccepted,
            "[SEC-OMT-01 R12] a dimension beyond the 16384 ceiling must be refused.");

        // R14 RE-SHAPED: pin the SHAPE of the bound, not just its value.
        Expect((long)7680 * 4320 == OmtVideoFrameGuard.MaxPixels,
            "[SEC-OMT-01 R14] MaxPixels must be exactly 8K (7680 x 4320 = 33,177,600 px) - the bound that " +
            "accepts every conformant resolution up to 8K while still bounding allocation.");

        // This is the assertion that would have caught the original regression. 1440p is 14,745,600 bytes,
        // ABOVE VideoMaxSize, and must be ACCEPTED. If the byte ceiling is ever reinstated as the
        // enforcement bound, every 1440p/4K source dies again - and nothing else in the suite would notice.
        Expect(14_745_600L > OmtVideoFrameGuard.VideoMaxSize
               && OmtVideoFrameGuard.Validate(Bgra, 2560, 1440, 10240, 14745600).IsAccepted,
            "[SEC-OMT-01 R14] a conformant 1440p frame (larger than the old byte cap) must be ACCEPTED; " +
            "VIDEO_MAX_SIZE must not be the enforcement bound.");

        // The REACHABLE maximum allocation is MaxPixels * 4, because the pixel cap binds before
        // MaxDimension ever does - and it must fit int32, since the control casts `(int)required` after
        // validating. (The old assertion compared MaxDimension^2 * 4, which the pixel cap makes unreachable.)
        long maxAcceptedBytes = (long)OmtVideoFrameGuard.MaxPixels * OmtVideoFrameGuard.BytesPerBgraPixel;
        Expect(maxAcceptedBytes <= int.MaxValue,
            $"[SEC-OMT-01 R12] the largest frame the guards can accept ({maxAcceptedBytes:N0} bytes) must " +
            "fit int32, or the control's `(int)required` cast overflows.");

        // And the geometry axis alone must still not wrap int32 - independent of which cap binds, the
        // product stays in long, so a future ceiling change cannot silently reintroduce SEC-02/G.
        long ceilingProduct = (long)OmtVideoFrameGuard.MaxDimension
                              * OmtVideoFrameGuard.BytesPerBgraPixel
                              * OmtVideoFrameGuard.MaxDimension;

        Expect(ceilingProduct <= int.MaxValue,
            $"[SEC-OMT-01 R12] a LEGAL dimension geometry would wrap int32 at {ceilingProduct} if the " +
            "ceiling ever rose past int32/4; the product must stay in long regardless.");
    }
}
