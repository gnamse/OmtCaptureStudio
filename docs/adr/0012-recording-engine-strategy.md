# ADR-0012: Recording engine stays FFmpeg; Media Foundation gated; PA16 refuse

- **Status:** Accepted (2026-11-09)
- **Deciders:** Agent (Phase 3 of `docs/IMPROVEMENT_PLAN.md`) on behalf of the project owner,
  consuming former `spikes/mf-sinkwriter` measurements and plan open-decision defaults
- **Scope:** Product strategy for the shipping recording engine; PA16 / hostile-peer audio codec
  policy. Spike evidence lives in this ADR; the throwaway tree was deleted after acceptance.
- **Related:** ADR-0002 (crash/segment salvageability), ADR-0005 (`RecordingArm` + `IRecordingSink`
  seam), ADR-0010 (composite fan-out; `tee` still measurement-blocked),
  `Services/Sinks/RecordingSinkSelector.cs`, `docs/IMPROVEMENT_PLAN.md` Phase 3
  (evidence table below supersedes deleted `spikes/mf-sinkwriter/README.md`)

## Context

Production recording is an FFmpeg subprocess behind `FfmpegPipeSink` /
`FfmpegProcessHost`, selected only through `RecordingSinkSelector.ForProduction()`
(Phase 2.2). That path carries the installer cost (~102 MB `ffmpeg.exe`), named-pipe
hardening (preflight, R25/R26, elevation 740, AV quarantine), and the full container /
encoder matrix operators already use: MP4 / MKV / MOV × AutoHardware / NVENC / QSV /
AMF / CpuX264 / ProRes.

A throwaway spike (formerly `spikes/mf-sinkwriter`, Vortice.MediaFoundation 3.6.2)
asked whether `IMFSinkWriter` can record this app's pipeline in-process. The spike
**validated** a 1080p60 BGRA → H.264 MP4 path, including fragmented-container crash
salvage and a hybrid AAC+H.264 write — but left several load-bearing questions open
(HW encoder identity, multi-hour soak, float32→PCM16 cost, ProRes/MKV/MOV matrix).
The tree was **deleted** after this ADR accepted stay-FFmpeg; numbers below are the
canonical record.

Phase 3 must choose among:

| Option | Meaning |
|---|---|
| **(A) Stay FFmpeg** | Harden the shipping subprocess; retain spike evidence in this ADR; no MF in `Services/` |
| **(B) MF primary + FFmpeg fallback** | `MediaFoundationRecordingSink : IRecordingSink` behind a flag; FFmpeg remains for matrix / fallback |
| **(C) MF-only** | Retire `ffmpeg.exe` from the installer after audio / HW / crash / soak gates |

Plan default (open-decision table): **Stay FFmpeg until audio + HW + crash gates**. Spike
recommendation: adopt MF as a *selectable* sink later, keep FFmpeg until multi-hour
field proof. Those agree on *shipping default today*; they disagree only on when to
start building option (B).

Separately, the receive path is FPA1-only. `OmtAudioFrameGuard` refuses PA16 (and any
non-FPA1 codec) by name rather than reinterpreting int16 planar as float32 — a
remote-triggerable OOB read if left codec-blind. Product must either keep refuse or
explicitly fund PA16 decode.

## Decision

### 1. Recording engine — **(A) Stay FFmpeg** as the production default

1. **Shipping takes continue to use `FfmpegPipeSink` via `RecordingSinkSelector.ForProduction()`.**
   No Media Foundation types land under `OmtCaptureStudio/Services/` in this phase.
2. **Do not ship the spike.** `spikes/mf-sinkwriter` was throwaway evidence and has been
   **deleted** (owner go-ahead after this ADR). Load-bearing numbers remain in the
   evidence table below — do not revive the tree as production code.
3. **Option (B) is the only plausible next step**, not (C). MF-only is **NO-GO** until
   (B) has been the field default through multi-hour soaks *and* the ProRes / MKV / MOV /
   HW-encoder matrix either moves to MF or is deliberately dropped as product.
4. **Do not start Phase 3.2 implementation yet.** Gates below are unmet. At most, future
   work notes a flag + factory hook on `RecordingSinkSelector` — never a half-baked
   production MF sink copied from the spike.

### 2. Go / no-go criteria before any production MF sink (option B)

Every row must be **GO** before flipping a feature flag default, and before removing
FFmpeg from any shipping preset that MF cannot serve.

