using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sources;

/// <summary>
/// Network media source adapter receiving real OMT video/audio streams via libomtnet.
/// </summary>
public class OmtNetworkSource : IMediaSource
{
    private readonly OmtReceiverService _receiver;
    private string _address;
    private bool _disposed;

    private readonly Action<IntPtr, int, int, int, int, double, long> _onVideoReceived;
    private readonly Action<IntPtr, int, int, int, long> _onAudioReceived;
    private readonly Action<StreamTelemetry> _onTelemetry;
    private readonly Action<string> _onStatus;
    private readonly Action<string> _onError;

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

        _onVideoReceived = (pData, len, w, h, str, fps, ts) =>
            VideoFrameReceived?.Invoke(pData, len, w, h, str, fps, ts);
        _onAudioReceived = (pData, ch, smp, rate, ts) =>
            AudioFrameReceived?.Invoke(pData, ch, smp, rate, ts);
        _onTelemetry = t => TelemetryUpdated?.Invoke(t);
        _onStatus = s => StatusChanged?.Invoke(s);
        _onError = err => ErrorOccurred?.Invoke(err);

        _receiver.VideoFrameReceived += _onVideoReceived;
        _receiver.AudioFrameReceived += _onAudioReceived;
        _receiver.TelemetryUpdated += _onTelemetry;
        _receiver.ConnectionStatusChanged += _onStatus;
        _receiver.ErrorOccurred += _onError;
    }

    public void SetAddress(string address)
    {
        _address = address ?? string.Empty;
    }

    public void Connect()
    {
        if (_disposed) return;
        _receiver.Connect(_address);
    }

    public void Disconnect()
    {
        _receiver.Disconnect();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _receiver.VideoFrameReceived -= _onVideoReceived;
        _receiver.AudioFrameReceived -= _onAudioReceived;
        _receiver.TelemetryUpdated -= _onTelemetry;
        _receiver.ConnectionStatusChanged -= _onStatus;
        _receiver.ErrorOccurred -= _onError;

        VideoFrameReceived = null;
        AudioFrameReceived = null;
        TelemetryUpdated = null;
        StatusChanged = null;
        ErrorOccurred = null;

        _receiver.Dispose();
        GC.SuppressFinalize(this);
    }
}
