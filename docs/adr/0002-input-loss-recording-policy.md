# ADR-0002: Input loss during recording is loud, self-healing, and never silently stitched

- **Status:** Accepted (2026-10-15)
- **Deciders:** Odogwu (orchestration) on behalf of the project owner
- **Scope:** `OmtReceiverService`, `CaptureSession`, `StreamRecorderService`, `FfmpegProcessHost` (watchdog), `MainWindowViewModel` (alarm surface), new input-health watchdog module
- **Builds on:** ADR-0001 (`BroadcastSessionState` gains `Reconnecting` / `InputLost` states)

## Context

The FFmpeg stall watchdog defines a stall as "data queued but no write". When the input
feed dies (sender reboot, cable pull, Wi-Fi drop) the queue is empty, so the watchdog
classifies a dead input as an idle window and the recording keeps writing the frozen last
frame — potentially for hours — with audio gaps. `OnSourceErrorOccurred` only logs;
nothing links input health to the recording lifecycle, and there is no auto-reconnect: a
transient 5-second outage ends the session until the operator notices.

## Decision

1. **Watchdog:** a recording-level input-health watchdog, distinct from the FFmpeg stall
   watchdog. While recording, if no video frame has arrived for **5 seconds** (default,
   operator-configurable), raise a loud `InputLost` state: UI banner, log entry. The
   threshold is measured in wall-clock absence of frames, not queue depth.
2. **Reconnect:** network sources (`OmtNetworkSource` path) auto-reconnect with backoff,
   reusing the worker-owns-its-resource / identity-check pattern already established in
   `OmtReceiverService`. Host desktop/window capture is out of scope for auto-reconnect —
   local capture failure is a different fault class and stays an error.
3. **Segment-per-outage:** a recording interrupted by input loss never stitches the gap
   invisibly. The take is finalized **immediately at the outage boundary** — the point is
   to stop writing the frozen last frame — and the grace period governs *resume-vs-stop*,
   not container lifetime: frames return within grace → resume recording into a new
   sequential segment (`…_partNN` suffix); they do not → the take ends at that final
   segment. (Clarified 2026-10-15 during implementation review; the implementation
   finalizes synchronously on the health-tick thread, consistent with the existing
   Disconnect → StopRecording path — offloading is a recorded follow-up, not a defect.)
4. **Alarm surface:** banner + log. No audible alarm in this pass.

## Consequences

- No recording can silently degrade; every outage boundary is explicit and named on disk.
- Transient blips self-heal without operator attention or take discontinuity.
- `BroadcastSessionState` gains `Reconnecting` / `InputLost` states rather than ad-hoc
  flags, keeping one named state machine (ADR-0001).
- False-alarm cost: a genuinely idle-but-alive source (e.g. static test signal with no
  video frames emitted) must be excluded from the watchdog — the watchdog arms only while
  a recording is active and the source is a network source.
