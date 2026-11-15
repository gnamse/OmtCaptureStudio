using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using OmtCaptureStudio.Models;

using OmtCaptureStudio.Services.Forensics;
using Vortice.MediaFoundation;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// Media Foundation recording sink (NET-025): records to a fragmented MP4 in-process, with no ffmpeg
/// subprocess, no pipe, and no external binary.
///
/// <para><b>Selectable, not the default.</b> The shipping default stays <see cref="FfmpegPipeSink"/>
/// (see <see cref="RecordingSinkSelector.ForProduction"/>); this sink is chosen only when the
/// operator selects it. It implements the existing <see cref="IRecordingSink"/> seam - no new
/// interface.</para>
///
/// <para><b>Input format: ARGB32 (BGRA) directly.</b> Measured in SPIKE-MF: the sink accepts BGRA
/// natively, and writing it straight through costs 1.52 ms/frame versus 5.24 ms/frame when we
/// convert BGRA-&gt;NV12 on the CPU ourselves. So there is deliberately <b>no colour convert here</b>
/// - that is why this path is cheap. The capture-side GPU work (eliminating the DXGI-&gt;staging-&gt;CPU
/// copy) is a separate, later change and does not gate this module.</para>
///
/// <para><b>Fragmented container is mandatory.</b> The default MPEG-4 container writes its index
/// (<c>moov</c>) only at finalize, so a hard kill leaves a file with no index that neither plays nor
/// re-muxes - 100% of the take lost. <c>TranscodeContainertype = Fmpeg4</c> produces a genuinely
/// fragmented MP4 (<c>moof</c>/<c>mvex</c>), which survives a hard kill and decodes clean. The
/// attribute is set on the writer's creation attributes and must never be dropped.</para>
///
/// <para><b>Audio order is load-bearing.</b> The AAC encoder refuses 32-bit float input, and if the
/// VIDEO stream is added first then EVERY audio input type is refused with
/// <c>MF_E_INVALIDMEDIATYPE</c> - an HRESULT that blames the media type, so it reads as a format
/// problem when it is an ordering problem. The audio stream is therefore added <b>before</b> the
/// video stream, and the app's interleaved float32 audio is converted to PCM16 on the write path.</para>
///
/// <para><b>Fail closed.</b> If any media-type negotiation fails, <see cref="Initialize"/> returns
/// false and nothing is written. A sink that swallowed a failed <c>SetInputMediaType</c> and kept
/// calling <c>WriteSample</c> would emit a file with a healthy-looking <c>aac</c> stream that decodes
/// as noise.</para>
///
/// <para><b>AOT:</b> all interop is compile-time Vortice 3.6.2 references - no reflection, no dynamic
/// IL. Verified: 0 IL/SYSLIB warnings in the product with the analyzer enabled.</para>
/// </summary>
public sealed class MediaFoundationRecordingSink : IRecordingSink
{
    private const int MaxQueuedVideoFrames = 128;
    private const int MaxQueuedAudioChunks = 512;

    private readonly object _lock = new();
    private BoundedMediaQueue<VideoFramePacket>? _videoQueue;
    private BoundedMediaQueue<AudioDataPacket>? _audioQueue;
    private MediaFoundationSinkHost? _host;
    private bool _hasAudio;
    private volatile bool _disposed;
    private long _videoDropsBeforeFinalize;
    private long _audioDropsBeforeFinalize;

    public event Action<string>? SinkError;

    /// <summary>Measured cost of the float32-&gt;PCM16 conversion, milliseconds per audio chunk.</summary>
    public double AudioConversionMillisecondsPerChunk =>
        _host?.AudioConversionMillisecondsPerChunk ?? 0.0;

