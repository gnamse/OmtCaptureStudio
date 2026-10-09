using System;
using System.Buffers;
using System.Diagnostics;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sinks;

public class FfmpegPipeSink : IRecordingSink
{
    private const int MaxQueuedVideoFrames = 128;
    private const int MaxQueuedAudioChunks = 512;

    private BoundedMediaQueue<VideoFramePacket>? _videoQueue;
    private BoundedMediaQueue<AudioDataPacket>? _audioQueue;
    private FfmpegProcessHost? _host;
    private bool _hasAudio;
    private volatile bool _disposed;
    private readonly object _lock = new();
    private long _videoDropsBeforeFinalize;
    private long _audioDropsBeforeFinalize;

    public event Action<string>? SinkError;

    public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
    {
        lock (_lock)
        {
            Cleanup();

            try
            {
                _hasAudio = format.Channels > 0 && format.SampleRate > 0;

                _videoQueue = new BoundedMediaQueue<VideoFramePacket>(MaxQueuedVideoFrames, p => LargeArrayPool.Shared.Return(p.Buffer));
                
                if (_hasAudio)
                {
                    _audioQueue = new BoundedMediaQueue<AudioDataPacket>(MaxQueuedAudioChunks, p => ArrayPool<byte>.Shared.Return(p.Buffer));
                }

                _host = new FfmpegProcessHost(_videoQueue, _audioQueue);
                _host.SinkError += msg => SinkError?.Invoke(msg);

                if (!_host.Start(config, format, outputPath))
                {
                    Cleanup();
                    return false;
                }

                _disposed = false;
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[FFmpeg Sink Init Error] {ex.Message}", ex);
                Cleanup();
                return false;
            }
        }
    }

    public unsafe void WriteVideo(IntPtr pData, int dataLength)
    {
        // Capture the queue ref into a local once: after the guard below, Cleanup() may null the
        // field on FinalizeSink's stop edge — dereferencing the field a second time could fault.
        var videoQueue = _videoQueue;
        if (_disposed || videoQueue == null || pData == IntPtr.Zero || dataLength <= 0) return;

        byte[] rented;
        try
        {
            rented = LargeArrayPool.Shared.Rent(dataLength);
            fixed (byte* pDst = rented)
            {
                Buffer.MemoryCopy((void*)pData, pDst, dataLength, dataLength);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[Video MemoryCopy Error] {ex.Message}", ex);
            return;
        }

        // Stop-edge: FinalizeSink may have run CompleteAdding()/Cleanup() while we copied. The
        // queue's Enqueue already drops exactly-once (via the ArrayPool return callback) when it is
        // adding-completed, but if the queue was disposed it may no longer accept items — so drop
        // the rented buffer here (return exactly once) rather than enqueue into a torn-down sink.
        // A Set() on a disposed signal or a lost rented buffer must never escape into the source loop.
        if (_disposed)
        {
            LargeArrayPool.Shared.Return(rented);
            return;
        }

        videoQueue.Enqueue(new VideoFramePacket(rented, dataLength));
    }

    public void WriteAudio(byte[] buffer, int offset, int count)
    {
        var audioQueue = _audioQueue;
        if (!_hasAudio || _disposed || audioQueue == null || buffer == null || count <= 0) return;

        byte[] rented;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(count);
            Buffer.BlockCopy(buffer, offset, rented, 0, count);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[Audio Buffer Copy Error] {ex.Message}", ex);
            return;
        }

        // Same stop-edge guard as WriteVideo — drop the rented buffer exactly once.
        if (_disposed)
        {
            ArrayPool<byte>.Shared.Return(rented);
            return;
        }

        audioQueue.Enqueue(new AudioDataPacket(rented, count));
    }

    public void FinalizeSink(out long totalBytesWritten)
    {
        totalBytesWritten = 0;
        lock (_lock)
        {
            try
            {
                _videoQueue?.CompleteAdding();
                _audioQueue?.CompleteAdding();

                if (_host != null)
                {
                    _videoDropsBeforeFinalize = _videoQueue?.DroppedCount ?? 0;
                    _audioDropsBeforeFinalize = _audioQueue?.DroppedCount ?? 0;
                    totalBytesWritten = _host.Stop();
                }
            }
            finally
            {
                Cleanup();
            }
        }
    }

    /// <summary>
    /// Number of video frames discarded (drop-oldest) by the bounded queue since the
    /// sink was last initialized. Reads the live queue while a session is active, and
    /// the final snapshot value after cleanup.
    /// </summary>
    public long VideoFramesDropped
    {
        get
        {
            var q = _videoQueue;
            return q != null ? q.DroppedCount : Interlocked.Read(ref _videoDropsBeforeFinalize);
        }
    }

    /// <summary>
    /// Number of audio chunks discarded (drop-oldest) by the bounded queue since the
    /// sink was last initialized.
    /// </summary>
    public long AudioChunksDropped
    {
        get
        {
            var q = _audioQueue;
            return q != null ? q.DroppedCount : Interlocked.Read(ref _audioDropsBeforeFinalize);
        }
    }

    private void Cleanup()
    {
        _disposed = true;
        _host?.Dispose();
        _videoQueue?.Dispose();
        _audioQueue?.Dispose();
        
        _host = null;
        _videoQueue = null;
        _audioQueue = null;
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
