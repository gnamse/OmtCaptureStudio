namespace OmtCaptureStudio.Models;

public class StreamTelemetry
{
    public bool IsConnected { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public string VideoFormat { get; set; } = "Unknown";
    public int AudioChannels { get; set; }
    public int SampleRate { get; set; }
    public long VideoFramesReceived { get; set; }
    public long AudioFramesReceived { get; set; }
    public long FramesDropped { get; set; }
    public double BitrateMbps { get; set; }

    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}x{Height} @ {FrameRate:F2} fps" : "No Signal";
    public string AudioFormatText => AudioChannels > 0 ? $"{AudioChannels} ch, {SampleRate / 1000.0:F1} kHz 32-bit Float" : "No Audio";
}