    public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
    {
        lock (_lock)
        {
            Cleanup();

            if (config is null) { AppLogger.LogError("[MF Sink] no recording config."); return false; }
            if (!format.HasVideo) { AppLogger.LogError("[MF Sink] the stream has no video; nothing to record."); return false; }

            // The MF sink writes MP4. MKV has no Media Foundation container; refuse rather than write
            // a file with the wrong extension and a container the operator did not ask for.
            if (config.ContainerFormat != OutputContainerFormat.MP4 &&
                config.ContainerFormat != OutputContainerFormat.MOV)
            {
                AppLogger.LogError($"[MF Sink] {config.ContainerFormat} is not supported by the Media Foundation sink (MP4/MOV only).");
                return false;
            }

            try
            {
                _hasAudio = format.HasAudio;

                _videoQueue = new BoundedMediaQueue<VideoFramePacket>(MaxQueuedVideoFrames, p => p.Release());

                if (_hasAudio)
                {
                    _audioQueue = new BoundedMediaQueue<AudioDataPacket>(MaxQueuedAudioChunks, p => p.Release());
                }

                _host = new MediaFoundationSinkHost(_videoQueue, _audioQueue);
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
                AppLogger.LogError($"[MF Sink Init Error] {ex.Message}", ex);
                Cleanup();
                return false;
            }
        }
    }

    public unsafe void WriteVideo(IntPtr pData, int dataLength)
    {
        var videoQueue = _videoQueue;
        if (_disposed || videoQueue == null || pData == IntPtr.Zero || dataLength <= 0) return;

        byte[]? rented = null;
        try
        {
            rented = MediaBufferPool.Video.Rent(dataLength);
            fixed (byte* pDst = rented)
            {
                Buffer.MemoryCopy((void*)pData, pDst, dataLength, dataLength);
            }
        }
        catch (Exception ex)
        {
            if (rented != null) MediaBufferPool.Video.Return(rented);
            AppLogger.LogError($"[MF Video MemoryCopy Error] {ex.Message}", ex);
            return;
        }

        // Stop-edge: FinalizeSink may have torn the sink down while we copied. Return exactly once.
        if (_disposed)
        {
            MediaBufferPool.Video.Return(rented);
            return;
        }

        try
        {
            videoQueue.Enqueue(new VideoFramePacket(rented, dataLength, MediaBufferPool.Video));
        }
        catch
        {
            MediaBufferPool.Video.Return(rented);
            throw;
        }
    }

