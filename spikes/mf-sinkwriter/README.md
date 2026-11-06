# SPIKE-MF — Media Foundation SinkWriter as an in-process recording engine

**Branch:** `feature/media-foundation-sink` (from `f52e6d9`) · **Status:** THROWAWAY — nothing here ships, nothing in `Services/` was touched.

## The question

Can Media Foundation's `IMFSinkWriter` record this app's pipeline (1080p screen capture → H.264 MP4) **in-process**, and what would we gain or lose versus the ffmpeg subprocess?

Motivation, all measured this week: ffmpeg-as-a-process costs ~102 MB of installer, a pipe at 1080p60 BGRA, and an entire hardening surface (preflight, R25/R26, the 740 elevation path, release-integrity preflight) plus deployment fragility (AV quarantine, stale-binary traps).

## How to run

```
cd spikes/mf-sinkwriter
dotnet run -c Release -- --list                                        # the package's real vocabulary
dotnet run -c Release -- --encode  10 C:/tmp/out.mp4                   # NV12 input path
dotnet run -c Release -- --encode  10 C:/tmp/out.mp4 --bgra            # BGRA input path
dotnet run -c Release -- --encode 600 C:/tmp/out.mp4 --bgra --fmpeg4   # then HARD KILL it: the crash test
```

Interop choice: **`Vortice.MediaFoundation` 3.6.2** — the same version line as the app's existing `Vortice.Direct3D11` / `DXGI` / `XAudio2`, so adopting it introduces **no version mixing**. The alternative (hand-written COM interop) was rejected: ~500 KB of generated interop we would have to maintain, for no capability gain.

Every GUID is looked up **from the package by reflection**, never typed from recall — a renamed or absent member fails loudly with the available names rather than silently encoding a wrong format. That discipline paid for itself twice (see Surprises).

---

## Measurement 1 — Feasibility: **YES**

10 s of synthetic 1080p60 → H.264 MP4, both input paths, verified with `ffprobe` and a **full `ffmpeg -v error -f null -` decode (exit 0, silent)**:

| input | frames | file | bitrate | decode |
|---|---|---|---|---|
| NV12 | 1406 | 57,709,035 B | 19.7 Mbps | **clean** |
| ARGB32 (BGRA) | 2837 | 99,045,694 B | 16.8 Mbps | **clean** |

`codec_name=h264`, `1920x1080`, `60/1` in both.

## Measurement 2 — Input format cost: **the sink accepts BGRA directly**

This is the make-or-break per-frame cost, measured separately from the encode:

| path | BGRA→NV12 convert | WriteSample | total per frame |
|---|---|---|---|
| NV12 (we convert on the CPU) | **4.509 ms/frame** (61.8% of wall) | 0.733 ms | **5.24 ms** |
| ARGB32 (the sink converts internally) | **not needed** | 1.521 ms | **1.52 ms** |

**`SetInputMediaType(ARGB32)` is accepted, and the direct path is ~3.4× cheaper per frame.** Writing our own NV12 converter would be both slower *and* more code. At 60 fps the budget is 16.67 ms/frame, so the direct path uses ~9% of it.

## Measurement 3 — Hardware encode: **NOT MEASURED — stated honestly**

I could not identify *which* MFT the sink selected: `IMFSinkWriter.GetStatistics` returns `E_INVALIDARG` for this configuration even after `Finalize`, and identifying the encoder needs an MFT enumeration this spike did not build. **What I can say:** the ARGB32 path sustained ~284 fps (≈21× real time) at 1080p, which is not consistent with a pure software encoder on one core, and `SinkWriterAttributeKeys` exposes `ReadwriteEnableHardwareTransforms` / `DisabledHardwareTransforms` to steer it. **Whether it lands on the same AMD AMF encoder the ffmpeg path uses is an open question for whoever continues this** — it is a 10-minute follow-up with `MFTEnumEx`, not a re-spike.

## Measurement 4 — Crash tolerance: **the load-bearing comparison — and it hinges entirely on one attribute**

Hard kill (`taskkill /F`, no `Finalize`) mid-take, then inspect the file:

| container | bytes at kill | `moov` | `moof` | ffprobe | full decode |
|---|---|---|---|---|---|
| **Default MPEG4** | 104,610,526 | **ABSENT** | none | **`moov atom not found` → Invalid data** | **FAILS** |
| **`Fmpeg4` (fragmented)** | 169,056,126 | present | **268 fragments** | reads fine, 80.4 s | **CLEAN** |

**With the default container, a crash destroys the entire take** — 105 MB written and not one frame recoverable; a remux attempt fails too. That is the opposite of ADR-0002's deliberate trade, where the ffmpeg path's `frag_keyframe+empty_moov` leaves a playable file.

**But MF can be told to fragment.** `MediaFactory.MFCreateSinkWriterFromURL` takes creation attributes, and `TranscodeAttributeKeys.TranscodeContainertype = TranscodeContainerTypeGuids.Fmpeg4` produces a genuinely fragmented MP4 (`moof`/`mvex` present, `mfra` written at finalize). Under a hard kill that file **survives, reads, and decodes clean**.

