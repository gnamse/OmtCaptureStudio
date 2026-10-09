using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sinks;

public class FfmpegProcessHost : IDisposable
{
    private Process? _ffmpegProcess;
    private NamedPipeServerStream? _videoPipe;
    private NamedPipeServerStream? _audioPipe;
    private Task? _videoPipeConnectTask;
    private Task? _audioPipeConnectTask;
    private readonly StringBuilder _ffmpegErrorLog = new();

    private Thread? _videoWorkerThread;
    private Thread? _audioWorkerThread;

    private volatile bool _isWritingActive;
    private volatile bool _disposed;
    private volatile bool _videoConnected;

    // Stall/drop watchdog. A stall check placed inside VideoWriterLoop cannot detect
    // a hang on its own: once FFmpeg stops draining the pipe, the OS pipe buffer
    // fills and the synchronous _videoPipe.Write blocks forever, so no loop iteration
    // ever runs to perform the check. A single low-frequency background thread is
    // therefore required (it is a plain `long`/volatile reader — no timers, no locks
    // beyond what the queue already exposes, thread-affinity-safe).
    private const long StallThresholdMs = 10_000;    // ~10s with no write while data is queued => fail loudly
    private const int WatchdogIntervalMs = 1_000;    // cheap once-per-second cadence
    private const int DropWindowSamples = 4;         // ~4s rolling window for drop-rate
    private const long DropWindowMinFrames = 200;    // need >= ~3.3s of traffic before judging drop rate
    private long _lastVideoWriteMs;
    private long _videoDequeued;
    private readonly long[] _dropRingDrops = new long[DropWindowSamples];
    private readonly long[] _dropRingDeq = new long[DropWindowSamples];
    private int _dropRingIndex;
    private int _dropRingFilled;
    private Thread? _watchdogThread;

    private readonly BoundedMediaQueue<VideoFramePacket> _videoQueue;
    private readonly BoundedMediaQueue<AudioDataPacket>? _audioQueue;
    private string? _outputPath;
    
    public event Action<string>? SinkError;

    public FfmpegProcessHost(
        BoundedMediaQueue<VideoFramePacket> videoQueue,
        BoundedMediaQueue<AudioDataPacket>? audioQueue)
    {
        _videoQueue = videoQueue;
        _audioQueue = audioQueue;
    }

