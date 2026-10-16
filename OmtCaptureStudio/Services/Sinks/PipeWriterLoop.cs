using System;
using System.Diagnostics;
using System.Threading;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// The common shape of a queued media packet: a rented payload, its length, and the ability to give
/// the payload back. Implemented by the packet structs so <see cref="PipeWriterLoop{T}"/> needs no
/// knowledge of pools — <c>Release()</c> returns to whoever rented it.
///
/// <para>Implemented explicitly by the structs, so the generic constraint dispatches without boxing.</para>
/// </summary>
public interface IMediaPacket
{
    byte[] Buffer { get; }
    int Length { get; }
    void Release();
}

/// <summary>Outcome of writing one packet to the transport.</summary>
public enum PipeWriteResult
{
    /// <summary>The bytes are on the pipe.</summary>
    Written,

    /// <summary>The transport is gone (pipe not connected). Reported as an unexpected disconnect.</summary>
    Disconnected
}

/// <summary>Writes one packet's payload to the transport.</summary>
public delegate PipeWriteResult PipeWrite(byte[] buffer, int length);

/// <summary>
/// Performs the transport handshake and reports readiness. Returns <c>null</c> on success, or the
/// error tail to report (the loop prefixes it, preserving the historic message wording).
/// </summary>
public delegate string? PipeConnect();

/// <summary>
/// ONE implementation of the pipe-writing loop, generic over the packet type (NET-004). Before this,
/// the video and audio loops were near-copies whose only real deltas were a pool, some error strings
/// and two video-only watchdog hooks.
///
/// <para><b>The loop never learns about pools.</b> It hands packets back with <c>Release()</c>, and the
/// packet returns itself to whoever rented it (<see cref="MediaBufferPool"/>). Parameterising the loop
/// by pool instead would re-open the silent mix-up that <see cref="MediaBufferPool"/> closed.</para>
///
/// <para><b>Exactly-once return</b> is the <c>finally</c> below, and it is the ONLY return site on the
/// consumer side — reached on the success path, the disconnected path and the write-threw path alike.</para>
///
/// <para><b>Error funnel</b>: the loop only ever calls <c>reportError</c>; the host passes its single
/// <c>FailRecording</c>, which owns the "was this stop expected?" suppression and the SinkError raise.
/// The loop reports at most once per run and then exits.</para>
///
/// <para><b>Threading</b>: <see cref="Run"/> blocks and is meant to be a dedicated thread body. It is
/// called directly (same thread) by the headless tests.</para>
/// </summary>
public sealed class PipeWriterLoop<T> where T : IMediaPacket
{
    private readonly BoundedMediaQueue<T> _queue;
    private readonly PipeConnect _connect;
    private readonly PipeWrite _write;
    private readonly Func<bool> _isCancelled;
    private readonly Action<string> _reportError;
    private readonly string _errorPrefix;
    private readonly Action? _onConnected;
    private readonly Action? _onDequeued;
    private readonly int _idleWaitMs;

    private long _written;

    public PipeWriterLoop(
        BoundedMediaQueue<T> queue,
        PipeConnect connect,
        PipeWrite write,
        Func<bool> isCancelled,
        Action<string> reportError,
        string errorPrefix,
        Action? onConnected = null,
        Action? onDequeued = null,
        int idleWaitMs = 20)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _isCancelled = isCancelled ?? throw new ArgumentNullException(nameof(isCancelled));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _errorPrefix = errorPrefix ?? string.Empty;
        _onConnected = onConnected;
        _onDequeued = onDequeued;
        _idleWaitMs = idleWaitMs;
    }

    /// <summary>Payloads successfully written to the transport.</summary>
    public long WrittenCount => Interlocked.Read(ref _written);

    /// <summary>True once the loop has exited for any reason (clean drain or failure).</summary>
    public bool HasExited => Volatile.Read(ref _exited) != 0;

    private int _exited;

    /// <summary>Runs the loop to completion on the calling thread.</summary>
    public void Run()
    {
        try
        {
            string? connectError;
            try
            {
                connectError = _connect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_errorPrefix} Pipe Handshake Error] {ex.Message}");
                _reportError($"{_errorPrefix} pipe handshake error: {ex.Message}");
                return;
            }

            if (connectError != null)
            {
                _reportError($"{_errorPrefix} {connectError}");
                return;
            }

            _onConnected?.Invoke();

            while (!_isCancelled())
            {
                if (_queue.TryDequeue(out T packet))
                {
                    _onDequeued?.Invoke();
                    try
                    {
                        if (_write(packet.Buffer, packet.Length) == PipeWriteResult.Disconnected)
                        {
                            _reportError($"{_errorPrefix} pipe disconnected unexpectedly.");
                            break; // leave via finally, which returns the buffer exactly once
                        }
                        Interlocked.Increment(ref _written);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[{_errorPrefix} Pipe Write Error] {ex.Message}");
                        _reportError($"{_errorPrefix} write error: {ex.Message}");
                        break; // leave via finally, which returns the buffer exactly once
                    }
                    finally
                    {
                        // The one return path: exactly once, to the pool that rented it.
                        packet.Release();
                    }
                }
                else
                {
                    if (_queue.IsAddingCompleted && _queue.Count == 0)
                    {
                        break; // drained
                    }
                    _queue.Wait(_idleWaitMs);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _exited, 1);
        }
    }
}
