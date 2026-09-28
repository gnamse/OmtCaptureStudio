using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sources;

/// <summary>
/// Network media source adapter receiving real OMT video/audio streams via libomtnet.
/// </summary>
public class OmtNetworkSource : IMediaSource
{
    private readonly OmtReceiverService _receiver;
    private string _address;

    public string Name => "OMT Network Stream";
    public string Address => _address;
    public bool IsConnected => _receiver.IsConnected;

    public event Action<IntPtr, int, int, int, int, double, long>? VideoFrameReceived;
    public event Action<IntPtr, int, int, int, long>? AudioFrameReceived;
    public event Action<StreamTelemetry>? TelemetryUpdated;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    public OmtNetworkSource(string address)
    {
        _address = address ?? string.Empty;
        _receiver = new OmtReceiverService();

        _receiver.VideoFrameReceived += (pData, len, w, h, str, fps, ts) =>
            VideoFrameReceived?.Invoke(pData, len, w, h, str, fps, ts);

        _receiver.AudioFrameReceived += (pData, ch, smp, rate, ts) =>
            AudioFrameReceived?.Invoke(pData, ch, smp, rate, ts);

        _receiver.TelemetryUpdated += t => TelemetryUpdated?.Invoke(t);
        _receiver.ConnectionStatusChanged += s => StatusChanged?.Invoke(s);
        _receiver.ErrorOccurred += err => ErrorOccurred?.Invoke(err);
    }

    public void SetAddress(string address)
    {
        _address = address ?? string.Empty;
    }

    public void Connect()
    {
        _receiver.Connect(_address);
    }

    public void Disconnect()
    {
        _receiver.Disconnect();
    }

    public void Dispose()
    {
        _receiver.Dispose();
        GC.SuppressFinalize(this);
    }
}