    public bool Start(RecordingConfig config, StreamFormat format, string outputPath)
    {
        _outputPath = outputPath;
        bool hasAudio = _audioQueue != null;

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

        if (hasAudio)
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

        double fps = format.FrameRate > 0 ? format.FrameRate : 60.0;
        string fpsStr = fps.ToString("0.###", CultureInfo.InvariantCulture);
        string vCodecArgs = GetVideoCodecArguments(config.EncoderChoice, config.Quality);

        string aInputArgs = "";
        string aCodecArgs = "-an ";

        if (hasAudio)
        {
            aInputArgs = $"-thread_queue_size 512 -f f32le -ar {format.SampleRate} -ac {format.Channels} -i \\\\.\\pipe\\{audioPipeName} ";
            aCodecArgs = config.ContainerFormat == OutputContainerFormat.MOV && config.EncoderChoice == VideoEncoderChoice.ProRes
                ? "-c:a pcm_s24le "
                : "-c:a aac -b:a 320k ";
        }

        // MP4/MOV are fragmented (-frag_keyframe+empty_moov) instead of a
        // single moov-at-end (faststart). Rationale: a non-fragmented MP4
        // streams mdat first and rewrites a multi-GB moov/index only at Stop,
        // which (a) blows the finalize time budget on slow disks and (b) makes
        // the whole file 100% unplayable if FFmpeg is killed/crashes mid-take.
        // Fragmented output writes playable moof/mdat incrementally, so a mid-
        // session crash still leaves a salvageable file and Stop finalizes in
        // ~O(1). No faststart needed: the index is already online per fragment.
        // MKV/TS are already crash-tolerant and are left untouched.
        string containerFlags = (config.ContainerFormat == OutputContainerFormat.MP4 || config.ContainerFormat == OutputContainerFormat.MOV)
            ? "-movflags +frag_keyframe+empty_moov "
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
                _videoQueue.CompleteAdding();
                _audioQueue?.CompleteAdding();
            }
        };

        _ffmpegProcess.Start();
        _ffmpegProcess.BeginErrorReadLine();

        _isWritingActive = true;
        _disposed = false;

        // (Re)initialize watchdog state for this session.
        _videoConnected = false;
        Volatile.Write(ref _lastVideoWriteMs, 0);
        Volatile.Write(ref _videoDequeued, 0);
        _dropRingIndex = 0;
        _dropRingFilled = 0;

        _videoWorkerThread = new Thread(VideoWriterLoop)
        {
            Name = "Ffmpeg_VideoPipeWriter",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _videoWorkerThread.Start();

        if (hasAudio)
        {
            _audioWorkerThread = new Thread(AudioWriterLoop)
            {
                Name = "Ffmpeg_AudioPipeWriter",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _audioWorkerThread.Start();
        }

        _watchdogThread = new Thread(WatchdogLoop)
        {
            Name = "Ffmpeg_Watchdog",
            IsBackground = true,
            Priority = ThreadPriority.Lowest
        };
        _watchdogThread.Start();

        return true;
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

        // Arm the watchdog only now so the (up to 8s) pipe-connect handshake can
        // never count toward the stall threshold.
        _videoConnected = true;
        Interlocked.Exchange(ref _lastVideoWriteMs, Environment.TickCount64);

        while (!_disposed)
        {
            if (_videoQueue.TryDequeue(out var packet))
            {
                Interlocked.Increment(ref _videoDequeued);
                try
                {
                    if (_videoPipe != null && _videoPipe.IsConnected)
                    {
                        _videoPipe.Write(packet.Buffer, 0, packet.Length);
                        Interlocked.Exchange(ref _lastVideoWriteMs, Environment.TickCount64);
                    }
                    else
                    {
                        if (_isWritingActive)
                        {
                            _isWritingActive = false;
                            SinkError?.Invoke("FFmpeg video pipe disconnected unexpectedly.");
                        }
                        break; // leave iteration via finally, which returns the buffer exactly once
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
                    break; // leave iteration via finally, which returns the buffer exactly once
                }
                finally
                {
                    LargeArrayPool.Shared.Return(packet.Buffer);
                }
            }
            else
            {
                if (_videoQueue.IsAddingCompleted && _videoQueue.Count == 0)
                {
                    break; // done
                }
                _videoQueue.Wait(20);
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

        while (!_disposed && _audioQueue != null)
        {
            if (_audioQueue.TryDequeue(out var packet))
            {
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
                        break; // leave iteration via finally, which returns the buffer exactly once
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
                    break; // leave iteration via finally, which returns the buffer exactly once
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packet.Buffer);
                }
            }
            else
            {
                if (_audioQueue.IsAddingCompleted && _audioQueue.Count == 0)
                {
                    break;
                }
                _audioQueue.Wait(20);
            }
        }
    }

    private void WatchdogLoop()
    {
        try
        {
            while (!_disposed && _isWritingActive)
            {
                Thread.Sleep(WatchdogIntervalMs);
                if (_disposed || !_isWritingActive) break;

                CheckForStall();
                CheckForDropRate(_dropRingIndex);

                // Record the latest (dropped, dequeued) sample into the ring. Slot
                // _dropRingIndex currently holds the sample from DropWindowSamples
                // ticks ago, which CheckForDropRate used as its window baseline.
                long drops = _videoQueue.DroppedCount;
                long deq = Volatile.Read(ref _videoDequeued);
                _dropRingDrops[_dropRingIndex] = drops;
                _dropRingDeq[_dropRingIndex] = deq;
                _dropRingIndex = (_dropRingIndex + 1) % DropWindowSamples;
                if (_dropRingFilled < DropWindowSamples) _dropRingFilled++;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFmpeg Watchdog Error] {ex.Message}");
        }
    }

    private void CheckForStall()
    {
        if (!_videoConnected) return;
        if (_videoQueue.Count == 0) return; // idle/quiet window is not a stall

        long lastWrite = Volatile.Read(ref _lastVideoWriteMs);
        long stalledMs = Environment.TickCount64 - lastWrite;
        if (stalledMs > StallThresholdMs)
        {
            FailRecording(
                $"FFmpeg output stalled (no data written for {stalledMs / 1000}s) — likely encoder/pipe hang.");
        }
    }

    private void CheckForDropRate(int prevSlot)
    {
        if (_dropRingFilled < DropWindowSamples) return; // window not full yet

        long dropsPrev = _dropRingDrops[prevSlot];
        long deqPrev = _dropRingDeq[prevSlot];
        long dropsNow = _videoQueue.DroppedCount;
        long deqNow = Volatile.Read(ref _videoDequeued);

        long dropsDelta = dropsNow - dropsPrev;
        long framesDelta = (deqNow - deqPrev) + dropsDelta;
        if (framesDelta < DropWindowMinFrames) return;    // not enough traffic to judge
        if (dropsDelta * 100 < framesDelta) return;        // drop rate < 1%: acceptable transient

        FailRecording(
            $"FFmpeg recording dropping frames: {dropsDelta} of {framesDelta} frames (>1%) lost " +
            $"over the last {(DropWindowSamples * WatchdogIntervalMs) / 1000}s — encoder/disk cannot " +
            "keep up; earliest frames are being silently discarded.");
    }

    private void FailRecording(string error)
    {
        if (!_isWritingActive) return;
        _isWritingActive = false;
        SinkError?.Invoke(error);
    }

    public long Stop()
    {
        _isWritingActive = false;
        long totalBytesWritten = 0;

        // Allow workers to flush queued frames (give up to 5 seconds)
        if (_videoWorkerThread != null && _videoWorkerThread.IsAlive)
        {
            _videoWorkerThread.Join(5000);
        }
        if (_audioWorkerThread != null && _audioWorkerThread.IsAlive)
        {
            _audioWorkerThread.Join(5000);
        }

        // Flush pipes and wait briefly before closing to ensure FFmpeg receives all data
        // Wrap in tasks with timeouts to prevent deadlocks if FFmpeg stops reading
        try { if (_videoPipe != null) Task.Run(() => _videoPipe.Flush()).Wait(2000); } catch { }
        try { if (_audioPipe != null) Task.Run(() => _audioPipe.Flush()).Wait(2000); } catch { }
        Thread.Sleep(500);

        // Close pipes to signal EOF to FFmpeg.
        try { _videoPipe?.Dispose(); } catch { }
        try { _audioPipe?.Dispose(); } catch { }
        _videoPipe = null;
        _audioPipe = null;

        // Wait for FFmpeg to finish writing file trailers and exit. The budget
        // must scale with output size: a non-fragmented container rewrite can
        // take arbitrarily long on a slow disk, but fragmented MP4/MOV finalize
        // is ~O(1). Base of 15s plus ~100 MB/s of projected finalize work, capped
        // at 180s so an enormous file on a slow disk is never killed at a fixed
        // 30s while FFmpeg is still healthily flushing.
        bool killed = false;
        if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
        {
            long outputSize = !string.IsNullOrEmpty(_outputPath) && File.Exists(_outputPath)
                ? new FileInfo(_outputPath).Length
                : 0;
            const long projectedFinalizeBytesPerSecond = 100L * 1024 * 1024; // ~100 MB/s
            int finalizeBudgetMs = (int)Math.Min(
                180_000L,
                15_000L + (outputSize / projectedFinalizeBytesPerSecond) * 1000L);

            _ffmpegProcess.WaitForExit(finalizeBudgetMs);
            if (!_ffmpegProcess.HasExited)
            {
                try { _ffmpegProcess.Kill(); } catch { }
                killed = true;
            }
        }

        if (killed)
        {
            throw new InvalidOperationException("FFmpeg recording process timed out during finalization and was killed. The output file is likely corrupted.");
        }

        if (_ffmpegProcess != null && _ffmpegProcess.HasExited && _ffmpegProcess.ExitCode != 0)
        {
            string lastError = GetLastFfmpegError();
            throw new InvalidOperationException($"FFmpeg exited with error code {_ffmpegProcess.ExitCode}. Error: {lastError}");
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

        return totalBytesWritten;
    }

    private string GetLastFfmpegError()
    {
        lock (_ffmpegErrorLog)
        {
            return _ffmpegErrorLog.ToString().Trim();
        }
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
        _isWritingActive = false;
        _disposed = true;

        _videoQueue.CompleteAdding();
        _audioQueue?.CompleteAdding();

        if (_watchdogThread != null && _watchdogThread.IsAlive)
        {
            _watchdogThread.Join(1500);
        }
        _watchdogThread = null;

        if (_videoWorkerThread != null && _videoWorkerThread.IsAlive)
        {
            _videoWorkerThread.Join(1000);
        }
        if (_audioWorkerThread != null && _audioWorkerThread.IsAlive)
        {
            _audioWorkerThread.Join(1000);
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
        
        GC.SuppressFinalize(this);
    }
}
