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
    private readonly bool _ownsDependencies;

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

    // Host capture audio source selection (presets + individual WASAPI hardware endpoints)
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
    }

    private void HookEvents()
    {
        _discoveryService.SourcesUpdated += OnSourcesUpdated;

        _session.VideoFrameAvailable += OnVideoFrameAvailable;
        _session.AudioLevelsUpdated += OnAudioLevelsUpdated;
        _session.TelemetryUpdated += OnTelemetryUpdated;
        _session.StatusChanged += OnStatusChanged;
        _session.ErrorOccurred += OnSessionError;

        _session.RecordingStarted += OnRecordingStarted;
        _session.RecordingStopped += OnRecordingStopped;
        _session.RecordingError += OnRecordingError;
    }

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
        if (_desktopCapture is { IsRunning: true } activeBroadcast && value != null)
        {
            try
            {
                activeBroadcast.SetAudioSource(HostAudioSourceFactory.Create(value));
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
            if (_session.IsConnected || ConnectButtonText == "Disconnect")
            {
                if (_session.IsRecording)
                {
                    StatusText = "Finalizing active recording before disconnecting...";
                    await _session.StopRecordingAsync();
                }

                _session.Disconnect();
                AppLogger.LogInfo("Disconnected from stream.");
                ViewportClearRequested?.Invoke();
                MetersResetRequested?.Invoke();
                ResetOfflineState();

                IsTestSignalActive = false;
                TestSignalBackground = TestSignalInactiveBrush;
                TestSignalForeground = StudioPalette.ButtonLightForeground;
                TestSignalAutomationName = "Enable Test Signal Generator";
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
                _session.Connect(info.Address);
                ConnectButtonText = "Disconnect";
                ConnectButtonBackground = DisconnectBrush;
                ConnectButtonForeground = StudioPalette.ButtonWhiteForeground;
                ConnectButtonAutomationName = "Disconnect Stream";
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
                if (_session.IsConnected)
                {
                    if (_session.IsRecording)
                    {
                        StatusText = "Finalizing active recording before switching source...";
                        await _session.StopRecordingAsync();
                    }
                    _session.Disconnect();
                }

                IsTestSignalActive = true;
                TestSignalBackground = TestSignalActiveBrush;
                TestSignalForeground = StudioPalette.ButtonWhiteForeground;
                TestSignalAutomationName = "Disable Test Signal Generator";
                SourceNameText = "Local Test Pattern (SMPTE)";
                StatusText = "Connected to internal synthetic test generator.";
                AppLogger.LogInfo("Started local test signal generator.");

                _session.Connect(new SyntheticPatternSource());

                ConnectButtonText = "Disconnect";
                ConnectButtonBackground = DisconnectBrush;
                ConnectButtonForeground = StudioPalette.ButtonWhiteForeground;
                ConnectButtonAutomationName = "Disconnect Stream";
            }
            else
            {
                IsTestSignalActive = false;
                TestSignalBackground = TestSignalInactiveBrush;
                TestSignalForeground = StudioPalette.ButtonLightForeground;
                TestSignalAutomationName = "Enable Test Signal Generator";
                StatusText = "Test pattern stopped.";

                if (_session.IsConnected)
                {
                    if (_session.IsRecording)
                    {
                        StatusText = "Finalizing active recording before disconnecting...";
                        await _session.StopRecordingAsync();
                    }
                    _session.Disconnect();
                    AppLogger.LogInfo("Disconnected test signal generator.");
                }

                ViewportClearRequested?.Invoke();
                MetersResetRequested?.Invoke();
                ResetOfflineState();
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
                    audioSource = HostAudioSourceFactory.Create(SelectedCaptureAudioOption);
                }
                catch
                {
                    source.Dispose();
                    throw;
                }

                string? bindIp = SelectedBroadcastNetwork?.IpAddress;
                _desktopCapture = new OmtDesktopCaptureGenerator(
                    source,
                    sourceName: "OmtCaptureStudio_DesktopCapture",
                    fps: 30,
                    audioSource: audioSource,
                    bindIpAddress: bindIp);
                _desktopCapture.Start();
                _activeCaptureTarget = target;
                IsDesktopCaptureActive = true;
                DesktopCaptureBackground = TestSignalActiveBrush;
                DesktopCaptureForeground = StudioPalette.ButtonWhiteForeground;
                DesktopCaptureAutomationName = "Disable Host Broadcast";
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
                IsDesktopCaptureActive = false;
                DesktopCaptureBackground = TestSignalInactiveBrush;
                DesktopCaptureForeground = StudioPalette.ButtonLightForeground;
                DesktopCaptureAutomationName = "Enable Host Broadcast";
                StatusText = "Host capture stopped.";
                AppLogger.LogInfo("Stopped host desktop capture.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Error toggling desktop capture: {ex.Message}", ex);
            IsDesktopCaptureActive = false;
            DesktopCaptureBackground = TestSignalInactiveBrush;
            DesktopCaptureForeground = StudioPalette.ButtonLightForeground;
            DesktopCaptureAutomationName = "Enable Host Broadcast";
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

        CaptureAudioOptions = list;

        if (previous != null)
        {
            var match = list.FirstOrDefault(o =>
                o.Mode == previous.Mode &&
                string.Equals(o.DeviceId, previous.DeviceId, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(o => o.Mode == previous.Mode && o.DeviceId == null)
                ?? list[0];
            SelectedCaptureAudioOption = match;
        }
        else
        {
            SelectedCaptureAudioOption = list[0];
        }
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
            _recordUiTimer?.Start();
        });
    }

    private void OnRecordingStopped(string filePath, TimeSpan duration, long fileSize)
    {
        AppLogger.LogInfo($"Recording stopped: {filePath} (Duration: {duration}, Size: {fileSize} bytes)");
        PostToUIThread(() =>
        {
            IsRecordingCloseOverlayVisible = false;
            _recordUiTimer?.Stop();
            IsRecordButtonEnabled = true;
            IsRecordingConfigEnabled = true;
            RecordButtonText = "● START RECORDING";
            RecordButtonBackground = RecordStartBrush;
            RecordButtonAutomationName = "Start Recording Stream";

            double mb = fileSize / (1024.0 * 1024.0);
            RecordDurationText = "00:00:00";
            RecordStatsText = $"Saved: {Path.GetFileName(filePath)} ({mb:F1} MB, {duration:mm\\:ss})";

            StatusText = $"Recording completed and saved to {filePath}";
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
        }
    }

    #endregion

    private void ResetOfflineState()
    {
        _isLive = false;
        _lastWidth = 0;
        _lastHeight = 0;
        _lastFps = 0;
        _lastHudUpdateTick = 0;
        IsRecordingConfigEnabled = true;
        ConnectButtonText = "Connect";
        ConnectButtonBackground = ConnectBrush;
        ConnectButtonForeground = StudioPalette.ButtonDarkForeground;
        ConnectButtonAutomationName = "Connect Stream";
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
        _discoveryService.SourcesUpdated -= OnSourcesUpdated;

        _session.VideoFrameAvailable -= OnVideoFrameAvailable;
        _session.AudioLevelsUpdated -= OnAudioLevelsUpdated;
        _session.TelemetryUpdated -= OnTelemetryUpdated;
        _session.StatusChanged -= OnStatusChanged;
        _session.ErrorOccurred -= OnSessionError;

        _session.RecordingStarted -= OnRecordingStarted;
        _session.RecordingStopped -= OnRecordingStopped;
        _session.RecordingError -= OnRecordingError;
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
}
