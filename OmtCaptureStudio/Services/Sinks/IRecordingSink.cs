using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// Output transport seam for encoded or containerized media recording.
/// Decouples recording orchestration from underlying processes, pipes, or files.
/// </summary>
public interface IRecordingSink : IDisposable
{
    /// <summary>
    /// Initializes and prepares the sink for recording.
    /// </summary>
    bool Initialize(RecordingConfig config, StreamFormat format, string outputPath);

    /// <summary>
    /// Writes an uncompressed BGRA video frame to the sink.
    /// </summary>
    void WriteVideo(IntPtr pData, int dataLength);

    /// <summary>
    /// Writes interleaved 32-bit float PCM audio bytes to the sink.
    /// </summary>
    void WriteAudio(byte[] buffer, int offset, int count);

    /// <summary>
    /// Finalizes the output container, flushes buffers, and returns the total bytes written.
    /// </summary>
    void FinalizeSink(out long totalBytesWritten);

    /// <summary>
    /// Number of video frames the transport's internal queue discarded (drop-oldest,
    /// encoder/disk backpressure) since the last Initialize.
    /// </summary>
    long VideoFramesDropped { get; }

    /// <summary>
    /// Number of audio chunks the transport's internal queue discarded since the last Initialize.
    /// </summary>
    long AudioChunksDropped { get; }

    /// <summary>
    /// The encoder actually used for this session, after any AutoHardware resolution. Empty when the
    /// transport does not have an encoder concept (in-memory test sink).
    /// </summary>
    string ResolvedEncoderName { get; }

    /// <summary>
    /// Capped tail of the transport's diagnostic stream (FFmpeg stderr), for the in-app diagnostics
    /// drawer. Empty for transports without one. Never null.
    /// </summary>
    string DiagnosticTail { get; }

    /// <summary>
    /// Raised when an asynchronous error occurs within the recording transport sink.
    /// </summary>
    event Action<string>? SinkError;
}
