using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sources;

/// <summary>
/// Media source seam representing a timed provider of raw uncompressed video and audio frames.
/// Polymorphically unifies network OMT streams and synthetic local test signal generators.
/// </summary>
public interface IMediaSource : IDisposable
{
    string Name { get; }
    string Address { get; }
    bool IsConnected { get; }

    event Action<IntPtr, int, int, int, int, double, long>? VideoFrameReceived;
    event Action<IntPtr, int, int, int, long>? AudioFrameReceived;
    event Action<StreamTelemetry>? TelemetryUpdated;
    event Action<string>? StatusChanged;
    event Action<string>? ErrorOccurred;

    void Connect();
    void Disconnect();
}