**This single attribute is the difference between MF being unusable and MF being a peer of the ffmpeg path for crash tolerance.** Note the MPEG4 sink's own attribute vocabulary (`MoovBeforeMdat`, `SpsppsPassthrough`) contains **no fragmentation switch** — the container-type attribute is the only way in.

### Also measured (cheap)
- **AOT: viable.** `dotnet publish /p:PublishAot=true` **succeeded** (AOT exe 2,813,952 B). The only trim warnings (`IL2026`/`IL2075`) come from *my own reflection helper* — **not from Vortice.MediaFoundation** — so the interop itself is AOT-clean; production code would not reflect.
- **Binary size:** `Vortice.MediaFoundation.dll` is **524,288 B** against **`ffmpeg.exe` at 102,856,192 B**. The v1.0.0.24 installer is 73,066,028 B; removing ffmpeg would cut most of the payload.
- **Integration shape:** a `MediaFoundationRecordingSink` would implement the existing 101-line `IRecordingSink`, chosen at `RecordingSinkSelector.Initialize` — a new deep module, no rewrite. It would retire `FfmpegProcessHost` (641 lines of process/pipe/handshake) and, with it, the preflight, R25/R26, the 740 path and the AV-quarantine exposure.

---

## Measurement 5 — the audio path (added after the first report: this was NOT tested then)

The app writes **interleaved 32-bit float PCM**. Feeding that to MF's AAC encoder:

| attempt | result |
|---|---|
| AAC output + **float32** input | **REFUSED** — `MF_E_INVALIDMEDIATYPE (0xC00D36B4)` |
| AAC output + **PCM16** input, stream added **after video** | **REFUSED** — same HRESULT |
| AAC output + **PCM16**, stream added **alone** | **ACCEPTED** (both containers) |
| **audio added FIRST, then video** | **ACCEPTED — both streams live** |

Two conclusions, both load-bearing for the real integration:

1. **The AAC encoder does not accept float32.** The production sink must convert the app's
   interleaved float32 → **PCM16** before `WriteSample`. (The cost of that conversion is
   unmeasured; it is per-audio-chunk, not per-video-frame, and the video measurements above show the
   budget has room — but it must be measured, not assumed.)
2. **Stream order matters.** Adding the video stream first makes *every* audio input type fail with
   `MF_E_INVALIDMEDIATYPE`; adding audio first makes both succeed. This cost real time to find —
   the HRESULT blames the media type, so it reads as a format problem when it is an ordering problem.
   **Add the audio stream before the video stream.**

It also produced a malformed-output trap worth recording: because `SetInputMediaType` had thrown, the
later `WriteSample` calls still ran and wrote **raw PCM reinterpreted as AAC**. The file had an
`aac` stream that `ffprobe` reported happily, while the decoder emitted
`Reserved bit set` / `Prediction is not allowed in AAC-LC` on every frame. A sink that swallows a
failed `SetInputMediaType` ships a file that looks valid and sounds like noise.

---

## Verdict: **VALIDATED** — with one mandatory condition and one open question

### What worked
- MF records 1080p60 → H.264 MP4 in-process, decode-clean, with no ffmpeg process, no pipe, and no hardening surface.
- **BGRA goes straight in** — no NV12 converter to write, and it's ~3.4× cheaper per frame than converting ourselves.
- **Fragmented MP4 gives full crash tolerance** through `TranscodeContainerTypeGuids.Fmpeg4`.
- ~~AOT-compatible~~ **AOT-compatible**, at ~512 KB versus 102 MB.

### What didn't
- **The default container is a trap.** A crash with plain MPEG4 loses 100% of the take, silently and unrecoverably. Any adoption MUST set `Fmpeg4`, and a test must pin it.
- `GetStatistics` is unusable here (`E_INVALIDARG`), so encoder identity can't be read that way.
- The spike's write loop is **unpaced**, so the fps figures are *throughput headroom*, not an achievable record rate; the real integration must pace to the source.

### Surprises
- The fragmentation discovery — it inverts the crash-tolerance answer.
- `ARGB32` being accepted at all, and being *faster* than self-converted NV12.
- Two API names differed from every reasonable guess (`Subtype` not `SubType`, `AvgBitrate` not `AverageBitRate`, `TranscodeContainertype` with a lowercase `t`). Reflecting rather than recalling is what turned each into a 1-minute fix instead of a silent wrong-format encode.

### Recommendation for the real build
**Adopt MF as a *selectable* sink, not a replacement** — behind the existing `RecordingSinkSelector`, with `Fmpeg4` mandatory and a crash-tolerance test that kills a take and asserts the file still decodes. Keep ffmpeg as the fallback until MF has recorded real multi-hour takes in the field, because the one thing this spike did **not** test is long-run stability, and ADR-0002's salvageability guarantee is exactly the property a switch puts at risk.
