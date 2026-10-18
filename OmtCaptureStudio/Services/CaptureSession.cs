using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Sinks;
using OmtCaptureStudio.Services.Sources;

using OmtCaptureStudio.Services.Forensics;
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
    private readonly FrameRateMeter _fpsMeter = new();
    // The A/V recording clock now lives inside RecordingArm (ADR-0005): the arm must reset it before
    // flipping the recording gate, so owning it here would put that ordering back out of the arm's reach.
    private bool _disposed;

    // ---- ADR-0002 input-loss resilience ----------------------------------------------------------
    private readonly Func<long> _clock;
    private readonly InputHealthWatchdog _inputWatchdog;
    // The ADR-0002 recovery policy now lives in TakeSupervisor (NET-018): CaptureSession keeps the
    // frame dispatch and format negotiation, owns the watchdog instance the frame path notifies, and
    // forwards the health tick. The take state, boundary evaluation, outage boundary, reconnect
    // backoff loop and grace expiry all moved with the supervisor.
    private readonly TakeSupervisor _supervisor;

    private readonly Action<AudioLevelData> _onLevelsCalculated;
    private readonly Action<byte[], int> _onAudioInterleavedAvailable;
    private readonly Action<string> _onRecordingStarted;
    private readonly Action<RecordingStopSummary> _onRecordingStopped;
    private readonly Action<string> _onRecordingError;
    private readonly Action _onInputLost;
    private readonly Action _onInputRecovered;
    private readonly Action _onInputRecoveryFailed;
    private readonly Action<int> _onSegmentRolledOver;
    private readonly Action _onMaxDurationReached;
    private readonly Action _onScheduledStopReached;

    public StreamFormat CurrentFormat { get; private set; } = StreamFormat.Empty;
    public bool IsConnected => _currentSource != null && _currentSource.IsConnected;
    public string? CurrentAddress => _currentSource?.Address;
    public string? CurrentSourceName => _currentSource?.Name;
    public bool IsRecording => _recorder.IsRecording;
    public string? CurrentRecordingPath => _recorder.CurrentRecordingPath;
    public TimeSpan RecordingElapsedTime => _recorder.ElapsedTime;
    public long RecordingFramesWritten => _recorder.FramesWritten;
    public long RecordingFramesDropped => _recorder.VideoFramesDropped;
    public long RecordingAudioChunksDropped => _recorder.AudioChunksDropped;

    /// <summary>1-based segment index of the active take (ADR-0002 segment-per-outage).</summary>
    public int RecordingSegmentIndex => _recorder.CurrentSegmentIndex;

    /// <summary>
    /// Destinations the composite sink detached, in detach order (NET-020). Empty for a
    /// single-destination take. Surfaced for the diagnostics bundle.
    /// </summary>
    public IReadOnlyList<string> DetachedDestinationNames => _recorder.DetachedDestinationNames;

    /// <summary>True while the input-health watchdog is armed (a take is active over a watched source).</summary>
    public bool IsInputHealthWatched => _inputWatchdog.IsArmed;

    /// <summary>True while the watchdog currently reports a frame-absence outage.</summary>
    public bool IsInputLost => _inputWatchdog.IsLost;

    /// <summary>
    /// Aggregates the recording path's health into one snapshot for the ViewModel to project
    /// (FEAT-07). The aggregation lives here, not ad hoc in code-behind; counters are Interlocked
    /// reads (no per-frame work) plus at most one file-size query.
    /// </summary>
    public RecordingHealthSnapshot GetRecordingHealthSnapshot() => new()
    {
        IsRecording = _recorder.IsRecording,
        HasTake = _recorder.CurrentTakeBasePath != null,
        SegmentIndex = _recorder.CurrentSegmentIndex,
        EncoderName = _recorder.ResolvedEncoderName,
        AchievedBitrateMbps = _recorder.AchievedBitrateMbps,
        VideoFramesWritten = _recorder.FramesWritten,
        VideoFramesDropped = _recorder.VideoFramesDropped,
        AudioChunksDropped = _recorder.AudioChunksDropped,
        MediaDuration = _recorder.MediaDuration,
        AudioDuration = _recorder.AudioDuration,
        WallClockDuration = _recorder.IsRecording ? _recorder.ElapsedTime : TimeSpan.Zero,
        AvDriftRatio = _recorder.AvDriftRatio,
        AvDriftClamped = _recorder.AvDriftClamped,
        DiagnosticTail = _recorder.DiagnosticTail
    };

    /// <summary>
    /// Wall-clock window with no video frame that counts as input loss. Operator-configurable;
    /// default 5 s, applied per take from <see cref="RecordingConfig.InputLossThresholdSeconds"/>.
    /// Delegates to <see cref="TakeSupervisor"/> so the recovery policy owns its own thresholds.
    /// </summary>
    public TimeSpan InputLossThreshold
    {
        get => _supervisor.InputLossThreshold;
        set => _supervisor.InputLossThreshold = value;
    }

    /// <summary>
    /// Bounded grace period to keep attempting auto-reconnect before giving up. Default 30 s,
    /// applied per take from <see cref="RecordingConfig.InputRecoveryGraceSeconds"/>.
    /// Delegates to <see cref="TakeSupervisor"/> so the recovery policy owns its own thresholds.
    /// </summary>
    public TimeSpan InputRecoveryGracePeriod
    {
        get => _supervisor.InputRecoveryGracePeriod;
        set => _supervisor.InputRecoveryGracePeriod = value;
    }

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
    public event Action<RecordingStopSummary>? RecordingStopped;
    public event Action<string>? RecordingError;

    // Input-loss resilience events (ADR-0002). Raised on the thread that calls CheckInputHealth()
    // (the UI health tick in production), so subscribers may touch presentation state directly.
    public event Action? InputLost;             // outage detected; take finalized at the boundary
    public event Action? InputRecovered;        // frames returned within grace; next segment resumed
    public event Action? InputRecoveryFailed;   // bounded grace expired; take stopped, alarm persists

    // Scheduled / segmented capture events (NET-008). Raised on the same UI health tick as the ADR-0002
    // events above, so subscribers may touch presentation state directly.
    public event Action<int>? SegmentRolledOver;  // rollover: the take CONTINUES in segment N
    public event Action? MaxDurationReached;      // max-duration auto-stop: the take ended
    public event Action? ScheduledStopReached;    // wall-clock scheduled stop: the take ended

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
        bool ownsDependencies = false,
        Func<long>? healthClock = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _ownsDependencies = ownsDependencies;

        // Injected clock so the headless suite can drive input-loss timing directly (no real sleeps).
        _clock = healthClock ?? (static () => Environment.TickCount64);
        _inputWatchdog = new InputHealthWatchdog(InputHealthWatchdog.DefaultThresholdMs, _clock);

        // The watchdog instance stays here because the frame path notifies it directly on the media
        // thread; the supervisor is handed the same instance so arming and the edge poll stay one
        // object's state (ADR-0002). The source probe is a lambda so a re-Connect is seen live.
        _supervisor = new TakeSupervisor(_recorder, _inputWatchdog, () => _currentSource, _clock);

        _onLevelsCalculated = levels => AudioLevelsUpdated?.Invoke(levels);
        _onAudioInterleavedAvailable = (buffer, count) =>
        {
            // The arm applies the A/V clock and writes the converted bytes under its own gate, so the
            // clock and the transport can never disagree about whether a take is live (ADR-0005).
            _recorder.ProcessAndWriteAudio(buffer, count);
        };
        _onRecordingStarted = path => RecordingStarted?.Invoke(path);
        _onRecordingStopped = summary => RecordingStopped?.Invoke(summary);
        _onRecordingError = err => RecordingError?.Invoke(err);
        _onInputLost = () => InputLost?.Invoke();
        _onInputRecovered = () => InputRecovered?.Invoke();
        _onInputRecoveryFailed = () => InputRecoveryFailed?.Invoke();
        _onSegmentRolledOver = idx => SegmentRolledOver?.Invoke(idx);
        _onMaxDurationReached = () => MaxDurationReached?.Invoke();
        _onScheduledStopReached = () => ScheduledStopReached?.Invoke();

        HookInternalAudioAndRecorder();
    }

    private void HookInternalAudioAndRecorder()
    {
        _audio.LevelsCalculated += _onLevelsCalculated;
        _audio.AudioInterleavedAvailable += _onAudioInterleavedAvailable;
        _recorder.RecordingStarted += _onRecordingStarted;
        _recorder.RecordingStopped += _onRecordingStopped;
        _recorder.RecordingError += _onRecordingError;

        _supervisor.InputLost += _onInputLost;
        _supervisor.InputRecovered += _onInputRecovered;
        _supervisor.InputRecoveryFailed += _onInputRecoveryFailed;
        _supervisor.SegmentRolledOver += _onSegmentRolledOver;
        _supervisor.MaxDurationReached += _onMaxDurationReached;
        _supervisor.ScheduledStopReached += _onScheduledStopReached;
    }

    private void UnhookInternalAudioAndRecorder()
    {
        _audio.LevelsCalculated -= _onLevelsCalculated;
        _audio.AudioInterleavedAvailable -= _onAudioInterleavedAvailable;
        _recorder.RecordingStarted -= _onRecordingStarted;
        _recorder.RecordingStopped -= _onRecordingStopped;
        _recorder.RecordingError -= _onRecordingError;

        _supervisor.InputLost -= _onInputLost;
        _supervisor.InputRecovered -= _onInputRecovered;
        _supervisor.InputRecoveryFailed -= _onInputRecoveryFailed;
        _supervisor.SegmentRolledOver -= _onSegmentRolledOver;
        _supervisor.MaxDurationReached -= _onMaxDurationReached;
        _supervisor.ScheduledStopReached -= _onScheduledStopReached;
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
        if (_disposed) throw new ObjectDisposedException(nameof(CaptureSession));

        Disconnect();

        lock (_stateLock)
        {
            CurrentFormat = StreamFormat.Empty;
            _fpsMeter.Reset();
            _currentSource = source;

            _currentSource.VideoFrameReceived += OnSourceVideoFrameReceived;
            _currentSource.AudioFrameReceived += OnSourceAudioFrameReceived;
            _currentSource.TelemetryUpdated += OnSourceTelemetryUpdated;
            _currentSource.StatusChanged += OnSourceStatusChanged;
            _currentSource.ErrorOccurred += OnSourceErrorOccurred;

            AppLogger.LogInfo($"Connecting to media source: {_currentSource.Name ?? _currentSource.Address}");
            _currentSource.Connect();
        }
    }

    private void OnSourceTelemetryUpdated(StreamTelemetry t) => TelemetryUpdated?.Invoke(t);
    private void OnSourceStatusChanged(string s) => StatusChanged?.Invoke(s);
    private void OnSourceErrorOccurred(string err)
    {
        AppLogger.LogError($"Media source error: {err}");
        ErrorOccurred?.Invoke(err);
    }

    private void OnSourceVideoFrameReceived(IntPtr pData, int length, int width, int height, int stride, double fps, long timestamp)
    {
        // Feed the input-health watchdog. Hot path: this only exchanges a long timestamp when armed
        // (no allocation, no lock), so it is safe to run once per frame (ADR-0002).
        _inputWatchdog.NotifyVideoFrame();

        // Track the true delivery rate so recording can stamp video at its actual
        // rate (the source-reported "fps" is a nominal maximum, e.g. vMix's "30").
        _fpsMeter.OnVideoFrame();

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
            _recorder.OnRecordingVideoFrame();
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
        // A caller-initiated disconnect ends the take — this is not an outage to recover from.
        _supervisor.EndTake();

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
                    _currentSource.VideoFrameReceived -= OnSourceVideoFrameReceived;
                    _currentSource.AudioFrameReceived -= OnSourceAudioFrameReceived;
                    _currentSource.TelemetryUpdated -= OnSourceTelemetryUpdated;
                    _currentSource.StatusChanged -= OnSourceStatusChanged;
                    _currentSource.ErrorOccurred -= OnSourceErrorOccurred;
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
        if (_disposed) return false;

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

        // Stamp video at the measured TRUE delivery rate, not the source-reported
        // nominal maximum (vMix's "30" actually delivers ~29.2 fps). Stamping at the
        // nominal rate is what makes the video track play fast and drift from the
        // audio. Fall back to the reported rate only if no reliable measurement yet.
        double effectiveFps = _fpsMeter.HasReliableEstimate ? _fpsMeter.Fps : fmt.FrameRate;
        if (effectiveFps <= 1.0) effectiveFps = fmt.FrameRate;

        AppLogger.LogInfo(
            $"Recording A/V clock: reported {fmt.FrameRate:F2} fps, measured {_fpsMeter.Fps:F2} fps, " +
            $"stamping video at {effectiveFps:F2} fps.");

        // The arm order — reset the A/V clock, then initialize the sink, then flip the gate — is now
        // enforced inside RecordingArm.Arm (ADR-0005) rather than being an ordering convention that
        // lived here as a comment. The clock reset happens before the gate can flip, by construction.
        var effectiveFormat = fmt with { FrameRate = effectiveFps };
        bool started = _recorder.StartRecording(config, effectiveFormat);
        if (started)
        {
            _supervisor.BeginTake(config, effectiveFormat);
        }

        return started;
    }

    /// <summary>
    /// Stops the active recording gracefully.
    /// </summary>
    public void StopRecording()
    {
        _supervisor.EndTake();
        _recorder.StopRecording();
    }

    /// <summary>
    /// Stops the active recording asynchronously.
    /// </summary>
    public Task StopRecordingAsync()
    {
        _supervisor.EndTake();
        return _recorder.StopRecordingAsync();
    }

    /// <summary>
    /// Periodic low-disk protection sweep. Cheap; call from a UI tick loop while recording.
    /// </summary>
    public void CheckDiskSpaceAndProtect()
    {
        _recorder.CheckDiskSpaceAndProtect();
    }

    /// <summary>
    /// Periodic input-health poll (ADR-0002). Call from the UI tick loop while a take is in progress,
    /// mirroring <see cref="CheckDiskSpaceAndProtect"/>. The poll is now delegated to
    /// <see cref="TakeSupervisor.Tick"/>, which owns the boundary evaluation, the watchdog edge and the
    /// reconnect/grace policy; this method only forwards. It is deliberately distinct from the FFmpeg
    /// stall watchdog in <c>Services/Sinks/FfmpegProcessHost.cs</c>, which only fires when data is queued
    /// but writes stall — a dead feed leaves the queue empty and reads to that watchdog as idle.
    /// </summary>
    public void CheckInputHealth()
    {
        if (_disposed) return;
        _supervisor.Tick();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Disconnect();
        UnhookInternalAudioAndRecorder();
        _supervisor.Dispose();

        VideoFrameAvailable = null;
        AudioLevelsUpdated = null;
        TelemetryUpdated = null;
        FormatChanged = null;
        StatusChanged = null;
        ErrorOccurred = null;
        RecordingStarted = null;
        RecordingStopped = null;
        RecordingError = null;
        InputLost = null;
        InputRecovered = null;
        InputRecoveryFailed = null;

        if (_ownsDependencies)
        {
            _audio.Dispose();
            _recorder.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
