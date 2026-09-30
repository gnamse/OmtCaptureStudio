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
    /// Raised when an asynchronous error occurs within the recording transport sink.
    /// </summary>
    event Action<string>? SinkError;
}
