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

    /// <summary>
    /// SEC-OMT-01 R8: frames the receiver REFUSED because they failed the trust-boundary guards. These
    /// never reach a consumer, so without surfacing them a peer that is refused every frame is
    /// indistinguishable from a peer that is simply silent - the operator sees a dead source and no reason.
    /// </summary>
    public long AudioFramesRejected { get; set; }

    /// <summary>SEC-OMT-01 R8: video frames refused by <see cref="OmtVideoFrameGuard"/>.</summary>
    public long VideoFramesRejected { get; set; }
    public long FramesDropped { get; set; }
    public double BitrateMbps { get; set; }

    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}x{Height} @ {FrameRate:F2} fps" : "No Signal";
    public string AudioFormatText => AudioChannels > 0 ? $"{AudioChannels} ch, {SampleRate / 1000.0:F1} kHz 32-bit Float" : "No Audio";
}
