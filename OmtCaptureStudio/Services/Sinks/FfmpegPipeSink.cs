using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// Production recording sink that pipes raw BGRA video and float PCM audio
/// to FFmpeg through Windows Named Pipes using an asynchronous decoupled pipeline.
/// </summary>
public class FfmpegPipeSink : IRecordingSink
{
    private const int MaxQueuedVideoFrames = 128;
    private const int MaxQueuedAudioChunks = 512;

    private readonly struct VideoFramePacket
    {
        public readonly byte[] Buffer;
        public readonly int Length;

        public VideoFramePacket(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }
    }

    private readonly struct AudioDataPacket
    {
        public readonly byte[] Buffer;
        public readonly int Length;

        public AudioDataPacket(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }
    }

    private Process? _ffmpegProcess;
    private NamedPipeServerStream? _videoPipe;
    private NamedPipeServerStream? _audioPipe;
    private Task? _videoPipeConnectTask;
    private Task? _audioPipeConnectTask;
    private string? _outputPath;
    private bool _hasAudio;
    private readonly StringBuilder _ffmpegErrorLog = new();
    private readonly object _lock = new();

    // Queues and synchronization for asynchronous, decoupled pipeline
    private readonly Queue<VideoFramePacket> _videoQueue = new();
    private readonly object _videoLock = new();
    private readonly AutoResetEvent _videoSignal = new(false);
    private Thread? _videoWorkerThread;

    private readonly Queue<AudioDataPacket> _audioQueue = new();
    private readonly object _audioLock = new();
    private readonly AutoResetEvent _audioSignal = new(false);
    private Thread? _audioWorkerThread;

    private volatile bool _isWritingActive;
    private volatile bool _disposed;
    private long _droppedVideoFrames;
    private long _droppedAudioChunks;

    public event Action<string>? SinkError;

