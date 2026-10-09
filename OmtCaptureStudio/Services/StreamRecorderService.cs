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
    // Disk-space guard thresholds.
    // Preflight assumes a default recording window and requires headroom beyond it
    // so a long high-bitrate capture has room before the periodic guard steps in.
    private static readonly TimeSpan PreflightWindow = TimeSpan.FromMinutes(60);
    private const long MinimumPreflightFreeBytes = 2L * 1024L * 1024L * 1024L; // 2 GB absolute floor
    private const long LowWaterMarkBytes = 1024L * 1024L * 1024L;              // 1 GB hard low-water mark
    private static readonly TimeSpan DiskCheckInterval = TimeSpan.FromSeconds(30);

    private readonly IRecordingSink _sink;
    private readonly bool _ownsSink;
    private bool _isRecording;
    private string? _currentRecordingPath;
    private DateTime _recordStartTime;
    private long _framesWritten;
    private long _audioBytesWritten;
    private readonly object _recordLock = new();
    private DateTime _lastDiskCheckUtc = DateTime.MinValue;

    public bool IsRecording => _isRecording;
    public string? CurrentRecordingPath => _currentRecordingPath;
    public TimeSpan ElapsedTime => _isRecording ? DateTime.Now - _recordStartTime : TimeSpan.Zero;
    public long FramesWritten => _framesWritten;
    public long AudioBytesWritten => _audioBytesWritten;
    /// <summary>Frames the recording transport discarded (drop-oldest) since recording started.</summary>
    public long VideoFramesDropped => _sink.VideoFramesDropped;
    /// <summary>Audio chunks the recording transport discarded since recording started.</summary>
    public long AudioChunksDropped => _sink.AudioChunksDropped;

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
        _sink.SinkError += OnSinkError;
    }

    private void OnSinkError(string error)
    {
        lock (_recordLock)
        {
            if (!_isRecording) return;
            _isRecording = false;
        }
        RecordingError?.Invoke(error);
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

                // Preflight: refuse to start a doomed recording before any bytes are written.
                if (!TryGetFreeSpace(_currentRecordingPath, out string driveName, out string driveFormat, out long freeBytes))
                {
                    // Drive status could not be determined (e.g. a mapped/UNC path offline).
                    // Surfacing a clear warning is safer than faking a PASS or FAIL — proceed,
                    // the periodic guard will still protect the session once storage is reachable.
                    AppLogger.LogError($"Disk preflight could not determine free space for {_currentRecordingPath}; proceeding without preflight check.");
                }
                else
                {
                    if (IsFattishFileSystem(driveFormat))
                    {
                        RecordingError?.Invoke(
                            $"Cannot record: {driveName} is formatted as {driveFormat}, which has a 4 GB single-file limit. " +
                            "Choose an output folder on an NTFS (or exFAT) volume for long recordings.");
                        _currentRecordingPath = null;
                        return false;
                    }

                    long estimated = EstimateSessionSize(config, format);
                    long requiredFloor = Math.Max(estimated * 4L, MinimumPreflightFreeBytes);
                    if (freeBytes < requiredFloor)
                    {
                        RecordingError?.Invoke(
                            $"Cannot record: only {FormatBytes(freeBytes)} free on {driveName}, but a {PreflightWindow.TotalMinutes:0}-minute " +
                            $"capture at the selected quality needs about {FormatBytes(requiredFloor)} free. " +
                            "Free up disk space or choose a different output folder.");
                        _currentRecordingPath = null;
                        return false;
                    }
                }

                bool initialized = _sink.Initialize(config, format, _currentRecordingPath);
                if (!initialized)
                {
                    AppLogger.LogError("Failed to initialize recording sink.");
                    RecordingError?.Invoke("Failed to initialize recording sink.");
                    return false;
                }

                _recordStartTime = DateTime.Now;
                _framesWritten = 0;
                _audioBytesWritten = 0;
                _isRecording = true;

                AppLogger.LogInfo($"Recording started internally: {_currentRecordingPath}");
                RecordingStarted?.Invoke(_currentRecordingPath);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Failed to start recording: {ex.Message}", ex);
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
    /// Periodic low-disk guard. Call from the UI tick loop; if free space on the
    /// output volume has crossed the hard low-water mark, gracefully stops and
    /// finalizes the recording (so the partial file is playable) and surfaces an error.
    /// Thread-safe and cheap (one DriveInfo query every <see cref="DiskCheckInterval"/>).
    /// </summary>
    public void CheckDiskSpaceAndProtect()
    {
        lock (_recordLock)
        {
            if (!_isRecording || _currentRecordingPath == null) return;

            var now = DateTime.UtcNow;
            if (now - _lastDiskCheckUtc < DiskCheckInterval) return;
            _lastDiskCheckUtc = now;

            if (!TryGetFreeSpace(_currentRecordingPath, out _, out string driveFormat, out long freeBytes))
            {
                return; // storage temporarily unreachable; retry on next interval
            }

            // Don't penalize already-low volumes (FAT32/exFAT can legitimately show little free).
            if (IsFattishFileSystem(driveFormat)) return;

            if (freeBytes < LowWaterMarkBytes)
            {
                _isRecording = false;
                string path = _currentRecordingPath ?? string.Empty;
                string message = $"Low disk space: only {FormatBytes(freeBytes)} free. Stopping and finalizing " +
                                 $"recording to {path} to keep it playable.";
                AppLogger.LogError(message);

                // Finalize off the calling (UI) thread and never block it on FFmpeg teardown.
                // RecordingError is marshaled to the UI thread by the VM's own handler.
                Task.Run(() =>
                {
                    try
                    {
                        _sink.FinalizeSink(out long fileSize);
                        RecordingError?.Invoke($"{message} Finalized {FormatBytes(fileSize)}.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogError($"Error finalizing recording on low disk: {ex.Message}", ex);
                        RecordingError?.Invoke($"{message} Finalization failed: {ex.Message}");
                    }
                });
            }
        }
    }

    /// <summary>
    /// Resolves the volume (and its free space / format) that will hold the given file.
    /// Uses DriveInfo directly; no reflection. Returns false when the volume can't be
    /// determined (unc or dismounted target), so callers can degrade gracefully.
    /// </summary>
    private static bool TryGetFreeSpace(string filePath, out string driveName, out string driveFormat, out long freeBytes)
    {
        driveName = string.Empty;
        driveFormat = string.Empty;
        freeBytes = 0;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(filePath));
            if (string.IsNullOrEmpty(root)) return false;

            var info = new DriveInfo(root);
            if (!info.IsReady) return false;

            driveName = info.Name;
            driveFormat = info.DriveFormat ?? string.Empty;
            freeBytes = info.AvailableFreeSpace;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFattishFileSystem(string driveFormat)
    {
        return driveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ||
               driveFormat.Equals("FAT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Estimates bytes for one capture at the configured quality/encoder from a baseline
    /// bitrate, scaled by the session pixel count and frame rate. We don't know the final
    /// duration in advance, so this sizes the default preflight window; the periodic guard
    /// protects whatever actually happens.
    /// </summary>
    internal static long EstimateSessionSize(RecordingConfig config, StreamFormat format)
    {
        // Baseline ≈ 20 Mbps for 1080p60 H.264 of a normal-quality broadcast feed.
        double baselineMbps = 20.0;

        double qualityScale = config.Quality switch
        {
            QualityPreset.Ultra => 2.0,
            QualityPreset.High => 1.5,
            QualityPreset.Medium => 1.0,
            QualityPreset.Low => 0.7,
            _ => 1.5
        };

        // ProRes is uncompressed-ish and very heavy.
        if (config.EncoderChoice == VideoEncoderChoice.ProRes)
        {
            qualityScale *= 6.0;
        }

        double pixels = Math.Max(1, (double)format.Width * format.Height);
        double fps = format.FrameRate > 0 ? format.FrameRate : 60.0;
        double pixelScale = pixels / (1920.0 * 1080.0) * (fps / 60.0);

        double estimatedMbps = baselineMbps * qualityScale * pixelScale;
        const double bitsPerByte = 8.0;
        double estimatedBytesPerSecond = estimatedMbps * 1_000_000.0 / bitsPerByte;
        return (long)(estimatedBytesPerSecond * PreflightWindow.TotalSeconds);
    }

    private static string FormatBytes(long bytes)
    {
        const double gb = 1024.0 * 1024.0 * 1024.0;
        const double mb = 1024.0 * 1024.0;
        return bytes >= gb ? $"{bytes / gb:F1} GB" : $"{bytes / mb:F0} MB";
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
                AppLogger.LogInfo($"Recording finalized internally: {path}, {fileSize} bytes");
                RecordingStopped?.Invoke(path, duration, fileSize);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Error finalizing recording: {ex.Message}", ex);
                RecordingError?.Invoke($"Error finalizing recording: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stops recording asynchronously without blocking the calling thread.
    /// </summary>
    public Task StopRecordingAsync()
    {
        return Task.Run(() => StopRecording());
    }

    public void Dispose()
    {
        StopRecording();
        _sink.SinkError -= OnSinkError;
        if (_ownsSink)
        {
            _sink.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
