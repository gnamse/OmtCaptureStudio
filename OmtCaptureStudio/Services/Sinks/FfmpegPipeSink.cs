using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;
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
    private bool _hasAudio;
    private bool _audioPrimed;
    private readonly StringBuilder _ffmpegErrorLog = new();
    private readonly object _lock = new();

    public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
    {
        lock (_lock)
        {
            Cleanup();

            try
            {
                _outputPath = outputPath;
                _hasAudio = format.Channels > 0 && format.SampleRate > 0;
                _audioPrimed = false;

                lock (_ffmpegErrorLog)
                {
                    _ffmpegErrorLog.Clear();
                }

                string pipeSuffix = Guid.NewGuid().ToString("N")[..8];
                string videoPipeName = $"omt_video_{pipeSuffix}";
                string audioPipeName = $"omt_audio_{pipeSuffix}";

                // Create asynchronous named pipe for high-throughput video (64 MB buffer)
                _videoPipe = new NamedPipeServerStream(
                    videoPipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    1024 * 1024 * 64, // 64 MB buffer
                    1024 * 1024 * 64);

                _videoPipeConnectTask = _videoPipe.WaitForConnectionAsync();

                if (_hasAudio)
                {
                    _audioPipe = new NamedPipeServerStream(
                        audioPipeName,
                        PipeDirection.Out,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        1024 * 1024 * 8,  // 8 MB buffer
                        1024 * 1024 * 8);

                    _audioPipeConnectTask = _audioPipe.WaitForConnectionAsync();
                }
                else
                {
                    _audioPipe = null;
                    _audioPipeConnectTask = null;
                }

                int fps = (int)Math.Round(format.FrameRate > 0 ? format.FrameRate : 60.0);
                string vCodecArgs = GetVideoCodecArguments(config.EncoderChoice, config.Quality);

                string aInputArgs;
                string aCodecArgs;

                if (_hasAudio)
                {
                    aInputArgs = $"-thread_queue_size 4096 -f f32le -ar {format.SampleRate} -ac {format.Channels} -i \\\\.\\pipe\\{audioPipeName} ";
                    aCodecArgs = config.ContainerFormat == OutputContainerFormat.MOV && config.EncoderChoice == VideoEncoderChoice.ProRes
                        ? "-c:a pcm_s24le "
                        : "-c:a aac -b:a 320k ";
                }
                else
                {
                    aInputArgs = "";
                    aCodecArgs = "-an ";
                }

                string containerFlags = (config.ContainerFormat == OutputContainerFormat.MP4 || config.ContainerFormat == OutputContainerFormat.MOV)
                    ? "-movflags +faststart "
                    : "";

                string args = $"-y " +
                    $"-thread_queue_size 4096 " +
                    $"-f rawvideo -vcodec rawvideo -pix_fmt bgra -s {format.Width}x{format.Height} -r {fps} -i \\\\.\\pipe\\{videoPipeName} " +
                    aInputArgs +
                    $"-max_interleave_delta 0 " +
                    $"{vCodecArgs} {aCodecArgs}{containerFlags}\"{_outputPath}\"";

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
                        RedirectStandardOutput = false
                    },
                    EnableRaisingEvents = true
                };

                _ffmpegProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        lock (_ffmpegErrorLog)
                        {
                            if (_ffmpegErrorLog.Length > 4000)
                            {
                                _ffmpegErrorLog.Remove(0, 2000);
                            }
                            _ffmpegErrorLog.AppendLine(e.Data);
                        }
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
        if (!_hasAudio || _audioPipe == null || buffer == null || count <= 0) return;

        try
        {
            if (_audioPipeConnectTask != null && !_audioPipeConnectTask.IsCompleted) return;
            if (!_audioPipe.IsConnected) return;

            // Prime audio with initial silence packet if this is the first write
            if (!_audioPrimed)
            {
                _audioPrimed = true;
            }

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
                // Signal EOF to FFmpeg by closing pipes.
                // Do not call Flush() before Dispose() as Win32 FlushFileBuffers blocks if FFmpeg is reading another stream.
                try { _videoPipe?.Dispose(); } catch { }
                try { _audioPipe?.Dispose(); } catch { }
                _videoPipe = null;
                _audioPipe = null;

                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.WaitForExit(10000);
                    if (!_ffmpegProcess.HasExited)
                    {
                        _ffmpegProcess.Kill();
                    }
                }

                if (!string.IsNullOrEmpty(_outputPath) && File.Exists(_outputPath))
                {
                    totalBytesWritten = new FileInfo(_outputPath).Length;
                }

                if (totalBytesWritten == 0)
                {
                    string lastError = GetLastFfmpegError();
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(lastError)
                            ? "FFmpeg did not produce a valid recorded file."
                            : $"FFmpeg error: {lastError}");
                }
            }
            finally
            {
                Cleanup();
            }
        }
    }

    private string GetLastFfmpegError()
    {
        lock (_ffmpegErrorLog)
        {
            string log = _ffmpegErrorLog.ToString().Trim();
            if (string.IsNullOrEmpty(log)) return "";

            string[] lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int count = Math.Min(3, lines.Length);
            return string.Join(" ", lines[^count..]).Trim();
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

    private static VideoEncoderChoice _detectedHardware = VideoEncoderChoice.AutoHardware;

    private static VideoEncoderChoice DetectHardwareEncoder()
    {
        if (_detectedHardware != VideoEncoderChoice.AutoHardware)
        {
            return _detectedHardware;
        }

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey != null)
            {
                foreach (var subName in classKey.GetSubKeyNames())
                {
                    if (subName.StartsWith("00"))
                    {
                        using var subKey = classKey.OpenSubKey(subName);
                        string driverDesc = subKey?.GetValue("DriverDesc")?.ToString() ?? "";
                        string providerName = subKey?.GetValue("ProviderName")?.ToString() ?? "";
                        string combined = $"{driverDesc} {providerName}";

                        if (combined.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                        {
                            return _detectedHardware = VideoEncoderChoice.NvidiaNvenc;
                        }
                        if (combined.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                            combined.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                            combined.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
                        {
                            return _detectedHardware = VideoEncoderChoice.AmdAmf;
                        }
                        if (combined.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                        {
                            return _detectedHardware = VideoEncoderChoice.IntelQuickSync;
                        }
                    }
                }
            }
        }
        catch
        {
            // Graceful fallback on any registry access failure
        }

        return _detectedHardware = VideoEncoderChoice.CpuX264;
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

        if (encoder == VideoEncoderChoice.AutoHardware)
        {
            encoder = DetectHardwareEncoder();
        }

        return encoder switch
        {
            VideoEncoderChoice.NvidiaNvenc => $"-c:v h264_nvenc -preset p4 -cq {crf} -pix_fmt yuv420p",
            VideoEncoderChoice.IntelQuickSync => $"-c:v h264_qsv -global_quality {crf} -pix_fmt yuv420p",
            VideoEncoderChoice.AmdAmf => $"-c:v h264_amf -quality quality -pix_fmt yuv420p",
            VideoEncoderChoice.ProRes => "-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le",
            VideoEncoderChoice.CpuX264 => $"-c:v libx264 -preset ultrafast -tune zerolatency -crf {crf} -pix_fmt yuv420p",
            _ => $"-c:v libx264 -preset ultrafast -tune zerolatency -crf {crf} -pix_fmt yuv420p"
        };
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
