using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Sources;

namespace OmtCaptureStudio.ViewModels;

public delegate void VideoFrameReceivedHandler(IntPtr pData, int dataLength, int width, int height, int stride);

/// <summary>
/// Main window view model managing presentation state, stream lifecycle,
/// recording orchestration, telemetry formatting, and UI commands.
/// Designed for CommunityToolkit.Mvvm with full Native AOT compatibility.
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    public static readonly IBrush ConnectBrush = StudioPalette.Connect;
    public static readonly IBrush DisconnectBrush = StudioPalette.Disconnect;
    public static readonly IBrush TestSignalActiveBrush = StudioPalette.TestSignalActive;
    public static readonly IBrush TestSignalInactiveBrush = StudioPalette.TestSignalInactive;
    public static readonly IBrush LiveIndicatorActiveBrush = StudioPalette.LiveActive;
    public static readonly IBrush LiveIndicatorInactiveBrush = StudioPalette.LiveInactive;
    public static readonly IBrush RecordStartBrush = StudioPalette.RecordStart;
    public static readonly IBrush RecordStopBrush = StudioPalette.RecordStop;

    private readonly CaptureSession _session;
    private readonly OmtDiscoveryService _discoveryService;
    private readonly DispatcherTimer? _recordUiTimer;
    private readonly DispatcherTimer? _captureTargetsTimer;
    private readonly DispatcherTimer? _inputHealthTimer;
    private readonly bool _ownsDependencies;

    // The broadcast-mode state machine (ADR-0001). Headless-constructible; presentation below is a
    // projection of this state, never an input to it.
    private readonly BroadcastSessionState _broadcastState = new();
    private readonly IBroadcastSessionEffects _broadcastEffects;

    private OmtDesktopCaptureGenerator? _desktopCapture;

    // The target currently being broadcast, so live tracking can stop the broadcast if the
    // window it is capturing closes.
    private CaptureTarget? _activeCaptureTarget;

    // Monitors are effectively static (DXGI enumeration needs a D3D11 device) — cache them
    // and refresh only the window list on the live-tracking timer.
    private IReadOnlyList<CaptureTarget> _cachedMonitors = Array.Empty<CaptureTarget>();
    private bool _monitorsEnumerated;

    private long _lastHudUpdateTick;
    private volatile int _lastWidth;
    private volatile int _lastHeight;
    private double _lastFps;
    private volatile bool _isLive;
    private volatile bool _isSafeExitConfirmed;
    private bool _disposed;
    private readonly Action _updateVideoHudDelegate;

    #region Observable Properties

    // 1. Version & Branding
    [ObservableProperty]
    private string _versionText = "v0.0.0";

    // 2. Source Selection & Discovery
    [ObservableProperty]
    private IReadOnlyList<OmtSourceInfo> _discoveredSources = Array.Empty<OmtSourceInfo>();

    [ObservableProperty]
    private OmtSourceInfo? _selectedSource;

    [ObservableProperty]
    private string? _selectedSourceToolTip;

    [ObservableProperty]
    private bool _isSourceDropDownOpen;

    // 3. Connect / Disconnect Action
    [ObservableProperty]
    private string _connectButtonText = "Connect";

    [ObservableProperty]
    private IBrush _connectButtonBackground = ConnectBrush;

    [ObservableProperty]
    private IBrush _connectButtonForeground = StudioPalette.ButtonDarkForeground;

    [ObservableProperty]
    private string _connectButtonAutomationName = "Connect Stream";

    // 4. Recording Status & Controls
    [ObservableProperty]
    private string _recordDurationText = "00:00:00";

    [ObservableProperty]
    private string _recordStatsText = "Ready";

    [ObservableProperty]
    private bool _isRecordButtonEnabled = true;

    [ObservableProperty]
    private bool _isRecordingConfigEnabled = true;

    [ObservableProperty]
    private string _recordButtonText = "● START RECORDING";

    [ObservableProperty]
    private IBrush _recordButtonBackground = RecordStartBrush;

    [ObservableProperty]
    private string _recordButtonAutomationName = "Record Stream";

    // 5. Test Signal Generator
    [ObservableProperty]
    private bool _isTestSignalActive;

    [ObservableProperty]
    private IBrush _testSignalBackground = TestSignalInactiveBrush;

    [ObservableProperty]
    private IBrush _testSignalForeground = StudioPalette.ButtonLightForeground;

    [ObservableProperty]
    private string _testSignalAutomationName = "Enable Test Signal Generator";

    // Host desktop capture broadcaster
    [ObservableProperty]
    private bool _isDesktopCaptureActive;

    [ObservableProperty]
    private IBrush _desktopCaptureBackground = TestSignalInactiveBrush;

    [ObservableProperty]
    private IBrush _desktopCaptureForeground = StudioPalette.ButtonLightForeground;

    [ObservableProperty]
    private string _desktopCaptureAutomationName = "Enable Host Broadcast";

    // Host capture target selection
    [ObservableProperty]
    private IReadOnlyList<CaptureTarget> _captureTargets = Array.Empty<CaptureTarget>();

    [ObservableProperty]
    private CaptureTarget? _selectedCaptureTarget;

    [ObservableProperty]
    private bool _isCaptureTargetDropDownOpen;

    // Grace period for an ASIO driver to finish its own startup before we snapshot its negotiated
    // state (sample rate / resampler / latency) once, for logging.
    private const int AsioTelemetryDelayMs = 600;

    // Host capture audio source selection (presets + individual WASAPI hardware endpoints + ASIO drivers)
    private static readonly IReadOnlyList<HostAudioCaptureOption> DefaultCaptureAudioOptions = new[]
    {
        new HostAudioCaptureOption(HostAudioCaptureMode.SystemAudio, "System Audio (Default \u00B7 No Echo)", null),
        new HostAudioCaptureOption(HostAudioCaptureMode.Microphone, "Microphone (Default)", null),
        new HostAudioCaptureOption(HostAudioCaptureMode.SystemAndMicrophone, "System Audio + Default Mic", null),
        new HostAudioCaptureOption(HostAudioCaptureMode.Mute, "Mute (Silent 48 kHz Clock)", null)
    };

    [ObservableProperty]
    private IReadOnlyList<HostAudioCaptureOption> _captureAudioOptions = DefaultCaptureAudioOptions;

    [ObservableProperty]
    private HostAudioCaptureOption _selectedCaptureAudioOption = DefaultCaptureAudioOptions[0];

    [ObservableProperty]
    private bool _isCaptureAudioDropDownOpen;

    // ---- ASIO input pair selection ------------------------------------------------------------
    // ASIO interfaces routinely expose 8/16/32 inputs, so "In 1/2" must be a choice, not a given.
    // The list of real pairs comes from opening the driver *briefly* (no buffers, no start), which
    // is why every bit of this is lazy: nothing here may run during ViewModel construction, and a
    // driver locked by another application (or absent entirely) must degrade to plain In 1/2.
    private readonly Dictionary<Guid, AsioDriverInfo> _asioDriversByClsid = new();
    private readonly Dictionary<Guid, AsioPairProbeResult> _asioPairCache = new();
    private readonly Dictionary<Guid, int> _asioChosenPairIndex = new();
    private readonly HashSet<Guid> _asioProbeInFlight = new();
    private bool _suppressAsioPairSync;

    [ObservableProperty]
    private bool _isAsioInputPairVisible;

    [ObservableProperty]
    private IReadOnlyList<AsioInputPairOption> _asioInputPairOptions = Array.Empty<AsioInputPairOption>();

    [ObservableProperty]
    private AsioInputPairOption? _selectedAsioInputPair;

    [ObservableProperty]
    private bool _isAsioInputPairDropDownOpen;

    [ObservableProperty]
    private string? _asioInputPairHint;

    public HostAudioCaptureMode SelectedCaptureAudioMode
    {
        get => SelectedCaptureAudioOption?.Mode ?? HostAudioCaptureMode.SystemAudio;
        set
        {
            var match = CaptureAudioOptions.FirstOrDefault(o => o.Mode == value && o.DeviceId == null)
                ?? CaptureAudioOptions.FirstOrDefault(o => o.Mode == value);
            if (match != null)
            {
                SelectedCaptureAudioOption = match;
            }
        }
    }

    // Host broadcast network interface selection
    private static readonly IReadOnlyList<BroadcastNetworkOption> DefaultBroadcastNetworks = new[]
    {
        new BroadcastNetworkOption { DisplayName = "All Networks (Auto)", IpAddress = null, InterfaceIndex = 0 },
        new BroadcastNetworkOption { DisplayName = "Localhost Only \u00B7 127.0.0.1", IpAddress = "127.0.0.1", InterfaceIndex = 0 }
    };

    [ObservableProperty]
    private IReadOnlyList<BroadcastNetworkOption> _broadcastNetworks = DefaultBroadcastNetworks;

    [ObservableProperty]
    private BroadcastNetworkOption _selectedBroadcastNetwork = DefaultBroadcastNetworks[0];

    [ObservableProperty]
    private bool _isBroadcastNetworkDropDownOpen;

    // 6. Viewport HUD & Overlay
    [ObservableProperty]
    private bool _isNoSignalOverlayVisible = true;

    [ObservableProperty]
    private IBrush _liveIndicatorBrush = LiveIndicatorInactiveBrush;

    [ObservableProperty]
    private string _liveStatusText = "OFFLINE";

    [ObservableProperty]
    private string _sourceNameText = "";

    [ObservableProperty]
    private string _videoSpecsText = "-- x -- @ -- fps";

    // Input-loss alarm banner (ADR-0002) — a projection of the Reconnecting / InputLost states.
    [ObservableProperty]
    private bool _isInputLossBannerVisible;

    [ObservableProperty]
    private string _inputLossBannerText = "";

    // Recording Health panel (FEAT-07) — a projection of CaptureSession.GetRecordingHealthSnapshot().
    // Aggregation lives in the engine/model; this is presentation only.
    [ObservableProperty]
    private bool _isRecordingHealthVisible;

    [ObservableProperty]
    private string _recordingHealthEncoderText = "—";

    [ObservableProperty]
    private string _recordingHealthBitrateText = "—";

    [ObservableProperty]
    private string _recordingHealthDropsText = "—";

    [ObservableProperty]
    private string _recordingHealthDriftText = "—";

    [ObservableProperty]
    private bool _isRecordingDropWarning;

    [ObservableProperty]
    private bool _isRecordingDriftWarning;

    [ObservableProperty]
    private IBrush _recordingDropsForeground = StudioPalette.ButtonLightForeground;

    [ObservableProperty]
    private IBrush _recordingDriftForeground = StudioPalette.ButtonLightForeground;

    // Stop-summary verdict (FEAT-07) and the diagnostics drawer.
    [ObservableProperty]
    private string _stopSummaryText = "";

    /// <summary>True when a stop verdict is available (drives the drawer's verdict row).</summary>
    [ObservableProperty]
    private bool _isStopSummaryVisible;

    [ObservableProperty]
    private string _diagnosticTailText = "";

    [ObservableProperty]
    private bool _isDiagnosticsDrawerOpen;

    // 7. Recording Configuration Choices
    public IReadOnlyList<OutputContainerFormat> ContainerFormats { get; } = Enum.GetValues<OutputContainerFormat>();

    [ObservableProperty]
    private OutputContainerFormat _selectedContainerFormat = OutputContainerFormat.MP4;

    public IReadOnlyList<VideoEncoderChoice> EncoderChoices { get; } = Enum.GetValues<VideoEncoderChoice>();

    [ObservableProperty]
    private VideoEncoderChoice _selectedEncoderChoice = VideoEncoderChoice.AutoHardware;

    public IReadOnlyList<QualityPreset> QualityPresets { get; } = Enum.GetValues<QualityPreset>();

    [ObservableProperty]
    private QualityPreset _selectedQualityPreset = QualityPreset.High;

    [ObservableProperty]
    private string _outputDirectory = "";

    // 8. Status & Telemetry
    [ObservableProperty]
    private string _statusText = "Ready. Discovering OMT sources on network...";

    [ObservableProperty]
    private string _audioTelemetryText = "Audio: --";

    [ObservableProperty]
    private string _bitrateText = "Bitrate: 0.0 Mbps";

    [ObservableProperty]
    private string _droppedFramesText = "Dropped: 0";

    // 9. Close Protection Modal
    [ObservableProperty]
    private bool _isRecordingCloseOverlayVisible;

    [ObservableProperty]
    private bool _isMainContentEnabled = true;

    // Target / Audio / Network selectors are locked while a host broadcast is LIVE —
    // changing any of them requires restarting the sender, which would drop receivers.
    [ObservableProperty]
    private bool _isCaptureSelectorEnabled = true;

    // Source picker is locked while connected to a stream.
    [ObservableProperty]
    private bool _isSourceSelectorEnabled = true;

    [ObservableProperty]
    private bool _isStopAndExitEnabled = true;

    [ObservableProperty]
    private bool _isKeepRecordingEnabled = true;

    [ObservableProperty]
    private string _stopAndExitButtonText = "Stop & Exit";

    #endregion

    #region Public State & Events

    public bool IsRecording => _session.IsRecording;
    public bool IsConnected => _session.IsConnected;

    /// <summary>The named broadcast-mode state (ADR-0001); all presentation is a projection of it.</summary>
    public BroadcastState BroadcastState => _broadcastState.State;

    /// <summary>True while the host desktop/window broadcast is live.</summary>
    public bool IsBroadcasting => _broadcastState.IsBroadcasting;

    public bool IsSafeExitConfirmed
    {
        get => _isSafeExitConfirmed;
        set => _isSafeExitConfirmed = value;
    }

    public event VideoFrameReceivedHandler? VideoFrameReceived;
    public event Action? ViewportClearRequested;
    public event Action<AudioLevelData>? AudioLevelsUpdated;
    public event Action? MetersResetRequested;
    public event Action? RequestClose;

    public Func<string?, Task<string?>>? PickFolderAsync { get; set; }

    #endregion

    public MainWindowViewModel()
        : this(new CaptureSession(), new OmtDiscoveryService(), ownsDependencies: true)
    {
    }

    public MainWindowViewModel(
        CaptureSession session,
        OmtDiscoveryService discoveryService,
        bool ownsDependencies = false)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _ownsDependencies = ownsDependencies;
        _broadcastEffects = new SessionEffects(_session);
        _updateVideoHudDelegate = UpdateVideoHudOnUIThread;

        // Load version info
        string version = "Unknown";
        try
        {
            string versionPath = Path.Combine(AppContext.BaseDirectory, "version.txt");
            if (File.Exists(versionPath))
            {
                version = File.ReadAllText(versionPath).Trim();
            }
        }
        catch { }

        AppLogger.LogInfo($"OMT Capture Studio application starting. Version: {version}");
        VersionText = $"v{version}";

        // Output directory default
        string myVideos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string defaultVideos = !string.IsNullOrWhiteSpace(myVideos)
            ? Path.Combine(myVideos, "OMT_Captures")
            : Path.Combine(AppContext.BaseDirectory, "OMT_Captures");
        if (!Directory.Exists(defaultVideos))
        {
            try { Directory.CreateDirectory(defaultVideos); } catch { }
        }
        OutputDirectory = defaultVideos;

        // UI Timer
        try
        {
            _recordUiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _recordUiTimer.Tick += RecordUiTimer_Tick;
        }
        catch
        {
            // Headless testing environment without Avalonia dispatcher
        }

        HookEvents();
        ApplyBroadcastStateProjection();
        _discoveryService.Start();

        // Seed the capture-target picker, audio device picker, and broadcast network picker
        // so they are populated before the user touches them.
        // Monitor enumeration creates a D3D11 device, so it's guarded for headless/no-GPU.
        try
        {
            PopulateCaptureTargets();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not enumerate capture targets at startup: {ex.Message}");
        }

        try
        {
            PopulateCaptureAudioOptions();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not enumerate audio devices at startup: {ex.Message}");
        }

        try
        {
            PopulateBroadcastNetworks();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not enumerate network interfaces at startup: {ex.Message}");
        }

        // Live window tracking: windows open and close constantly, so re-enumerate the window
        // list periodically (cheap EnumWindows; monitors stay cached). Guarded for headless.
        try
        {
            _captureTargetsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _captureTargetsTimer.Tick += CaptureTargetsTimer_Tick;
            _captureTargetsTimer.Start();
        }
        catch
        {
            // Headless testing environment without Avalonia dispatcher
        }

        // Input-loss watchdog tick (ADR-0002). Always running, like the capture-targets tick, so it
        // also covers the outage window when no recording is active. Runs on the UI thread, so the
        // state/banner mutations it triggers need no extra marshaling.
        try
        {
            _inputHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _inputHealthTimer.Tick += InputHealthTimer_Tick;
            _inputHealthTimer.Start();
        }
        catch
        {
            // Headless testing environment without Avalonia dispatcher
        }
    }

    private void HookEvents()
    {
        _broadcastState.StateChanged += OnBroadcastStateChanged;
        _discoveryService.SourcesUpdated += OnSourcesUpdated;

        _session.VideoFrameAvailable += OnVideoFrameAvailable;
        _session.AudioLevelsUpdated += OnAudioLevelsUpdated;
        _session.TelemetryUpdated += OnTelemetryUpdated;
        _session.StatusChanged += OnStatusChanged;
        _session.ErrorOccurred += OnSessionError;

        _session.RecordingStarted += OnRecordingStarted;
        _session.RecordingStopped += OnRecordingStopped;
        _session.RecordingError += OnRecordingError;

        _session.InputLost += OnSessionInputLost;
        _session.InputRecovered += OnSessionInputRecovered;
        _session.InputRecoveryFailed += OnSessionInputRecoveryFailed;
    }

    /// <summary>
    /// ADR-0002 outage transitions. Raised from the UI health tick, so the state change (and the
    /// banner projection it triggers) already happens on the UI thread.
    /// </summary>
    private void OnSessionInputLost() => _broadcastState.BeginReconnecting();

    private void OnSessionInputRecovered() => _broadcastState.EndReconnecting();

    private void OnSessionInputRecoveryFailed() => _broadcastState.MarkInputLost();

    public void SetAudioMonitoring(bool enabled)
    {
        _session.IsAudioMonitoringEnabled = enabled;
    }

    #region Property Change Handlers

    partial void OnSelectedContainerFormatChanged(OutputContainerFormat value)
    {
        if (value == OutputContainerFormat.MOV)
        {
            SelectedEncoderChoice = VideoEncoderChoice.ProRes;
        }
        else if (SelectedEncoderChoice == VideoEncoderChoice.ProRes)
        {
            SelectedEncoderChoice = VideoEncoderChoice.AutoHardware;
        }
    }

    partial void OnSelectedSourceChanged(OmtSourceInfo? value)
    {
        SelectedSourceToolTip = value != null
            ? $"{value.DisplayName}\nAddress: {value.Address}"
            : null;
    }

    partial void OnIsCaptureTargetDropDownOpenChanged(bool value)
    {
        // Refresh the window list every time the picker opens (windows come and go);
        // monitors are static but re-enumerating is cheap and keeps the list current.
        if (value)
        {
            try { PopulateCaptureTargets(); } catch { /* headless / no-desktop */ }
        }
    }

    partial void OnIsCaptureAudioDropDownOpenChanged(bool value)
    {
        if (value)
        {
            try { PopulateCaptureAudioOptions(); } catch { /* headless */ }
        }
    }

    partial void OnIsBroadcastNetworkDropDownOpenChanged(bool value)
    {
        if (value)
        {
            try { PopulateBroadcastNetworks(); } catch { /* headless */ }
        }
    }

    partial void OnIsRecordingCloseOverlayVisibleChanged(bool value)
    {
        IsMainContentEnabled = !value;
    }

    partial void OnSelectedCaptureAudioOptionChanged(HostAudioCaptureOption value)
    {
        OnPropertyChanged(nameof(SelectedCaptureAudioMode));
        SyncAsioInputPairSelection(value);
        if (_desktopCapture is { IsRunning: true } activeBroadcast && value != null)
        {
            try
            {
                // SetAudioSource disposes the outgoing source exactly once — critical for ASIO, which
                // holds the driver exclusively; if this throws, the generator disposes the new one.
                activeBroadcast.SetAudioSource(CreateCaptureAudioSource(value));
                StatusText = $"Host broadcast audio switched to: {value.DisplayName}";
                AppLogger.LogInfo($"Switched host broadcast audio to {value.Mode} ({value.DisplayName}).");
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Failed to switch host broadcast audio: {ex.Message}", ex);
                StatusText = $"Host audio switch error: {ex.Message}";
            }
        }
    }

    partial void OnSelectedBroadcastNetworkChanged(BroadcastNetworkOption value)
    {
        if (_desktopCapture is { IsRunning: true } activeBroadcast && value != null)
        {
            try
            {
                activeBroadcast.SetBindIpAddress(value.IpAddress);
                string endpoint = activeBroadcast.BroadcastEndpointUrl ?? activeBroadcast.Address ?? "OMT";
                StatusText = $"Host broadcast network switched to: {value.DisplayName} ({endpoint})";
                AppLogger.LogInfo($"Switched host broadcast network to {value.DisplayName} ({endpoint}).");
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Failed to switch host broadcast network: {ex.Message}", ex);
                StatusText = $"Host network switch error: {ex.Message}";
            }
        }
    }

    #endregion

    #region Relay Commands

    private static void PostToUIThread(Action action, DispatcherPriority? priority = null)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action, priority ?? DispatcherPriority.Normal);
        }
    }

    [RelayCommand]
    private void RefreshSources()
    {
        if (IsRecordingCloseOverlayVisible) return;

        try
        {
            _discoveryService.RefreshNow();
            StatusText = "Scanning network for OMT sources...";
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Error refreshing sources: {ex.Message}", ex);
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ToggleConnectAsync()
    {
        if (IsRecordingCloseOverlayVisible) return;

        try
        {
            if (_broadcastState.IsSessionActive)
            {
                if (_session.IsRecording)
                {
                    StatusText = "Finalizing active recording before disconnecting...";
                }

                // The module owns the "stop-recording-first, then disconnect" ordering; the
                // resulting state change drives every visual through ApplyBroadcastStateProjection.
                await _broadcastState.LeaveReceiveModeAsync(_broadcastEffects);
                AppLogger.LogInfo("Disconnected from stream.");
            }
            else
            {
                if (SelectedSource is not OmtSourceInfo info || string.IsNullOrWhiteSpace(info.Address))
                {
                    StatusText = "Please select an OMT source from the dropdown.";
                    return;
                }

                SourceNameText = info.DisplayName;
                AppLogger.LogInfo($"Connecting to stream at {info.Address}");
                await _broadcastState.EnterNetworkConnectedAsync(_broadcastEffects, () => _session.Connect(info.Address));
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Error toggling connection: {ex.Message}", ex);
            StatusText = $"Connection error: {ex.Message}";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ToggleTestSignalAsync()
    {
        if (IsRecordingCloseOverlayVisible) return;
        await SetTestSignalActiveAsync(!IsTestSignalActive);
    }

    public async Task SetTestSignalActiveAsync(bool active)
    {
        try
        {
            if (active)
            {
                if (_broadcastState.IsSessionActive && _session.IsRecording)
                {
                    StatusText = "Finalizing active recording before switching source...";
                }

                // Leaves any active receive mode (stop-recording-first), enters TestSignal and
                // connects the synthetic source. The projection owns the resulting visuals.
                await _broadcastState.EnterTestSignalAsync(_broadcastEffects, () =>
                {
                    StatusText = "Connected to internal synthetic test generator.";
                    _session.Connect(new SyntheticPatternSource());
                });

                SourceNameText = "Local Test Pattern (SMPTE)";
                AppLogger.LogInfo("Started local test signal generator.");
            }
            else
            {
                StatusText = "Test pattern stopped.";

                if (_broadcastState.IsSessionActive && _session.IsRecording)
                {
                    StatusText = "Finalizing active recording before disconnecting...";
                }

                bool wasReceiving = _broadcastState.IsSessionActive;
                await _broadcastState.LeaveReceiveModeAsync(_broadcastEffects);
                if (wasReceiving)
                {
                    AppLogger.LogInfo("Disconnected test signal generator.");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Error toggling test signal: {ex.Message}", ex);
            StatusText = $"Test signal error: {ex.Message}";
            OnPropertyChanged(nameof(IsTestSignalActive));
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ToggleDesktopCaptureAsync()
    {
        if (IsRecordingCloseOverlayVisible) return;
        await SetDesktopCaptureActiveAsync(!IsDesktopCaptureActive);
    }

    public async Task SetDesktopCaptureActiveAsync(bool active)
    {
        try
        {
            if (active)
            {
                if (CaptureTargets.Count == 0)
                {
                    PopulateCaptureTargets();
                }

                var target = SelectedCaptureTarget
                    ?? MonitorEnumerator.Enumerate().FirstOrDefault()
                    ?? throw new InvalidOperationException("No capture target available.");
                IFrameSource source = FrameSourceFactory.Create(target);
                IHostAudioSource audioSource;
                try
                {
                    audioSource = CreateCaptureAudioSource(SelectedCaptureAudioOption);
                }
                catch
                {
                    source.Dispose();
                    throw;
                }

                string? bindIp = SelectedBroadcastNetwork?.IpAddress;

                // Once the generator is constructed it owns both sources and disposes each exactly
                // once; anything thrown before that would leave an ASIO driver locked exclusively,
                // so every failure branch below releases what it still owns.
                OmtDesktopCaptureGenerator? generator = null;
                try
                {
                    generator = new OmtDesktopCaptureGenerator(
                        source,
                        sourceName: "OmtCaptureStudio_DesktopCapture",
                        fps: 30,
                        audioSource: audioSource,
                        bindIpAddress: bindIp);
                    generator.Start();
                    _desktopCapture = generator;
                }
                catch
                {
                    if (generator != null)
                    {
                        try { generator.Dispose(); } catch { }
                    }
                    else
                    {
                        try { source.Dispose(); } catch { }
                        try { audioSource.Dispose(); } catch { }
                    }
                    throw;
                }

                _activeCaptureTarget = target;
                _broadcastState.StartBroadcasting();
                string audioLabel = SelectedCaptureAudioOption?.DisplayName ?? "System Audio";
                string networkLabel = SelectedBroadcastNetwork?.DisplayName ?? "All Networks (Auto)";
                string endpointUrl = _desktopCapture.BroadcastEndpointUrl ?? _desktopCapture.Address ?? "OMT";
                StatusText = $"Broadcasting host capture ({audioLabel}) on {networkLabel} ({endpointUrl}) — select it in Source and Connect.";
                AppLogger.LogInfo($"Started host desktop capture: {endpointUrl} (Audio: {audioLabel}, Network: {networkLabel})");
            }
            else
            {
                _desktopCapture?.Dispose();
                _desktopCapture = null;
                _activeCaptureTarget = null;
                _broadcastState.StopBroadcasting();
                StatusText = "Host capture stopped.";
                AppLogger.LogInfo("Stopped host desktop capture.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Error toggling desktop capture: {ex.Message}", ex);
            _broadcastState.StopBroadcasting();
            StatusText = $"Host capture error: {ex.Message}";
        }
    }

    private void PopulateCaptureTargets()
    {
        // Monitors are cached after the first DXGI enumeration attempt (it spins up a
        // D3D11 device); only refresh them when not yet enumerated.
        if (!_monitorsEnumerated)
        {
            _cachedMonitors = MonitorEnumerator.Enumerate();
            _monitorsEnumerated = true;
        }

        var list = new List<CaptureTarget>(_cachedMonitors);
        list.AddRange(WindowEnumerator.Enumerate());
        CaptureTargets = list;

        if (SelectedCaptureTarget == null ||
            !list.Any(t => t.Kind == SelectedCaptureTarget.Kind &&
                           t.MonitorIndex == SelectedCaptureTarget.MonitorIndex &&
                           t.WindowHandle == SelectedCaptureTarget.WindowHandle))
        {
            SelectedCaptureTarget = list.FirstOrDefault();
        }
    }

    private void PopulateCaptureAudioOptions()
    {
        var previous = SelectedCaptureAudioOption;
        var list = AudioDeviceEnumerator.EnumerateOptions();
        if (list.Count == 0)
        {
            list = DefaultCaptureAudioOptions;
        }

        // Copy into a mutable list: the enumerator's result is shared state, and we splice the
        // ASIO drivers in below the built-in presets without disturbing either order.
        var options = new List<HostAudioCaptureOption>(list);
        InsertAsioOptions(options);

        CaptureAudioOptions = options;

        if (previous != null)
        {
            var match = options.FirstOrDefault(o =>
                o.Mode == previous.Mode &&
                string.Equals(o.DeviceId, previous.DeviceId, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault(o => o.Mode == previous.Mode && o.DeviceId == null)
                ?? options[0];
            SelectedCaptureAudioOption = match;
        }
        else
        {
            SelectedCaptureAudioOption = options[0];
        }
    }

    /// <summary>
    /// Splices one entry per registered ASIO driver into the broadcast-audio dropdown, keeping every
    /// existing WASAPI entry and its position: they land after the built-in presets and before the
    /// per-endpoint entries (or before "Mute" when no endpoints were enumerated).
    /// ASIO drivers grant exclusive access, so this only ever reads the registry — a driver is never
    /// opened here, only when the user actually selects it. Registry failures (headless, no drivers,
    /// locked-down hive) leave the dropdown exactly as it was.
    /// </summary>
    /// This never opens a driver: probing is deferred to <see cref="BeginAsioInputPairProbe"/>, which
    /// only ever runs once the user selects (or opens the dropdown of) an ASIO entry.
    /// </summary>
    private void InsertAsioOptions(List<HostAudioCaptureOption> options)
    {
        IReadOnlyList<AsioDriverInfo> drivers;
        try
        {
            drivers = AsioDriverEnumerator.Enumerate();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not enumerate ASIO drivers: {ex.Message}");
            return;
        }

        if (drivers.Count == 0)
        {
            return;
        }

        int insertAt = options.FindIndex(o => o.DeviceId != null && o.Mode != HostAudioCaptureMode.Mute);
        if (insertAt < 0)
        {
            int muteIndex = options.FindIndex(o => o.Mode == HostAudioCaptureMode.Mute);
            insertAt = muteIndex >= 0 ? muteIndex : options.Count;
        }

        var entries = new List<HostAudioCaptureOption>(drivers.Count);
        foreach (AsioDriverInfo driver in drivers)
        {
            _asioDriversByClsid[driver.Clsid] = driver;

            // Remembered per driver (not per dropdown rebuild) so re-opening the audio picker never
            // silently resets a chosen 3/4 back to 1/2. The first pair stays the default.
            int first = _asioChosenPairIndex.TryGetValue(driver.Clsid, out int saved) ? saved : 0;
            entries.Add(HostAudioCaptureOption.ForAsio(
                driver.Clsid,
                $"ASIO · {driver.DisplayName} ({AsioPairText(driver.Clsid, first)})",
                first));
        }

        options.InsertRange(insertAt, entries);
    }

    // ============================================================================================
    // ASIO input pair selection
    // ============================================================================================

    /// <summary>
    /// Keeps the pair picker in step with the selected audio option. Purely reactive: the options
    /// list hold real driver data only once <see cref="BeginAsioInputPairProbe"/> reports in, which
    /// is what keeps this ViewModel constructible with no ASIO driver (and no Avalonia app) at all.
    /// </summary>
    private void SyncAsioInputPairSelection(HostAudioCaptureOption? value)
    {
        if (_suppressAsioPairSync) return;

        Guid clsid = value != null && value.IsAsio && value.AsioDriverClsid.HasValue
            ? value.AsioDriverClsid.Value
            : Guid.Empty;

        if (clsid == Guid.Empty)
        {
            ClearAsioInputPairState();
            return;
        }

        // Visible immediately so the control exists while the probe is still running; the hint below
        // tells the user what is happening (a driver can take a second to answer).
        IsAsioInputPairVisible = true;

        if (_asioPairCache.TryGetValue(clsid, out AsioPairProbeResult? cached))
        {
            ApplyAsioPairProbe(value!, clsid, cached, announce: false);
            return;
        }

        AsioInputPairHint = "Probing driver for its input channels…";
        BeginAsioInputPairProbe(clsid);
    }

    /// <summary>Re-arms the probe when the user opens the pair picker after an earlier failure.</summary>
    partial void OnIsAsioInputPairDropDownOpenChanged(bool value)
    {
        if (!value) return;

        if (SelectedCaptureAudioOption is { } option && option.IsAsio && option.AsioDriverClsid is Guid clsid)
        {
            bool usable = _asioPairCache.TryGetValue(clsid, out AsioPairProbeResult? cached) && cached.Channels.Count > 0;
            if (!usable && !_asioProbeInFlight.Contains(clsid))
            {
                // A DAW may have released the driver since the last attempt, so allow one retry.
                _asioPairCache.Remove(clsid);
                AsioInputPairHint = "Re-probing driver for its input channels…";
                BeginAsioInputPairProbe(clsid);
            }
        }
    }

    /// <summary>
    /// Applies the user's pair choice to the selected option. Mutating the dropdown entry in place
    /// (rather than just setting a derived value) keeps the new instance a member of
    /// <see cref="CaptureAudioOptions"/>, which is what the ComboBox's SelectedItem binding needs —
    /// and re-selecting it lets the existing handler hand the rebuilt option to a live broadcast.
    /// </summary>
    partial void OnSelectedAsioInputPairChanged(AsioInputPairOption? value)
    {
        if (_suppressAsioPairSync || value == null) return;

        HostAudioCaptureOption? current = SelectedCaptureAudioOption;
        if (current == null || !current.IsAsio || current.AsioDriverClsid is not Guid clsid) return;
        if (current.AsioFirstInputChannelIndex == value.FirstChannelIndex) return;

        HostAudioCaptureOption updated = current with
        {
            AsioFirstInputChannelIndex = value.FirstChannelIndex,
            DisplayName = $"ASIO · {DriverDisplayName(clsid)} ({value.DisplayName})"
        };

        _asioChosenPairIndex[clsid] = value.FirstChannelIndex;

        try
        {
            // Keep the dropdown entry in sync, then deliberately re-select without suppressing so a
            // running broadcast switches to the new pair immediately.
            var list = new List<HostAudioCaptureOption>(CaptureAudioOptions ?? Array.Empty<HostAudioCaptureOption>());
            int index = list.FindIndex(o => o.Mode == HostAudioCaptureMode.Asio && o.AsioDriverClsid == clsid);
            if (index >= 0) list[index] = updated; else list.Add(updated);
            CaptureAudioOptions = list;

            _suppressAsioPairSync = true;
            try { SelectedAsioInputPair = value; } finally { _suppressAsioPairSync = false; }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not apply ASIO input pair {value.DisplayName}: {ex.Message}", ex);
            return;
        }

        SelectedCaptureAudioOption = updated;
        AppLogger.LogInfo($"ASIO input pair set to {value.DisplayName} for '{updated.DisplayName}'.");
    }

    /// <summary>
    /// Reads the driver's real input channels on a worker thread (the probe itself spins up its own
    /// STA thread and never throws), then publishes the result back on the UI thread.
    /// </summary>
    private void BeginAsioInputPairProbe(Guid clsid)
    {
        if (_asioProbeInFlight.Contains(clsid)) return;

        if (!_asioDriversByClsid.TryGetValue(clsid, out AsioDriverInfo? driver))
        {
            try
            {
                driver = AsioDriverEnumerator.Enumerate().FirstOrDefault(d => d.Clsid == clsid);
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"[ASIO] could not resolve driver {clsid:B} for probing: {ex.Message}");
                driver = null;
            }
        }

        if (driver == null)
        {
            AsioInputPairHint = "Driver is no longer registered on this machine; using input pair 1/2.";
            return;
        }

        _asioProbeInFlight.Add(clsid);
        AsioDriverInfo target = driver;

        try
        {
            _ = Task.Run(() =>
            {
                IReadOnlyList<AsioInputChannel> channels = AsioInputChannelProbe.Probe(target, out string? reason);
                PostToUIThreadSafely(() => CompleteAsioInputPairProbe(clsid, new AsioPairProbeResult(channels, reason)));
            });
        }
        catch (Exception ex)
        {
            _asioProbeInFlight.Remove(clsid);
            AppLogger.LogInfo($"[ASIO] input probe could not be scheduled: {ex.Message}");
            AsioInputPairHint = "Input probe could not run; using input pair 1/2.";
        }
    }

    private void CompleteAsioInputPairProbe(Guid clsid, AsioPairProbeResult result)
    {
        _asioProbeInFlight.Remove(clsid);
        _asioPairCache[clsid] = result;

        HostAudioCaptureOption? option = SelectedCaptureAudioOption;
        if (option == null || !option.IsAsio || option.AsioDriverClsid != clsid)
        {
            return; // The user moved on to another device meanwhile; the cache serves them later.
        }

        ApplyAsioPairProbe(option, clsid, result, announce: true);
    }

    /// <summary>Populates the pair list from the probe result and degrades gracefully when it failed.</summary>
    private void ApplyAsioPairProbe(HostAudioCaptureOption option, Guid clsid, AsioPairProbeResult result, bool announce)
    {
        try
        {
            var pairs = BuildAsioPairs(result.Channels);
            try
            {
                _suppressAsioPairSync = true;
                AsioInputPairOptions = pairs;
                SelectedAsioInputPair =
                    pairs.FirstOrDefault(p => p.FirstChannelIndex == option.AsioFirstInputChannelIndex)
                    ?? pairs.FirstOrDefault();
            }
            finally
            {
                _suppressAsioPairSync = false;
            }

            if (pairs.Count == 0)
            {
                // Degrade exactly as before: the single "ASIO · name (In 1/2)" entry keeps working,
                // the picker hides itself, and the reason is surfaced without breaking anything.
                IsAsioInputPairVisible = false;
                string reason = string.IsNullOrWhiteSpace(result.Reason)
                    ? "the driver reported no input channels"
                    : result.Reason!;
                AsioInputPairHint = $"Input pairs unavailable: {reason}. Using input pair 1/2.";
                AppLogger.LogInfo($"[ASIO] input channel probe failed for '{option.DisplayName}': {reason}");
                if (announce) StatusText = $"ASIO input probe unavailable ({reason}); keeping input pair 1/2.";
                return;
            }

            IsAsioInputPairVisible = true;
            if (announce)
            {
                StatusText = $"ASIO driver exposes {result.Channels.Count} input channel(s): {pairs.Count} pair(s) available.";
                AppLogger.LogInfo($"[ASIO] {DriverDisplayName(clsid)} exposes {result.Channels.Count} input channel(s).");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not apply ASIO input pairs: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Derives the selectable stereo pairs from the driver's input channel count: channels 0+1, 2+3,
    /// 4+5 … with a trailing odd channel offered on its own (the capture source duplicates it to L+R).
    /// </summary>
    private static IReadOnlyList<AsioInputPairOption> BuildAsioPairs(IReadOnlyList<AsioInputChannel> channels)
    {
        var pairs = new List<AsioInputPairOption>();
        if (channels == null) return pairs;

        int count = channels.Count;
        for (int i = 0; i + 1 < count; i += 2)
        {
            pairs.Add(new AsioInputPairOption(i, $"In {i + 1}/{i + 2}"));
        }

        if (count % 2 == 1)
        {
            pairs.Add(new AsioInputPairOption(count - 1, $"In {count}"));
        }

        return pairs;
    }

    private string DriverDisplayName(Guid clsid) =>
        _asioDriversByClsid.TryGetValue(clsid, out AsioDriverInfo? driver) ? driver.DisplayName : "ASIO driver";

    /// <summary>Human label for a remembered pair index, used while rebuilding the audio dropdown.</summary>
    private string AsioPairText(Guid clsid, int firstChannelIndex)
    {
        if (_asioPairCache.TryGetValue(clsid, out AsioPairProbeResult? result))
        {
            foreach (AsioInputPairOption pair in BuildAsioPairs(result.Channels))
            {
                if (pair.FirstChannelIndex == firstChannelIndex) return pair.DisplayName;
            }
        }

        return firstChannelIndex % 2 == 0
            ? $"In {firstChannelIndex + 1}/{firstChannelIndex + 2}"
            : $"In {firstChannelIndex + 1}";
    }

    private void ClearAsioInputPairState()
    {
        try
        {
            _suppressAsioPairSync = true;
            IsAsioInputPairVisible = false;
            AsioInputPairOptions = Array.Empty<AsioInputPairOption>();
            SelectedAsioInputPair = null;
            AsioInputPairHint = null;
        }
        finally
        {
            _suppressAsioPairSync = false;
        }
    }

    /// <summary>
    /// Like <see cref="PostToUIThread"/> but headless-safe: with no Avalonia platform there is no UI
    /// dispatcher to hand the callback to, so it runs inline rather than being lost.
    /// </summary>
    private static void PostToUIThreadSafely(Action action)
    {
        try
        {
            PostToUIThread(action);
        }
        catch
        {
            try { action(); } catch { /* Nothing left to try; the probe is best-effort. */ }
        }
    }

    /// <summary>
    /// Builds the live source for a dropdown option and, for ASIO only, schedules one deferred
    /// telemetry snapshot: the negotiated sample rate, whether we fell back to resampling, and the
    /// driver's reported input latency. Those are published by the driver's own worker thread during
    /// startup, so reading them straight after construction would always return zeros.
    /// </summary>
    private static IHostAudioSource CreateCaptureAudioSource(HostAudioCaptureOption option)
    {
        IHostAudioSource source = HostAudioSourceFactory.Create(option);

        // ADR-0003: no downcast to a concrete driver adapter — the negotiated state is on the
        // interface now, so every source can report it the same way.
        LogSourceStateDeferred(source, option.DisplayName);

        return source;
    }

    private static void LogSourceStateDeferred(IHostAudioSource source, string displayName)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AsioTelemetryDelayMs).ConfigureAwait(false);
                    AppLogger.LogInfo($"[HostAudio] {displayName}: {DescribeSourceState(source)}");
                }
                catch
                {
                    // Telemetry must never surface as a failure to the caller.
                }
            });
        }
        catch
        {
            // No thread pool available (headless shutdown); nothing to report.
        }
    }

    /// <summary>One-line summary of a host source's negotiated state, for logs and status text.</summary>
    private static string DescribeSourceState(IHostAudioSource source)
    {
        int sampleRate = source.SampleRate > 0 ? source.SampleRate : 48000;

        string rate = source.IsResamplingRequired
            ? $"device runs at {source.DeviceSampleRate} Hz, resampling to {sampleRate} Hz"
            : $"native {sampleRate} Hz (no resampling)";

        string note = string.IsNullOrWhiteSpace(source.RateNegotiationNote)
            ? string.Empty
            : $" [{source.RateNegotiationNote}]";

        string latency = source.InputLatencyFrames > 0
            ? $", input latency {source.InputLatencyFrames * 1000 / sampleRate} ms ({source.InputLatencyFrames} samples)"
            : string.Empty;

        string error = string.IsNullOrWhiteSpace(source.LastError)
            ? string.Empty
            : $", ERROR: {source.LastError}";

        string active = source.IsHardwareActive ? string.Empty : ", hardware INACTIVE (silence)";

        return $"{rate}{note}{latency}{error}{active}";
    }

    private void PopulateBroadcastNetworks()
    {
        var previous = SelectedBroadcastNetwork;
        var list = NetworkInterfaceEnumerator.Enumerate();
        if (list.Count == 0)
        {
            list = DefaultBroadcastNetworks;
        }

        BroadcastNetworks = list;

        if (previous != null)
        {
            var match = list.FirstOrDefault(n =>
                n.IsAllNetworks == previous.IsAllNetworks &&
                n.IsLoopbackOnly == previous.IsLoopbackOnly &&
                string.Equals(n.IpAddress, previous.IpAddress, StringComparison.OrdinalIgnoreCase))
                ?? list[0];
            SelectedBroadcastNetwork = match;
        }
        else
        {
            SelectedBroadcastNetwork = list[0];
        }
    }

    /// <summary>
    /// Re-enumerates only the window list (monitors stay cached) and merges it into the
    /// current targets, preserving the user's selection. Skips the update when nothing
    /// changed, so we don't churn the bound collection every tick.
    /// </summary>
    private void RefreshWindowTargets()
    {
        var windows = WindowEnumerator.Enumerate();

        // Preserve current selection identity across the refresh.
        var previous = SelectedCaptureTarget;

        var list = new List<CaptureTarget>(_cachedMonitors.Count + windows.Count);
        list.AddRange(_cachedMonitors);
        list.AddRange(windows);

        if (TargetListsEqual(CaptureTargets, list))
        {
            return;
        }

        // If the selected target was a window that has since closed, drop the selection.
        CaptureTargets = list;

        if (previous != null)
        {
            var match = list.FirstOrDefault(t => t.Kind == previous.Kind &&
                                                 t.MonitorIndex == previous.MonitorIndex &&
                                                 t.WindowHandle == previous.WindowHandle);
            SelectedCaptureTarget = match; // null when the selected window closed
        }
        else
        {
            SelectedCaptureTarget = list.FirstOrDefault();
        }

        // If we are broadcasting a window that has since closed, stop cleanly rather than
        // streaming a stale/black frame captured from a dead handle.
        if (_activeCaptureTarget is { Kind: CaptureTargetKind.Window } active &&
            !list.Any(t => t.Kind == active.Kind && t.WindowHandle == active.WindowHandle))
        {
            _ = SetDesktopCaptureActiveAsync(false);
            StatusText = "Host broadcast stopped: the captured window was closed.";
        }
    }

    private static bool TargetListsEqual(IReadOnlyList<CaptureTarget> a, IReadOnlyList<CaptureTarget> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Kind != b[i].Kind ||
                a[i].MonitorIndex != b[i].MonitorIndex ||
                a[i].WindowHandle != b[i].WindowHandle ||
                !string.Equals(a[i].DisplayName, b[i].DisplayName, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private void CaptureTargetsTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            RefreshWindowTargets();
        }
        catch
        {
            // Enumeration failure (e.g. session teardown) must never crash the UI thread.
        }
    }

    /// <summary>
    /// Periodic input-health poll driving the ADR-0002 watchdog. On the UI thread, so the state and
    /// banner mutations it triggers need no extra marshaling.
    /// </summary>
    private void InputHealthTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            _session.CheckInputHealth();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Input-health poll failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RefreshCaptureTargets()
    {
        try
        {
            PopulateCaptureTargets();
            PopulateCaptureAudioOptions();
            PopulateBroadcastNetworks();
            StatusText = $"Capture targets ({CaptureTargets.Count}), audio devices ({CaptureAudioOptions.Count}), and networks ({BroadcastNetworks.Count}) refreshed.";
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Error refreshing capture targets: {ex.Message}", ex);
            StatusText = $"Refresh capture targets failed: {ex.Message}";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ToggleRecordAsync()
    {
        if (IsRecordingCloseOverlayVisible) return;

        if (_session.IsRecording)
        {
            IsRecordButtonEnabled = false;
            RecordButtonText = "Finalizing...";
            StatusText = "Finalizing recording and writing container...";
            try
            {
                await _session.StopRecordingAsync();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Error stopping recording: {ex.Message}", ex);
                StatusText = $"Error stopping recording: {ex.Message}";
            }
            finally
            {
                IsRecordButtonEnabled = true;
            }
        }
        else
        {
            if (!_session.IsConnected || !_session.CurrentFormat.HasVideo)
            {
                StatusText = "Please connect to an active OMT stream before recording.";
                return;
            }

            string outDir = OutputDirectory?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(outDir))
            {
                StatusText = "Please specify a valid destination folder before recording.";
                return;
            }

            var config = new RecordingConfig
            {
                ContainerFormat = SelectedContainerFormat,
                EncoderChoice = SelectedEncoderChoice,
                Quality = SelectedQualityPreset,
                OutputDirectory = outDir
            };

            bool started = _session.StartRecording(config);
            if (!started)
            {
                StatusText = "Failed to initiate recording. Ensure FFmpeg is available and destination folder is writable.";
            }
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task BrowseOutputDirectoryAsync()
    {
        if (IsRecordingCloseOverlayVisible || !IsRecordingConfigEnabled) return;

        if (PickFolderAsync != null)
        {
            try
            {
                var selected = await PickFolderAsync(OutputDirectory);
                if (!string.IsNullOrWhiteSpace(selected))
                {
                    OutputDirectory = selected;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Error browsing output directory: {ex.Message}", ex);
                StatusText = $"Folder selection cancelled or failed: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private void OpenOutputDirectory()
    {
        string dir = OutputDirectory?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(dir))
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Failed to open output directory: {ex.Message}", ex);
                StatusText = $"Unable to open folder: {ex.Message}";
            }
        }
        else
        {
            StatusText = "Destination folder path is not set.";
        }
    }

    [RelayCommand]
    private void KeepRecording()
    {
        IsRecordingCloseOverlayVisible = false;
        IsStopAndExitEnabled = true;
        IsKeepRecordingEnabled = true;
        IsSafeExitConfirmed = false;
        StatusText = "Exit cancelled. Recording continues.";
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task StopAndExitAsync()
    {
        IsStopAndExitEnabled = false;
        IsKeepRecordingEnabled = false;
        StopAndExitButtonText = "Finalizing File...";
        StatusText = "Stopping recording and finalizing file before exit...";

        try
        {
            await _session.StopRecordingAsync();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Error stopping recording during exit: {ex.Message}", ex);
            StatusText = $"Error stopping recording: {ex.Message}";
        }
        finally
        {
            IsSafeExitConfirmed = true;
            RequestClose?.Invoke();
        }
    }

    #endregion

    #region Internal Engine Event Handlers

    private void OnSourcesUpdated(List<OmtSourceInfo> sources)
    {
        PostToUIThread(() =>
        {
            if (_disposed) return;
            if (IsSourceDropDownOpen) return;

            if (DiscoveredSources.Count == sources.Count &&
                DiscoveredSources.Select(s => s.Address).SequenceEqual(sources.Select(s => s.Address)))
            {
                return;
            }

            string? prevAddress = SelectedSource?.Address;
            var newList = new List<OmtSourceInfo>(sources);
            DiscoveredSources = newList;

            if (!string.IsNullOrEmpty(prevAddress))
            {
                var match = newList.FirstOrDefault(s => s.Address == prevAddress);
                if (match != null)
                {
                    SelectedSource = match;
                }
                else if (newList.Count > 0)
                {
                    SelectedSource = newList[0];
                }
                else
                {
                    SelectedSource = null;
                }
            }
            else if (newList.Count > 0 && SelectedSource == null)
            {
                SelectedSource = newList[0];
            }
        });
    }

    private void OnStatusChanged(string status)
    {
        PostToUIThread(() =>
        {
            StatusText = status;
        });
    }

    private void OnSessionError(string error)
    {
        AppLogger.LogError($"Session error: {error}");
        PostToUIThread(() =>
        {
            StatusText = $"Error: {error}";
        });
    }

    private void OnVideoFrameAvailable(IntPtr pData, int dataLength, int width, int height, int stride, double fps, long timestamp)
    {
        if (!_session.IsConnected || _disposed)
            return;

        // 1. Submit raw video buffer to the hardware/Skia viewport control
        VideoFrameReceived?.Invoke(pData, dataLength, width, height, stride);

        // 2. Throttle HUD overlay updates (at most once every 500ms or on format change)
        long now = Environment.TickCount64;
        if (!_isLive || width != _lastWidth || height != _lastHeight || Math.Abs(fps - _lastFps) > 0.5 || now - _lastHudUpdateTick >= 500)
        {
            _isLive = true;
            _lastHudUpdateTick = now;
            _lastWidth = width;
            _lastHeight = height;
            _lastFps = fps;

            PostToUIThread(_updateVideoHudDelegate, DispatcherPriority.Normal);
        }
    }

    private void UpdateVideoHudOnUIThread()
    {
        if (!_session.IsConnected || _disposed) return;
        IsNoSignalOverlayVisible = false;
        LiveIndicatorBrush = LiveIndicatorActiveBrush;
        LiveStatusText = "LIVE";
        VideoSpecsText = $"{_lastWidth}x{_lastHeight} @ {_lastFps:F2} fps";
    }

    private void OnAudioLevelsUpdated(AudioLevelData levels)
    {
        if (!_session.IsConnected || _disposed)
            return;

        AudioLevelsUpdated?.Invoke(levels);
    }

    private void OnTelemetryUpdated(StreamTelemetry t)
    {
        if (!_session.IsConnected || _disposed)
            return;

        PostToUIThread(() =>
        {
            if (!_session.IsConnected || _disposed) return;
            AudioTelemetryText = $"Audio: {t.AudioFormatText}";
            BitrateText = $"Bitrate: {t.BitrateMbps:F1} Mbps";
            DroppedFramesText = $"Dropped: {t.FramesDropped}";
        });
    }

    private void OnRecordingStarted(string filePath)
    {
        AppLogger.LogInfo($"Recording started: {filePath}");
        PostToUIThread(() =>
        {
            IsRecordButtonEnabled = true;
            IsRecordingConfigEnabled = false;
            RecordButtonText = "■ STOP RECORDING";
            RecordButtonBackground = RecordStopBrush;
            RecordButtonAutomationName = "Stop Recording Stream";
            RecordStatsText = $"Recording to {Path.GetFileName(filePath)}";

            // A new take clears the previous verdict, then the panel re-projects.
            StopSummaryText = string.Empty;
            _recordUiTimer?.Start();
            ProjectRecordingHealth(_session.GetRecordingHealthSnapshot());
        });
    }

    private void OnRecordingStopped(RecordingStopSummary summary)
    {
        // DEF-01: this used to report Stopwatch wall clock, which counted the input-loss dead air (QA
        // measured 11.05s reported against 5.92s on disk). It now reports MEDIA duration — frames /
        // stamped fps — and logs wall clock separately, clearly labelled.
        AppLogger.LogInfo(
            $"Recording stopped: {summary.Path} (media duration: {summary.MediaDuration}, " +
            $"wall-clock duration: {summary.WallClockDuration} [includes input-loss dead air], size: {summary.FileSizeBytes} bytes)");
        AppLogger.LogInfo(RecordingHealth.BuildStopVerdict(summary));

        PostToUIThread(() =>
        {
            IsRecordingCloseOverlayVisible = false;
            _recordUiTimer?.Stop();
            IsRecordButtonEnabled = true;
            IsRecordingConfigEnabled = true;
            RecordButtonText = "● START RECORDING";
            RecordButtonBackground = RecordStartBrush;
            RecordButtonAutomationName = "Start Recording Stream";

            double mb = summary.FileSizeBytes / (1024.0 * 1024.0);
            RecordDurationText = "00:00:00";
            RecordStatsText = $"Saved: {Path.GetFileName(summary.Path)} ({mb:F1} MB, {summary.MediaDuration:mm\\:ss})";

            // FEAT-07: the stop-summary verdict, shown in the UI (and logged above) from the summary's
            // own snapshot, since the live session has already torn the transport down.
            StopSummaryText = RecordingHealth.BuildStopVerdict(summary);
            ProjectRecordingHealth(summary.ToHealthSnapshot());

            StatusText = $"Recording completed and saved to {summary.Path}";
        });
    }

    private void OnRecordingError(string error)
    {
        AppLogger.LogError($"Recording error: {error}");
        PostToUIThread(() =>
        {
            IsRecordingCloseOverlayVisible = false;
            _recordUiTimer?.Stop();
            IsRecordButtonEnabled = true;
            IsRecordingConfigEnabled = true;
            RecordButtonText = "● START RECORDING";
            RecordButtonBackground = RecordStartBrush;
            RecordButtonAutomationName = "Start Recording Stream";
            RecordDurationText = "00:00:00";
            RecordStatsText = "Ready";
            StatusText = $"Recording Error: {error}";
        });
    }

    private void RecordUiTimer_Tick(object? sender, EventArgs e)
    {
        if (_session.IsRecording)
        {
            // Periodic low-disk protection (internally throttled to ~30s; cheap DriveInfo query).
            _session.CheckDiskSpaceAndProtect();

            var elapsed = _session.RecordingElapsedTime;
            RecordDurationText = elapsed.ToString(@"hh\:mm\:ss");

            long frames = _session.RecordingFramesWritten;
            RecordStatsText = $"Writing... {frames:N0} frames";

            // FEAT-07: refresh the recording-health projection once per tick (Interlocked reads plus at
            // most one file-size query — no per-frame work, and we are already on the UI thread).
            ProjectRecordingHealth(_session.GetRecordingHealthSnapshot());
        }
    }

    /// <summary>
    /// Projects one health snapshot onto the panel (FEAT-07). Pure mapping — thresholds and text come
    /// from <see cref="RecordingHealth"/>, so the UI and the headless tests agree by construction.
    /// </summary>
    private void ProjectRecordingHealth(RecordingHealthSnapshot snapshot)
    {
        RecordingHealthEncoderText = RecordingHealth.EncoderText(snapshot);
        RecordingHealthBitrateText = RecordingHealth.BitrateText(snapshot);
        RecordingHealthDropsText = RecordingHealth.DropsText(snapshot);
        RecordingHealthDriftText = RecordingHealth.DriftText(snapshot);

        IsRecordingDropWarning = RecordingHealth.IsDropWarning(snapshot);
        IsRecordingDriftWarning = RecordingHealth.IsDriftWarning(snapshot);
        RecordingDropsForeground = IsRecordingDropWarning ? StudioPalette.MeterDanger : StudioPalette.ButtonLightForeground;
        RecordingDriftForeground = IsRecordingDriftWarning ? StudioPalette.MeterWarnHigh : StudioPalette.ButtonLightForeground;

        DiagnosticTailText = snapshot.DiagnosticTail ?? string.Empty;

        // The panel stays up after a stop so the verdict remains readable, and hides once the verdict
        // is cleared by the next take.
        IsRecordingHealthVisible = snapshot.IsRecording || !string.IsNullOrEmpty(StopSummaryText);
    }

    partial void OnStopSummaryTextChanged(string value)
    {
        IsStopSummaryVisible = !string.IsNullOrEmpty(value);
    }

    [RelayCommand]
    private void ToggleDiagnosticsDrawer()
    {
        IsDiagnosticsDrawerOpen = !IsDiagnosticsDrawerOpen;
    }

    #endregion

    /// <summary>
    /// Projects the broadcast state machine onto the presentation layer — the single place state
    /// becomes button text, brushes, automation names and selector locks (ADR-0001 decision 3).
    /// Display text is an output of state here, never an input to it.
    /// </summary>
    private void ApplyBroadcastStateProjection()
    {
        BroadcastState state = _broadcastState.State;
        bool receiving = _broadcastState.IsSessionActive;

        // ADR-0002 input-loss alarm banner — part of this single projection, not set ad hoc.
        bool reconnecting = state == BroadcastState.Reconnecting;
        bool inputLost = state == BroadcastState.InputLost;
        IsInputLossBannerVisible = reconnecting || inputLost;
        InputLossBannerText = inputLost
            ? "INPUT LOST — no video arrived within the input-loss window. The take was finalized and auto-reconnect has given up; reconnect the source to continue."
            : "INPUT LOST — no video is arriving. Finalized the current segment; reconnecting automatically…";

        // Connect / Disconnect action button.
        ConnectButtonText = receiving ? "Disconnect" : "Connect";
        ConnectButtonBackground = receiving ? DisconnectBrush : ConnectBrush;
        ConnectButtonForeground = receiving ? StudioPalette.ButtonWhiteForeground : StudioPalette.ButtonDarkForeground;
        ConnectButtonAutomationName = receiving ? "Disconnect Stream" : "Connect Stream";

        // Test-signal toggle.
        bool testSignalActive = state == BroadcastState.TestSignal;
        IsTestSignalActive = testSignalActive;
        TestSignalBackground = testSignalActive ? TestSignalActiveBrush : TestSignalInactiveBrush;
        TestSignalForeground = testSignalActive ? StudioPalette.ButtonWhiteForeground : StudioPalette.ButtonLightForeground;
        TestSignalAutomationName = testSignalActive ? "Disable Test Signal Generator" : "Enable Test Signal Generator";

        // Host-broadcast toggle.
        bool broadcasting = _broadcastState.IsBroadcasting;
        IsDesktopCaptureActive = broadcasting;
        DesktopCaptureBackground = broadcasting ? TestSignalActiveBrush : TestSignalInactiveBrush;
        DesktopCaptureForeground = broadcasting ? StudioPalette.ButtonWhiteForeground : StudioPalette.ButtonLightForeground;
        DesktopCaptureAutomationName = broadcasting ? "Disable Host Broadcast" : "Enable Host Broadcast";

        // Selector locks, derived once here from the state machine (DESIGN §7).
        IsSourceSelectorEnabled = _broadcastState.IsSourceSelectorEnabled;
        IsCaptureSelectorEnabled = _broadcastState.IsCaptureSelectorEnabled;
    }

    /// <summary>
    /// Reacts to one module transition: re-project the presentation, and when a receive mode is
    /// left, clear the viewport/meters and reset the offline HUD readouts (<see cref="ResetOfflineHud"/>)
    /// — replacing the previously scattered <c>ResetOfflineState()</c> call sites.
    /// </summary>
    private void OnBroadcastStateChanged(object? sender, BroadcastStateChangedEventArgs e)
    {
        ApplyBroadcastStateProjection();

        if (IsSessionMode(e.OldState) && !IsSessionMode(e.NewState))
        {
            ViewportClearRequested?.Invoke();
            MetersResetRequested?.Invoke();
            ResetOfflineHud();
        }
    }

    /// <summary>
    /// True for any state in which a session is attached — the two receiving modes plus the ADR-0002
    /// outage states (frames have stopped, but the session is still attached, so nothing is torn down).
    /// </summary>
    private static bool IsSessionMode(BroadcastState state) =>
        state is BroadcastState.NetworkConnected or BroadcastState.TestSignal
               or BroadcastState.Reconnecting or BroadcastState.InputLost;

    /// <summary>
    /// Resets the viewport HUD and telemetry readouts shown while no stream is being received.
    /// Button text/brushes are no longer reset here — they are projections of the state machine.
    /// </summary>
    private void ResetOfflineHud()
    {
        _isLive = false;
        _lastWidth = 0;
        _lastHeight = 0;
        _lastFps = 0;
        _lastHudUpdateTick = 0;
        IsRecordingConfigEnabled = true;
        IsNoSignalOverlayVisible = true;
        LiveIndicatorBrush = LiveIndicatorInactiveBrush;
        LiveStatusText = "OFFLINE";
        VideoSpecsText = "-- x -- @ -- fps";
        SourceNameText = "";
        AudioTelemetryText = "Audio: --";
        BitrateText = "Bitrate: 0.0 Mbps";
        DroppedFramesText = "Dropped: 0";
    }

    private void UnhookEvents()
    {
        _broadcastState.StateChanged -= OnBroadcastStateChanged;
        _discoveryService.SourcesUpdated -= OnSourcesUpdated;

        _session.VideoFrameAvailable -= OnVideoFrameAvailable;
        _session.AudioLevelsUpdated -= OnAudioLevelsUpdated;
        _session.TelemetryUpdated -= OnTelemetryUpdated;
        _session.StatusChanged -= OnStatusChanged;
        _session.ErrorOccurred -= OnSessionError;

        _session.RecordingStarted -= OnRecordingStarted;
        _session.RecordingStopped -= OnRecordingStopped;
        _session.RecordingError -= OnRecordingError;

        _session.InputLost -= OnSessionInputLost;
        _session.InputRecovered -= OnSessionInputRecovered;
        _session.InputRecoveryFailed -= OnSessionInputRecoveryFailed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _recordUiTimer?.Stop();
        if (_recordUiTimer != null)
        {
            _recordUiTimer.Tick -= RecordUiTimer_Tick;
        }
        _captureTargetsTimer?.Stop();
        if (_captureTargetsTimer != null)
        {
            _captureTargetsTimer.Tick -= CaptureTargetsTimer_Tick;
        }
        _inputHealthTimer?.Stop();
        if (_inputHealthTimer != null)
        {
            _inputHealthTimer.Tick -= InputHealthTimer_Tick;
        }
        UnhookEvents();
        PickFolderAsync = null;
        VideoFrameReceived = null;
        ViewportClearRequested = null;
        AudioLevelsUpdated = null;
        MetersResetRequested = null;
        RequestClose = null;

        _desktopCapture?.Dispose();
        _desktopCapture = null;

        if (_ownsDependencies)
        {
            _discoveryService.Dispose();
            _session.Dispose();
        }
    }

    /// <summary>
    /// Adapts <see cref="CaptureSession"/> to the state machine's <see cref="IBroadcastSessionEffects"/>
    /// seam, so the module owns the stop-recording-first ordering without knowing the session type.
    /// </summary>
    private sealed class SessionEffects : IBroadcastSessionEffects
    {
        private readonly CaptureSession _session;

        public SessionEffects(CaptureSession session) => _session = session;

        public bool IsRecording => _session.IsRecording;

        public Task StopRecordingAsync() => _session.StopRecordingAsync();

        public void DisconnectSession() => _session.Disconnect();
    }
}

/// <summary>
/// One selectable ASIO stereo pair offered by the input-pair dropdown, e.g. <c>In 3/4</c>.
/// <paramref name="FirstChannelIndex"/> is the zero-based ASIO input channel that becomes Left;
/// the next one becomes Right (the capture source duplicates it for a trailing mono channel).
/// </summary>
public sealed record AsioInputPairOption(int FirstChannelIndex, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Outcome of one <see cref="OmtCaptureStudio.Services.AsioInputChannelProbe"/> run, cached per driver
/// <see cref="Guid"/> in the view model so re-opening the dropdown never re-opens the driver.
/// </summary>
/// <param name="Channels">Input channels the driver reported (empty when the probe failed).</param>
/// <param name="Reason">Why the probe failed, or <c>null</c> on success.</param>
internal sealed record AsioPairProbeResult(IReadOnlyList<AsioInputChannel> Channels, string? Reason);
