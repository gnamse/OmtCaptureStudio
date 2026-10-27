using System;

namespace OmtCaptureStudio.Services;

/// <summary>
/// The three shell states named by ADR-0011. <see cref="ModeSelection"/> is the picker the app opens
/// on — it is NOT a transient splash screen, it is a first-class state the operator can always return
/// to (when nothing is live). <see cref="CaptureMode"/> is the record stack; <see cref="BroadcastMode"/>
/// is the host-broadcast controls.
/// </summary>
public enum AppMode
{
    ModeSelection,
    CaptureMode,
    BroadcastMode
}

/// <summary>Endpoints of a single <see cref="AppModeState.ModeChanged"/> transition.</summary>
public sealed class AppModeChangedEventArgs : EventArgs
{
    public AppModeChangedEventArgs(AppMode oldMode, AppMode newMode)
    {
        OldMode = oldMode;
        NewMode = newMode;
    }

    public AppMode OldMode { get; }

    public AppMode NewMode { get; }
}

/// <summary>
/// ADR-0011: the headless owner of the shell state. Same shape as <see cref="BroadcastSessionState"/> —
/// it names the state once, owns the transition rule, and raises a single event per transition.
/// Presentation layers only project it; they never feed a display string back in.
/// <para>
/// No Avalonia type is touched here (CODING_STANDARDS §2.2), so the headless suite constructs it with no
/// UI platform present.
/// </para>
/// <para>
/// The module deliberately knows nothing about recording or broadcasting — "is a switch allowed right
/// now?" is the host's question, not the state machine's. The host asks it and decides whether to call
/// <see cref="SetMode"/>; the state only records what actually happened. That keeps the module testable
/// without a session fake and stops the gating rule being duplicated in two places.
/// </para>
/// </summary>
public sealed class AppModeState
{
    private AppMode _currentMode = AppMode.ModeSelection;

    /// <summary>
    /// The current shell state. Starts in <see cref="AppMode.ModeSelection"/> — ADR-0011 decision 1 is
    /// explicit that the picker is always shown at open, even when <c>LastAppMode</c> remembers a mode.
    /// </summary>
    public AppMode CurrentMode => _currentMode;

    /// <summary>True while the picker is on screen. The host uses this to gate the mode views.</summary>
    public bool IsModeSelection => _currentMode == AppMode.ModeSelection;

    /// <summary>True while either operating mode is on screen.</summary>
    public bool IsOperatingMode => _currentMode is AppMode.CaptureMode or AppMode.BroadcastMode;

    /// <summary>Raised once per real transition. A no-op <see cref="SetMode"/> raises nothing.</summary>
    public event EventHandler<AppModeChangedEventArgs>? ModeChanged;

    /// <summary>
    /// Enters <paramref name="mode"/>. Returns <c>true</c> when the state actually changed, so a caller
    /// can tell "already there" from "moved" without subscribing. Never throws.
    /// </summary>
    public bool SetMode(AppMode mode)
    {
        if (_currentMode == mode)
        {
            return false;
        }

        AppMode previous = _currentMode;
        _currentMode = mode;
        ModeChanged?.Invoke(this, new AppModeChangedEventArgs(previous, mode));
        return true;
    }
}
