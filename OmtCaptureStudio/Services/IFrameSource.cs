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
}
