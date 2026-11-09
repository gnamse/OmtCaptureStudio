using Avalonia.Media;
using OmtCaptureStudio.Services;

namespace OmtCaptureStudio.ViewModels;

/// <summary>
/// Broadcast-mode chrome projection — Phase 1.6 slice along ADR-0011 patterns.
/// Binding surface stays on the facade VM; this partial owns broadcast row visibility,
/// live-setting summary, and BroadcastSessionState → presentation projection.
/// </summary>
public partial class MainWindowViewModel
{

    /// <summary>
    /// NET-012/NET-014/ADR-0011: the broadcast configuration row's visibility.
    ///
    /// <para>Keyed to <see cref="IsBroadcastModeVisible"/>: under the mode-based shell the MODE is the
    /// reveal (ADR-0011 decision 1), so the pickers are shown whenever the operator is in Broadcast mode
    /// and stay ENABLED for selection before anything is live. OR'd with the live state, so a running
    /// broadcast can never be orphaned behind a hidden row even if the mode moves underneath it.</para>
    ///
    /// <para>Deliberately keyed to broadcast state and NOT to recording state: hiding this row must never
    /// disturb a take.</para>
    ///
    /// <para>Replaced the old <c>IsBroadcastRevealed</c> flag. Once the reveal toggle left the UI nothing
    /// could set it, so it stayed false forever: the row never showed, <see cref="IsBroadcastSelecting"/>
    /// was never true (the selection hint was permanently invisible), and
    /// <see cref="IsLiveSettingSummaryVisible"/> was inverted so the compact summary covered the hint.</para>
    /// </summary>
    public bool IsBroadcastRowVisible => IsBroadcastModeVisible || IsDesktopCaptureActive;

    /// <summary>
    /// True in the middle state: the row is open for selection and nothing is being sent yet. Gates the
    /// broadcast selection hint, which under the old reveal flag could never appear.
    /// </summary>
    public bool IsBroadcastSelecting => IsBroadcastRowVisible && !IsDesktopCaptureActive;

    /// <summary>The in-row action: arm the live state, or stop it (NET-014).</summary>
    public string BroadcastActionText => IsDesktopCaptureActive ? "Stop Broadcasting" : "Start Broadcasting";

    /// <summary>States the action, not the state - screen readers announce what the press will do.</summary>
    public string BroadcastActionAutomationName => BroadcastActionText;

    /// <summary>Stop reads like the app's other stop affordances; Start is the accent call to action.</summary>
    public IBrush BroadcastActionBackground =>
        IsDesktopCaptureActive ? RecordStopBrush : TestSignalActiveBrush;

    public IBrush BroadcastActionForeground => StudioPalette.ButtonWhiteForeground;

    /// <summary>
    /// The compact broadcast summary substitutes for the row exactly when the row is hidden - the point
    /// is that hiding the pickers never hides WHICH devices are selected.
    ///
    /// <para>Because it is the complement of <see cref="IsBroadcastRowVisible"/>, and the row is now
    /// visible throughout Broadcast mode, the summary stands down while the operator is selecting. That
    /// is deliberate: it must never cover the selection hint.</para>
    /// </summary>
    public bool IsLiveSettingSummaryVisible => !IsBroadcastRowVisible;

    /// <summary>
    /// The compact "what is selected to go out" summary shown in the header while the broadcast row is
    /// collapsed, so hiding the pickers never hides WHICH devices are in play.
    /// </summary>
    public string LiveSettingSummary
    {
        get
        {
            string target = string.IsNullOrWhiteSpace(SelectedCaptureTarget?.DisplayName)
                ? "No target"
                : SelectedCaptureTarget!.DisplayName;
            string audio = string.IsNullOrWhiteSpace(SelectedCaptureAudioOption?.DisplayName)
                ? "No audio"
                : SelectedCaptureAudioOption!.DisplayName;

            return $"{target} · {audio}";
        }
    }

    partial void OnIsDesktopCaptureActiveChanged(bool value)
    {
        // The row's visibility and the collapsed-state summary both ride the broadcast state.
        OnPropertyChanged(nameof(IsBroadcastRowVisible));
        OnPropertyChanged(nameof(IsBroadcastSelecting));
        OnPropertyChanged(nameof(IsLiveSettingSummaryVisible));
        OnPropertyChanged(nameof(LiveSettingSummary));
        OnPropertyChanged(nameof(BroadcastActionText));
        OnPropertyChanged(nameof(BroadcastActionAutomationName));
        OnPropertyChanged(nameof(BroadcastActionBackground));
    }

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

        // NET-012: the collapsed broadcast row's summary is derived from the same projection, so it is
        // current the moment the row hides.
        OnPropertyChanged(nameof(LiveSettingSummary));

        // ADR-0011: the "Change Mode" gate is a projection of "is anything live", so it re-derives
        // whenever the broadcast axis moves — no separate bookkeeping in the commands.
        NotifyModeGateChanged();
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

}
