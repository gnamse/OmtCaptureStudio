using System.Diagnostics;
using libomtnet;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

public class OmtReceiverService : IDisposable
{
    private OMTReceive? _receiver;
    private Thread? _receiveThread;
    private volatile bool _isRunning;
    private string? _currentAddress;
    private readonly object _lock = new();

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

        lock (_lock)
        {
            try
            {
                _currentAddress = address;
                ConnectionStatusChanged?.Invoke($"Connecting to {address}...");

                // Prefer BGRA for direct WPF WriteableBitmap rendering and FFmpeg rawvideo input
                _receiver = new OMTReceive(
                    address,
                    OMTFrameType.Video | OMTFrameType.Audio,
                    OMTPreferredVideoFormat.BGRA,
                    OMTReceiveFlags.None
                );

                _isRunning = true;
                _receiveThread = new Thread(ReceiveLoop)
                {
                    Name = "OMT_ReceiveThread",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _receiveThread.Start();

                ConnectionStatusChanged?.Invoke($"Connected to {address}");
            }
            catch (Exception ex)
            {
                ConnectionStatusChanged?.Invoke($"Connection Failed: {ex.Message}");
                ErrorOccurred?.Invoke($"Failed to connect to {address}: {ex.Message}");
                Disconnect();
            }
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _isRunning = false;

            if (_receiveThread != null && _receiveThread.IsAlive)
            {
                _receiveThread.Join(500);
                _receiveThread = null;
            }

            if (_receiver != null)
            {
                try
                {
                    _receiver.Dispose();
                }
                catch { }
                _receiver = null;
            }

            _currentAddress = null;
            ConnectionStatusChanged?.Invoke("Disconnected");
        }
    }

    private void ReceiveLoop()
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

        while (_isRunning && _receiver != null)
        {
            try
            {
                // Receive Video or Audio with a 50ms timeout
                if (_receiver.Receive(OMTFrameType.Video | OMTFrameType.Audio, 50, ref frame))
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

                    var vStats = _receiver.GetVideoStatistics();
                    var aStats = _receiver.GetAudioStatistics();

                    double mbps = (vStats.BytesReceivedSinceLast + aStats.BytesReceivedSinceLast) * 8.0 / 1_000_000.0;

                    var telemetry = new StreamTelemetry
                    {
                        IsConnected = _receiver.IsConnected(),
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

    public void Dispose()
    {
        Disconnect();
    }
}
