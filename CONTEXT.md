# OMT Capture Studio Domain Context

This document captures the domain model, ubiquitous vocabulary, and architectural seams for **OMT Capture Studio**.

---

## Ubiquitous Vocabulary

### Media & Pipeline Core
- **CaptureSession**: The central engine module that coordinates an active media session. It encapsulates stream connection state, frame dispatch, audio pipeline feeds, telemetry aggregation, and recording lifecycle. Presentation layers (WPF) consume `CaptureSession` headlessly.
- **MediaSource**: An abstraction representing a timed provider of raw uncompressed video and audio frames. Implementations include `OmtNetworkSource` (network stream via `libomtnet`) and `SyntheticPatternSource` (local SMPTE color bars and sine wave).
- **VideoFrame**: A raw uncompressed video image payload, typically BGRA 8-bit, characterized by width, height, stride, framerate, and 64-bit microsecond timestamp.
- **AudioFrame**: An uncompressed PCM audio payload, delivered by OMT as 32-bit floating-point planar channels (pointer to array of float pointers).
- **StreamFormat**: An immutable value object encapsulating the active capture stream parameters (width, height, framerate, sample rate, channels) cached by `CaptureSession` and automatically consumed by recording and display subsystems.

### Audio Subsystem
- **AudioEngine**: The deep audio module responsible for ingesting planar audio frames, calculating real-time RMS/peak telemetry (decibels) for VU metering, running speaker monitoring via NAudio, and delivering interleaved PCM streams to recording taps using reusable pooled buffers.
- **AudioLevelData**: Real-time calculated telemetry for stereo or multi-channel audio, containing peak levels, RMS levels, and peak-hold decibel values.

### Recording Subsystem
- **StreamRecorder**: The module orchestrating file recording. It tracks recording duration, frame counts, byte counts, and delegates actual stream writing to an `IRecordingSink`.
- **RecordingSink**: An output transport seam for encoded or containerized media. In production, `FfmpegPipeSink` delivers raw video/audio to FFmpeg over Windows Named Pipes. In test environments, `TestRecordingSink` verifies frame throughput in memory without external process dependencies.
- **RecordingConfig**: User-specified parameters for output container format (MP4, MKV, MOV), video encoder (AutoHardware, NVENC, QSV, AMF, CPU x264, ProRes), quality preset, and destination directory.
