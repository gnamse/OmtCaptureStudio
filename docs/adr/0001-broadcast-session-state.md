# ADR-0001: Broadcast mode transitions are owned by a headless `BroadcastSessionState` module

- **Status:** Accepted (2026-10-15)
- **Deciders:** Odogwu (orchestration) on behalf of the project owner
- **Scope:** `OmtCaptureStudio/ViewModels/MainWindowViewModel.cs`, new `OmtCaptureStudio/Services/BroadcastSessionState.cs`

## Context

`MainWindowViewModel` (≈1790 lines) never names connection state. It re-derives it in four
incompatible ways — comparing the `ConnectButtonText` display string (`== "Disconnect"`),
testing `IsConnected` against button text, syncing `IsSourceSelectorEnabled` off the same
string, and resetting 18 scattered properties in `ResetOfflineState()`. The transition
sequence "stop recording → disconnect → update brush + foreground + automation name" is
pasted six times across the three mode toggles (network connect, test signal, desktop
broadcast). Every new mode must remember all of it, and the headless suite asserts those
quadruples one property at a time.

## Decision

1. Introduce `BroadcastSessionState`, a headless-constructible module (no Avalonia
   `Application` dependency — CODING_STANDARDS §2.2) owning broadcast-mode state as an
   explicit enum: `Offline`, `Connecting`, `NetworkConnected`, `TestSignal`,
   `Broadcasting`. `Reconnecting` is reserved for ADR-0002 and must compile from day one
   so the state extension is a rename-free addition.
2. The module owns every transition rule: leaving any active mode runs stop-recording
   first; selector locks are derived from state; a single `StateChanged` event is raised
   per transition.
3. `MainWindowViewModel` keeps only the mapping state → `StudioPalette` brushes/text,
   expressed once. String-derived state (`ConnectButtonText == "Disconnect"` and friends)
   is deleted; button text becomes a projection of state, never an input to it.
4. Out of scope for this pass (recorded as follow-ups, not decided here): extracting the
   ASIO input-pair selector, and reworking the test harness fixtures.

**Amendment (2026-10-15, during implementation review):** the scalar enum models the
*receive* mode only. The host-broadcast *sender* axis is orthogonal by design — the app
deliberately supports broadcasting the desktop and then connecting to that same broadcast
to monitor and record it (`Broadcasting` + `NetworkConnected` simultaneously). The sender
bit is therefore tracked as `BroadcastSessionState.IsBroadcasting` on the same module, and
the presentation projection is a pure function of `(State, IsBroadcasting)`. `InputLost`
joins the enum when ADR-0002 lands, per that ADR.

## Consequences

- A new mode touches one module, not six pasted sequences (locality).
- The suite can assert `state == Broadcasting` once instead of six presentation
  properties (leverage).
- `MainWindowViewModel` shrinks; brush changes stop being behavior changes.
- Risk: the extraction must be behavior-preserving; the headless suite is the regression
  gate and must pass 100% before and after.
