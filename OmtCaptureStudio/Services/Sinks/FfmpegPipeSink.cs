using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// Production recording sink that pipes raw BGRA video and float PCM audio
/// to FFmpeg through Windows Named Pipes.
/// </summary>
public class FfmpegPipeSink : IRecordingSink
{
    private Process? _ffmpegProcess;
    private NamedPipeServerStream? _videoPipe;
    private NamedPipeServerStream? _audioPipe;
    private Task? _videoPipeConnectTask;
    private Task? _audioPipeConnectTask;
    private string? _outputPath;
    private readonly object _lock = new();

    public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
    {
        lock (_lock)
        {
            Cleanup();

            try
            {
                _outputPath = outputPath;

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

                int fps = (int)Math.Round(format.FrameRate > 0 ? format.FrameRate : 60.0);
                int sampleRate = format.SampleRate > 0 ? format.SampleRate : 48000;
                int channels = format.Channels > 0 ? format.Channels : 2;

                // Build FFmpeg command
                string vCodecArgs = GetVideoCodecArguments(config.EncoderChoice, config.Quality);
                string aCodecArgs = config.ContainerFormat == OutputContainerFormat.MOV && config.EncoderChoice == VideoEncoderChoice.ProRes
                    ? "-c:a pcm_s24le"
                    : "-c:a aac -b:a 320k";

                string args = $"-y " +
                    $"-f rawvideo -vcodec rawvideo -pix_fmt bgra -s {format.Width}x{format.Height} -r {fps} -i \\\\.\\pipe\\{videoPipeName} " +
                    $"-f f32le -ar {sampleRate} -ac {channels} -i \\\\.\\pipe\\{audioPipeName} " +
                    $"{vCodecArgs} {aCodecArgs} \"{_outputPath}\"";

                string localFfmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
                string ffmpegBinary = File.Exists(localFfmpeg) ? localFfmpeg : "ffmpeg";

                _ffmpegProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegBinary,
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
        if (_videoPipe == null || pData == IntPtr.Zero || dataLength <= 0) return;

        try
        {
            if (_videoPipeConnectTask != null && !_videoPipeConnectTask.IsCompleted) return;
            if (!_videoPipe.IsConnected) return;

            byte* ptr = (byte*)pData.ToPointer();
            var readOnlySpan = new ReadOnlySpan<byte>(ptr, dataLength);
            _videoPipe.Write(readOnlySpan);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Video Write Error] {ex.Message}");
        }
    }

    public void WriteAudio(byte[] buffer, int offset, int count)
    {
        if (_audioPipe == null || buffer == null || count <= 0) return;

        try
        {
            if (_audioPipeConnectTask != null && !_audioPipeConnectTask.IsCompleted) return;
            if (!_audioPipe.IsConnected) return;

            _audioPipe.Write(buffer, offset, count);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio Write Error] {ex.Message}");
        }
    }

    public void FinalizeSink(out long totalBytesWritten)
    {
        totalBytesWritten = 0;
        lock (_lock)
        {
            try
            {
                // Close pipes to signal EOF to FFmpeg
                try { _videoPipe?.Flush(); _videoPipe?.Dispose(); } catch { }
                try { _audioPipe?.Flush(); _audioPipe?.Dispose(); } catch { }
                _videoPipe = null;
                _audioPipe = null;

                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.WaitForExit(5000);
                    if (!_ffmpegProcess.HasExited)
                    {
                        _ffmpegProcess.Kill();
                    }
                }

                if (!string.IsNullOrEmpty(_outputPath) && File.Exists(_outputPath))
                {
                    totalBytesWritten = new FileInfo(_outputPath).Length;
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
        try { _videoPipe?.Dispose(); } catch { }
        try { _audioPipe?.Dispose(); } catch { }
        try { _ffmpegProcess?.Dispose(); } catch { }

        _videoPipe = null;
        _audioPipe = null;
        _ffmpegProcess = null;
        _videoPipeConnectTask = null;
        _audioPipeConnectTask = null;
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
            _ => $"-c:v libx264 -preset veryfast -crf {crf} -pix_fmt yuv420p"
        };
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
