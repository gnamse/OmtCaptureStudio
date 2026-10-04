using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Sinks;
using OmtCaptureStudio.Services.Sources;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Headless capture session engine coordinating polymorphic media sources, audio routing,
/// stream format negotiation, telemetry aggregation, and recording orchestration.
/// Decoupled entirely from presentation layers (WPF).
/// </summary>
public class CaptureSession : IDisposable
{
    private IMediaSource? _currentSource;
    private readonly AudioEngine _audio;
    private readonly StreamRecorderService _recorder;
    private readonly bool _ownsDependencies;
    private readonly object _stateLock = new();

    public StreamFormat CurrentFormat { get; private set; } = StreamFormat.Empty;
    public bool IsConnected => _currentSource != null && _currentSource.IsConnected;
    public string? CurrentAddress => _currentSource?.Address;
    public string? CurrentSourceName => _currentSource?.Name;
    public bool IsRecording => _recorder.IsRecording;
    public string? CurrentRecordingPath => _recorder.CurrentRecordingPath;
    public TimeSpan RecordingElapsedTime => _recorder.ElapsedTime;
    public long RecordingFramesWritten => _recorder.FramesWritten;

    public bool IsAudioMonitoringEnabled
    {
        get => _audio.IsMonitoringEnabled;
        set => _audio.IsMonitoringEnabled = value;
    }

    // Media & Telemetry Events
    public event Action<IntPtr, int, int, int, int, double, long>? VideoFrameAvailable;
    public event Action<AudioLevelData>? AudioLevelsUpdated;
    public event Action<StreamTelemetry>? TelemetryUpdated;
    public event Action<StreamFormat>? FormatChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    // Recording Events
    public event Action<string>? RecordingStarted;
    public event Action<string, TimeSpan, long>? RecordingStopped;
    public event Action<string>? RecordingError;

    /// <summary>
    /// Default constructor initializing the production engine stack.
    /// </summary>
    public CaptureSession()
        : this(new AudioEngine(), new StreamRecorderService(), ownsDependencies: true)
    {
    }

    /// <summary>
    /// Constructor supporting dependency injection of custom audio engines and recording sinks.
    /// </summary>
    public CaptureSession(
        AudioEngine audio,
        StreamRecorderService recorder,
        bool ownsDependencies = false)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _ownsDependencies = ownsDependencies;

        HookInternalAudioAndRecorder();
    }

    private void HookInternalAudioAndRecorder()
    {
        // Audio Engine Taps
        _audio.LevelsCalculated += levels =>
        {
            AudioLevelsUpdated?.Invoke(levels);
        };

        _audio.AudioInterleavedAvailable += (buffer, count) =>
        {
            if (_recorder.IsRecording)
            {
                _recorder.WriteAudioData(buffer, 0, count);
            }
        };

        // Recorder Events Forwarding
        _recorder.RecordingStarted += path => RecordingStarted?.Invoke(path);
        _recorder.RecordingStopped += (path, dur, sz) => RecordingStopped?.Invoke(path, dur, sz);
        _recorder.RecordingError += err => RecordingError?.Invoke(err);
    }

    /// <summary>
    /// Connects to an OMT network source address.
    /// </summary>
    public void Connect(string address)
    {
        Connect(new OmtNetworkSource(address));
    }

    /// <summary>
    /// Polymorphically connects to any media source (network OMT stream or synthetic pattern generator).
    /// </summary>
    public void Connect(IMediaSource source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        Disconnect();

        lock (_stateLock)
        {
            CurrentFormat = StreamFormat.Empty;
            _currentSource = source;

            _currentSource.VideoFrameReceived += OnSourceVideoFrameReceived;
            _currentSource.AudioFrameReceived += OnSourceAudioFrameReceived;
            _currentSource.TelemetryUpdated += t => TelemetryUpdated?.Invoke(t);
            _currentSource.StatusChanged += s => StatusChanged?.Invoke(s);
            _currentSource.ErrorOccurred += err => { AppLogger.LogError($"Media source error: {err}"); ErrorOccurred?.Invoke(err); };

            AppLogger.LogInfo($"Connecting to media source: {_currentSource.Name ?? _currentSource.Address}");
            _currentSource.Connect();
        }
    }

    private void OnSourceVideoFrameReceived(IntPtr pData, int length, int width, int height, int stride, double fps, long timestamp)
    {
        lock (_stateLock)
        {
            if (CurrentFormat.Width != width || CurrentFormat.Height != height || Math.Abs(CurrentFormat.FrameRate - fps) > 0.05)
            {
                CurrentFormat = CurrentFormat with
                {
                    Width = width,
                    Height = height,
                    FrameRate = fps
                };
                FormatChanged?.Invoke(CurrentFormat);
            }
        }

        // 1. Notify preview consumers (GUI renderer) immediately with highest priority
        VideoFrameAvailable?.Invoke(pData, length, width, height, stride, fps, timestamp);

        // 2. Forward to recorder if active
        if (_recorder.IsRecording)
        {
            _recorder.WriteVideoFrame(pData, length);
        }
    }

    private void OnSourceAudioFrameReceived(IntPtr pPlanarData, int channels, int samples, int rate, long timestamp)
    {
        lock (_stateLock)
        {
            if (CurrentFormat.Channels != channels || CurrentFormat.SampleRate != rate)
            {
                CurrentFormat = CurrentFormat with
                {
                    Channels = channels,
                    SampleRate = rate
                };
                FormatChanged?.Invoke(CurrentFormat);
            }
        }

        // Ingest into AudioEngine (metering, monitoring, interleaving)
        _audio.IngestAudioFrame(pPlanarData, channels, samples, rate, timestamp);
    }

    /// <summary>
    /// Disconnects the active media source and stops recording if active.
    /// </summary>
    public void Disconnect()
    {
        if (_recorder.IsRecording)
        {
            _recorder.StopRecording();
        }

        lock (_stateLock)
        {
            if (_currentSource != null)
            {
                try
                {
                    AppLogger.LogInfo("Disconnecting current media source.");
                    _currentSource.Disconnect();
                    _currentSource.Dispose();
                }
                catch (Exception ex) { AppLogger.LogError("Error disconnecting media source", ex); }
                _currentSource = null;
            }

            CurrentFormat = StreamFormat.Empty;
        }
    }

    /// <summary>
    /// Disconnects the active media source asynchronously and stops recording if active.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_recorder.IsRecording)
        {
            await _recorder.StopRecordingAsync();
        }

        Disconnect();
    }

    /// <summary>
    /// Starts recording using the active stream format parameters automatically.
    /// </summary>
    public bool StartRecording(RecordingConfig config)
    {
        if (!IsConnected)
        {
            RecordingError?.Invoke("Cannot record: Media source is not connected.");
            return false;
        }

        StreamFormat fmt;
        lock (_stateLock)
        {
            fmt = CurrentFormat;
        }

        if (!fmt.HasVideo)
        {
            RecordingError?.Invoke("Cannot record: No video stream format detected yet.");
            return false;
        }

        return _recorder.StartRecording(config, fmt);
    }

    /// <summary>
    /// Stops the active recording gracefully.
    /// </summary>
    public void StopRecording()
    {
        _recorder.StopRecording();
    }

    /// <summary>
    /// Stops the active recording asynchronously.
    /// </summary>
    public Task StopRecordingAsync()
    {
        return _recorder.StopRecordingAsync();
    }

    public void Dispose()
    {
        Disconnect();

        if (_ownsDependencies)
        {
            _audio.Dispose();
            _recorder.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
