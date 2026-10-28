namespace OmtCaptureStudio.Services;

/// <summary>Why an incoming OMT video frame was refused.</summary>
public enum VideoFrameRejection
{
    /// <summary>The frame is BGRA and its geometry fits both its payload and the frame-size envelope.</summary>
    None = 0,

    /// <summary>The sender announced a video codec this receiver cannot interpret.</summary>
    UnsupportedCodec = 1,

    /// <summary>A dimension is non-positive or beyond the supported ceiling, or the stride cannot cover a row.</summary>
    GeometryOutOfRange = 2,

    /// <summary>The geometry needs more bytes than the payload actually carries.</summary>
    GeometryMismatch = 3,

    /// <summary>The geometry exceeds the SDK's own maximum video frame size.</summary>
    FrameTooLarge = 4
}

/// <summary>The verdict for one incoming OMT video frame.</summary>
public readonly record struct VideoFrameValidation(VideoFrameRejection Reason, string Detail, int ReadBytes = 0)
{
    /// <summary>True when the frame may be forwarded to the video consumers unchanged.</summary>
    public bool IsAccepted => Reason == VideoFrameRejection.None;

    /// <summary>
    /// The number of bytes <c>VideoViewportControl.UpdateFrame</c> may copy — the effective bound, equal to
    /// <c>stride * height</c>, already proven <c>&lt;= dataLength</c> and <c>&lt;= VIDEO_MAX_SIZE</c>.
    /// Deliberately not <c>dataLength</c>. Zero when the frame was refused.
    /// </summary>
    public int ReadBytes { get; init; } = ReadBytes;

    /// <summary>The one accepted verdict for an empty frame.</summary>
    public static VideoFrameValidation Accepted { get; } = new(VideoFrameRejection.None, string.Empty, 0);
}

/// <summary>
/// Pure policy for deciding whether an incoming OMT video frame may reach the video consumers.
///
/// <para>SEC-OMT-01 SEC-02 (R11-R14). This is the VIDEO TWIN of <see cref="Audio.OmtAudioFrameGuard"/>,
/// and it exists for exactly the same reason: <c>VideoViewportControl.UpdateFrame</c> copies
/// <c>stride * height</c> bytes out of the peer's pointer and never bounded that copy by the payload it
/// was handed. A peer announcing 8K over a small payload therefore made the UI read ~126 MB out of a
/// ≤10 MB buffer, and — because <c>stride * height</c> was computed in <c>int32</c> — some reachable
/// pairs wrapped the product to <c>0</c> (copy silently skipped) or negative (<c>new byte[negative]</c>
/// throws on the receive thread). Every one of those fields is chosen by the peer.</para>
///
/// <para>No I/O and no state: the verdict is a pure function of the five fields the SDK surfaces, so the
/// whole contract is asserted as data by the security regression table.</para>
/// </summary>
public static class OmtVideoFrameGuard
{
    /// <summary>The only video codec this receiver understands — and the one it asks the SDK for.</summary>
    public const int BgraCodec = 1095911234;

    /// <summary>Four bytes per BGRA pixel.</summary>
    public const int BytesPerBgraPixel = 4;

    /// <summary>Sane per-dimension ceiling; a legitimate capture is nowhere near 16K.</summary>
    public const int MaxDimension = 16384;

    /// <summary>
    /// R14, RE-SHAPED: the resource bound is a PIXEL COUNT, not a byte count. The previous 10 MiB byte
    /// ceiling (<see cref="VideoMaxSize"/>, the SDK's own <c>VIDEO_MAX_SIZE</c>) was the wrong shape - it
    /// refuses conformant high-resolution frames outright: 2560x1440 BGRA is 14,745,600 bytes and
    /// 3840x2160 is 33,177,600, both far over 10 MiB, so every conformant 1440p/4K capture would have been
    /// dropped. The 1080p-only test vectors were why nothing caught it.
    ///
    /// <para>8K (7680x4320) is 33,177,600 pixels, so that is the bound: every conformant resolution up to
    /// and including 8K is accepted, and allocation stays bounded. Memory SAFETY is unaffected - that has
    /// always been R13 (<c>stride*height &lt;= dataLength</c>), which is untouched.</para>
    /// </summary>
    public const int MaxPixels = 33177600;

