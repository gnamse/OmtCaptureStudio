# ADR-0005: One deep RecordingArm module for the arm/disarm sequence

- **Status:** **Accepted (design) — implemented in two slices.** Slice 1 (storage policy extraction) is
  complete; Slice 2 (RecordingArm) is pending a ruling on the low-disk behavior change recorded below.
- **Deciders:** Odogwu (orchestration) on behalf of the project owner
- **Scope:** `Services/RecordingArm.cs` (new), `Services/RecordingStorageGuard.cs` (new),
  `Services/CaptureSession.cs`, `Services/StreamRecorderService.cs`, `Models/RecordingHealth.cs`
  (one field on `RecordingStopSummary`), `OmtCaptureStudio.Tests/Program.cs`
- **Related:** ADR-0001 (broadcast session state), ADR-0002 (input-loss recording policy), NET-007
  (recording health / stop summary). Architecture-review candidate #3.

## Context

The recording pipeline's most load-bearing invariant — *stamp video at the measured true fps and arm
the audio resampler BEFORE the recording gate flips* — had no home. It was an emergent property of
three pieces of state living in two classes:

| Concern | Where it lived | Why that is a problem |
|---|---|---|
| Measured-fps A/V clock (`_fpsMeter`, `_audioResampler`) | `CaptureSession` | The arm *ordering* (reset clock → init sink → flip gate) is only expressed as a comment in `CaptureSession.StartRecording`. |
| The recording gate (`_isRecording`) | `StreamRecorderService` | Its own comment explains how a stale read lets a source thread walk into sink teardown — a race documented in prose rather than enforced in code. |
| Media-thread guards | `CaptureSession` frame callbacks | Each call site independently re-reads `_recorder.IsRecording`. |

Reconnaissance at the time of this ADR (the work order's line numbers were stale) found three
finalize paths and **one of them is not a finalize path at all**:

1. **User stop** — `CaptureSession.StopRecording()` → `StreamRecorderService.StopRecording()`, under
   `_recordLock`: flips the gate, captures encoder/diagnostics, calls `_sink.FinalizeSink`, builds a
   `RecordingStopSummary` (DEF-01 media duration), raises `RecordingStopped`.
2. **Watchdog boundary** (ADR-0002) — `CaptureSession.HandleOutageBoundary()` (on the health-tick
   thread) → the *same* `StopRecording()`. Already convergent.
3. **Low disk** — `StreamRecorderService.CheckDiskSpaceAndProtect()` (on the UI tick thread) flips
   `_isRecording` and then calls `_sink.FinalizeSink` **directly, inside a `Task.Run`**, bypassing
   `StopRecording()` entirely. It therefore never raises `RecordingStopped`, never builds a
   `RecordingStopSummary`, and never clears the take/segment state.

So three different threads can tear the transport down, and the low-disk path is a *silent* stop:
the operator gets an error string but no stop summary and no verdict line.

A fourth mutation exists: `OnSinkError` flips the gate on whatever thread the sink raises from,
without finalizing at all.

## Decision

**One deep `RecordingArm` module owns the whole arm/disarm sequence as a single operation.**

1. **Arm sequence**, in one method, in this order:
   *reset the A/V clock → (storage preflight via the guard) → initialize the sink → publish the
   format stamps/counters → flip the gate → raise `RecordingStarted`.* The clock reset precedes the
   gate flip by construction, not by convention.
2. **One authoritative gate.** `RecordingArm.IsRecording` is the only gate; the arm is the only
   mutation point. Every reader — `CaptureSession`'s video/audio frame callbacks, the recorder, the
   sink's stop edge, the watchdog's boundary finalize — reads that one property.
3. **One finalize path.** `Disarm(RecordingStopReason)` is the single path: *flip the gate → drain →
   finalize the sink → build the summary → raise `RecordingStopped`.* User stop, low-disk, and the
   watchdog boundary differ only in the reason they pass.
4. **An explicit drain step.** Disarm flips the gate, then bounded-waits (≤50 ms) on a writers-in-flight
   counter so a source thread already inside a write finishes before the transport is torn down. This
   turns the currently-documented stale-gate race into an enforced, bounded one.
5. **`RecordingStorageGuard` owns storage policy** — preflight (FAT32 4 GB guard, session-size
   headroom floor) and the periodic low-water mark — as pure functions of drive facts, so they are
   testable without a real volume.
6. **The `IRecordingSink` seam stays as it is.** Two real adapters (`FfmpegPipeSink`,
   `NullRecordingSink`) justify it, and this ADR does not change the transport contract.

## Consequences

**User-visible (requires a ruling before Slice 2 is written).** Routing the low-disk stop through the
single `Disarm` path means a low-disk stop will **newly** raise `RecordingStopped` with a
`RecordingStopSummary` — so the operator will now get a stop summary line and health verdict for a
low-disk stop, where today they get only an error message. This is almost certainly the *desired*
behavior (a low-disk stop is a stop, and DEF-01's media-duration reporting should apply to it), but
it is a visible change, not a pure refactor.

**Model change.** `RecordingStopSummary` gains a `Reason` field (`RecordingStopReason`:
`UserRequested` / `LowDisk` / `InputLoss` / `SinkError` / `Dispose`) so `Disarm` can report *why*.
That means touching `Models/RecordingHealth.cs`, which is outside the work order's permitted list —
flagged rather than assumed.

**Hot path.** The writers-in-flight counter adds two allocation-free interlocked operations per video
frame and per audio chunk. No allocation, no lock, no branch added to the common path.

**`StreamRecorderService` becomes a compatibility facade.** Its public surface is preserved (tests,
the ViewModel, and `CaptureSession` keep compiling unchanged) but its body delegates to the arm, so
the fragmented finalize code disappears rather than being wrapped. `EstimateSessionSize` survives as a
forwarder.

## Slice 1 (complete)

`RecordingStorageGuard` extracted: the preflight thresholds, FAT32 guard, session-size estimation,
low-water verdict and byte formatting moved out of `StreamRecorderService` (≈3.0 KB of policy
deleted from a service whose job is orchestration). Behavior is identical, including the retention of
the `Task.Run` finalize on the low-disk path — deliberately left in place for Slice 2 so this slice
carries no behavior change at all. Covered by headless policy tests (FAT32 case-insensitivity and
exFAT exemption, 4× headroom plus the 2 GB floor, the undetermined-volume proceed, low-water with the
FAT exemption, estimate scaling by quality/pixels/fps/ProRes and the zero-fps fallback, formatting).

## Slice 2 (pending)

Introduce `RecordingArm`, make it the single gate and the single finalize path, move the A/V clock
into it, route `CaptureSession`'s three triggers through `Disarm(reason)`, and convert
`StreamRecorderService` to a facade. Acceptance: `grep` shows no `Task.Run` finalize and exactly one
`FinalizeSink` call site outside the arm; the ADR-0002 watchdog tests and the DEF-01/health tests stay
green; the real `--real-sender-soak` passes on the final tree.
