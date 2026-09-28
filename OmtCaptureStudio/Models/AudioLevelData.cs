namespace OmtCaptureStudio.Models;

public class ChannelAudioLevel
{
    public int ChannelIndex { get; set; }
    public float PeakDb { get; set; } = -60f;
    public float RmsDb { get; set; } = -60f;
    public bool IsClipping { get; set; }
}

public class AudioLevelData
{
    public ChannelAudioLevel[] Channels { get; set; } = [];
}
