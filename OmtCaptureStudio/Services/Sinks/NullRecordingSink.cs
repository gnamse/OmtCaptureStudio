using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// In-memory test sink for fast automated verification of frame cadences and byte counts
/// without requiring FFmpeg or disk operations.
/// </summary>
public class NullRecordingSink : IRecordingSink
{
    public long VideoFramesReceived => _videoFramesCount;
    public long AudioBytesReceived => _audioBytesCount;
    public bool IsInitialized { get; private set; }
    public StreamFormat? InitializedFormat { get; private set; }
    public string? OutputPath { get; private set; }
    public event Action<string>? SinkError { add { } remove { } }

    public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
    {
        _videoFramesCount = 0;
        _audioBytesCount = 0;
        InitializedFormat = format;
        OutputPath = outputPath;
        IsInitialized = true;
        return true;
    }

    public void WriteVideo(IntPtr pData, int dataLength)
    {
        if (pData != IntPtr.Zero && dataLength > 0)
        {
            Interlocked.Increment(ref _videoFramesCount);
        }
    }

    public void WriteAudio(byte[] buffer, int offset, int count)
    {
        if (buffer != null && count > 0)
        {
            Interlocked.Add(ref _audioBytesCount, count);
        }
    }

    public void FinalizeSink(out long totalBytesWritten)
    {
        totalBytesWritten = _audioBytesCount + (_videoFramesCount * 1920 * 1080 * 4 / 50); // Simulated compressed size
        IsInitialized = false;
    }

    private long _videoFramesCount;
    private long _audioBytesCount;

    public void Dispose()
    {
        IsInitialized = false;
    }
}
