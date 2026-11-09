using CommunityToolkit.Mvvm.Input;
using OmtCaptureStudio.Services;

namespace OmtCaptureStudio.ViewModels;

/// <summary>
/// ADR-0011 mode-shell projection and commands — first MainWindowViewModel slice (Phase 1.6).
/// Binding surface stays on the facade VM; this partial owns picker/mode gate projection only.
/// </summary>
public partial class MainWindowViewModel
{

    // ---- ADR-0011: the shell projection -------------------------------------------------------

    /// <summary>The live shell mode. Presentation binds to the visibility flags below, never to this directly.</summary>
    public AppMode CurrentAppMode => _appModeState.CurrentMode;

    /// <summary>True while the picker is the active shell content.</summary>
    public bool IsPickerVisible => _appModeState.IsModeSelection;

    /// <summary>True while the capture stack is the active shell content.</summary>
    public bool IsCaptureModeVisible => _appModeState.CurrentMode == AppMode.CaptureMode;

    /// <summary>True while the broadcast controls are the active shell content.</summary>
    public bool IsBroadcastModeVisible => _appModeState.CurrentMode == AppMode.BroadcastMode;

    /// <summary>
    /// ADR-0011 decision 4: "Change Mode" is disabled while a take or a host broadcast is live. A mode
    /// switch must never silently tear down a running session, so this is the single gate the views read.
    /// </summary>
    public bool IsModeChangeAllowed => !IsRecording && !_broadcastState.IsBroadcasting;

    /// <summary>The mode the operator last CHOSE — pre-highlights a picker card. Null = nothing highlighted.</summary>
    public AppMode? LastUsedAppMode => _lastUsedAppMode;

    /// <summary>True when the Capture card should be pre-highlighted on the picker.</summary>
    public bool IsCaptureCardHighlighted => _lastUsedAppMode == AppMode.CaptureMode;

    /// <summary>True when the Broadcast card should be pre-highlighted on the picker.</summary>
    public bool IsBroadcastCardHighlighted => _lastUsedAppMode == AppMode.BroadcastMode;

    /// <summary>The hint shown beside a disabled "Change Mode" affordance — empty when the switch is allowed.</summary>
    public string ModeChangeBlockedHint => IsRecording
        ? "Stop the recording before changing modes."
        : _broadcastState.IsBroadcasting
            ? "Stop the host broadcast before changing modes."
            : string.Empty;

    /// <summary>
    /// ADR-0011: the picker's card action. Enters the chosen operating mode and remembers it as
    /// <see cref="LastUsedAppMode"/> so the next open pre-highlights the same card. Persists immediately
    /// — a mode choice is a committed operator intent, not a transient UI state.
    /// </summary>
    [RelayCommand]
    private void SelectMode(AppMode mode)
    {
        if (mode == AppMode.ModeSelection)
        {
            return; // The picker is the destination of ChangeMode, not a card on it.
        }

        _lastUsedAppMode = mode;
        _appModeState.SetMode(mode);
        PersistSettings();
        ApplyAppModeProjection();
    }

    /// <summary>
    /// ADR-0011 decision 4: "Change Mode" returns to the picker. Refused while a take or a host
    /// broadcast is live — the guard lives here as well as in the view, so a stale binding can never
    /// tear a session down.
    /// </summary>
    [RelayCommand]
    private void ChangeMode()
    {
        if (!IsModeChangeAllowed)
        {
            return;
        }

        _appModeState.SetMode(AppMode.ModeSelection);
    }

    /// <summary>
    /// ADR-0011: one projection for the shell. Called on construction and on every mode transition,
    /// mirroring <see cref="ApplyBroadcastStateProjection"/> — the views read derived flags, never the
    /// enum, so a new presentation concern is added here once rather than at each call site.
    /// </summary>
    private void ApplyAppModeProjection()
    {
        OnPropertyChanged(nameof(CurrentAppMode));
        OnPropertyChanged(nameof(IsPickerVisible));
        OnPropertyChanged(nameof(IsCaptureModeVisible));
        OnPropertyChanged(nameof(IsBroadcastModeVisible));
        OnPropertyChanged(nameof(LastUsedAppMode));
        OnPropertyChanged(nameof(IsCaptureCardHighlighted));
        OnPropertyChanged(nameof(IsBroadcastCardHighlighted));

        // The mode now OWNS the broadcast row's visibility, so entering or leaving Broadcast mode must
        // publish the whole dependent set. This is the notification the deleted OnIsBroadcastRevealedChanged
        // hook used to raise - without it the row, the selection hint and the compact summary all keep
        // stale bindings when the mode changes.
        OnPropertyChanged(nameof(IsBroadcastRowVisible));
        OnPropertyChanged(nameof(IsBroadcastSelecting));
        OnPropertyChanged(nameof(IsLiveSettingSummaryVisible));
        OnPropertyChanged(nameof(LiveSettingSummary));

        NotifyModeGateChanged();
    }

    /// <summary>
    /// ADR-0011: "Change Mode" is disabled while a take or a host broadcast is live. Both the gate and
    /// its explanatory hint are published together so a view can never show an enabled-looking button
    /// with a stale hint, or a disabled one with no reason.
    /// </summary>
    private void NotifyModeGateChanged()
    {
        OnPropertyChanged(nameof(IsModeChangeAllowed));
        OnPropertyChanged(nameof(ModeChangeBlockedHint));
    }

    /// <summary>One module transition: re-project the shell, and refresh the mode gate.</summary>
    private void OnAppModeChanged(object? sender, AppModeChangedEventArgs e)
    {
        ApplyAppModeProjection();
    }

}