    public void WriteAudio(byte[] buffer, int offset, int count)
    {
        var audioQueue = _audioQueue;
        if (!_hasAudio || _disposed || audioQueue == null || buffer == null || count <= 0) return;

        byte[]? rented = null;
        try
        {
            rented = MediaBufferPool.Audio.Rent(count);
            Buffer.BlockCopy(buffer, offset, rented, 0, count);
        }
        catch (Exception ex)
        {
            if (rented != null) MediaBufferPool.Audio.Return(rented);
            AppLogger.LogError($"[MF Audio Buffer Copy Error] {ex.Message}", ex);
            return;
        }

        if (_disposed)
        {
            MediaBufferPool.Audio.Return(rented);
            return;
        }

        try
        {
            audioQueue.Enqueue(new AudioDataPacket(rented, count, MediaBufferPool.Audio));
        }
        catch
        {
            MediaBufferPool.Audio.Return(rented);
            throw;
        }
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

    public long VideoFramesDropped
    {
        get
        {
            var q = _videoQueue;
            return q != null ? q.DroppedCount : Interlocked.Read(ref _videoDropsBeforeFinalize);
        }
    }

    public long AudioChunksDropped
    {
        get
        {
            var q = _audioQueue;
            return q != null ? q.DroppedCount : Interlocked.Read(ref _audioDropsBeforeFinalize);
        }
    }

    /// <summary>
    /// Media Foundation does not expose the selected encoder through IMFSinkWriter.GetStatistics
    /// (it returns E_INVALIDARG even after finalize - measured in SPIKE-MF), so this reports the
    /// container/encoder family we configured rather than a resolved MFT name. Naming a specific MFT
    /// here would be an unverified claim.
    /// </summary>
    public string ResolvedEncoderName => _host != null ? "Media Foundation H.264" : string.Empty;

    /// <summary>Media Foundation has no child stderr; this carries our own last transport message.</summary>
    public string DiagnosticTail => _host?.DiagnosticTail ?? string.Empty;

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
        SinkError = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Owns the <see cref="IMFSinkWriter"/> and drains the sink's queues on one background thread.
/// Mirrors the shape of <c>FfmpegProcessHost</c> so the two sinks have the same lifecycle contract:
/// start, drain, stop, report bytes.
/// </summary>
internal sealed class MediaFoundationSinkHost : IDisposable
{
    // MFStartup/MFShutdown are process-wide; refcount so several sinks in one process (as the test
    // suite creates) do not shut MF down underneath each other.
    private static readonly object StartupLock = new();
    private static int _startupRefCount;

    private readonly BoundedMediaQueue<VideoFramePacket> _videoQueue;
    private readonly BoundedMediaQueue<AudioDataPacket>? _audioQueue;
    private readonly Stopwatch _convertWatch = new();

    private IMFSinkWriter? _writer;
    private int _videoStream = -1;
    private int _audioStream = -1;
    private Thread? _thread;
    private string _outputPath = string.Empty;
    private volatile string _lastMessage = string.Empty;

    private long _videoIndex;
    private long _audioIndex;
    private long _videoDurationHns;
    private long _audioDurationHns;
    private double _audioConvertMsTotal;
    private long _audioConvertCount;
    private int _channels;
    private bool _faulted;
    private bool _mfStarted;

    public MediaFoundationSinkHost(BoundedMediaQueue<VideoFramePacket> videoQueue, BoundedMediaQueue<AudioDataPacket>? audioQueue)
    {
        _videoQueue = videoQueue ?? throw new ArgumentNullException(nameof(videoQueue));
        _audioQueue = audioQueue;
    }

    public event Action<string>? SinkError;

    public double AudioConversionMillisecondsPerChunk =>
        _audioConvertCount > 0 ? _audioConvertMsTotal / _audioConvertCount : 0.0;

    public string DiagnosticTail => _lastMessage;

    public bool Start(RecordingConfig config, StreamFormat format, string outputPath)
    {
        _outputPath = outputPath;

        try
        {
            StartMediaFoundation();
            _mfStarted = true;

            bool fragmented = true;   // mandatory - see the sink's remarks
            using var creationAttrs = MediaFactory.MFCreateAttributes(1);
            if (fragmented)
            {
                creationAttrs.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Fmpeg4);
            }

            _writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, creationAttrs);

            // ---- AUDIO STREAM FIRST (mandatory) -------------------------------------------------
            if (_audioQueue != null && format.Channels > 0 && format.SampleRate > 0)
            {
                _channels = format.Channels;
                uint rate = (uint)format.SampleRate;
                uint channels = (uint)format.Channels;

                using (var audioOut = MediaFactory.MFCreateMediaType())
                {
                    audioOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    audioOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                    audioOut.Set(MediaTypeAttributeKeys.AudioNumChannels, channels);
                    audioOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, rate);
                    audioOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    _audioStream = _writer.AddStream(audioOut);
                }

                using (var audioIn = MediaFactory.MFCreateMediaType())
                {
                    // PCM16, NOT float32: the AAC encoder refuses float (measured, MF_E_INVALIDMEDIATYPE).
                    audioIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    audioIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                    audioIn.Set(MediaTypeAttributeKeys.AudioNumChannels, channels);
                    audioIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, rate);
                    audioIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    audioIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, channels * 2u);
                    audioIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, rate * channels * 2u);
                    _writer.SetInputMediaType(_audioStream, audioIn, null);
                }

                _audioDurationHns = 10_000_000L * 480 / format.SampleRate;   // 480 samples = 10 ms @48k
            }

            // ---- VIDEO STREAM (ARGB32 straight in - no conversion) ------------------------------
            uint fpsNum = (uint)Math.Max(1, Math.Round(format.FrameRate * 1000.0));
            const uint FpsDen = 1000;
            uint width = (uint)format.Width;
            uint height = (uint)format.Height;

            using (var videoOut = MediaFactory.MFCreateMediaType())
            {
                videoOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                videoOut.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                videoOut.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | height);
                videoOut.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)fpsNum << 32) | FpsDen);
                videoOut.Set(MediaTypeAttributeKeys.AvgBitrate, ResolveBitrate(config, format));
                videoOut.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);          // progressive
                videoOut.Set(MediaTypeAttributeKeys.PixelAspectRatio, ((ulong)1 << 32) | 1u);
                _videoStream = _writer.AddStream(videoOut);
            }

            using (var videoIn = MediaFactory.MFCreateMediaType())
            {
                videoIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                videoIn.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
                videoIn.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | height);
                videoIn.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)fpsNum << 32) | FpsDen);
                videoIn.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
                videoIn.Set(MediaTypeAttributeKeys.PixelAspectRatio, ((ulong)1 << 32) | 1u);
                _writer.SetInputMediaType(_videoStream, videoIn, null);
            }

            _videoDurationHns = (long)(10_000_000.0 * FpsDen / fpsNum);

            _writer.BeginWriting();
        }
        catch (Exception ex)
        {
            // FAIL CLOSED. Nothing has been written yet and we do not start the drain thread, so no
            // partially-configured writer can emit a file whose streams do not match their tags.
            _lastMessage = ex.Message;
            AppLogger.LogError($"[MF Sink] setup failed, refusing the take: {ex.Message}", ex);
            SinkError?.Invoke($"Media Foundation could not start this take: {ex.Message}");
            DisposeWriter();
            return false;
        }

        _thread = new Thread(DrainLoop)
        {
            IsBackground = true,
            Name = "mf-sink-writer"
        };
        _thread.Start();
        return true;
    }

    private static uint ResolveBitrate(RecordingConfig config, StreamFormat format)
    {
        // Scale a 1080p60 baseline by the actual pixel rate, so a 4K source is not starved and a
        // small source is not inflated. Deliberate, simple, and documented rather than magic.
        double baseline = 20_000_000.0;
        double baselinePixels = 1920.0 * 1080.0 * 60.0;
        double actualPixels = Math.Max(1.0, format.Width * (double)format.Height * Math.Max(1.0, format.FrameRate));
        double scaled = baseline * (actualPixels / baselinePixels);

        scaled *= config.Quality switch
        {
            QualityPreset.Ultra => 1.5,
            QualityPreset.High => 1.0,
            QualityPreset.Medium => 0.65,
            QualityPreset.Low => 0.4,
            _ => 1.0
        };

        if (scaled < 1_000_000.0) scaled = 1_000_000.0;
        if (scaled > 120_000_000.0) scaled = 120_000_000.0;
        return (uint)scaled;
    }

    private void DrainLoop()
    {
        try
        {
            while (true)
            {
                bool didWork = false;

                while (_videoQueue.TryDequeue(out var video))
                {
                    try { WriteVideoSample(video); }
                    finally { video.Release(); }
                    didWork = true;
                }

                if (_audioQueue != null)
                {
                    while (_audioQueue.TryDequeue(out var audio))
                    {
                        try { WriteAudioSample(audio); }
                        finally { audio.Release(); }
                        didWork = true;
                    }
                }

                if (_videoQueue.IsAddingCompleted && (_audioQueue == null || _audioQueue.IsAddingCompleted))
                {
                    break;
                }

                if (!didWork)
                {
                    _videoQueue.Wait(5);
                }
            }
        }
        catch (Exception ex)
        {
            _faulted = true;
            _lastMessage = ex.Message;
            AppLogger.LogError($"[MF Sink] writer thread failed: {ex.Message}", ex);
            SinkError?.Invoke($"Media Foundation recording failed: {ex.Message}");
        }
    }

    private void WriteVideoSample(in VideoFramePacket packet)
    {
        var writer = _writer;
        if (writer == null) return;

        using var buffer = MediaFactory.MFCreateMemoryBuffer(packet.Length);
        buffer.Lock(out IntPtr dest, out _, out _);
        Marshal.Copy(packet.Buffer, 0, dest, packet.Length);
        buffer.Unlock();
        buffer.CurrentLength = packet.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = _videoIndex * _videoDurationHns;
        sample.SampleDuration = _videoDurationHns;
        writer.WriteSample(_videoStream, sample);

        _videoIndex++;
    }

    private void WriteAudioSample(in AudioDataPacket packet)
    {
        var writer = _writer;
        if (writer == null || _audioStream < 0) return;

        // The app hands us interleaved 32-bit float PCM; the AAC encoder wants PCM16 (measured).
        _convertWatch.Restart();
        int samples = packet.Length / 4;                       // 4 bytes per float sample
        int outBytes = samples * 2;                            // 2 bytes per PCM16 sample
        byte[] converted = MediaBufferPool.Audio.Rent(outBytes);
        try
        {
            for (int i = 0; i < samples; i++)
            {
                int b = i * 4;
                float v = BitConverter.ToSingle(packet.Buffer, b);
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                int s = (int)(v * 32767f);
                converted[i * 2] = (byte)(s & 0xFF);
                converted[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            _convertWatch.Stop();
            _audioConvertMsTotal += _convertWatch.Elapsed.TotalMilliseconds;
            _audioConvertCount++;

            using var buffer = MediaFactory.MFCreateMemoryBuffer(outBytes);
            buffer.Lock(out IntPtr dest, out _, out _);
            Marshal.Copy(converted, 0, dest, outBytes);
            buffer.Unlock();
            buffer.CurrentLength = outBytes;

            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = _audioIndex * _audioDurationHns;
            sample.SampleDuration = _audioDurationHns;
            writer.WriteSample(_audioStream, sample);

            _audioIndex++;
        }
        finally
        {
            MediaBufferPool.Audio.Return(converted);
        }
    }

    /// <summary>Signals the writer to stop, finalizes the container, and returns the bytes on disk.</summary>
    public long Stop()
    {
        var thread = _thread;
        if (thread != null)
        {
            // Bounded wait: a wedged encoder must not hang the take's stop path forever.
            if (!thread.Join(TimeSpan.FromSeconds(20)))
            {
                _lastMessage = "the Media Foundation writer thread did not stop within 20s.";
                AppLogger.LogError($"[MF Sink] {_lastMessage}");
            }
            _thread = null;
        }

        // Drain anything the loop did not get to, so the ledger stays balanced exactly-once.
        while (_videoQueue.TryDequeue(out var video)) video.Release();
        if (_audioQueue != null)
        {
            while (_audioQueue.TryDequeue(out var audio)) audio.Release();
        }

        long bytes = 0;
        try
        {
            if (_writer != null && !_faulted)
            {
                _writer.Finalize();
            }
            bytes = File.Exists(_outputPath) ? new FileInfo(_outputPath).Length : 0;
        }
        catch (Exception ex)
        {
            _lastMessage = ex.Message;
            AppLogger.LogError($"[MF Sink] finalize failed: {ex.Message}", ex);
        }
        finally
        {
            DisposeWriter();
        }

        return bytes;
    }

    private static void StartMediaFoundation()
    {
        lock (StartupLock)
        {
            if (_startupRefCount == 0)
            {
                MediaFactory.MFStartup(false);
            }
            _startupRefCount++;
        }
    }

    private static void StopMediaFoundation()
    {
        lock (StartupLock)
        {
            if (_startupRefCount > 0)
            {
                _startupRefCount--;
                if (_startupRefCount == 0)
                {
                    try { MediaFactory.MFShutdown(); }
                    catch { /* shutdown is best-effort; never mask a real failure */ }
                }
            }
        }
    }

    private void DisposeWriter()
    {
        try { _writer?.Dispose(); }
        catch { }
        _writer = null;

        // Exactly once per successful startup: Stop() and Dispose() can both reach here.
        if (_mfStarted)
        {
            _mfStarted = false;
            StopMediaFoundation();
        }
    }

    public void Dispose()
    {
        var thread = _thread;
        if (thread != null)
        {
            try { thread.Join(TimeSpan.FromSeconds(5)); } catch { }
            _thread = null;
        }

        while (_videoQueue.TryDequeue(out var video)) video.Release();
        if (_audioQueue != null)
        {
            while (_audioQueue.TryDequeue(out var audio)) audio.Release();
        }

        DisposeWriter();
        SinkError = null;
        GC.SuppressFinalize(this);
    }
}
