using System.IO;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Sinks;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Orchestrates file recording sessions, tracks timing, frame counts, and file paths,
/// while delegating transport mechanics to an IRecordingSink seam.
/// </summary>
public class StreamRecorderService : IDisposable
{
    private readonly IRecordingSink _sink;
    private readonly bool _ownsSink;
    private bool _isRecording;
    private string? _currentRecordingPath;
    private DateTime _recordStartTime;
    private long _framesWritten;
    private long _audioBytesWritten;
    private readonly object _recordLock = new();

    public bool IsRecording => _isRecording;
    public string? CurrentRecordingPath => _currentRecordingPath;
    public TimeSpan ElapsedTime => _isRecording ? DateTime.Now - _recordStartTime : TimeSpan.Zero;
    public long FramesWritten => _framesWritten;
    public long AudioBytesWritten => _audioBytesWritten;

    public event Action<string>? RecordingStarted;
    public event Action<string, TimeSpan, long>? RecordingStopped;
    public event Action<string>? RecordingError;

    /// <summary>
    /// Default constructor utilizing production FFmpeg named pipe sink.
    /// </summary>
    public StreamRecorderService()
        : this(new FfmpegPipeSink(), ownsSink: true)
    {
    }

    /// <summary>
    /// Constructor supporting dependency injection of custom or test recording sinks.
    /// </summary>
    public StreamRecorderService(IRecordingSink sink, bool ownsSink = false)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _ownsSink = ownsSink;
    }

    /// <summary>
    /// Starts recording the incoming OMT stream using the specified StreamFormat.
    /// </summary>
    public bool StartRecording(RecordingConfig config, StreamFormat format)
    {
        lock (_recordLock)
        {
            if (_isRecording) return false;

            try
            {
                if (format.Width <= 0 || format.Height <= 0)
                {
                    RecordingError?.Invoke("Cannot record: Invalid video dimensions.");
                    return false;
                }

                if (!Directory.Exists(config.OutputDirectory))
                {
                    Directory.CreateDirectory(config.OutputDirectory);
                }

                string ext = config.ContainerFormat switch
                {
                    OutputContainerFormat.MKV => ".mkv",
                    OutputContainerFormat.MOV => ".mov",
                    _ => ".mp4"
                };

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string filename = $"{config.FilenamePrefix}_{timestamp}{ext}";
                _currentRecordingPath = Path.Combine(config.OutputDirectory, filename);

                bool initialized = _sink.Initialize(config, format, _currentRecordingPath);
                if (!initialized)
                {
                    RecordingError?.Invoke("Failed to initialize recording sink.");
                    return false;
                }

                _recordStartTime = DateTime.Now;
                _framesWritten = 0;
                _audioBytesWritten = 0;
                _isRecording = true;

                RecordingStarted?.Invoke(_currentRecordingPath);
                return true;
            }
            catch (Exception ex)
            {
                RecordingError?.Invoke($"Failed to start recording: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Overload for backwards compatibility with raw scalar stream parameters.
    /// </summary>
    public bool StartRecording(
        RecordingConfig config, 
        int width, 
        int height, 
        double frameRate, 
        int sampleRate, 
        int channels)
    {
        var format = new StreamFormat
        {
            Width = width,
            Height = height,
            FrameRate = frameRate,
            SampleRate = sampleRate,
            Channels = channels
        };

        return StartRecording(config, format);
    }

    /// <summary>
    /// Writes a decoded BGRA video frame to the recording sink.
    /// </summary>
    public void WriteVideoFrame(IntPtr pData, int dataLength)
    {
        if (!_isRecording || pData == IntPtr.Zero || dataLength <= 0) return;

        _sink.WriteVideo(pData, dataLength);
        Interlocked.Increment(ref _framesWritten);
    }

    /// <summary>
    /// Writes interleaved 32-bit float audio bytes to the recording sink.
    /// </summary>
    public void WriteAudioData(byte[] interleavedBytes)
    {
        if (interleavedBytes != null)
        {
            WriteAudioData(interleavedBytes, 0, interleavedBytes.Length);
        }
    }

    /// <summary>
    /// Writes interleaved 32-bit float audio bytes with offset and count to the recording sink.
    /// </summary>
    public void WriteAudioData(byte[] buffer, int offset, int count)
    {
        if (!_isRecording || buffer == null || count <= 0) return;

        _sink.WriteAudio(buffer, offset, count);
        Interlocked.Add(ref _audioBytesWritten, count);
    }

    /// <summary>
    /// Stops recording gracefully, finalizing the output container.
    /// </summary>
    public void StopRecording()
    {
        lock (_recordLock)
        {
            if (!_isRecording) return;
            _isRecording = false;

            string path = _currentRecordingPath ?? string.Empty;
            TimeSpan duration = DateTime.Now - _recordStartTime;

            try
            {
                _sink.FinalizeSink(out long fileSize);
                RecordingStopped?.Invoke(path, duration, fileSize);
            }
            catch (Exception ex)
            {
                RecordingError?.Invoke($"Error finalizing recording: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        StopRecording();
        if (_ownsSink)
        {
            _sink.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
