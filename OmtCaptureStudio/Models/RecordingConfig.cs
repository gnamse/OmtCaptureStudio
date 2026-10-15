namespace OmtCaptureStudio.Models;

public enum OutputContainerFormat
{
    MP4,
    MKV,
    MOV
}

public enum VideoEncoderChoice
{
    AutoHardware,
    NvidiaNvenc,
    IntelQuickSync,
    AmdAmf,
    CpuX264,
    ProRes
}

public enum QualityPreset
{
    Ultra,
    High,
    Medium,
    Low
}

public class RecordingConfig
{
    public OutputContainerFormat ContainerFormat { get; set; } = OutputContainerFormat.MP4;
    public VideoEncoderChoice EncoderChoice { get; set; } = VideoEncoderChoice.AutoHardware;
    public QualityPreset Quality { get; set; } = QualityPreset.High;
    public string OutputDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public string FilenamePrefix { get; set; } = "OMT_Capture";

    /// <summary>
    /// Input-loss watchdog window (ADR-0002): wall-clock seconds with no video frame while recording
    /// before the take is treated as interrupted. Operator-configurable; default 5 s. Applied per take
    /// when recording starts.
    /// </summary>
    public double InputLossThresholdSeconds { get; set; } = 5.0;

    /// <summary>
    /// Bounded grace period (ADR-0002): how long to keep attempting auto-reconnect after an outage
    /// before giving up and stopping the take. Default 30 s.
    /// </summary>
    public double InputRecoveryGraceSeconds { get; set; } = 30.0;
}
