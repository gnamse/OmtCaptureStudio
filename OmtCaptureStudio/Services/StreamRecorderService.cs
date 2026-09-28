using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

public class StreamRecorderService : IDisposable
{
    private Process? _ffmpegProcess;
    private NamedPipeServerStream? _videoPipe;
    private NamedPipeServerStream? _audioPipe;
    private Task? _videoPipeConnectTask;
    private Task? _audioPipeConnectTask;
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

    public event Action<string>? RecordingStarted;
    public event Action<string, TimeSpan, long>? RecordingStopped;
    public event Action<string>? RecordingError;

    /// <summary>
    /// Starts recording the incoming OMT stream to the specified path.
    /// </summary>
    public bool StartRecording(
        RecordingConfig config, 
        int width, 
        int height, 
        double frameRate, 
        int sampleRate, 
        int channels)
    {
        lock (_recordLock)
        {
            if (_isRecording) return false;

            try
            {
                if (width <= 0 || height <= 0)
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

                string pipeSuffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                string videoPipeName = $"omt_video_{pipeSuffix}";
                string audioPipeName = $"omt_audio_{pipeSuffix}";

                // Create asynchronous named pipes for high throughput
                _videoPipe = new NamedPipeServerStream(
                    videoPipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    1024 * 1024 * 32, // 32 MB buffer
                    1024 * 1024 * 32);

                _audioPipe = new NamedPipeServerStream(
                    audioPipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    1024 * 1024 * 4,  // 4 MB buffer
                    1024 * 1024 * 4);

                _videoPipeConnectTask = _videoPipe.WaitForConnectionAsync();
                _audioPipeConnectTask = _audioPipe.WaitForConnectionAsync();

                int fps = (int)Math.Round(frameRate > 0 ? frameRate : 60);

                // Build FFmpeg command
                string vCodecArgs = GetVideoCodecArguments(config.EncoderChoice, config.Quality);
                string aCodecArgs = config.ContainerFormat == OutputContainerFormat.MOV && config.EncoderChoice == VideoEncoderChoice.ProRes
                    ? "-c:a pcm_s24le"
                    : "-c:a aac -b:a 320k";

                string args = $"-y " +
                    $"-f rawvideo -vcodec rawvideo -pix_fmt bgra -s {width}x{height} -r {fps} -i \\\\.\\pipe\\{videoPipeName} " +
                    $"-f f32le -ar {sampleRate} -ac {channels} -i \\\\.\\pipe\\{audioPipeName} " +
                    $"{vCodecArgs} {aCodecArgs} \"{_currentRecordingPath}\"";

                _ffmpegProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    },
                    EnableRaisingEvents = true
                };

                _ffmpegProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        Debug.WriteLine($"[FFmpeg] {e.Data}");
                    }
                };

                _ffmpegProcess.Start();
                _ffmpegProcess.BeginErrorReadLine();

                _recordStartTime = DateTime.Now;
                _framesWritten = 0;
                _audioBytesWritten = 0;
                _isRecording = true;

                RecordingStarted?.Invoke(_currentRecordingPath);
                return true;
            }
            catch (Exception ex)
            {
                CleanupPipesAndProcess();
                RecordingError?.Invoke($"Failed to start recording: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Writes a decoded BGRA video frame to the recording pipe.
    /// </summary>
    public unsafe void WriteVideoFrame(IntPtr pData, int dataLength)
    {
        if (!_isRecording || _videoPipe == null || pData == IntPtr.Zero || dataLength <= 0) return;

        try
        {
            if (_videoPipeConnectTask != null && !_videoPipeConnectTask.IsCompleted) return;
            if (!_videoPipe.IsConnected) return;

            byte* ptr = (byte*)pData.ToPointer();
            var readOnlySpan = new ReadOnlySpan<byte>(ptr, dataLength);
            _videoPipe.Write(readOnlySpan);
            _framesWritten++;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Video Write Error] {ex.Message}");
        }
    }

    /// <summary>
    /// Writes interleaved 32-bit float audio bytes to the recording pipe.
    /// </summary>
    public void WriteAudioData(byte[] interleavedBytes)
    {
        if (!_isRecording || _audioPipe == null || interleavedBytes == null || interleavedBytes.Length == 0) return;

        try
        {
            if (_audioPipeConnectTask != null && !_audioPipeConnectTask.IsCompleted) return;
            if (!_audioPipe.IsConnected) return;

            _audioPipe.Write(interleavedBytes, 0, interleavedBytes.Length);
            _audioBytesWritten += interleavedBytes.Length;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio Write Error] {ex.Message}");
        }
    }

    /// <summary>
    /// Stops recording gracefully, allowing FFmpeg to finalize container headers.
    /// </summary>
    public void StopRecording()
    {
        lock (_recordLock)
        {
            if (!_isRecording) return;
            _isRecording = false;

            string path = _currentRecordingPath ?? string.Empty;
            TimeSpan duration = DateTime.Now - _recordStartTime;
            long fileSize = 0;

            try
            {
                // Flashing and closing pipes signals EOF to FFmpeg
                try { _videoPipe?.Flush(); _videoPipe?.Close(); } catch { }
                try { _audioPipe?.Flush(); _audioPipe?.Close(); } catch { }

                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    // Allow up to 5 seconds for FFmpeg to finish writing moov/mkv headers
                    if (!_ffmpegProcess.WaitForExit(5000))
                    {
                        _ffmpegProcess.Kill();
                    }
                }

                if (File.Exists(path))
                {
                    fileSize = new FileInfo(path).Length;
                }

                RecordingStopped?.Invoke(path, duration, fileSize);
            }
            catch (Exception ex)
            {
                RecordingError?.Invoke($"Error finalizing recording: {ex.Message}");
            }
            finally
            {
                CleanupPipesAndProcess();
            }
        }
    }

    private void CleanupPipesAndProcess()
    {
        try { _videoPipe?.Dispose(); } catch { }
        try { _audioPipe?.Dispose(); } catch { }
        try { _ffmpegProcess?.Dispose(); } catch { }

        _videoPipe = null;
        _audioPipe = null;
        _ffmpegProcess = null;
        _videoPipeConnectTask = null;
        _audioPipeConnectTask = null;
        _isRecording = false;
    }

    private static string GetVideoCodecArguments(VideoEncoderChoice encoder, QualityPreset quality)
    {
        int crf = quality switch
        {
            QualityPreset.Ultra => 16,
            QualityPreset.High => 20,
            QualityPreset.Medium => 24,
            QualityPreset.Low => 28,
            _ => 20
        };

        return encoder switch
        {
            VideoEncoderChoice.NvidiaNvenc => $"-c:v h264_nvenc -preset p4 -cq {crf} -pix_fmt yuv420p",
            VideoEncoderChoice.IntelQuickSync => $"-c:v h264_qsv -global_quality {crf} -pix_fmt yuv420p",
            VideoEncoderChoice.AmdAmf => $"-c:v h264_amf -quality quality -pix_fmt yuv420p",
            VideoEncoderChoice.ProRes => "-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le",
            VideoEncoderChoice.CpuX264 => $"-c:v libx264 -preset veryfast -crf {crf} -pix_fmt yuv420p",
            _ => $"-c:v libx264 -preset veryfast -crf {crf} -pix_fmt yuv420p" // Safe default fallback
        };
    }

    public void Dispose()
    {
        StopRecording();
    }
}