| Gate | Required evidence | Spike status (2026-11) | Verdict |
|---|---|---|---|
| **Crash / fragment parity (ADR-0002)** | Hard-kill mid-take leaves a decode-clean file; suite pins fragmented container | Default MPEG4: **100% loss** (no `moov`). `TranscodeContainertype = Fmpeg4`: hard-kill → `moov` + 268 `moof`, ffprobe OK, full decode **CLEAN** | **GO *if* `Fmpeg4` is mandatory** — plain MPEG4 is a hard NO |
| **BGRA / per-frame budget** | Direct ARGB32 input within 16.67 ms @ 60 fps | ARGB32 WriteSample **1.52 ms/frame** (~9% of budget); self NV12 convert **5.24 ms** (reject that path) | **GO** for H.264 MP4 BGRA |
| **AAC / audio order** | App float32 interleaved → AAC without silent-noise files | AAC refuses float32 (`MF_E_INVALIDMEDIATYPE`); PCM16 alone OK; **audio stream must be added before video**; swallowing `SetInputMediaType` failure wrote raw PCM as "aac" (looks valid, sounds like noise) | **CONDITIONAL** — production must convert float32→PCM16, add audio-first, and fail closed on media-type errors. Conversion cost **unmeasured** |
| **HW encoder identity** | Same operator-visible encoder class as today's AMF/NVENC/QSV choice (or an explicit product acceptance of MF's pick) | Throughput ~284 fps headroom suggests HW, but `GetStatistics` → `E_INVALIDARG`; **MFT not enumerated** | **NO-GO** — identity open |
| **AOT / COM / size** | AOT publish green without reflection helpers; installer size story honest | Spike AOT publish **succeeded** (exe 2,813,952 B); trim warnings from spike reflection helper only; `Vortice.MediaFoundation.dll` **524,288 B** vs `ffmpeg.exe` **102,856,192 B** | **GO** for interop shape / size *incentive*; production must not copy the reflection helper |
| **Multi-hour soak** | Real paced takes (hours) finalize clean; no COM / MFT stalls | Spike write loop **unpaced**; long-run **not tested** | **NO-GO** |
| **Container / codec matrix** | MP4 + MKV + MOV; H.264 HW/CPU + **ProRes** presets either work on MF or stay on FFmpeg fallback | Spike measured **H.264 MP4 only** (plus hybrid AAC). ProRes archive preset and MKV crash-resilient presets are FFmpeg-only today | **NO-GO for MF-only**; **NO-GO for MF-default** until matrix plan exists |
| **Fan-out (ADR-0010)** | Dual-folder failure model preserved (`detach` child, take survives) | Unrelated to MF spike; `tee` still blocked on `empty_moov` extradata loss | Composite of FFmpeg children remains shipping; MF dual-folder is a **separate** design if ever needed |

**Aggregate:** feasibility is real; **shipping gates are not met.** Stay on (A). Revisit (B)
only after HW identity, paced multi-hour soak, measured PCM16 convert cost, and an
explicit matrix plan (which presets stay FFmpeg-fallback forever).

### 3. PA16 / hostile-peer policy — **refuse until product requires decode**

1. **Keep refuse-by-name.** `OmtAudioFrameGuard` continues to reject PA16 (`UnsupportedCodec`)
   and any non-FPA1 codec. Suite rows (`OmtAudioFrameGuardTests`, `SecurityRegressionTable` R1)
   that assert refuse + "PA16" in the detail string remain load-bearing.
2. **No silent reinterpretation.** A PA16 payload must never be read as float32, regardless
   of declared length. That is a security invariant, not a quality preference.
3. **Operator-visible messaging:** refusal detail already names the codec (e.g. `PA16 (…)`)
   so logs/diagnostics are actionable. No separate toast required for this pass; do not
   soften the log to a bare HRESULT / integer.
4. **Decode is out of scope** until a real sender requirement appears. Adding PA16 decode
   is a funded product change (convert int16 planar → float32 under the geometry bound),
   not a drive-by in a recording-engine PR.

## Consequences

- **Installer / deployment:** `ffmpeg.exe` remains a hard prerequisite and release payload
  (~102 MB). The measured MF size win stays on the table for a future (B)/(C) pass.
- **Seam:** `IRecordingSink` + `RecordingSinkSelector` remain the only integration surface
  if/when MF is built. Phase 2.2's "sole `new FfmpegPipeSink()` site" stays true.
- **Crash story:** ADR-0002 salvageability continues to ride FFmpeg
  `frag_keyframe+empty_moov`. Any future MF sink **must** set `Fmpeg4` (or equivalent
  fragmented container) and pin it with a kill-the-take suite step — default MPEG4 is
  forbidden.
- **ADR-0010:** unchanged. Two-child composite of FFmpeg sinks remains shipping; `tee`
  still blocked.
- **Tests / AOT / CI:** no `spikes/` tree in the repo; no production dependency on MF
  spike code. AOT and the headless suite are unaffected by the delete.
- **PA16:** security posture unchanged; product question closed as refuse-by-default.

