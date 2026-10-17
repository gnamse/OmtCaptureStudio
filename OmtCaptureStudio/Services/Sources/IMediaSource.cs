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

/// <summary>
/// Optional capability of an <see cref="IMediaSource"/> whose feed arrives from a remote peer
/// (a network OMT stream) and can therefore go dead mid-recording — unlike in-process sources
/// such as the synthetic test signal or host desktop capture, which cannot.
/// <para>
/// The input-loss watchdog only arms for sources that implement this and report
/// <see cref="RequiresInputHealthWatch"/> (ADR-0002 decision 2).
/// </para>
/// </summary>
public interface IRecoverableMediaSource
{
    /// <summary>True when a recording over this feed must be watched for input loss.</summary>
    bool RequiresInputHealthWatch { get; }

    /// <summary>
    /// (Re)establishes the feed after a gap. Safe to call repeatedly, and must reuse the
    /// worker-owns-its-resource / identity-check disposal pattern (a bounded join that times out
    /// must never dispose an object a live receive loop still holds). May block briefly; the
    /// caller runs it off the UI thread.
    /// </summary>
    void EnsureConnected();
}
