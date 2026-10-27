# ADR-0011: Mode-based application shell

**Status:** Accepted — 2026-10-19
**Deciders:** Odogwu (design ruling, owner-directed), implementation via @coder

## Context

The window currently surfaces both operating modes at once: a Broadcast toggle in the
top bar reveals the Broadcast Studio row, while the capture/record stack (source,
camera, record controls, recording health, segments) is always present. The operator
must read the whole shell to know which mode they are in. The owner directed: on
open, the application asks **Broadcast or Capture**, then loads only the selected
mode's UI.

## Decision

1. **Three shell states**, owned by a headless `AppModeState` (same pattern as
   `BroadcastSessionState` — presentation only projects it):
   - `ModeSelection` — the picker the app opens on. Two large, obvious choices:
     **Capture** (record to file) and **Broadcast** (stream to OMT receivers).
     The last used mode is pre-highlighted (persisted `LastAppMode`), but the picker
     is always shown at open — no auto-skip.
   - `CaptureMode` — the current record stack, unchanged in behavior.
   - `BroadcastMode` — the broadcast controls (screen/window target, audio device,
     network interface, Start/Stop, endpoint status). The current reveal-toggle
     disappears: the mode IS the reveal.
2. **Shared chrome stays shared**: title/status bar, Recording Health panel, and the
   diagnostics drawer live outside the mode content region and appear in both modes.
3. **One window, swapped content region** — not two windows. The window keeps its
   identity, settings, and single log/forensics pipeline; the content region binds to
   the shell state's projection (mirror of `ApplyBroadcastStateProjection`).
4. **Return path**: a small "Change Mode" affordance in each mode view returns to
   `ModeSelection`. It is **disabled while a take or broadcast is active** (with a
   hint) — an active recording is never silently torn down by a mode switch.
5. **Persistence**: `LastAppMode` rides the existing settings store under the
   ADR-0006 policy (transient lock never punished, corruption quarantined).

## Consequences

- Pure presentation restructure: recording/broadcast engines, sinks, forensics are
  untouched. The capture stack keeps its existing command wiring.
- The picker and both mode views must meet the DESIGN.md quality floor: StudioPalette
  brushes only, `AutomationProperties.Name` on everything interactive, `LiveSetting`
  on status lines, no inline hex.
- Geometry discipline carries: no overlap/overflow at the 950×650 floor and 2560×1440;
  QA renders both modes plus the picker.
- XAML rule: parse with zero non-whitespace text/tail nodes (NET-013).
- A fresh scan item; implemented after the v1.0.0.20 queue closed.
