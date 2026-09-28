using System.Diagnostics;
using System.Runtime.InteropServices;
using libomtnet;

namespace OmtCaptureStudio.Services;

public class OmtTestPatternGenerator : IDisposable
{
    private OMTSend? _sender;
    private Thread? _sendThread;
    private volatile bool _isRunning;
    private readonly string _sourceName;
    private readonly int _width = 1920;
    private readonly int _height = 1080;
    private readonly int _fps = 60;
    private readonly int _sampleRate = 48000;
    private readonly int _channels = 2;

    public bool IsRunning => _isRunning;
    public string SourceName => _sourceName;
    public string? Url => _sender?.URL;
    public string? Address => _sender?.Address;
    public int Port => _sender?.Port ?? 0;

    public OmtTestPatternGenerator(string sourceName = "OmtCaptureStudio_TestPattern")
    {
        _sourceName = sourceName;
    }

    public void Start()
    {
        if (_isRunning) return;

        try
        {
            _sender = new OMTSend(_sourceName, OMTQuality.High);
            _isRunning = true;

            _sendThread = new Thread(SendLoop)
            {
                Name = "OMT_TestPatternThread",
                IsBackground = true
            };
            _sendThread.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Test Pattern Start Error] {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        _isRunning = false;

        if (_sendThread != null && _sendThread.IsAlive)
        {
            _sendThread.Join(500);
            _sendThread = null;
        }

        if (_sender != null)
        {
            try
            {
                _sender.Dispose();
            }
            catch { }
            _sender = null;
        }
    }

    private unsafe void SendLoop()
    {
        int stride = _width * 4;
        int videoBufferBytes = stride * _height;
        byte[] videoBuffer = new byte[videoBufferBytes];
        GCHandle videoHandle = GCHandle.Alloc(videoBuffer, GCHandleType.Pinned);
        IntPtr pVideo = videoHandle.AddrOfPinnedObject();

        // 48000 Hz / 60 fps = 800 samples per channel per frame
        int samplesPerFrame = _sampleRate / _fps;
        int audioSamplesTotal = _channels * samplesPerFrame;
        float[] audioBuffer = new float[audioSamplesTotal];
        GCHandle audioHandle = GCHandle.Alloc(audioBuffer, GCHandleType.Pinned);
        IntPtr pAudio = audioHandle.AddrOfPinnedObject();

        long frameIndex = 0;
        double audioPhaseL = 0;
        double audioPhaseR = 0;
        double freqL = 440.0;
        double freqR = 880.0;

        // Standard 8 SMPTE Colors (BGRA: B, G, R, A)
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

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            while (_isRunning && _sender != null)
            {
                long targetTimeMs = frameIndex * 1000 / _fps;
                long elapsedMs = sw.ElapsedMilliseconds;

                if (elapsedMs < targetTimeMs)
                {
                    int delay = (int)(targetTimeMs - elapsedMs);
                    if (delay > 0) Thread.Sleep(delay);
                }

                frameIndex++;

                // 1. Draw SMPTE Color Bars into Video Buffer
                fixed (byte* pBytes = videoBuffer)
                {
                    uint* pPixels = (uint*)pBytes;
                    int barWidth = _width / 8;
                    int motionX = (int)((frameIndex * 8) % _width);

                    for (int y = 0; y < _height; y++)
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            int pixelIdx = y * _width + x;

                            if (y < _height * 0.75)
                            {
                                int barIdx = Math.Min(x / barWidth, 7);
                                pPixels[pixelIdx] = colors[barIdx];
                            }
                            else if (y < _height * 0.85)
                            {
                                // Moving tick indicator
                                bool isMovingTick = Math.Abs(x - motionX) < 12;
                                pPixels[pixelIdx] = isMovingTick ? 0xFFFFFFFF : 0xFF222222;
                            }
                            else
                            {
                                // Gray gradient ramp
                                uint ugray = (uint)((x * 255) / _width);
                                pPixels[pixelIdx] = 0xFF000000u | (ugray << 16) | (ugray << 8) | ugray;
                            }
                        }
                    }
                }

                // 2. Generate Planar Audio (Sine waves at 440 Hz and 880 Hz)
                fixed (float* pAudioSamples = audioBuffer)
                {
                    float* pLeft = pAudioSamples;
                    float* pRight = pAudioSamples + samplesPerFrame;

                    for (int i = 0; i < samplesPerFrame; i++)
                    {
                        pLeft[i] = (float)(Math.Sin(audioPhaseL) * 0.3);
                        pRight[i] = (float)(Math.Sin(audioPhaseR) * 0.3);

                        audioPhaseL += 2.0 * Math.PI * freqL / _sampleRate;
                        audioPhaseR += 2.0 * Math.PI * freqR / _sampleRate;

                        if (audioPhaseL > 2.0 * Math.PI) audioPhaseL -= 2.0 * Math.PI;
                        if (audioPhaseR > 2.0 * Math.PI) audioPhaseR -= 2.0 * Math.PI;
                    }
                }

                // 3. Send Video Frame
                OMTMediaFrame vFrame = new()
                {
                    Type = OMTFrameType.Video,
                    Codec = (int)OMTCodec.BGRA,
                    Width = _width,
                    Height = _height,
                    Stride = stride,
                    FrameRateN = _fps,
                    FrameRateD = 1,
                    Data = pVideo,
                    DataLength = videoBufferBytes,
                    Timestamp = -1 // Automatic timestamp throttling
                };
                _sender.Send(vFrame);

                // 4. Send Audio Frame
                OMTMediaFrame aFrame = new()
                {
                    Type = OMTFrameType.Audio,
                    Codec = (int)OMTCodec.FPA1,
                    Channels = _channels,
                    SampleRate = _sampleRate,
                    SamplesPerChannel = samplesPerFrame,
                    Data = pAudio,
                    DataLength = audioSamplesTotal * sizeof(float),
                    Timestamp = -1
                };
                _sender.Send(aFrame);
            }
        }
        finally
        {
            if (videoHandle.IsAllocated) videoHandle.Free();
            if (audioHandle.IsAllocated) audioHandle.Free();
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