## Evidence table (load-bearing spike numbers)

Copied from the deleted `spikes/mf-sinkwriter/README.md` so deletion does not erase
the decision basis. All runs: synthetic 1080p60, Vortice.MediaFoundation 3.6.2,
decode verified with `ffmpeg -v error -f null -` unless noted.

| Topic | Result |
|---|---|
| Feasibility NV12 → H.264 MP4 | 10 s, 1406 frames, 57,709,035 B, 19.7 Mbps, decode **clean** |
| Feasibility ARGB32/BGRA → H.264 MP4 | 10 s, 2837 frames, 99,045,694 B, 16.8 Mbps, decode **clean** |
| Per-frame NV12 (self convert) | convert **4.509 ms** + WriteSample 0.733 ms = **5.24 ms** |
| Per-frame ARGB32 (sink converts) | WriteSample **1.521 ms** (~3.4× cheaper); hybrid run **0.985 ms** |
| Hard-kill default MPEG4 | 104,610,526 B written, **no `moov`**, ffprobe invalid, decode **FAIL** — entire take lost |
| Hard-kill `Fmpeg4` fragmented | 169,056,126 B, `moov` + **268 `moof`**, 80.4 s readable, decode **CLEAN** |
| AAC + float32 input | **REFUSED** `MF_E_INVALIDMEDIATYPE (0xC00D36B4)` |
| AAC + PCM16, video stream first | **REFUSED** (same HRESULT — ordering trap) |
| AAC + PCM16, audio stream first | **ACCEPTED**; hybrid `proof-hybrid.mp4` 732,942 B, h264+aac 48k 2ch, decode **CLEAN** |
| Malformed-output trap | Failed `SetInputMediaType` + continued `WriteSample` → file tagged `aac` that decodes as noise |
| 0-byte `--av` false alarm | Early `return` before `BeginWriting`/`Finalize` in probe — **not** an ARGB32 defect |
| AOT publish | Succeeded; native exe **2,813,952 B**; IL2026/IL2075 from spike reflection helper only |
| Binary size comparison | MF interop DLL **524,288 B** vs ffmpeg.exe **102,856,192 B**; v1.0.0.24 installer 73,066,028 B |
| HW MFT identity | **Unmeasured** (`GetStatistics` `E_INVALIDARG`; no `MFTEnumEx`) |
| Multi-hour / paced record | **Unmeasured** (unpaced throughput only) |
| ProRes / MKV / MOV on MF | **Unmeasured** |

Interop choice recorded: prefer **Vortice.MediaFoundation** on the same 3.6.x line as
existing Vortice packages — no hand-rolled COM. Production GUID/attribute names must be
compile-time references, not the spike's reflection helper.

## Follow-ups

1. **`spikes/mf-sinkwriter` deleted** (2026-11-09) after owner go-ahead; evidence table
   above is the sole retained decision basis.
2. **Phase 3.2 (only if owner reopens (B)):** implement `MediaFoundationRecordingSink :
   IRecordingSink` behind an explicit flag in `RecordingSinkSelector` factories; mandatory
   `Fmpeg4`; fail closed on media-type setup; float32→PCM16 with measured cost; MFT
   identity probe; paced multi-hour soak; suite kill-test; keep FFmpeg for ProRes/MKV (or
   whatever the matrix plan assigns). **Do not resurrect the spike tree into `Services/`.**
3. **Ticket stubs (not started):**
   - `feat(recording):` MF sink behind flag once gates green
   - `test(recording):` hard-kill asserts fragmented MF output decode-clean
   - `docs(adr):` amend 0012 when (B) flips or matrix plan lands
4. **PA16 decode:** only if a real sender requires it — separate product ticket; keep
   refuse + named detail until then.
5. **CI artifact secrets** (`CI_PREREQ_ARCHIVE_URL`) remain an owner action from Phase 1 —
   orthogonal to this ADR, still blocking Actions suite green.
6. **ADR-0010 `tee`:** still measurement-blocked; do not couple to MF work.

## Alternatives considered

- **(B) now, flag off-by-default:** rejected. Gates (HW identity, soak, matrix, PCM16 cost)
  are unmet; a dormant production sink copied from a spike is half-baked surface area and
  AOT/COM risk without operator value.
- **(C) MF-only:** rejected. Would drop ProRes archive and MKV presets (or require a
  second engine anyway), and would retire crash-hardened FFmpeg without field soak.
- **Fill ADR gap 0004 for this topic:** rejected. README / plan convention is next free
  number after the highest accepted ADR, or intentional gap fill when history requires it.
  Recording-engine strategy was never reserved at 0004; **0012** is correct. Gaps
  0004 / 0007–0009 stay vacant (no stubs).
