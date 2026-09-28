namespace OmtCaptureStudio.Models;

/// <summary>
/// Immutable value object representing the active format of a video/audio capture stream.
/// </summary>
public record StreamFormat
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }

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
