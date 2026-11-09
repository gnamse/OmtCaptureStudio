using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Sources;

using OmtCaptureStudio.Services.Forensics;
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

    // ADR-0011: the SHELL state — which of the three modes the window is showing. Headless and owned
    // here; the views only project it. Defaults to ModeSelection so the picker is always what opens.
    private readonly AppModeState _appModeState;

    /// <summary>
    /// ADR-0011: the mode the operator last CHOSE (the value that pre-highlights a picker card).
    /// Null means "never chosen one yet" — the picker then highlights nothing. Deliberately NOT the
    /// live <see cref="CurrentAppMode"/>: returning to the picker must not erase the memory of which
    /// mode they used (ADR-0011 decision 1, "no auto-skip" + "last mode pre-highlighted").
    /// </summary>
    private AppMode? _lastUsedAppMode;

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

    // ---- ASIO input pair selection (NET-019: the policy lives in AsioInputPairSelector) ---------
    // ASIO interfaces routinely expose 8/16/32 inputs, so "In 1/2" must be a choice, not a given.
    // The list of real pairs comes from opening the driver *briefly* (no buffers, no start), which
    // is why every bit of this is lazy: nothing here may run during ViewModel construction, and a
    // driver locked by another application (or absent entirely) must degrade to plain In 1/2.
    // The probe lifecycle, the cache, the retry and the suppression invariant all live in the
    // selector; this view model only applies the projection it emits.
    private readonly AsioInputPairSelector _asioSelector;

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

    /// <summary>
    /// NET-008: the quiet "Segment N complete - continuing" notice shown at a ROLLOVER boundary.
    /// Deliberately separate from <c>StopSummaryText</c>: an unattended take rolling every 15 minutes
    /// must not flash an alarming stop verdict each time - the take did not stop.
    /// </summary>
    [ObservableProperty]
    private string _segmentContinuationText = string.Empty;

    [ObservableProperty]
    private bool _isSegmentContinuationVisible;

    /// <summary>
    /// The continuation notice uses the healthy token, deliberately distinct from the stop verdict's
    /// text colour: a rollover is a normal scheduled event, not a failure (NET-008).
    /// </summary>
    public IBrush SegmentContinuationForeground { get; } = StudioPalette.MeterNormal;

    // ------------------------------------------------------------------------------------------
    // NET-010: FFmpeg preflight. FFmpeg is the recording feature's only external dependency; when it
    // is missing or unrunnable, recording is unavailable but preview/discovery/broadcast keep working,
    // so this is a persistent status warning rather than a modal.
    // ------------------------------------------------------------------------------------------

    /// <summary>True when the startup preflight found no runnable ffmpeg - recording is unavailable.</summary>
    [ObservableProperty]
    private bool _isRecordingUnavailable;

    /// <summary>The persistent warning text, including the locations that were checked.</summary>
    [ObservableProperty]
    private string _recordingUnavailableText = string.Empty;

    [ObservableProperty]
    private string _recordingHealthFfmpegText = "—";

    /// <summary>The resolved ffmpeg version (NET-010), stamped into every projected health snapshot.</summary>
    public string FfmpegVersion { get; private set; } = string.Empty;

    /// <summary>The warning token, matching the recording path's failure colour.</summary>
    public IBrush RecordingUnavailableForeground { get; } = StudioPalette.MeterDanger;

    // ------------------------------------------------------------------------------------------
    // NET-012 (distill): progressive disclosure. The Operate-mode primary path is
    // pick source -> connect -> record; everything else is configuration behind smart defaults
    // (MP4 / AutoHardware / High / no rollover / no schedule - which are already the defaults).
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// True when the recording bar's ADVANCED tier is expanded. Collapsed by default so the default
    /// view is preset -> folder -> record and nothing else.
    /// </summary>
    [ObservableProperty]
    private bool _isAdvancedRecordingOptionsExpanded;

    /// <summary>
    /// NET-023: the manual "Check for updates" action, and the deferred startup check's workhorse.
    /// Ask-first: this REPORTS an available release and stops. Nothing is downloaded, verified or started
    /// here - that is <see cref="InstallUpdateAsync"/>, reached only by an explicit action or by the
    /// opt-in toggle.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateCheckResult? checkedResult = await RunUpdateCheckAsync().ConfigureAwait(false);
        if (checkedResult is null) return;

        // Narrowed into a non-null local before it is captured: a lambda capturing a nullable
        // local re-widens the type and the compiler warns, which is correct - it cannot see that
        // the closure only runs after this check.
        UpdateCheckResult result = checkedResult;

        PostToUIThread(() =>
        {
            _lastUpdateCheckUtc = DateTime.UtcNow;
            ApplyUpdateCheckResult(result);
            PersistSettings();
        });
    }

    /// <summary>
    /// Runs the once-per-launch check and returns its result, or null when no service is wired. Public so
    /// the headless suite drives the real policy with a fake feed and no window.
    /// </summary>
    public async Task<UpdateCheckResult?> RunUpdateCheckAsync()
    {
        UpdateService? svc = _updateService;
        if (svc is null)
        {
            StatusText = "Update checks are unavailable in this build.";
            return null;
        }

        return await svc.CheckForUpdateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Projects a check result onto the banner. A found release is REMEMBERED, not installed. Public and
    /// synchronous so the projection is assertable headlessly.
    /// </summary>
    public void ApplyUpdateCheckResult(UpdateCheckResult result)
    {
        _pendingUpdate = result.Outcome == UpdateCheckOutcome.UpdateAvailable ? result.Release : null;
        _verifiedInstallerPath = null;   // a new check invalidates any earlier download

        if (_pendingUpdate is null)
        {
            IsUpdateBannerVisible = false;
            UpdateBannerText = string.Empty;
            if (result.Outcome == UpdateCheckOutcome.UpToDate) StatusText = result.Detail;
            return;
        }

        UpdateBannerText = $"v{_pendingUpdate.Version} available";
        IsUpdateBannerVisible = true;
        StatusText = result.Detail;
    }

    /// <summary>
    /// NET-023: the deferred startup check. With the toggle OFF (the default) it only reveals the banner.
    /// With it ON it proceeds through download, SHA-256 verification and install - the opt-in path.
    /// </summary>
    public async Task RunDeferredUpdateCheckAsync()
    {
        if (_updateService is null) return;

        UpdateCheckResult result = await _updateService.CheckForUpdateAsync().ConfigureAwait(false);
        PostToUIThread(() =>
        {
            _lastUpdateCheckUtc = DateTime.UtcNow;
            ApplyUpdateCheckResult(result);
            PersistSettings();
        });

        if (result.Outcome != UpdateCheckOutcome.UpdateAvailable) return;
        if (!UpdatesAutoInstall) return;   // ask-first: banner only

        await InstallUpdateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads, verifies and starts the installer for the pending release. Refuses on the portable
    /// channel - a portable copy must never overwrite itself while running.
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        UpdateService? svc = _updateService;
        UpdateRelease? release = _pendingUpdate;
        if (svc is null || release is null) return;

        if (svc.Channel == InstallChannel.Portable)
        {
            // Portable installs are notify-only. The releases page is the whole interaction.
            PostToUIThread(() => StatusText = "This is a portable copy - opening the releases page instead.");
            ViewUpdateRelease();
            return;
        }

        string? installer = _verifiedInstallerPath;
        if (installer is null)
        {
            installer = await svc.DownloadAndVerifyAsync(release).ConfigureAwait(false);
            if (installer is null)
            {
                PostToUIThread(() => StatusText = "The update could not be verified and was refused.");
                return;
            }
            _verifiedInstallerPath = installer;
        }

        bool started = svc.TryStartInstall(installer, out string detail);
        PostToUIThread(() => StatusText = detail);
        if (started)
        {
            // Inno closes us, replaces the files and relaunches - there is deliberately no teardown here.
            AppLogger.LogInfo($"Update installer started from '{installer}'. The app will close for replacement.");
        }
    }

    /// <summary>Opens the releases page - the portable channel's only action.</summary>
    [RelayCommand]
    private void ViewUpdateRelease() => OpenReleasesPage();

    private void OpenReleasesPage()
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(UpdateService.BuildReleasesPageStartInfo());
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the releases page: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------------------------------
    // NET-011: an optional SECOND destination. Non-empty means the take fans out through a composite
    // sink, so a primary that fills or fails is shed while the take carries on to the backup folder.
    // ------------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _backupOutputDirectory = "";

    [ObservableProperty]
    private string _diagnosticTailText = "";

    [ObservableProperty]
    private bool _isDiagnosticsDrawerOpen;

    // NET-020: diagnostics bundle export. The offer rides the existing next-launch notice - it is
    // DERIVED from the same fact (dumps present) rather than duplicating the notice, so the operator is
    // not told twice.
    [ObservableProperty]
    private string _diagnosticsBundleStatusText = string.Empty;

    [ObservableProperty]
    private bool _isDiagnosticsBundleStatusVisible;

    [ObservableProperty]
    private bool _isDiagnosticsBundleOffered;

    [ObservableProperty]
    private string _diagnosticsBundleOfferText = string.Empty;

    private int _pendingCrashDumpCount;

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

    // ------------------------------------------------------------------------------------------
    // NET-008: scheduled / segmented capture + named presets.
    //
    // Every control below lives inside the existing `IsRecordingConfigEnabled` region, so a mid-flight
    // change can never restart a running take (the live-changing-selector locking rule).
    // ------------------------------------------------------------------------------------------

    /// <summary>The named preset catalog, plus "Custom" so the picker can never lie about the state.</summary>
    public IReadOnlyList<string> RecordingPresetNames { get; } = BuildRecordingPresetNames();

    /// <summary>The selected preset name. Selecting one applies its axis; editing any control = Custom.</summary>
    [ObservableProperty]
    private string _selectedRecordingPreset = RecordingPresets.CustomName;

    public static IReadOnlyList<RecordingChoiceOption> RolloverMinuteOptions { get; } = new RecordingChoiceOption[]
    {
        new("Off", 0), new("1 min", 1), new("5 min", 5), new("10 min", 10),
        new("15 min", 15), new("30 min", 30), new("60 min", 60)
    };

    public static IReadOnlyList<RecordingChoiceOption> RolloverGigabyteOptions { get; } = new RecordingChoiceOption[]
    {
        new("Off", 0), new("1 GB", 1), new("2 GB", 2), new("5 GB", 5), new("10 GB", 10)
    };

    public static IReadOnlyList<RecordingChoiceOption> MaxDurationOptions { get; } = new RecordingChoiceOption[]
    {
        new("Off", 0), new("15 min", 15), new("30 min", 30), new("1 h", 60),
        new("2 h", 120), new("4 h", 240), new("8 h", 480)
    };

    public static IReadOnlyList<RecordingChoiceOption> ScheduleDelayOptions { get; } = new RecordingChoiceOption[]
    {
        new("Off", 0), new("1 min", 1), new("5 min", 5), new("15 min", 15), new("30 min", 30), new("60 min", 60)
    };

    [ObservableProperty]
    private RecordingChoiceOption _selectedRolloverMinutes = RolloverMinuteOptions[0];

    [ObservableProperty]
    private RecordingChoiceOption _selectedRolloverGigabytes = RolloverGigabyteOptions[0];

    [ObservableProperty]
    private RecordingChoiceOption _selectedMaxDuration = MaxDurationOptions[0];

    [ObservableProperty]
    private RecordingChoiceOption _selectedScheduleDelay = ScheduleDelayOptions[0];

    /// <summary>
    /// NET-016: "stop after N minutes". Deliberately the SAME shape as START IN (a delay measured from
    /// the moment the take is armed) rather than a time-of-day picker: the start precedent is
    /// delay-based, and a delay means the same thing whether the operator armed at 2 p.m. or 3 a.m.
    /// The horizon is longer than START IN's because a take is usually longer than a wait.
    /// </summary>
    public static IReadOnlyList<RecordingChoiceOption> StopAfterOptions { get; } = new RecordingChoiceOption[]
    {
        new("Off", 0), new("15 min", 15), new("30 min", 30), new("1 h", 60),
        new("2 h", 120), new("4 h", 240), new("8 h", 480)
    };

    [ObservableProperty]
    private RecordingChoiceOption _selectedStopAfter = StopAfterOptions[0];

    /// <summary>True while a preset is being applied, so the per-axis hooks don't flip the picker back.</summary>
    private bool _applyingRecordingPreset;

    // ============================================================================================
    // ADR-0006: operator settings persistence.
    //
    // The composition root attaches the store BEFORE this ViewModel is bound to the window, so the
    // restored values are already in place for the first render (no visible flicker from default to
    // restored). Persisting rides the existing change hooks below: one immediate full-file write per
    // committed change - the file is a few hundred bytes, so a debounce would buy nothing and would
    // introduce a window in which a crash loses the last change.
    // ============================================================================================

    private SettingsStore? _settingsStore;

    /// <summary>While true, changes are APPLIED but not written back - restoring must be silent.</summary>
    private bool _isRestoringSettings;

    // NET-023 auto-update. Ask-first: a check only ever REPORTS; downloading and installing are separate,
    // operator-authorised steps.
    private UpdateService? _updateService;
    private UpdateRelease? _pendingUpdate;
    private string? _verifiedInstallerPath;
    private DateTime? _lastUpdateCheckUtc;

    // ------------------------------------------------------------------------------------------
    // NET-016: the remaining-time readout.
    //
    // The countdown is projected on the EXISTING UI health tick from the existing pure
    // RecordingBoundaries arithmetic - no new timer. The active take's clock is held here because the
    // ViewModel is what armed the take; nothing on the transport path is touched.
    // ------------------------------------------------------------------------------------------

    /// <summary>The config the ACTIVE take was armed with - the boundary arithmetic reads this, not the UI.</summary>
    private RecordingConfig? _activeTakeConfig;

    private long _activeTakeStartUtcTicks;

    /// <summary>Survives the take clock: a rollover restarts the segment, not the take.</summary>
    private long _activeSegmentStartUtcTicks;

    /// <summary>Last rendered countdown, so an unchanged second does not rebuild the string.</summary>
    private int _lastRemainingSeconds = -1;

    private RecordingBoundary _lastRemainingBoundary = RecordingBoundary.None;

    [ObservableProperty]
    private string _remainingTimeText = string.Empty;

    [ObservableProperty]
    private bool _isRemainingTimeVisible;

    /// <summary>
    /// ADR-0006: the persisted values the recording UI does not expose - the ADR-0002 watchdog
    /// thresholds, the filename prefix and the scheduled instants (NET-016 gives the last one a control).
    /// A new take is seeded from these, so a restored value is HONOURED rather than merely stored.
    /// </summary>
    private RecordingConfig _operatorDefaults = new();

    /// <summary>
    /// Applies restored settings and attaches the store. Called once, by the composition root, before
    /// the window binds. Nothing is written here - nothing has changed yet.
    /// </summary>
    public void AttachSettings(SettingsStore store, OperatorSettings restored)
    {
        // BOTH guards, for two different reasons: _isRestoringSettings stops any of these assignments
        // writing a file (nothing has changed yet), and _applyingRecordingPreset stops the preset-name
        // assignment below from APPLYING the preset and overwriting a restored axis with the preset's
        // idea of it - the file's per-axis values are the authority.
        _isRestoringSettings = true;
        _applyingRecordingPreset = true;
        try
        {
            _operatorDefaults = SettingsStore.ToConfig(restored, _operatorDefaults);

            // ADR-0011: remember which card to pre-highlight. Deliberately does NOT enter that mode —
            // the picker is always what opens (decision 1), and silently skipping it would hide the
            // choice the ADR says the operator must make.
            _lastUsedAppMode = restored.LastAppMode;

            // NET-023: a missing value is NOT consent - ask-first is the default, so an older or
            // hand-edited file restores to false rather than to whatever the enum-less bool defaults to.
            UpdatesAutoInstall = restored.UpdatesAutoInstall ?? false;
            _lastUpdateCheckUtc = restored.LastUpdateCheckUtc;

            SelectedContainerFormat = _operatorDefaults.ContainerFormat;
            SelectedEncoderChoice = _operatorDefaults.EncoderChoice;
            SelectedQualityPreset = _operatorDefaults.Quality;
            OutputDirectory = _operatorDefaults.OutputDirectory;
            BackupOutputDirectory = _operatorDefaults.BackupOutputDirectory ?? string.Empty;

            SelectedRolloverMinutes = OptionFor(RolloverMinuteOptions, _operatorDefaults.SegmentRolloverMinutes);
            SelectedRolloverGigabytes = OptionFor(RolloverGigabyteOptions, _operatorDefaults.SegmentRolloverGigabytes);
            SelectedMaxDuration = OptionFor(MaxDurationOptions, _operatorDefaults.MaxDurationMinutes);
            SelectedScheduleDelay = OptionFor(ScheduleDelayOptions, _operatorDefaults.ScheduledStartDelayMinutes);
            SelectedStopAfter = OptionFor(StopAfterOptions, _operatorDefaults.ScheduledStopAfterMinutes);

            // Restored by name, never by index - and only if it is a name this build still knows.
            if (!string.IsNullOrWhiteSpace(restored.SelectedPreset)
                && RecordingPresetNames.Contains(restored.SelectedPreset))
            {
                SelectedRecordingPreset = restored.SelectedPreset!;
            }
        }
        finally
        {
            _applyingRecordingPreset = false;
            _isRestoringSettings = false;
        }

        // Attach last, so none of the assignments above could have written a file.
        _settingsStore = store;
    }

    /// <summary>
    /// ADR-0006: the config for a new take - the operator's persisted (non-UI) fields as the BASE, with
    /// the live UI selections layered on top. Seeding from <see cref="RecordingConfig"/>'s own defaults
    /// is what used to happen; now a restored watchdog threshold or filename prefix is actually used.
    /// </summary>
    private RecordingConfig BuildRecordingConfigFromUi()
    {
        var config = _operatorDefaults.Clone();

        config.ContainerFormat = SelectedContainerFormat;
        config.EncoderChoice = SelectedEncoderChoice;
        config.Quality = SelectedQualityPreset;
        config.OutputDirectory = OutputDirectory?.Trim() ?? string.Empty;
        config.BackupOutputDirectory = string.IsNullOrWhiteSpace(BackupOutputDirectory)
            ? null
            : BackupOutputDirectory.Trim();
        config.SegmentRolloverMinutes = SelectedRolloverMinutes?.Value ?? 0;
        config.SegmentRolloverGigabytes = SelectedRolloverGigabytes?.Value ?? 0;
        config.MaxDurationMinutes = SelectedMaxDuration?.Value ?? 0;
        config.ScheduledStartDelayMinutes = SelectedScheduleDelay?.Value ?? 0;
        config.ScheduledStopAfterMinutes = SelectedStopAfter?.Value ?? 0;

        return config;
    }

    /// <summary>
    /// ADR-0006: one immediate full-file write per committed change. Never throws and never surfaces a
    /// failure to the operator - a settings write must not be able to interrupt a take.
    /// </summary>
    private void PersistSettings()
    {
        if (_settingsStore is null || _isRestoringSettings) return;

        _operatorDefaults = BuildRecordingConfigFromUi();
        // FromConfig is a shared mapper whose signature other callers depend on, so the update fields are
        // STAMPED onto the result rather than widening it.
        OperatorSettings settings = SettingsStore.FromConfig(_operatorDefaults, SelectedRecordingPreset, _lastUsedAppMode);
        settings.UpdatesAutoInstall = UpdatesAutoInstall;
        settings.LastUpdateCheckUtc = _lastUpdateCheckUtc;
        _settingsStore.Save(settings);
    }

    // 8. Status & Telemetry
    [ObservableProperty]
    private string _statusText = "Ready. Discovering OMT sources on network...";

    [ObservableProperty]
    private string _audioTelemetryText = "Audio: --";

    [ObservableProperty]
    private string _bitrateText = "Bitrate: 0.0 Mbps";

    [ObservableProperty]
    private string _droppedFramesText = "Dropped: 0";

    // 8b. Updates (NET-023)
    /// <summary>Opt in to installing a found update automatically. Default OFF - ask-first.</summary>
    [ObservableProperty]
    private bool _updatesAutoInstall;

    /// <summary>Whether the update banner is showing. A projection of a found release, never set ad hoc.</summary>
    [ObservableProperty]
    private bool _isUpdateBannerVisible;

    /// <summary>The banner copy, e.g. "v1.0.0.23 available".</summary>
    [ObservableProperty]
    private string _updateBannerText = string.Empty;

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
        : this(new CaptureSession(), new OmtDiscoveryService(), new AppModeState(), ownsDependencies: true)
    {
    }

    public MainWindowViewModel(
        CaptureSession session,
        OmtDiscoveryService discoveryService,
        AppModeState? appModeState = null,
        bool ownsDependencies = false,
        UpdateService? updateService = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        // Optional so existing construction sites (tests, the parameterless ctor) keep compiling. A
        // standalone instance starts in ModeSelection, which is the ADR-0011 open state anyway.
        _appModeState = appModeState ?? new AppModeState();
        _updateService = updateService;
        _ownsDependencies = ownsDependencies;
        _broadcastEffects = new SessionEffects(_session);
        _updateVideoHudDelegate = UpdateVideoHudOnUIThread;

        // NET-019: the ASIO pair probe lifecycle lives in the selector. It gets the real probe, the
        // headless-safe UI hop, a live read of the operator's audio selection, and a driver
        // re-enumeration fallback. Nothing here opens a driver — the probe stays deferred until the
        // operator actually selects an ASIO device.
        _asioSelector = new AsioInputPairSelector(
            driver =>
            {
                IReadOnlyList<AsioInputChannel> channels = AsioInputChannelProbe.Probe(driver, out string? reason);
                return new AsioPairProbeResult(channels, reason);
            },
            PostToUIThreadSafely,
            () => SelectedCaptureAudioOption,
            () =>
            {
                try
                {
                    return AsioDriverEnumerator.Enumerate();
                }
                catch (Exception ex)
                {
                    AppLogger.LogInfo($"[ASIO] could not re-enumerate drivers for probing: {ex.Message}");
                    return Array.Empty<AsioDriverInfo>();
                }
            });

        _asioSelector.ProjectionChanged += ApplyAsioProjection;

        // NET-020: seed the next-launch offer. Cheap and fully guarded (two directory probes behind a
        // try/catch inside CrashForensics), never a hardware probe, so it is safe to run here.
        RefreshDiagnosticsOffer();

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
        ApplyAppModeProjection();
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
        _appModeState.ModeChanged += OnAppModeChanged;
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

        // NET-008: a manual container change means the config no longer matches a named preset.
        MarkRecordingPresetCustom();

        PersistSettings();   // ADR-0006
    }

    partial void OnSelectedSourceChanged(OmtSourceInfo? value)
    {
        SelectedSourceToolTip = value != null
            ? $"{value.DisplayName}\nAddress: {value.Address}"
            : null;
    }


    partial void OnIsRecordingCloseOverlayVisibleChanged(bool value)
    {
        IsMainContentEnabled = !value;
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
            // NET-010: refuse with the real reason up front, rather than letting the transport fail at
            // the worst moment - a start failure today surfaces only as a SinkError, by which time the
            // operator has usually walked away.
            if (IsRecordingUnavailable)
            {
                StatusText = RecordingUnavailableText;
                return;
            }

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

            DateTime nowUtc = DateTime.UtcNow;

            // ADR-0006: built from the persisted operator defaults + the live UI selections. The
            // segmented/scheduled values are read from the bound option selections, so the config can
            // never carry a value the operator cannot see in the UI.
            var config = BuildRecordingConfigFromUi();

            // A start delay is resolved to an absolute instant HERE, once, so what the arm evaluates is
            // a plain timestamp compare rather than a moving target.
            if (RecordingSchedule.ResolveStartUtc(config, nowUtc) is { } startUtc)
            {
                config.ScheduledStartUtc = startUtc;
                config.ScheduledStartDelayMinutes = 0;
            }

            // NET-016: the stop-after delay is resolved to an absolute instant HERE, once, exactly as the
            // start is above - so the arm evaluates a plain timestamp compare rather than a moving target.
            if (RecordingSchedule.ResolveStopUtc(config, nowUtc) is { } stopUtc)
            {
                config.ScheduledStopUtc = stopUtc;
            }

            // The take clock for the remaining-time readout. Both start now; a rollover restarts only
            // the segment clock.
            _activeTakeConfig = config;
            _activeTakeStartUtcTicks = nowUtc.Ticks;
            _activeSegmentStartUtcTicks = nowUtc.Ticks;
            _lastRemainingSeconds = -1;
            _lastRemainingBoundary = RecordingBoundary.None;

            // A new take clears any rollover notice left over from the previous one.
            SegmentContinuationText = string.Empty;

            bool started = _session.StartRecording(config);
            if (!started)
            {
                StatusText = "Failed to initiate recording. Ensure FFmpeg is available and destination folder is writable.";
            }
        }
    }

    // ============================================================================================
    // NET-008: named presets + scheduled / segmented capture
    // ============================================================================================

    private static string[] BuildRecordingPresetNames()
    {
        var names = new string[RecordingPresets.BuiltIn.Count + 1];
        for (int i = 0; i < RecordingPresets.BuiltIn.Count; i++)
        {
            names[i] = RecordingPresets.BuiltIn[i].Name;
        }

        names[^1] = RecordingPresets.CustomName;
        return names;
    }

    /// <summary>Finds the option whose value matches, else the first (Off).</summary>
    private static RecordingChoiceOption OptionFor(IReadOnlyList<RecordingChoiceOption> options, double value)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].Value.Equals(value)) return options[i];
        }

        return options[0];
    }

    /// <summary>
    /// Applies the startup FFmpeg preflight (NET-010). Called once from the composition root: the
    /// engine has no view of the preflight, so the ViewModel carries the provenance and stamps it onto
    /// the health readout. Applying an available result CLEARS the warning, so a transient failure that
    /// later resolves is not sticky.
    /// </summary>
    public void ApplyFfmpegPreflight(FfmpegPreflightResult result)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));

        FfmpegVersion = result.Version ?? string.Empty;
        IsRecordingUnavailable = !result.IsAvailable;
        RecordingUnavailableText = result.IsAvailable
            ? string.Empty
            : $"Recording unavailable: {result.Detail}";

        if (result.IsAvailable)
        {
            AppLogger.LogInfo(
                $"FFmpeg preflight OK: {result.VersionLine} (resolved: {result.ExecutablePath})");
        }
        else
        {
            AppLogger.LogError(
                $"FFmpeg preflight FAILED - {result.Detail}. Preview, discovery and broadcast keep " +
                "working; recording is unavailable.");
            StatusText = RecordingUnavailableText;
        }

        ProjectRecordingHealth(_session.GetRecordingHealthSnapshot());
    }

    /// <summary>
    /// Selecting a preset writes its axis onto the controls. The destination is only written when the
    /// preset carries one - a preset authored on another machine must not silently redirect output.
    /// </summary>
    partial void OnSelectedRecordingPresetChanged(string value)
    {
        if (_applyingRecordingPreset)
        {
            // ADR-0006: still persist - the preset name itself is a persisted field, and the axis hooks
            // that fired during application were suppressed by the guard.
            PersistSettings();
            return;
        }

        RecordingPreset? preset = RecordingPresets.FindByName(value);
        if (preset == null)
        {
            PersistSettings(); // "Custom" - nothing to apply, but the name is still the operator's choice
            return;
        }

        _applyingRecordingPreset = true;
        try
        {
            SelectedContainerFormat = preset.ContainerFormat;
            SelectedEncoderChoice = preset.EncoderChoice;
            SelectedQualityPreset = preset.Quality;
            SelectedRolloverMinutes = OptionFor(RolloverMinuteOptions, preset.SegmentRolloverMinutes);
            SelectedRolloverGigabytes = OptionFor(RolloverGigabyteOptions, preset.SegmentRolloverGigabytes);
            SelectedMaxDuration = OptionFor(MaxDurationOptions, preset.MaxDurationMinutes);

            if (!string.IsNullOrWhiteSpace(preset.OutputDirectory))
            {
                OutputDirectory = preset.OutputDirectory!;
            }

            StatusText = $"Recording preset applied: {preset.Name}";
        }
        finally
        {
            _applyingRecordingPreset = false;
        }

        // ADR-0006: the preset choice and every axis it set are persisted together, after the guard is
        // released, so the file records the post-apply state rather than an intermediate one.
        PersistSettings();
    }

    /// <summary>
    /// Any manual edit to a preset axis means the config no longer matches a named preset, so the
    /// picker flips to "Custom" rather than displaying a name that has stopped being true.
    /// </summary>
    private void MarkRecordingPresetCustom()
    {
        if (_applyingRecordingPreset) return;

        _applyingRecordingPreset = true;
        try
        {
            SelectedRecordingPreset = RecordingPresets.CustomName;
        }
        finally
        {
            _applyingRecordingPreset = false;
        }
    }

    partial void OnSelectedEncoderChoiceChanged(VideoEncoderChoice value)
    {
        MarkRecordingPresetCustom();
        PersistSettings();   // ADR-0006
    }

    partial void OnSelectedQualityPresetChanged(QualityPreset value)
    {
        MarkRecordingPresetCustom();
        PersistSettings();   // ADR-0006
    }

    partial void OnSelectedRolloverMinutesChanged(RecordingChoiceOption value)
    {
        MarkRecordingPresetCustom();
        PersistSettings();   // ADR-0006
    }

    partial void OnSelectedRolloverGigabytesChanged(RecordingChoiceOption value)
    {
        MarkRecordingPresetCustom();
        PersistSettings();   // ADR-0006
    }

    partial void OnSelectedMaxDurationChanged(RecordingChoiceOption value)
    {
        MarkRecordingPresetCustom();
        PersistSettings();   // ADR-0006
    }

    // ADR-0006: the destination fields and the start delay had no hook before - persistence needs one.
    partial void OnOutputDirectoryChanged(string value) => PersistSettings();

    partial void OnBackupOutputDirectoryChanged(string value) => PersistSettings();

    partial void OnSelectedScheduleDelayChanged(RecordingChoiceOption value) => PersistSettings();

    partial void OnSelectedStopAfterChanged(RecordingChoiceOption value) => PersistSettings();

    /// <summary>
    /// Picks the optional second destination (NET-011). Reuses the same folder-picker hook as the
    /// primary, so no new platform surface is needed.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task BrowseBackupDirectoryAsync()
    {
        if (IsRecordingCloseOverlayVisible || !IsRecordingConfigEnabled) return;
        if (PickFolderAsync == null) return;

        try
        {
            string? selected = await PickFolderAsync(BackupOutputDirectory);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                BackupOutputDirectory = selected;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Error browsing backup directory: {ex.Message}", ex);
            StatusText = $"Backup folder selection cancelled or failed: {ex.Message}";
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

            // ADR-0011: "Change Mode" must disable the moment a take goes live. Re-derived here
            // rather than in the command so every path into "recording" is covered.
            NotifyModeGateChanged();
        });
    }

    private void OnRecordingStopped(RecordingStopSummary summary)
    {
        // NET-008: a ROLLOVER boundary is not a stop. The take continues in the next …_partNN, so none
        // of the stop-side teardown below may run - no timer stop, no button flip back to START, no
        // config re-unlock, no stop verdict, no "saved" copy. It gets its own quiet continuation line.
        if (RecordingHealth.IsSegmentBoundary(summary.Reason))
        {
            // NET-016: the SEGMENT clock restarts; the TAKE clock does not (the same split ADR-0008's
            // boundary arithmetic makes). Without this the next-segment countdown would be wrong.
            _activeSegmentStartUtcTicks = DateTime.UtcNow.Ticks;
            _lastRemainingSeconds = -1;

            string note = RecordingHealth.BuildSegmentContinuationText(summary);
            AppLogger.LogInfo(
                $"Segment boundary (roller): {summary.Path} (media duration: {summary.MediaDuration}, " +
                $"size: {summary.FileSizeBytes} bytes); the take continues in segment {summary.SegmentIndex + 1}.");

            PostToUIThread(() =>
            {
                SegmentContinuationText = note;
                StatusText = note;

                // The take is still live, so the health panel must stay up - and the STOP panel is
                // explicitly not used for a boundary.
                IsRecordingHealthVisible = true;
            });

            return;
        }

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

            // NET-016: the take is over, so the countdown goes with it.
            _activeTakeConfig = null;
            ClearRemainingTime();

            // A genuine stop supersedes any rollover notice from earlier in the take.
            SegmentContinuationText = string.Empty;
            RecordStatsText = $"Saved: {Path.GetFileName(summary.Path)} ({mb:F1} MB, {summary.MediaDuration:mm\\:ss})";

            // FEAT-07: the stop-summary verdict, shown in the UI (and logged above) from the summary's
            // own snapshot, since the live session has already torn the transport down.
            StopSummaryText = RecordingHealth.BuildStopVerdict(summary);
            ProjectRecordingHealth(summary.ToHealthSnapshot());

            StatusText = $"Recording completed and saved to {summary.Path}";

            // ADR-0011: the take is over, so "Change Mode" is allowed again.
            NotifyModeGateChanged();
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

            // ADR-0011: the failed take released the session, so the mode gate must reopen.
            NotifyModeGateChanged();
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
        // NET-010: stamp the app-level ffmpeg provenance onto the snapshot the UI shows - the engine
        // never sees the preflight, and a field report needs the encoder build it recorded through.
        snapshot = snapshot with { FfmpegVersion = FfmpegVersion };

        RecordingHealthEncoderText = RecordingHealth.EncoderText(snapshot);
        RecordingHealthFfmpegText = RecordingHealth.FfmpegText(snapshot);
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

        UpdateRemainingTime();
    }

    /// <summary>
    /// NET-016: the remaining-time readout, projected on the EXISTING health tick - no new timer. The
    /// arithmetic is the pure <see cref="RecordingBoundaries.NearestTimeBoundary"/>; nothing here reads a
    /// file, touches the transport, or allocates on the hot path.
    ///
    /// <para>The string is rebuilt only when the rendered SECOND or the boundary itself changes, so a tick
    /// that changes nothing allocates nothing.</para>
    /// </summary>
    private void UpdateRemainingTime()
    {
        RecordingConfig? config = _activeTakeConfig;

        if (!_session.IsRecording || config is null)
        {
            ClearRemainingTime();
            return;
        }

        RecordingBoundaryEta? eta = RecordingBoundaries.NearestTimeBoundary(
            config, _activeTakeStartUtcTicks, _activeSegmentStartUtcTicks, DateTime.UtcNow.Ticks);

        if (eta is not { } nearest)
        {
            // Nothing is bounded: no countdown, rather than a countdown to nothing.
            ClearRemainingTime();
            return;
        }

        int seconds = (int)nearest.Remaining.TotalSeconds;
        if (seconds == _lastRemainingSeconds && nearest.Boundary == _lastRemainingBoundary)
        {
            IsRemainingTimeVisible = true;
            return;
        }

        _lastRemainingSeconds = seconds;
        _lastRemainingBoundary = nearest.Boundary;

        // The COUNTDOWN LEADS, so a narrow column trims the label and never the number.
        RemainingTimeText =
            $"ends in {FormatRemaining(nearest.Remaining)} · {RemainingBoundaryLabel(nearest.Boundary)}";
        IsRemainingTimeVisible = true;
    }

    private void ClearRemainingTime()
    {
        _lastRemainingSeconds = -1;
        _lastRemainingBoundary = RecordingBoundary.None;
        IsRemainingTimeVisible = false;
        RemainingTimeText = string.Empty;
    }

    private static string FormatRemaining(TimeSpan remaining)
        => remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";

    /// <summary>Names WHAT is about to happen, not merely that something is.</summary>
    private static string RemainingBoundaryLabel(RecordingBoundary boundary) => boundary switch
    {
        RecordingBoundary.MaxDuration => "max length",
        RecordingBoundary.ScheduledStop => "scheduled stop",
        RecordingBoundary.SegmentRollover => "rollover",
        _ => string.Empty
    };

    partial void OnStopSummaryTextChanged(string value)
    {
        IsStopSummaryVisible = !string.IsNullOrEmpty(value);
    }

    partial void OnSegmentContinuationTextChanged(string value)
    {
        IsSegmentContinuationVisible = !string.IsNullOrEmpty(value);
    }

    [RelayCommand]
    private void ToggleDiagnosticsDrawer()
    {
        IsDiagnosticsDrawerOpen = !IsDiagnosticsDrawerOpen;

        // Re-count on open: the operator may have created a bundle since launch, and a stale "N dumps
        // are waiting" would keep asking for something already sent.
        if (IsDiagnosticsDrawerOpen) RefreshDiagnosticsOffer();
    }

    /// <summary>
    /// NET-020: writes one support archive - the recent logs, any crash reports and minidumps from BOTH
    /// forensics locations, plus the generated readout. Never throws; the outcome is reported in the
    /// drawer and the status line either way.
    /// </summary>
    [RelayCommand]
    private void CreateDiagnosticsBundle()
    {
        DiagnosticsSnapshot snapshot = BuildDiagnosticsSnapshot();
        string directory = DiagnosticsBundle.DefaultBundleDirectory(snapshot);
        string path = Path.Combine(directory, DiagnosticsBundle.DefaultFileName(snapshot.GeneratedUtc));

        DiagnosticsBundleResult result = DiagnosticsBundle.Create(snapshot, path);

        DiagnosticsBundleStatusText = result.Summary;
        IsDiagnosticsBundleStatusVisible = true;

        // Only claim the status line on success - a failure has its own line in the drawer, and
        // clobbering the operator's current status with an export error would hide the real problem.
        if (result.Success) StatusText = result.Summary;

        RefreshDiagnosticsOffer();
    }

    /// <summary>Re-counts dumps and republishes the offer. Cheap; called at launch and on drawer open.</summary>
    private void RefreshDiagnosticsOffer()
    {
        int count = 0;
        try
        {
            count = CrashForensics.CountDumps();
        }
        catch
        {
            count = 0;
        }

        _pendingCrashDumpCount = count;
        IsDiagnosticsBundleOffered = count > 0;
        DiagnosticsBundleOfferText = count > 0
            ? $"{count} crash dump(s) from a previous run are waiting - create a diagnostics bundle to attach to a bug report"
            : string.Empty;
    }

    /// <summary>
    /// Gathers the live facts the bundle reports. Read through the existing public surfaces only: the
    /// version and preflight results the view model already holds, and the session's health snapshot.
    /// </summary>
    private DiagnosticsSnapshot BuildDiagnosticsSnapshot()
    {
        string[] logDirs = SafeDirectories(() => CrashForensics.LogDirectories());
        string[] dumpDirs = SafeDirectories(() => CrashForensics.DumpDirectories());

        return new DiagnosticsSnapshot
        {
            AppVersion = VersionText,
            FfmpegVersion = FfmpegVersion,
            IsRecordingUnavailable = IsRecordingUnavailable,
            RecordingUnavailableText = RecordingUnavailableText,

            // Stamped the same way ProjectRecordingHealth does it: the session knows nothing about the
            // preflight, so the app-level fact is attached here (NET-010's pattern).
            Health = _session.GetRecordingHealthSnapshot() with { FfmpegVersion = FfmpegVersion },

            DetachedChildNames = DetachedDestinationNames(),

            LogDirectory = logDirs[0],
            PrimaryLogDirectory = logDirs[^1],
            DumpDirectory = dumpDirs[0],
            PrimaryDumpDirectory = dumpDirs[^1],

            CrashReportPath = SafeNewestDumpPath(),
            GeneratedUtc = DateTimeOffset.UtcNow,
            OsDescription = RuntimeInformation.OSDescription
        };
    }

    /// <summary>
    /// Destinations the composite sink detached, for the bundle's fan-out section. Read through the
    /// session's public surface; guarded because a diagnostics read must never be what breaks a run.
    /// </summary>
    private IReadOnlyList<string> DetachedDestinationNames()
    {
        try { return _session.DetachedDestinationNames; }
        catch { return Array.Empty<string>(); }
    }

    private static string[] SafeDirectories(Func<string[]> read)
    {
        try
        {
            string[] dirs = read();
            if (dirs is { Length: > 0 }) return dirs;
        }
        catch { }

        return new[] { string.Empty };
    }

    private static string? SafeNewestDumpPath()
    {
        try { return CrashForensics.NewestDumpPath(); }
        catch { return null; }
    }

    #endregion

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
/// <see cref="Guid"/> by <see cref="AsioInputPairSelector"/> so re-opening the dropdown never
/// re-opens the driver. Public because it is a parameter of the (public) selector ctor — an
/// internal type there is a CS0051 accessibility error.
/// </summary>
/// <param name="Channels">Input channels the driver reported (empty when the probe failed).</param>
/// <param name="Reason">Why the probe failed, or <c>null</c> on success.</param>
public sealed record AsioPairProbeResult(IReadOnlyList<AsioInputChannel> Channels, string? Reason);

/// <summary>
/// One labelled choice in a recording-settings dropdown (NET-008): the operator sees
/// <see cref="Label"/>, the config receives <see cref="Value"/>. A class rather than a struct so a
/// ComboBox selection matches the instance in <c>ItemsSource</c> by reference.
/// </summary>
public sealed class RecordingChoiceOption
{
    public RecordingChoiceOption(string label, double value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public double Value { get; }

    public override string ToString() => Label;
}
