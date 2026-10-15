using System.Diagnostics;
using libomtnet;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

public class OmtReceiverService : IDisposable
{
    private const int DisconnectJoinTimeoutMs = 1000;

    private OMTReceive? _receiver;
    private Thread? _receiveThread;
    private volatile bool _isRunning;
    private string? _currentAddress;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsConnected => _receiver != null && _receiver.IsConnected();
    public string? CurrentAddress => _currentAddress;

    // Events
    public event Action<IntPtr, int, int, int, int, double, long>? VideoFrameReceived;
    public event Action<IntPtr, int, int, int, long>? AudioFrameReceived;
    public event Action<StreamTelemetry>? TelemetryUpdated;
    public event Action<string>? ConnectionStatusChanged;
    public event Action<string>? ErrorOccurred;

    public void Connect(string address)
    {
        Disconnect();

        OMTReceive? receiver = null;
        bool threadStarted = false;
        try
        {
            _currentAddress = address;
            ConnectionStatusChanged?.Invoke($"Connecting to {address}...");

            // Prefer BGRA for direct WPF WriteableBitmap rendering and FFmpeg rawvideo input
            receiver = new OMTReceive(
                address,
                OMTFrameType.Video | OMTFrameType.Audio,
                OMTPreferredVideoFormat.BGRA,
                OMTReceiveFlags.None
            );

            lock (_lock)
            {
                _receiver = receiver;
                _isRunning = true;

                var thread = new Thread(() => ReceiveLoop(receiver))
                {
                    Name = "OMT_ReceiveThread",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _receiveThread = thread;
                thread.Start();
                threadStarted = true;
            }

            ConnectionStatusChanged?.Invoke($"Connected to {address}");
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _isRunning = false;
                _receiver = null;
                _receiveThread = null;
            }
            _currentAddress = null;

            // The receive loop owns disposal of the receiver it runs. If it never started,
            // dispose here so we don't leak the native handle.
            if (!threadStarted)
            {
                try { receiver?.Dispose(); } catch { }
            }

            ConnectionStatusChanged?.Invoke($"Connection Failed: {ex.Message}");
            ErrorOccurred?.Invoke($"Failed to connect to {address}: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        Thread? thread;
        lock (_lock)
        {
            _isRunning = false;
            thread = _receiveThread;
            _receiveThread = null;
            _receiver = null; // signals the running loop to exit; the loop disposes its own instance
            _currentAddress = null;
        }

        // Bounded wait. We deliberately do NOT dispose the receiver here: if the join times
        // out the loop is still using it, and the loop's finally disposes it once it exits.
        if (thread != null && thread.IsAlive)
        {
            thread.Join(DisconnectJoinTimeoutMs);
        }

        ConnectionStatusChanged?.Invoke("Disconnected");
    }

    private void ReceiveLoop(OMTReceive receiver)
    {
        OMTMediaFrame frame = new();
        DateTime lastStatsTime = DateTime.UtcNow;
        long videoCount = 0;
        long audioCount = 0;
        int lastWidth = 0;
        int lastHeight = 0;
        double lastFps = 0;
        int lastChannels = 0;
        int lastSampleRate = 0;

        try
        {
            // Exit when asked to stop OR when this thread's receiver is no longer the current
            // one (a reconnect swapped it out). The identity check guarantees two loops never
            // share a receiver, which the old shared-flag design allowed.
            while (_isRunning && ReferenceEquals(_receiver, receiver))
            {
                try
                {
                    // Receive Video or Audio with a 50ms timeout
                    if (receiver.Receive(OMTFrameType.Video | OMTFrameType.Audio, 50, ref frame))
                    {
                        if (frame.Type == OMTFrameType.Video && frame.Data != IntPtr.Zero && frame.DataLength > 0)
                        {
                            videoCount++;
                            lastWidth = frame.Width;
                            lastHeight = frame.Height;
                            lastFps = frame.FrameRateD > 0 ? (double)frame.FrameRateN / frame.FrameRateD : 60.0;

                            VideoFrameReceived?.Invoke(
                                frame.Data,
                                frame.DataLength,
                                frame.Width,
                                frame.Height,
                                frame.Stride,
                                lastFps,
                                frame.Timestamp
                            );
                        }
                        else if (frame.Type == OMTFrameType.Audio && frame.Data != IntPtr.Zero && frame.DataLength > 0)
                        {
                            audioCount++;
                            lastChannels = frame.Channels;
                            lastSampleRate = frame.SampleRate;

                            AudioFrameReceived?.Invoke(
                                frame.Data,
                                frame.Channels,
                                frame.SamplesPerChannel,
                                frame.SampleRate,
                                frame.Timestamp
                            );
                        }
                    }

                    // Update Telemetry once per second
                    if ((DateTime.UtcNow - lastStatsTime).TotalMilliseconds >= 1000)
                    {
                        lastStatsTime = DateTime.UtcNow;

                        var vStats = receiver.GetVideoStatistics();
                        var aStats = receiver.GetAudioStatistics();

                        double mbps = (vStats.BytesReceivedSinceLast + aStats.BytesReceivedSinceLast) * 8.0 / 1_000_000.0;

                        var telemetry = new StreamTelemetry
                        {
                            IsConnected = receiver.IsConnected(),
                            Width = lastWidth,
                            Height = lastHeight,
                            FrameRate = lastFps,
                            VideoFormat = "BGRA 8-bit",
                            AudioChannels = lastChannels,
                            SampleRate = lastSampleRate,
                            VideoFramesReceived = videoCount,
                            AudioFramesReceived = audioCount,
                            FramesDropped = vStats.FramesDropped,
                            BitrateMbps = mbps
                        };

                        TelemetryUpdated?.Invoke(telemetry);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OMT Receive Loop Error] {ex.Message}");
                }
            }
        }
        finally
        {
            // The loop owns the receiver it was handed: dispose exactly once, after last use.
            try { receiver.Dispose(); } catch { }
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
        ConnectionStatusChanged = null;
        ErrorOccurred = null;

        GC.SuppressFinalize(this);
    }
}
