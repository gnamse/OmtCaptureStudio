namespace OmtCaptureStudio.Services.Audio;

/// <summary>
/// Why an incoming OMT audio frame was refused.
///
/// <para>DIAG-VMIX / receiver hardening. The receive path downstream of
/// <c>OmtReceiverService</c> is FPA1-only and codec-BLIND: <see cref="AudioLevelCalculator"/> and
/// <see cref="AudioInterleaver"/> each cast the payload to <c>float*</c> unconditionally and read
/// <c>channels * samplesPerChannel * 4</c> bytes. The payload is sized by the SENDER, so a sender that
/// announces a 16-bit codec (PA16) or under-declares its buffer makes this process read past the end of
/// the buffer it was handed. That is a remote-triggerable out-of-bounds read, not merely a quality bug,
/// which is why the frame is refused rather than reinterpreted.</para>
/// </summary>
public enum AudioFrameRejection
{
    /// <summary>The frame is FPA1 and its payload exactly matches its announced geometry.</summary>
    None = 0,

    /// <summary>The sender announced an audio codec this receiver cannot interpret.</summary>
    UnsupportedCodec = 1,

    /// <summary>The payload length disagrees with the announced channels/samples, so reading it would over-run the buffer.</summary>
    GeometryMismatch = 2,

    /// <summary>The announced channel count or samples-per-channel is outside the bounds the consumers accept.</summary>
    GeometryOutOfRange = 3,

    /// <summary>The declared payload exceeds the audio frame envelope, so it cannot be a real audio frame.</summary>
    PayloadTooLarge = 4
}

/// <summary>The verdict for one incoming OMT audio frame.</summary>
public readonly record struct AudioFrameValidation(AudioFrameRejection Reason, string Detail, int ReadBytes = 0)
{
    /// <summary>True when the frame may be forwarded to the consumers unchanged.</summary>
    public bool IsAccepted => Reason == AudioFrameRejection.None;

    /// <summary>
    /// The number of bytes a consumer may read from the payload — <b>the effective bound</b>, and the
    /// only length any consumer is entitled to use.
    ///
    /// <para>On acceptance this is the geometry (<c>channels * samplesPerChannel * 4</c>), which the
    /// boundary has already proven is <c>&lt;= dataLength</c>, so it is simultaneously the intersection
    /// <c>min(geometry, payload)</c>. It is deliberately NOT <c>dataLength</c>: a padded sender's surplus
    /// bytes are not audio, and nothing may walk into them.</para>
    ///
    /// <para>Zero when the frame was refused — a refused frame has no readable extent.</para>
    /// </summary>
    public int ReadBytes { get; init; } = ReadBytes;

    /// <summary>The one accepted verdict for an empty frame.</summary>
    public static AudioFrameValidation Accepted { get; } = new(AudioFrameRejection.None, string.Empty, 0);
}

/// <summary>
/// Pure policy for deciding whether an incoming OMT audio frame may reach the audio consumers.
///
/// <para>No I/O, no state: the whole decision is a function of the four fields the SDK hands us, so it is
/// asserted headlessly against a table of hostile and well-formed frames.</para>
/// </summary>
public static class OmtAudioFrameGuard
{
    /// <summary>The only audio codec this receiver understands: 32-bit float planar ("FPA1").</summary>
    public const int Fpa1Codec = 826363974;

    /// <summary>16-bit planar ("PA16"). Defined so it can be NAMED in the refusal instead of logged as a bare integer.</summary>
    public const int Pa16Codec = 909197648;

    /// <summary>FPA1 carries one 32-bit float per sample.</summary>
    public const int BytesPerFpa1Sample = 4;

    /// <summary>Upper bound the audio consumers accept; mirrors <see cref="AudioInterleaver"/>'s own guard.</summary>
    public const int MaxChannels = 64;

    /// <summary>
    /// Upper bound on samples per channel, ruled at 65536 so that the worst geometry is EXACTLY the
    /// payload envelope:
    ///
    /// <para><c>MaxChannels * MaxSamplesPerChannel * BytesPerFpa1Sample == AudioMaxSize</c>
    /// (<c>64 x 65536 x 4 == 16,777,216</c>). One self-documenting number, instead of two bounds that
    /// merely happen to be ordered and invite drift later. It was 192000, which made the worst geometry
    /// 49,152,000 bytes — 47x the SDK's own AUDIO_MAX_SIZE, a bound far looser than the envelope it was
    /// supposed to respect.</para>
    ///
    /// <para>65536 samples/ch is 1.37 s at 48 kHz: enormously generous against the largest frame any real
    /// sender has been measured delivering (vMix's 5760-sample priming chunk), while the int32 safety
    /// proofs are recomputed at 16 MiB rather than 49 MB — strictly safer on every axis.</para>
    /// </summary>
    public const int MaxSamplesPerChannel = 65536;

