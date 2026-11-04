namespace OmtCaptureStudio.Services;

/// <summary>
/// A timed provider of raw BGRA video frames into a reusable pinned buffer, suitable
/// for feeding OMTSend. Implementations: monitor capture (DesktopFrameCapturer) and
/// window capture (WindowFrameCapturer).
/// Single-threaded: <see cref="TryCaptureFrame"/> is called from one sender thread at
/// a time. Native AOT safe (no reflection).
/// </summary>
public interface IFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }
    int Stride { get; }

    /// <summary>Pointer to the reusable pinned BGRA buffer (tight stride = Width * 4).</summary>
    IntPtr BufferPtr { get; }

    /// <summary>
    /// Copies the latest frame into the pinned buffer. Returns false when no newer frame
    /// is available (the buffer still holds the previous frame).
    /// </summary>
    bool TryCaptureFrame();

    /// <summary>
    /// DIAG-BROADCAST: whether this provider is running on a DEGRADED fallback path - specifically DXGI
    /// Desktop Duplication being unavailable so GDI capture is in use.
    ///
    /// <para>A default interface member defaulting to FALSE, following the
    /// <c>IRecordingSink.HasSurvivingDestination</c> precedent: default to the OLD behaviour rather than
    /// the optimistic one, and force no implementation outside this repo to change. Here the old
    /// behaviour is "no degradation known", which is also the honest answer for a synthetic provider that
    /// has no such concept. This exists so the stall message can tell the operator WHY capture failed,
    /// not merely that it did - the single most diagnostic sentence for a GPU/session-specific failure.</para>
    /// </summary>
    bool IsUsingFallbackCapture => false;
}