    /// <summary>The SDK's own maximum video frame size in BYTES. Retained for reference and for the
    /// regression table's assertion; no longer the enforcement bound - see <see cref="MaxPixels"/>.</summary>
    public const int VideoMaxSize = 10485760;

    /// <summary>
    /// Names a codec integer. The OMT codec codes are four-character ASCII tags packed little-endian
    /// (BGRA, UYVY, NV12...), so decoding beats a hard-coded table: an unexpected value is reported
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
    /// Decides whether a video frame may be forwarded. Accepts ONLY a BGRA frame whose geometry fits both
    /// the declared payload and the SDK's frame-size envelope.
    ///
    /// <para>The geometry arithmetic is done in <c>long</c> throughout, so a hostile
    /// <c>stride * height</c> cannot wrap into a small (or negative) number that then passes a check.</para>
    /// </summary>
    /// <param name="codec">The codec the sender announced for this frame.</param>
    /// <param name="width">Announced width in pixels.</param>
    /// <param name="height">Announced height in pixels.</param>
    /// <param name="stride">Announced row stride in bytes.</param>
    /// <param name="dataLength">The payload length actually delivered, in bytes.</param>
    public static VideoFrameValidation Validate(int codec, int width, int height, int stride, int dataLength)
    {
        // R11: the peer's own BGRA claim must be true. `-pix_fmt bgra`, the viewport's copy and the
        // telemetry all assume it, so an UYVY/P216/NV12 frame read as BGRA is misinterpreted pixels.
        if (codec != BgraCodec)
        {
            return new VideoFrameValidation(
                VideoFrameRejection.UnsupportedCodec,
                $"announced codec {CodecName(codec)}. This receiver only accepts BGRA; " +
                "it will not reinterpret another pixel layout as BGRA.");
        }

        // R12: dimensions must be sane before any product is formed from them.
        if (width < 1 || width > MaxDimension)
        {
            return new VideoFrameValidation(
                VideoFrameRejection.GeometryOutOfRange,
                $"announced width {width}; the supported range is 1..{MaxDimension}.");
        }

        if (height < 1 || height > MaxDimension)
        {
            return new VideoFrameValidation(
                VideoFrameRejection.GeometryOutOfRange,
                $"announced height {height}; the supported range is 1..{MaxDimension}.");
        }

        if (stride < (long)width * BytesPerBgraPixel)
        {
            return new VideoFrameValidation(
                VideoFrameRejection.GeometryOutOfRange,
                $"announced stride {stride} cannot cover a {width}-pixel BGRA row " +
                $"({(long)width * BytesPerBgraPixel} bytes needed).");
        }

        // long arithmetic: this is the product that wrapped in int32 (SEC-02/G).
        long required = (long)stride * height;

        // R13: never read past the payload the peer actually delivered.
        if (required > dataLength)
        {
            return new VideoFrameValidation(
                VideoFrameRejection.GeometryMismatch,
                $"{width}x{height} at stride {stride} needs {required} bytes but the payload is " +
                $"{dataLength} bytes — reading it would run {required - dataLength} byte(s) PAST the end.");
        }

        // R14 (re-shaped): bound the PIXEL COUNT, not the byte count. A byte ceiling of 10 MiB refused
        // conformant 1440p (14.7 MB) and 4K (33.2 MB) outright; 8K = 33,177,600 pixels accepts every
        // conformant resolution while still bounding allocation.
        long pixels = (long)width * height;
        if (pixels > MaxPixels)
        {
            return new VideoFrameValidation(
                VideoFrameRejection.FrameTooLarge,
                $"{width}x{height} is {pixels} pixels, above the {MaxPixels}-pixel (8K) maximum.");
        }

        return new VideoFrameValidation(VideoFrameRejection.None, string.Empty, (int)required);
    }
}
