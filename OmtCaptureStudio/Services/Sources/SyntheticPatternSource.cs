using System.Diagnostics;
using System.Runtime.InteropServices;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;

namespace OmtCaptureStudio.Services.Sources;

/// <summary>
/// Synthetic media source generating standard SMPTE color bars and dual-frequency stereo audio
/// directly in-process with zero network socket delays.
/// </summary>
public class SyntheticPatternSource : IMediaSource
{
    private Thread? _thread;
    private volatile bool _isRunning;
    private readonly int _width = 1920;
    private readonly int _height = 1080;
    private readonly RationalFrameRate _frameRate;
    private readonly int _sampleRate = 48000;
    private readonly int _channels = 2;
    private bool _disposed;

    public SyntheticPatternSource(RationalFrameRate? frameRate = null)
    {
        _frameRate = frameRate is { IsValid: true } explicitRate
            ? explicitRate
            : RationalFrameRate.FromInteger(60);
    }

    public string Name => "Local Synthetic Test Pattern";
    public string Address => "internal://test-pattern";
    public bool IsConnected => _isRunning;

    public event Action<IntPtr, int, int, int, int, double, long>? VideoFrameReceived;
    public event Action<IntPtr, int, int, int, long>? AudioFrameReceived;
    public event Action<StreamTelemetry>? TelemetryUpdated;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    public void Connect()
    {
        if (_disposed || _isRunning) return;

        _isRunning = true;
        StatusChanged?.Invoke("Connected to Internal Test Pattern Generator");

        _thread = new Thread(GenerateLoop)
        {
            Name = "Synthetic_Pattern_Thread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public void Disconnect()
    {
        _isRunning = false;
        var thread = _thread;
        if (thread != null && thread.IsAlive)
        {
            // Wait for the loop to observe _isRunning == false and fully unwind
            // (free GCHandles, return from GenerateLoop) before considering it
            // dead. A single Join(300) can time out under load while the old
            // loop is still finishing a frame; nulling _thread regardless would
            // let a rapid reconnect start a SECOND thread that re-pins fresh
            // video/audio buffers while the old one is still exiting.
            // Retry Join in small increments up to a bounded budget. The loop
            // always exits on _isRunning == false, so this budget is only a
            // safety upper bound.
            const int joinIncrementMs = 50;
            const int joinBudgetMs = 5000;
            int retries = joinBudgetMs / joinIncrementMs;
            while (thread.IsAlive && retries-- > 0)
            {
                thread.Join(joinIncrementMs);
            }
        }
        _thread = null;
        StatusChanged?.Invoke("Disconnected");
    }

    private unsafe void GenerateLoop()
    {
        int stride = _width * 4;
        int videoBufferBytes = stride * _height;
        byte[] videoBuffer = new byte[videoBufferBytes];
        GCHandle videoHandle = GCHandle.Alloc(videoBuffer, GCHandleType.Pinned);
        IntPtr pVideo = videoHandle.AddrOfPinnedObject();

        int maxSamples = Math.Max(1, _frameRate.MaxSamplesPerFrame(_sampleRate));
        float[] audioBuffer = new float[_channels * maxSamples];
        GCHandle audioHandle = GCHandle.Alloc(audioBuffer, GCHandleType.Pinned);
        IntPtr pAudio = audioHandle.AddrOfPinnedObject();

        double audioPhaseL = 0;
        double audioPhaseR = 0;
        double freqL = 440.0;
        double freqR = 880.0;

        uint[] colors = new uint[]
        {
            0xFFFFFFFF, // White
            0xFF00FFFF, // Yellow
            0xFFFFFF00, // Cyan
            0xFF00FF00, // Green
            0xFFFF00FF, // Magenta
            0xFF0000FF, // Red
            0xFFFF0000, // Blue
            0xFF000000  // Black
        };

        var stopwatch = Stopwatch.StartNew();
        using var pacer = new FramePacer(_frameRate);

        DateTime lastTelemetryTime = DateTime.UtcNow;

        try
        {
            while (_isRunning)
            {
                long frameIndex = pacer.WaitNext();
                int samplesPerFrame = _frameRate.SamplesForFrame(frameIndex, _sampleRate);
                if (samplesPerFrame < 1) samplesPerFrame = 1;
                if (samplesPerFrame > maxSamples) samplesPerFrame = maxSamples;
                long timestampUs = stopwatch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

                // 1. Render SMPTE Color Bars
                fixed (byte* pBuf = videoBuffer)
                {
                    uint* pPixels = (uint*)pBuf;
                    int barWidth = _width / 8;

                    for (int y = 0; y < _height; y++)
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            int barIdx = Math.Min(x / barWidth, 7);
                            pPixels[y * _width + x] = colors[barIdx];
                        }
                    }

                    // Draw moving horizontal indicator bar at bottom
                    int tickerY = _height - 60;
                    int tickerX = (int)((frameIndex * 8) % _width);
                    for (int ty = tickerY; ty < tickerY + 40 && ty < _height; ty++)
                    {
                        for (int tx = tickerX; tx < tickerX + 50 && tx < _width; tx++)
                        {
                            pPixels[ty * _width + tx] = 0xFFFFFFFF;
                        }
                    }
                }

                VideoFrameReceived?.Invoke(pVideo, videoBufferBytes, _width, _height, stride, _frameRate.FramesPerSecond, timestampUs);

                // 2. Synthesize Stereo Audio Sine Waves
                for (int s = 0; s < samplesPerFrame; s++)
                {
                    float sampleL = (float)(Math.Sin(audioPhaseL) * 0.3);
                    float sampleR = (float)(Math.Sin(audioPhaseR) * 0.3);

                    audioPhaseL += 2.0 * Math.PI * freqL / _sampleRate;
                    if (audioPhaseL > 2.0 * Math.PI) audioPhaseL -= 2.0 * Math.PI;

                    audioPhaseR += 2.0 * Math.PI * freqR / _sampleRate;
                    if (audioPhaseR > 2.0 * Math.PI) audioPhaseR -= 2.0 * Math.PI;

                    // Planar: Left channel in first half, Right channel in second half
                    audioBuffer[s] = sampleL;
                    audioBuffer[samplesPerFrame + s] = sampleR;
                }

                AudioFrameReceived?.Invoke(pAudio, _channels, samplesPerFrame, _sampleRate, timestampUs);

                // 3. Telemetry once per second
                if ((DateTime.UtcNow - lastTelemetryTime).TotalMilliseconds >= 1000)
                {
                    lastTelemetryTime = DateTime.UtcNow;
                    TelemetryUpdated?.Invoke(new StreamTelemetry
                    {
                        IsConnected = true,
                        Width = _width,
                        Height = _height,
                        FrameRate = _frameRate.FramesPerSecond,
                        VideoFormat = "BGRA 8-bit",
                        AudioChannels = _channels,
                        SampleRate = _sampleRate,
                        BitrateMbps = (videoBufferBytes * _frameRate.FramesPerSecond * 8.0) / 1_000_000.0,
                        VideoFramesReceived = frameIndex,
                        AudioFramesReceived = frameIndex,
                        FramesDropped = 0
                    });
                }

            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
        finally
        {
            videoHandle.Free();
            audioHandle.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Disconnect();
        VideoFrameReceived = null;
        AudioFrameReceived = null;
        TelemetryUpdated = null;
        StatusChanged = null;
        ErrorOccurred = null;
        GC.SuppressFinalize(this);
    }
}