    /// <summary>
    /// SEC-OMT-01 R9: the declared-payload envelope, ruled at 16 MiB. The SDK's own
    /// <c>OMTConstants.AUDIO_MAX_SIZE</c> is 1 MiB, but the GEOMETRY ceiling above admits a larger frame
    /// than that, so the two bounds had to be reconciled rather than left contradicting each other; 16 MiB
    /// covers the padded ceiling geometry with headroom while still refusing an absurd declaration outright
    /// — a length no real audio frame can carry.
    /// </summary>
    public const int AudioMaxSize = 16777216;

    /// <summary>
    /// Names a codec integer. The OMT codec codes are four-character ASCII tags packed little-endian
    /// (FPA1, PA16, BGRA...), so decoding beats a hard-coded table: an unexpected value is reported
    /// legibly instead of as an anonymous number.
    /// </summary>
    public static string CodecName(int codec)
    {
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            int b = (codec >> (8 * i)) & 0xFF;
            chars[i] = b >= 32 && b < 127 ? (char)b : '?';
        }

        return $"{new string(chars)} ({codec})";
    }

    /// <summary>
    /// Decides whether an audio frame may be forwarded. Accepts an FPA1 payload whose length is
    /// <b>at least</b> <c>channels * samplesPerChannel * 4</c> — the byte count the consumers will actually
    /// read — and the effective read is clamped to that geometry (<see cref="AudioFrameValidation.ReadBytes"/>),
    /// never to the payload. A payload SHORTER than the geometry is refused, so no consumer can be driven
    /// past the end of the buffer; surplus bytes are padding and are never read.
    /// </summary>
    /// <param name="codec">The codec the sender announced for this frame.</param>
    /// <param name="channels">Announced channel count.</param>
    /// <param name="samplesPerChannel">Announced samples per channel.</param>
    /// <param name="dataLength">The payload length actually delivered, in bytes.</param>
    public static AudioFrameValidation Validate(int codec, int channels, int samplesPerChannel, int dataLength)
    {
        if (codec != Fpa1Codec)
        {
            string named = CodecName(codec);
            string extra = codec == Pa16Codec
                ? " That is 16-bit planar audio; this receiver only accepts 32-bit float planar and will " +
                  "not reinterpret the buffer."
                : " This receiver only accepts FPA1 (32-bit float planar).";

            return new AudioFrameValidation(
                AudioFrameRejection.UnsupportedCodec,
                $"announced codec {named}.{extra}");
        }

        if (channels < 1 || channels > MaxChannels)
        {
            return new AudioFrameValidation(
                AudioFrameRejection.GeometryOutOfRange,
                $"announced {channels} channel(s); the supported range is 1..{MaxChannels}.");
        }

        if (samplesPerChannel < 1 || samplesPerChannel > MaxSamplesPerChannel)
        {
            return new AudioFrameValidation(
                AudioFrameRejection.GeometryOutOfRange,
                $"announced {samplesPerChannel} samples per channel; the supported range is 1..{MaxSamplesPerChannel}.");
        }

        // SEC-OMT-01 R9: a declared payload beyond the envelope cannot be a real audio frame. Checked
        // BEFORE the geometry comparison so an absurd declaration is named as such rather than reported as
        // a geometry mismatch.
        if (dataLength > AudioMaxSize)
        {
            return new AudioFrameValidation(
                AudioFrameRejection.PayloadTooLarge,
                $"declared payload {dataLength} bytes exceeds the {AudioMaxSize}-byte audio frame envelope.");
        }

        // long arithmetic: hostile inputs must not be able to overflow the expectation into a small number.
        long expected = (long)channels * samplesPerChannel * BytesPerFpa1Sample;

        // SEC-OMT-01 R2/R3: the boundary is `>=`, NOT equality. Reading `expected` bytes out of a
        // `dataLength`-byte buffer is in-bounds, so strict equality added ZERO security while silently
        // dropping any conformant sender that pads or rounds its buffer (OMTFrame.SetDataLength is
        // sender-set). The guarantee the consumers need is dataLength >= expected, and that is what is
        // enforced here; the read length stays exactly `expected`, so widening this did NOT widen the
        // one-byte-short over-read that is still refused below.
        if (dataLength < expected)
        {
            return new AudioFrameValidation(
                AudioFrameRejection.GeometryMismatch,
                $"{channels} ch x {samplesPerChannel} samples of FPA1 needs {expected} bytes but the payload " +
                $"is only {dataLength} bytes — reading it would run {expected - dataLength} byte(s) PAST the " +
                "end of the payload.");
        }

        // Accepted. The effective read is the GEOMETRY, not dataLength: it is the intersection
        // min(geometry, payload) because the boundary just proved dataLength >= expected, and a padded
        // sender's surplus bytes are not audio.
        return new AudioFrameValidation(AudioFrameRejection.None, string.Empty, (int)expected);
    }
}
