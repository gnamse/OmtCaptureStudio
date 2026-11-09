# OMT Capture Studio Domain Context

This document captures the domain model, ubiquitous vocabulary, and architectural seams for **OMT Capture Studio**.

---

## Ubiquitous Vocabulary

### Media & Pipeline Core
- **CaptureSession**: The central engine module that coordinates an active media session. It encapsulates stream connection state, frame dispatch, audio pipeline feeds, telemetry aggregation, and recording lifecycle. Presentation layers (Avalonia ViewModels) consume `CaptureSession` headlessly.
- **MediaSource**: An abstraction representing a timed provider of raw uncompressed video and audio frames. Implementations include `OmtNetworkSource` (network stream via `libomtnet`) and `SyntheticPatternSource` (local SMPTE color bars and sine wave).
- **VideoFrame**: A raw uncompressed video image payload, typically BGRA 8-bit, characterized by width, height, stride, framerate, and 64-bit microsecond timestamp.
- **AudioFrame**: An uncompressed PCM audio payload, delivered by OMT as 32-bit floating-point planar channels (pointer to array of float pointers).
- **StreamFormat**: An immutable value object encapsulating the active capture stream parameters (width, height, framerate, sample rate, channels) cached by `CaptureSession` and automatically consumed by recording and display subsystems.

### Audio Subsystem
- **AudioEngine**: The deep audio module responsible for ingesting planar audio frames, calculating real-time RMS/peak telemetry (decibels) for VU metering, running speaker monitoring via Vortice XAudio2 (`XAudio2Monitor`), and delivering interleaved PCM streams to recording taps using reusable pooled buffers.
- **AudioLevelData**: Real-time calculated telemetry for stereo or multi-channel audio, containing peak levels, RMS levels, and peak-hold decibel values.

### Recording Subsystem
- **StreamRecorder**: The module orchestrating file recording. It tracks recording duration, frame counts, byte counts, and delegates actual stream writing to an `IRecordingSink`.
- **RecordingSink**: An output transport seam for encoded or containerized media. In production, `RecordingSinkSelector.ForProduction()` constructs `FfmpegPipeSink` (raw A/V over Windows Named Pipes to an FFmpeg subprocess). Media Foundation remains a gated future option behind the same `IRecordingSink` seam — see `docs/adr/0012-recording-engine-strategy.md`. In test environments, `NullRecordingSink` / in-memory stubs verify throughput without an external process.
- **RecordingConfig**: User-specified parameters for output container format (MP4, MKV, MOV), video encoder (AutoHardware, NVENC, QSV, AMF, CPU x264, ProRes), quality preset, and destination directory.
- **PA16 policy**: Incoming OMT audio that announces PA16 (or any non-FPA1 codec) is **refused by name** via `OmtAudioFrameGuard` — never reinterpreted as float32. Decode support waits on an explicit product requirement (ADR-0012 §3).

### Broadcast Control & Resilience
- **BroadcastSessionState**: The headless module that owns broadcast-mode state (`Offline`, `Connecting`, `NetworkConnected`, `TestSignal`, `Broadcasting`; `Reconnecting` and `InputLost` reserved per ADR-0002). It owns every mode-transition rule (stop-recording-first, selector locking) and raises a single `StateChanged` event; presentation layers only project state onto `StudioPalette` visuals. See `docs/adr/0001-broadcast-session-state.md`.
- **Input-Loss Watchdog**: The recording-level guard that raises `InputLost` when no video frame arrives for a configurable window (default 5 s) while recording. Distinct from the FFmpeg stall watchdog, which only fires when data is queued but writes stall. See `docs/adr/0002-input-loss-recording-policy.md`.
- **Segment**: One independently playable recording file produced by a continuous take. An input-loss outage boundary finalizes the current segment and resumes into the next sequential segment (`…_partNN`), so a silent gap is never stitched invisibly (ADR-0002).
