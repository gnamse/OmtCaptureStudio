namespace OmtCaptureStudio.Models;

/// <summary>
/// Immutable value object representing the active format of a video/audio capture stream.
/// </summary>
public record StreamFormat
{
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Nominal frames per second as a double. Prefer <see cref="FrameRateRational"/> when a ratio is known.</summary>
    public double FrameRate { get; init; }

    /// <summary>Exact rate numerator. Zero means "not stored"; readers then recover a ratio from <see cref="FrameRate"/>.</summary>
    public int FrameRateNumerator { get; init; }

    /// <summary>Exact rate denominator. Zero means "not stored". An integer rate is stored as <c>N/1</c>.</summary>
    public int FrameRateDenominator { get; init; }

    public int SampleRate { get; init; }
    public int Channels { get; init; }

    /// <summary>
    /// The rate as a ratio. An explicit numerator/denominator wins, so 60000/1001 is not rounded
    /// through the double. A legacy value that only set <see cref="FrameRate"/> is recovered from that double.
    /// </summary>
    public RationalFrameRate FrameRateRational =>
        FrameRateNumerator > 0 && FrameRateDenominator > 0
            ? new RationalFrameRate(FrameRateNumerator, FrameRateDenominator)
            : RationalFrameRate.FromDouble(FrameRate);

    public bool HasVideo => Width > 0 && Height > 0;
    public bool HasAudio => SampleRate > 0 && Channels > 0;
    public bool IsValid => HasVideo && HasAudio;

    public string VideoSpecsText => HasVideo 
        ? $"{Width}x{Height} @ {FrameRate:F2} fps" 
        : "No Video";

    public string AudioSpecsText => HasAudio 
        ? $"{Channels}ch {SampleRate / 1000.0:F1} kHz" 
        : "No Audio";

    public static StreamFormat Empty => new()
    {
        Width = 0,
        Height = 0,
        FrameRate = 0.0,
        SampleRate = 0,
        Channels = 0
    };
}
