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
    /// Raised when an asynchronous error occurs within the recording transport sink. A sink that can
    /// still write (a composite that shed one destination) raises this to REPORT the loss and expects
    /// the caller to keep recording - see <see cref="IsViable"/>.
    /// </summary>
    event Action<string>? SinkError;

    /// <summary>
    /// Asked AFTER a sink error: does this sink still have a destination it can write to? (NET-011)
    ///
    /// <para>The default is <c>false</c> - a single-destination sink has nowhere to go once its
    /// transport fails, so the caller's existing stop path runs exactly as before. A composite returns
    /// <c>true</c> while a child survives, which is what turns a failure into a DEGRADE (keep recording)
    /// rather than a stop.</para>
    ///
    /// <para>Note the polarity: this is deliberately <b>not</b> "is the sink healthy right now". A
    /// healthy single-destination sink is still not survivable after a failure, and answering <c>true</c>
    /// to a question like that would silently disable stop-on-sink-error for every existing adapter.</para>
    /// </summary>
    bool HasSurvivingDestination => false;

    /// <summary>
    /// Names of the destinations this sink has detached, in detach order (NET-020). Read by the
    /// diagnostics bundle: a backup that silently stopped receiving data is otherwise only visible in
    /// the error stream, long after the take that lost it.
    ///
    /// <para>The default is empty - a single-destination sink has nothing to detach, so the bundle says
    /// "no destination was detached", which is the truth rather than a missing value. Same shape and
    /// same reasoning as <see cref="HasSurvivingDestination"/>: default to the OLD behaviour, never to
    /// the optimistic one.</para>
    /// </summary>
    IReadOnlyList<string> DetachedDestinationNames => Array.Empty<string>();

    /// <summary>
    /// Told that the destination this sink is writing to has failed for <paramref name="reason"/> (low
    /// space, I/O error) (NET-011). Returns <c>true</c> when the sink shed that destination and has
    /// another to continue on, reporting it through <paramref name="survivingPath"/>; <c>false</c> when
    /// it has nowhere left, which means the caller must stop.
    ///
    /// <para>The default returns <c>false</c> - a single-destination sink has nowhere to move to, so
    /// the caller's existing stop path runs unchanged.</para>
    /// </summary>
    bool TryShedFailedDestination(string reason, out string? survivingPath)
    {
        survivingPath = null;
        return false;
    }
}
