using System;
using System.Threading.Tasks;

namespace OmtCaptureStudio.Services;

/// <summary>
/// The named operating modes of the broadcast session (ADR-0001 decision 1). <see cref="Reconnecting"/>
/// and <see cref="InputLost"/> are the ADR-0002 outage states: a live network feed that has gone quiet
/// moves to <see cref="Reconnecting"/> while auto-reconnect is attempted, and to
/// <see cref="InputLost"/> once the bounded grace period expires
/// (docs/adr/0002-input-loss-recording-policy.md).
/// </summary>
public enum BroadcastState
{
    Offline,
    Connecting,
    NetworkConnected,
    TestSignal,
    Broadcasting,
    Reconnecting,
    InputLost
}

/// <summary>
/// Endpoints of a single <see cref="BroadcastSessionState.StateChanged"/> transition.
/// </summary>
public sealed class BroadcastStateChangedEventArgs : EventArgs
{
    public BroadcastStateChangedEventArgs(BroadcastState oldState, BroadcastState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    public BroadcastState OldState { get; }

    public BroadcastState NewState { get; }
}

/// <summary>
/// The effects a transition needs from its host, so the module can own the *ordering* rule
/// ("leaving an active mode stops recording first") while the host owns the media session.
/// Production hosts adapt <see cref="CaptureSession"/> onto this; tests inject a recording fake.
/// </summary>
public interface IBroadcastSessionEffects
{
    bool IsRecording { get; }

    Task StopRecordingAsync();

    void DisconnectSession();
}

/// <summary>
/// Headless-constructible module owning the broadcast-mode state machine (ADR-0001).
/// <para>
/// It names the operating state once (<see cref="State"/>) instead of the ViewModel re-deriving it
/// from display strings, owns every transition rule — leaving an active receive mode runs
/// stop-recording-first and then disconnects, selector locks are derived from state — and raises a
/// single <see cref="StateChanged"/> event per transition. Presentation layers only project state
/// onto <c>StudioPalette</c> brushes/text; they never feed a display string back in.
/// </para>
/// <para>
/// No Avalonia <c>Application</c> is touched here (CODING_STANDARDS §2.2); the module is constructed
/// by the headless suite with no UI platform present.
/// </para>
/// <para>
/// The host-broadcast (sender) axis is deliberately tracked alongside the receive mode as
/// <see cref="IsBroadcasting"/>: the app can broadcast its own desktop and then connect to that
/// broadcast to monitor it (see the host-broadcast status text), so the sender is orthogonal to the
/// receive mode. The enum still names the sender-only mode as <see cref="BroadcastState.Broadcasting"/>.
/// </para>
/// </summary>
public sealed class BroadcastSessionState
{
    private bool _isBroadcasting;

    /// <summary>The current operating mode. Starts <see cref="BroadcastState.Offline"/>.</summary>
    public BroadcastState State { get; private set; } = BroadcastState.Offline;

    /// <summary>True while the host desktop/window is being broadcast as an OMT source.</summary>
    public bool IsBroadcasting => _isBroadcasting;

    /// <summary>True while the capture session is actively receiving frames (network or test signal).</summary>
    public bool IsReceiving => State is BroadcastState.NetworkConnected or BroadcastState.TestSignal;

    /// <summary>
    /// True whenever a receive session is attached — including the ADR-0002 outage states
    /// (<see cref="BroadcastState.Reconnecting"/>, <see cref="BroadcastState.InputLost"/>), where the
    /// source is still attached but frames have stopped. It is leaving the *session* (not merely the
    /// frames) that runs stop-recording-first and disconnects.
    /// </summary>
    public bool IsSessionActive =>
        State is BroadcastState.NetworkConnected or BroadcastState.TestSignal
               or BroadcastState.Reconnecting or BroadcastState.InputLost;

    /// <summary>The Source picker is locked while a session is attached — including during an outage (DESIGN §7).</summary>
    public bool IsSourceSelectorEnabled => !IsSessionActive;

    /// <summary>Target/Audio/Network selectors are locked while a host broadcast is live (DESIGN §7).</summary>
    public bool IsCaptureSelectorEnabled => !_isBroadcasting;

    /// <summary>Raised once per transition (and once when only the broadcast axis changes).</summary>
    public event EventHandler<BroadcastStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Connects to a network source: leaves any active receive mode first (stop-recording-first),
    /// passes through <see cref="BroadcastState.Connecting"/>, then commits
    /// <see cref="BroadcastState.NetworkConnected"/>. The connect effect is a delegate so the
    /// ordering rule stays owned (and testable) here rather than in the ViewModel.
    /// </summary>
    public async Task EnterNetworkConnectedAsync(IBroadcastSessionEffects effects, Action connectNetwork)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(connectNetwork);

