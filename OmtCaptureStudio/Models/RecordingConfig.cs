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
}
