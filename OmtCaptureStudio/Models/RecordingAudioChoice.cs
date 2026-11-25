namespace OmtCaptureStudio.Models;

/// <summary>
/// One row in the recording-audio picker. The first row is always the OMT source. The rest are
/// the host inputs the broadcast sender already enumerates.
/// </summary>
public sealed class RecordingAudioChoice
{
    public static RecordingAudioChoice Omt { get; } = new("OMT source audio", RecordingAudioRoute.OmtSource, null);

    public RecordingAudioChoice(string label, RecordingAudioRoute route, HostAudioCaptureOption? host)
    {
        Label = label;
        Route = route;
        Host = host;
    }

    public string Label { get; }
    public RecordingAudioRoute Route { get; }
    public HostAudioCaptureOption? Host { get; }

    public override string ToString() => Label;

    public bool SameAs(RecordingAudioChoice? other)
    {
        if (other == null || other.Route != Route) return false;
        if (Route == RecordingAudioRoute.OmtSource) return true;
        HostAudioCaptureOption? a = Host;
        HostAudioCaptureOption? b = other.Host;
        if (a == null || b == null) return false;
        return a.Mode == b.Mode
               && string.Equals(a.DeviceId ?? string.Empty, b.DeviceId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
               && a.AsioFirstInputChannelIndex == b.AsioFirstInputChannelIndex;
    }
}