    public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
    {
        lock (_lock)
        {
            Cleanup();

            try
            {
                _outputPath = outputPath;
                _hasAudio = format.Channels > 0 && format.SampleRate > 0;

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

                double fps = format.FrameRate > 0 ? format.FrameRate : 60.0;
                string fpsStr = fps.ToString("0.###", CultureInfo.InvariantCulture);
                string vCodecArgs = GetVideoCodecArguments(config.EncoderChoice, config.Quality);

                string aInputArgs;
                string aCodecArgs;

                if (_hasAudio)
                {
                    aInputArgs = $"-thread_queue_size 512 -f f32le -ar {format.SampleRate} -ac {format.Channels} -i \\\\.\\pipe\\{audioPipeName} ";
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

                string args = $"-nostdin -y " +
                    $"-thread_queue_size 128 " +
                    $"-f rawvideo -vcodec rawvideo -pix_fmt bgra -s {format.Width}x{format.Height} -r {fpsStr} -i \\\\.\\pipe\\{videoPipeName} " +
                    aInputArgs +
                    $"-max_interleave_delta 5000000 " +
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

                _ffmpegProcess.Exited += (s, e) =>
                {
                    if (_isWritingActive && !_disposed)
                    {
                        _isWritingActive = false;
                        string err = GetLastFfmpegError();
                        SinkError?.Invoke(string.IsNullOrWhiteSpace(err)
                            ? "FFmpeg recording process terminated unexpectedly."
                            : $"FFmpeg process terminated unexpectedly: {err}");
                        try { _videoSignal.Set(); } catch { }
                        try { _audioSignal.Set(); } catch { }
                    }
                };

                _ffmpegProcess.Start();
                _ffmpegProcess.BeginErrorReadLine();

                _isWritingActive = true;
                _disposed = false;
                _droppedVideoFrames = 0;
                _droppedAudioChunks = 0;

                _videoWorkerThread = new Thread(VideoWriterLoop)
                {
                    Name = "Ffmpeg_VideoPipeWriter",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _videoWorkerThread.Start();

                if (_hasAudio)
                {
                    _audioWorkerThread = new Thread(AudioWriterLoop)
                    {
                        Name = "Ffmpeg_AudioPipeWriter",
                        IsBackground = true,
                        Priority = ThreadPriority.AboveNormal
                    };
                    _audioWorkerThread.Start();
                }

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
        if (!_isWritingActive || _disposed || pData == IntPtr.Zero || dataLength <= 0) return;

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

        lock (_videoLock)
        {
            if (!_isWritingActive || _disposed)
            {
                ArrayPool<byte>.Shared.Return(rented);
                return;
            }

            if (_videoQueue.Count >= MaxQueuedVideoFrames)
            {
                var dropped = _videoQueue.Dequeue();
                ArrayPool<byte>.Shared.Return(dropped.Buffer);
                Interlocked.Increment(ref _droppedVideoFrames);
            }

            _videoQueue.Enqueue(new VideoFramePacket(rented, dataLength));
            _videoSignal.Set();
        }
    }

    public void WriteAudio(byte[] buffer, int offset, int count)
    {
        if (!_hasAudio || !_isWritingActive || _disposed || buffer == null || count <= 0) return;

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

        lock (_audioLock)
        {
            if (!_isWritingActive || _disposed)
            {
                ArrayPool<byte>.Shared.Return(rented);
                return;
            }

            if (_audioQueue.Count >= MaxQueuedAudioChunks)
            {
                var dropped = _audioQueue.Dequeue();
                ArrayPool<byte>.Shared.Return(dropped.Buffer);
                Interlocked.Increment(ref _droppedAudioChunks);
            }

            _audioQueue.Enqueue(new AudioDataPacket(rented, count));
            _audioSignal.Set();
        }
    }

    private void VideoWriterLoop()
    {
        try
        {
            if (_videoPipeConnectTask != null)
            {
                _videoPipeConnectTask.Wait(8000);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Video Pipe Handshake Error] {ex.Message}");
            if (_isWritingActive)
            {
                _isWritingActive = false;
                SinkError?.Invoke($"FFmpeg video pipe handshake error: {ex.Message}");
            }
            return;
        }

        if (_videoPipe == null || !_videoPipe.IsConnected)
        {
            if (_isWritingActive)
            {
                _isWritingActive = false;
                SinkError?.Invoke("FFmpeg video pipe failed to connect.");
            }
            return;
        }

        while (!_disposed)
        {
            VideoFramePacket packet = default;
            lock (_videoLock)
            {
                if (_videoQueue.Count > 0)
                {
                    packet = _videoQueue.Dequeue();
                }
                else if (!_isWritingActive)
                {
                    // Recording stopped and all queued frames drained
                    break;
                }
            }

            if (packet.Buffer == null)
            {
                try
                {
                    _videoSignal.WaitOne(20);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                continue;
            }

            try
            {
                if (_videoPipe != null && _videoPipe.IsConnected)
                {
                    _videoPipe.Write(packet.Buffer, 0, packet.Length);
                }
                else
                {
                    if (_isWritingActive)
                    {
                        _isWritingActive = false;
                        SinkError?.Invoke("FFmpeg video pipe disconnected unexpectedly.");
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Video Pipe Write Error] {ex.Message}");
                if (_isWritingActive)
                {
                    _isWritingActive = false;
                    SinkError?.Invoke($"FFmpeg video write error: {ex.Message}");
                }
                break;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet.Buffer);
            }
        }
    }

    private void AudioWriterLoop()
    {
        try
        {
            if (_audioPipeConnectTask != null)
            {
                _audioPipeConnectTask.Wait(8000);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio Pipe Handshake Error] {ex.Message}");
            if (_isWritingActive)
            {
                _isWritingActive = false;
                SinkError?.Invoke($"FFmpeg audio pipe handshake error: {ex.Message}");
            }
            return;
        }

        if (_audioPipe == null || !_audioPipe.IsConnected)
        {
            if (_isWritingActive)
            {
                _isWritingActive = false;
                SinkError?.Invoke("FFmpeg audio pipe failed to connect.");
            }
            return;
        }

        while (!_disposed)
        {
            AudioDataPacket packet = default;
            lock (_audioLock)
            {
                if (_audioQueue.Count > 0)
                {
                    packet = _audioQueue.Dequeue();
                }
                else if (!_isWritingActive)
                {
                    // Recording stopped and all queued audio drained
                    break;
                }
            }

            if (packet.Buffer == null)
            {
                try
                {
                    _audioSignal.WaitOne(20);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                continue;
            }

            try
            {
                if (_audioPipe != null && _audioPipe.IsConnected)
                {
                    _audioPipe.Write(packet.Buffer, 0, packet.Length);
                }
                else
                {
                    if (_isWritingActive)
                    {
                        _isWritingActive = false;
                        SinkError?.Invoke("FFmpeg audio pipe disconnected unexpectedly.");
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio Pipe Write Error] {ex.Message}");
                if (_isWritingActive)
                {
                    _isWritingActive = false;
                    SinkError?.Invoke($"FFmpeg audio write error: {ex.Message}");
                }
                break;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet.Buffer);
            }
        }
    }

    public void FinalizeSink(out long totalBytesWritten)
    {
        totalBytesWritten = 0;
        lock (_lock)
        {
            try
            {
                // 1. Signal workers that no new incoming frames will be enqueued
                _isWritingActive = false;

                lock (_videoLock)
                {
                    _videoSignal.Set();
                }
                lock (_audioLock)
                {
                    _audioSignal.Set();
                }

                // 2. Allow workers to flush queued frames (give up to 5 seconds)
                if (_videoWorkerThread != null && _videoWorkerThread.IsAlive)
                {
                    _videoWorkerThread.Join(5000);
                }
                if (_audioWorkerThread != null && _audioWorkerThread.IsAlive)
                {
                    _audioWorkerThread.Join(5000);
                }

                // 3. Close pipes to signal EOF to FFmpeg.
                // Closing the named pipes tells FFmpeg that input streams have ended.
                try { _videoPipe?.Dispose(); } catch { }
                try { _audioPipe?.Dispose(); } catch { }
                _videoPipe = null;
                _audioPipe = null;

                // 4. Wait for FFmpeg to finish writing file trailers and exit
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
            return _ffmpegErrorLog.ToString().Trim();
        }
    }

    private void Cleanup()
    {
        _isWritingActive = false;
        _disposed = true;

        try { _videoSignal.Set(); } catch { }
        try { _audioSignal.Set(); } catch { }

        if (_videoWorkerThread != null && _videoWorkerThread.IsAlive)
        {
            _videoWorkerThread.Join(1000);
        }
        if (_audioWorkerThread != null && _audioWorkerThread.IsAlive)
        {
            _audioWorkerThread.Join(1000);
        }

        lock (_videoLock)
        {
            while (_videoQueue.Count > 0)
            {
                var p = _videoQueue.Dequeue();
                ArrayPool<byte>.Shared.Return(p.Buffer);
            }
        }

        lock (_audioLock)
        {
            while (_audioQueue.Count > 0)
            {
                var p = _audioQueue.Dequeue();
                ArrayPool<byte>.Shared.Return(p.Buffer);
            }
        }

        try { _videoPipe?.Dispose(); } catch { }
        try { _audioPipe?.Dispose(); } catch { }
        try { _ffmpegProcess?.Dispose(); } catch { }

        _videoPipe = null;
        _audioPipe = null;
        _ffmpegProcess = null;
        _videoPipeConnectTask = null;
        _audioPipeConnectTask = null;
        _videoWorkerThread = null;
        _audioWorkerThread = null;
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
        try { _videoSignal.Dispose(); } catch { }
        try { _audioSignal.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
