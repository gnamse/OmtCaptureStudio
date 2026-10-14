using System.Diagnostics;
using System.Threading;
using libomtnet;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Broadcasts a host capture target (a monitor or a window — any <see cref="IFrameSource"/>)
/// over OMT by sending each captured frame through <see cref="OMTSend"/>. Mirrors
/// <see cref="OmtTestPatternGenerator"/>'s paced send loop, but sources real screen content.
/// Video only (audio is a later phase).
/// </summary>
public sealed class OmtDesktopCaptureGenerator : IDisposable
{
    private const int StopJoinTimeoutMs = 1000;

    private readonly string _sourceName;
    private readonly int _fps;
    private readonly IFrameSource _frameSource;
    private readonly object _lock = new();

    private OMTSend? _sender;
    private IFrameSource? _capturer;
    private Thread? _sendThread;
    private volatile bool _isRunning;
    private bool _started;
    private bool _disposed;

    public bool IsRunning => _isRunning;
    public string SourceName => _sourceName;
    public string? Url => _sender?.URL;
    public string? Address => _sender?.Address;
    public int Port => _sender?.Port ?? 0;

    public OmtDesktopCaptureGenerator(IFrameSource frameSource, string sourceName = "OmtCaptureStudio_DesktopCapture", int fps = 30)
    {
        _frameSource = frameSource;
        _sourceName = sourceName;
        _fps = fps;
    }

    public void Start()
    {
        if (_isRunning || _disposed) return;
        _started = true;

        IFrameSource source = _frameSource;
        OMTSend? sender = null;
        bool threadStarted = false;
        try
        {
            sender = new OMTSend(_sourceName, OMTQuality.High);

            lock (_lock)
            {
                _capturer = source;
                _sender = sender;
                _isRunning = true;

                var thread = new Thread(() => SendLoop(source, sender))
                {
                    Name = "OMT_DesktopCaptureThread",
                    IsBackground = true
                };
                _sendThread = thread;
                thread.Start();
                threadStarted = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Desktop Capture Start Error] {ex.Message}");
            lock (_lock)
            {
                _isRunning = false;
                _capturer = null;
                _sender = null;
                _sendThread = null;
            }

            // The send loop owns disposal of the source + sender it runs. If it never
            // started, dispose here so we don't leak the native handles.
            if (!threadStarted)
            {
                try { sender?.Dispose(); } catch { }
                try { source.Dispose(); } catch { }
            }

            throw;
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lock)
        {
            _isRunning = false;
            thread = _sendThread;
            _sendThread = null;
            _capturer = null; // identity mismatch -> the loop exits and disposes its own source/sender
            _sender = null;
        }

        // Bounded wait. We do NOT dispose here: if the join times out the loop is still
        // using the source/sender, and the loop's finally disposes them once it exits.
        if (thread != null && thread.IsAlive)
        {
            thread.Join(StopJoinTimeoutMs);
        }
    }

    private void SendLoop(IFrameSource capturer, OMTSend sender)
    {
        Stopwatch sw = Stopwatch.StartNew();
        long frameIndex = 0;
        bool hasFrame = false;

        try
        {
            while (_isRunning && ReferenceEquals(_capturer, capturer) && ReferenceEquals(_sender, sender))
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
                if (capturer.TryCaptureFrame())
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
                    Width = capturer.Width,
                    Height = capturer.Height,
                    Stride = capturer.Stride,
                    FrameRateN = _fps,
                    FrameRateD = 1,
                    Data = capturer.BufferPtr,
                    DataLength = capturer.Stride * capturer.Height,
                    Timestamp = -1 // automatic timestamp throttling
                };
                sender.Send(vFrame);
            }
        }
        finally
        {
            // The loop owns the source and sender it was handed: dispose exactly once.
            try { capturer.Dispose(); } catch { }
            try { sender.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (!_started)
        {
            try { _frameSource.Dispose(); } catch { }
        }
        GC.SuppressFinalize(this);
    }
}
