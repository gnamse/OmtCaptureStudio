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

    public event Action<string>? SinkError;

    public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
    {
        lock (_lock)
        {
            Cleanup();

            try
            {
                _hasAudio = format.Channels > 0 && format.SampleRate > 0;

                _videoQueue = new BoundedMediaQueue<VideoFramePacket>(MaxQueuedVideoFrames, p => ArrayPool<byte>.Shared.Return(p.Buffer));
                
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
                Debug.WriteLine($"[FFmpeg Sink Init Error] {ex.Message}");
                Cleanup();
                return false;
            }
        }
    }

    public unsafe void WriteVideo(IntPtr pData, int dataLength)
    {
        if (_disposed || pData == IntPtr.Zero || dataLength <= 0 || _videoQueue == null) return;

        byte[] rented;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(dataLength);
            fixed (byte* pDst = rented)
            {
                Buffer.MemoryCopy((void*)pData, pDst, dataLength, dataLength);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Video MemoryCopy Error] {ex.Message}");
            return;
        }

        _videoQueue.Enqueue(new VideoFramePacket(rented, dataLength));
    }

    public void WriteAudio(byte[] buffer, int offset, int count)
    {
        if (!_hasAudio || _disposed || buffer == null || count <= 0 || _audioQueue == null) return;

        byte[] rented;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(count);
            Buffer.BlockCopy(buffer, offset, rented, 0, count);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio Buffer Copy Error] {ex.Message}");
            return;
        }

        _audioQueue.Enqueue(new AudioDataPacket(rented, count));
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
                    totalBytesWritten = _host.Stop();
                }
            }
            finally
            {
                Cleanup();
            }
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
