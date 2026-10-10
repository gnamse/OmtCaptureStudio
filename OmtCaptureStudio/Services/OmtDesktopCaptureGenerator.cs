using System.Diagnostics;
using System.Threading;
using libomtnet;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Broadcasts the host desktop over OMT by capturing the primary monitor with
/// <see cref="DesktopFrameCapturer"/> and sending each frame through <see cref="OMTSend"/>.
/// Mirrors <see cref="OmtTestPatternGenerator"/>'s paced send loop, but sources real
/// screen content. Video only (audio is a later phase).
/// </summary>
public sealed class OmtDesktopCaptureGenerator : IDisposable
{
    private readonly string _sourceName;
    private readonly int _fps;

    private OMTSend? _sender;
    private DesktopFrameCapturer? _capturer;
    private Thread? _sendThread;
    private volatile bool _isRunning;

    public bool IsRunning => _isRunning;
    public string SourceName => _sourceName;
    public string? Url => _sender?.URL;
    public string? Address => _sender?.Address;
    public int Port => _sender?.Port ?? 0;

    public OmtDesktopCaptureGenerator(string sourceName = "OmtCaptureStudio_DesktopCapture", int fps = 30)
    {
        _sourceName = sourceName;
        _fps = fps;
    }

    public void Start()
    {
        if (_isRunning) return;
        try
        {
            _capturer = new DesktopFrameCapturer(); // throws if no desktop / no D3D11 hardware device
            _sender = new OMTSend(_sourceName, OMTQuality.High);
            _isRunning = true;

            _sendThread = new Thread(SendLoop)
            {
                Name = "OMT_DesktopCaptureThread",
                IsBackground = true
            };
            _sendThread.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Desktop Capture Start Error] {ex.Message}");
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        _isRunning = false;

        if (_sendThread != null && _sendThread.IsAlive)
        {
            _sendThread.Join(1000);
            _sendThread = null;
        }

        _capturer?.Dispose();
        _capturer = null;

        if (_sender != null)
        {
            try { _sender.Dispose(); } catch { }
            _sender = null;
        }
    }

    private void SendLoop()
    {
        Stopwatch sw = Stopwatch.StartNew();
        long frameIndex = 0;
        bool hasFrame = false;

        while (_isRunning && _sender != null && _capturer != null)
        {
            long targetMs = frameIndex * 1000 / _fps;
            long elapsedMs = sw.ElapsedMilliseconds;
            if (elapsedMs < targetMs)
            {
                int delay = (int)(targetMs - elapsedMs);
                if (delay > 0) Thread.Sleep(delay);
            }
            frameIndex++;

            // Desktop Duplication only yields a frame when the screen changes, so a static
            // desktop would otherwise starve receivers. Once one real frame is captured,
            // keep re-broadcasting the latest buffer to hold the stream at the paced rate.
            if (_capturer.TryCaptureFrame())
            {
                hasFrame = true;
            }
            else if (!hasFrame)
            {
                continue; // no frame captured yet
            }

            OMTMediaFrame vFrame = new()
            {
                Type = OMTFrameType.Video,
                Codec = (int)OMTCodec.BGRA,
                Width = _capturer.Width,
                Height = _capturer.Height,
                Stride = _capturer.Stride,
                FrameRateN = _fps,
                FrameRateD = 1,
                Data = _capturer.BufferPtr,
                DataLength = _capturer.Stride * _capturer.Height,
                Timestamp = -1 // automatic timestamp throttling
            };
            _sender.Send(vFrame);
        }
    }

    public void Dispose() => Stop();
}