        await StopAndReleaseReceiveAsync(effects);

        SetState(BroadcastState.Connecting);
        try
        {
            connectNetwork();
            SetState(BroadcastState.NetworkConnected);
        }
        catch
        {
            // A failed connect must not strand the machine in Connecting.
            SetState(_isBroadcasting ? BroadcastState.Broadcasting : BroadcastState.Offline);
            throw;
        }
    }

    /// <summary>
    /// Enters the local test-signal mode. If a receive mode is already active it is left first
    /// (stop-recording-first, then disconnect); the switch from one receive mode to another is a
    /// single transition, so the ViewModel never sees a transient <see cref="BroadcastState.Offline"/>.
    /// </summary>
    public async Task EnterTestSignalAsync(IBroadcastSessionEffects effects, Action connectTestSignal)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(connectTestSignal);

        await StopAndReleaseReceiveAsync(effects);

        SetState(BroadcastState.TestSignal);
        connectTestSignal();
    }

    /// <summary>
    /// Leaves the active receive mode to idle — <see cref="BroadcastState.Broadcasting"/> when the
    /// host broadcast is still live, otherwise <see cref="BroadcastState.Offline"/> — running
    /// stop-recording-first. A no-op when no receive mode is active.
    /// </summary>
    public async Task LeaveReceiveModeAsync(IBroadcastSessionEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);

        if (!IsSessionActive)
        {
            return;
        }

        await StopAndReleaseReceiveAsync(effects);
        SetState(_isBroadcasting ? BroadcastState.Broadcasting : BroadcastState.Offline);
    }

    /// <summary>Marks the host desktop broadcast live. Orthogonal to the receive mode.</summary>
    public void StartBroadcasting()
    {
        if (_isBroadcasting)
        {
            return;
        }

        _isBroadcasting = true;
        if (State is BroadcastState.Offline or BroadcastState.Connecting)
        {
            SetState(BroadcastState.Broadcasting);
        }
        else
        {
            RaiseChanged(State, State); // Only the broadcast axis moved; still one notification.
        }
    }

    /// <summary>Marks the host desktop broadcast stopped. Orthogonal to the receive mode.</summary>
    public void StopBroadcasting()
    {
        if (!_isBroadcasting)
        {
            return;
        }

        _isBroadcasting = false;
        if (State == BroadcastState.Broadcasting)
        {
            SetState(BroadcastState.Offline);
        }
        else
        {
            RaiseChanged(State, State);
        }
    }

    // ---- ADR-0002 input-loss outage transitions (driven by the recording watchdog) ----

    /// <summary>
    /// The network feed has gone quiet and auto-reconnect is being attempted:
    /// <see cref="BroadcastState.NetworkConnected"/> → <see cref="BroadcastState.Reconnecting"/>.
    /// The session stays attached, so the source picker remains locked and leaving it still runs
    /// stop-recording-first.
    /// </summary>
    public void BeginReconnecting()
    {
        if (State != BroadcastState.NetworkConnected)
        {
            return;
        }

        SetState(BroadcastState.Reconnecting);
    }

    /// <summary>
    /// Frames returned within the grace period: <see cref="BroadcastState.Reconnecting"/> →
    /// <see cref="BroadcastState.NetworkConnected"/> (recording has resumed into a new segment).
    /// </summary>
    public void EndReconnecting()
    {
        if (State != BroadcastState.Reconnecting)
        {
            return;
        }

        SetState(BroadcastState.NetworkConnected);
    }

    /// <summary>
    /// The bounded grace period expired with no frames: <see cref="BroadcastState.Reconnecting"/> →
    /// <see cref="BroadcastState.InputLost"/>. The take has been finalized; the alarm persists until
    /// the operator reconnects.
    /// </summary>
    public void MarkInputLost()
    {
        if (State != BroadcastState.Reconnecting)
        {
            return;
        }

        SetState(BroadcastState.InputLost);
    }

    private async Task StopAndReleaseReceiveAsync(IBroadcastSessionEffects effects)
    {
        if (!IsSessionActive)
        {
            return;
        }

        if (effects.IsRecording)
        {
            // Plain await: the continuation must stay on the caller's (UI) thread so any
            // state-changed projection runs there (CODING_STANDARDS §2.3).
            await effects.StopRecordingAsync();
        }

        effects.DisconnectSession();
    }

    private void SetState(BroadcastState next)
    {
        if (State == next)
        {
            return;
        }

        BroadcastState previous = State;
        State = next;
        RaiseChanged(previous, next);
    }

    private void RaiseChanged(BroadcastState oldState, BroadcastState newState)
        => StateChanged?.Invoke(this, new BroadcastStateChangedEventArgs(oldState, newState));
}
