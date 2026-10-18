# ADR-0006: Operator settings persistence — survive the relaunch

- **Status:** **Accepted — IMPLEMENTED.** Ruled by Odogwu (work order NET-015), on behalf of the
  project owner, before the code was written.
- **Deciders:** Odogwu (orchestration) on behalf of the project owner
- **Scope:** `Services/OperatorSettings.cs` (new — the persisted DTO + source-gen JSON context),
  `Services/SettingsStore.cs` (new — load/save/validate/fallback), `App.axaml.cs` (composition-root
  wiring), `ViewModels/MainWindowViewModel.cs` (apply restored values; hook writes to the existing
  change notifications), `OmtCaptureStudio.Tests/Program.cs`
- **Related:** ADR-0002 (watchdog thresholds — persisted here), NET-008 (rollover/schedule fields),
  NET-009/NET-010 rider (the `%LOCALAPPDATA%` forensics home this shares), NET-011 (backup
  destination). Architecture-review candidate #8, scan #2.

## Context

Nothing was persisted. Every launch rebuilt `RecordingConfig` from code defaults in the composition
root: the destination reverted to `MyVideos`, the backup folder vanished, the preset picker
re-selected *Custom*, and rollover/max-duration/schedule settings reset to off. The operator had to
reconstruct all of it from memory.

The scenario that makes this a defect rather than an inconvenience is the 3 a.m. one: a crash, a
reboot, a relaunch. The recovery path is exactly the path that loses the configuration — and it is
the path taken by someone who is tired, under time pressure, and about to record something they only
get one chance at.

It also quietly undermined the preset feature. A preset is described as "one action", and it is —
until the app closes, at which point the operator is back to hand-configuring.

## Decision

**1. One JSON file, in the non-portable home.** `%LOCALAPPDATA%\OmtCaptureStudio\settings.json` —
the same directory the ADR-0009 rider put the crash-forensics fallback in. The application directory
is deliberately *not* used: under the installer it is not writable, and a portable ZIP handed between
machines should not carry the previous operator's folder paths into someone else's session.

**2. Source-generated `System.Text.Json`.** The app ships Native AOT (`PublishAot=true`), where
reflection-based serialization is trimmed away — a reflective `JsonSerializer.Serialize` would
compile, pass every test on a JIT build, and then fail in the field. `SettingsJsonContext` emits the
converters at compile time, so **the build itself is the proof** that the format works.

**3. A persistence DTO, not `RecordingConfig`.** The config is the recording pipeline's live contract
and grows when the pipeline grows (NET-008 added five fields, NET-011 added one). Serializing it
directly would make every pipeline field silently a persistence field, and every rename a breaking
file-format change. `OperatorSettings` is the operator-facing subset, free to lag, and carries a
`Version`.

**4. Enums persist as NAMES, not ordinals.** Reordering an enum in code must not silently reinterpret
a saved choice.

**5. Every DTO member is nullable.** Absent means "the operator never set this", which must fall back
to that field's DEFAULT — not to `default(T)`. This is what makes a partial or hand-edited file safe.

**6. Scope.** All fourteen operator-facing `RecordingConfig` fields (container, encoder, quality,
destination, prefix, both watchdog thresholds, rollover minutes/GB, max duration, schedule delay,
scheduled start/stop instants, backup destination) plus the selected preset name.

**Deliberately NOT persisted: the Advanced-expander state.** The ruling left this to my judgement and
I agree with excluding it. It is a *session* UI preference, not operator configuration: a disclosure
that reopens expanded means the operator sees a dozen controls they did not ask to see, on every
launch, forever — which is exactly what NET-012 was raised to stop. Persisting it would also make the
one piece of UI state that is *supposed* to default to simple the only one that does not.

## Policies

**Load.** Loaded once, at the composition root, before the ViewModel is bound to the window.

| Situation | Behaviour |
|---|---|
| File missing | First run. All defaults. Nothing quarantined. |
| File unparseable | **Quarantined** — renamed `settings.json.corrupt` (then `.corrupt.1`, `.corrupt.2`… so a second corruption cannot destroy the first file's evidence). All defaults. |
| File unreadable (locked, permissions) | All defaults, **file left in place**. A transient lock must not cost the operator their settings, and a file we could not read is not evidence of corruption. |
| Field path vanished (drive gone) | **That field only** falls back to its default; the rest of the file is honoured. |
| Valid | Applied. |

**Path validation.** An existing folder is usable. A folder that does not exist *yet* is usable as
long as its **drive** does — the recorder creates the folder, and falling back because the operator
has not made a subfolder yet would be obnoxious. A **vanished drive** is not usable: that is the 3
a.m. case, where continuing to point at `Z:\captures` fails only when Record is pressed.

**Write.** On every committed change to a persisted field, from the ViewModel's existing
`On<Observable>Changed` hooks — an immediate full-file write. The file is a few hundred bytes; a
debounce would add a timer, a flush-on-exit path, and a window in which a crash loses the last
change, which is the exact scenario this ADR exists for. **Writes are exception-tolerant**: a failed
write is logged and never interrupts the operator. Restoring settings writes nothing (guarded).

## Consequences

**Good.** A crash-and-relaunch costs nothing. Presets become genuinely "one action". A field report
can be reproduced because the operator's configuration can be read from the file. The corrupt-file
policy means a bad file cannot brick startup *and* cannot be silently destroyed.

**Costs.** A settings file is new state to reason about: a stale path can outlive the machine it was
valid on (mitigated by load-time validation); a future schema change needs a migration rather than a
rename (mitigated by `Version`); and the ViewModel now has a write side-effect on its change
notifications, which is why restoring is explicitly guarded against writing back.

**Known limit.** Path validation probes the filesystem at load only. A drive that vanishes *during* a
session is caught by the existing recording storage guard (ADR-0004/0005), not here — settings
persistence is a startup concern, and duplicating that policy would create a second answer to "is this
destination usable".
