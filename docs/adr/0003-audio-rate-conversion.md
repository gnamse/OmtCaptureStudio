# ADR-0003: One stereo capture ring, and one rate-conversion policy

- **Status:** **Accepted (Option A) — implemented in two slices.** Slices are recorded below.
  - **Slice 1 (complete):** `StereoCaptureRing` extraction, `HostAudioFormat` constants relocation,
    `IHostAudioSource` widening, headless ring/interface tests. Conversion placement is deliberately
    unchanged in this slice.
  - **Slice 2 (complete):** WASAPI rate conversion relocated to the consumer thread; both adapters
    now share one polyphase-sinc policy in `Services/Audio/FixedRatioStereoResampler.cs` (promoted out
    of its private-nested home in `AsioHostAudioSource`), with a delay-free passthrough when the device
    rate already matches the 48 kHz stream rate (float-quantised kernel: measured deviation ≤ 1e-6 —
    NOT bit-exact; asserted as tolerance, never as exactness).
  - **Consequence (user-audible, expected):** on a rate-mismatched WASAPI endpoint the output is
    cleaner than it was — the sinc replaces the per-sample linear interpolation. Added latency on that
    path is the documented 48-frame (~1 ms) FIR lookahead; the matched-rate path is unchanged.
  - **Consequence (untested live — probed and closed by QA):** the rate-mismatch WASAPI path cannot
    be triggered on this host. QA's COM probe of all 25 WASAPI endpoints (2026-10-15): 24 have a
    48 kHz shared mix format; the one exception (Poly BT600 mic, 16 kHz native) is still negotiated
    to 48 kHz because `AudioClientInitialize` in shared mode with
    `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | SRC_DEFAULT_QUALITY` returns S_OK — the Windows mixer
    delivers 48 kHz directly. The `initHr < 0` fallback branch (`WasapiHostAudioSource.cs` 342–371)
    is therefore entered **only** when a driver explicitly rejects AUTOCONVERTPCM in shared mode.
    The sinc path itself is verified headlessly (Step 0.678: 57.8 dB SER) and live via the ASIO
    44.1→48 path (Step 0.69). Release notes should state: mismatch conversion on WASAPI is a
    driver-rejection fallback, not the normal path.
- **Deciders:** Odogwu (orchestration) on behalf of the project owner
- **Scope:** `Services/IHostAudioSource.cs`, `Services/AsioHostAudioSource.cs`,
  `Services/WasapiHostAudioSource.cs`, new `Services/Audio/*`, callers
  (`OmtDesktopCaptureGenerator`, `MainWindowViewModel`)
- **Builds on:** ADR-0001 (state machine), ADR-0002 (input-loss watchdog), NET-007 (recording health)

## Context

### Verified duplication (line numbers re-checked against `0971d6c`)

| Concern | ASIO | WASAPI |
| --- | --- | --- |
| Ring write | `AsioHostAudioSource.WriteRingLocked` (825–850) | `WasapiEndpointCapture.WritePlanarSample` (216–238) |
| Ring read | `AsioHostAudioSource.ReadRingLocked` (852–871) | `WasapiEndpointCapture.ReadPlanar` (190–214) |
| Short "no data" read | `AsioHostAudioSource` ~230–236 | — |
| Sample conversion | `SampleTypeSizeBytes` + converters (~877+) | `ReadFrameStereo` (461+) |
| Resampler | `FixedRatioStereoResampler` (1055+), 96 taps / 64 phases, Blackman-Harris | linear interpolation inside `IngestPacket` (429–457) |

Both rings are byte-for-byte the same policy: capacity = 1 s at 48 kHz, latency cap = 120 ms,
drop-to-half-cap on overflow, and underrun → zero-fill. The ASIO copy literally carries the comment
`// Ring buffer (mirrors WasapiEndpointCapture)`. Both paths already have a **pure passthrough when
the device rate already matches 48 kHz** (ASIO 566–580, WASAPI 429) — so the asymmetry exists only
on the *rate-mismatch* branch.

The shared vocabulary has also leaked across the seam: `WasapiHostAudioSource.TargetSampleRate` /
`TargetChannels` are `public const`s that `AsioHostAudioSource` (53, 56, 57) and
`OmtDesktopCaptureGenerator` (260, 261) both reach into. One module reaching into a peer for a
constant is exactly the coupling this pass is meant to remove.

### The finding that changes deliverable 2: the two resamplers sit at different points in the pipeline

This is not a kernel-quality difference — it is a **push vs pull** difference, and it is the reason
the WASAPI path is cheap:

- **WASAPI converts on the CAPTURE thread, before the ring.** `IngestPacket` is called from the
  capture loop (call site 376) and writes already-48 kHz samples into the ring
  (`WritePlanarSample`). Per-sample cost: one lerp (`l0 + (l1 - l0) * frac`) on frames that are
  already in a local packet — no lookahead, no extra buffering, no added latency.
- **ASIO converts on the CONSUMER thread, after the ring.** `FillPlanarAudio` calls `FillResampled`
  (call site 230), which pulls from the ring and feeds `FixedRatioStereoResampler`. That resampler
  needs `HalfWidth` = 48 frames of **lookahead** (`Process`, 1106–1110) and its own FIFO, and is
  documented as "confined to whichever thread calls it (the `FillPlanarAudio` consumer)".

So the two rings do **not** hold the same thing: ASIO's ring holds device-rate samples, WASAPI's
holds target-rate samples. "Use the sinc for both" therefore cannot be a swap of one function for
another — it means moving WASAPI's rate conversion out of the real-time capture callback and onto
the consumer (a new FIFO on that path, +~48 frames ≈ 1 ms of lookahead, and a different ring
contract). Running a 96-tap × 64-phase sinc on the capture thread instead would put ~96 MACs per
sample per channel on a real-time audio callback, which is the specific hazard AC4 asked about.

## Decision

**Adopted (proposed):**

1. **`StereoCaptureRing`** — one deep module owning capacity, the 120 ms latency cap, the locked
   read/write, the drop-to-half overflow rule and the underrun→silence rule. Both hardware adapters
   become thin converters that push device samples into it; their COM interop stays local. The
   deletion test applies: the duplicated ring bodies must be DELETED, not wrapped.
2. **`HostAudioConstants`** (or equivalent neutral home) — `TargetSampleRate` / `TargetChannels`
   move off `WasapiHostAudioSource` so ASIO and `OmtDesktopCaptureGenerator` stop reaching into the
   WASAPI type for shared vocabulary.
3. **Widen `IHostAudioSource`** with `IsHardwareActive`, `LastError` and the negotiated device rate,
   so `MainWindowViewModel` stops downcasting to `AsioHostAudioSource` (call sites 1244 / 1252 /
   1276).
4. **Rate conversion: ONE policy — the polyphase sinc for both paths, with passthrough at 48 kHz** —
   **but only if WASAPI's conversion is relocated to the consumer**, which is the open decision below.

**Ruling (Odogwu, 2026-10-15): Option A — unify at the consumer.** The push-vs-pull finding settled
it: running the sinc on the real-time capture thread was the hazard AC4 named, whereas the consumer
thread already carries exactly that load for ASIO, so relocating WASAPI's conversion removes work
from the real-time callback rather than adding it. Option B would have enshrined a documented quality
gap on the user-audible mismatch path. Considered alternatives:

- **Option A — unify at the consumer (true one-policy).** Move WASAPI conversion to the consumer and
  use `FixedRatioStereoResampler` for both. Delivers the stated decision exactly; costs a FIFO +
  ~1 ms lookahead on the WASAPI mismatch path, makes both rings device-rate, and is a larger slice
  than "replace a function" — it also changes what the WASAPI ring contains.
- **Option B — keep push-linear on the WASAPI capture thread, documented.** Records the asymmetry as
  deliberate (real-time callback must stay O(1) per sample; the lerp is a stopgap quality ceiling),
  and still deletes the duplicated *ring* and *conversion* code. Does not satisfy the literal
  "one rate-conversion policy", but satisfies "one ring policy" and makes the difference a decision
  rather than an accident.

Recommendation: **Option A, as its own slice**, because the user-audible artefact is the mismatch
path (44.1→48) and correctness/consistency was the stated priority; the CPU delta is negligible at
stereo. But it should not ride in the same pass as the ring extraction, because it is the riskiest
part and the ring extraction is independently valuable and independently testable.

## Consequences

- One implementation of the latency-cap invariant (AC3 becomes grep-able: exactly one occurrence).
- Ring behaviour becomes headlessly testable for the first time — today it is only reachable through
  live hardware, which is the testability hole this pass exists to close.
- Option A changes WASAPI's mismatch-path output from linear-interpolated to sinc-interpolated:
  user-audible, strictly an improvement (less aliasing on the 44.1→48 conversion), and it adds ~1 ms
  of latency on that path only; the 48 kHz path is bit-exact passthrough in both options.
  Measured: sinc **57.8 dB SER** vs linear **40.5 dB SER** on a 200 Hz–8 kHz sweep at 44.1→48
  (a 17.3 dB improvement); matched-rate identity deviation **3.48e-17** over 2048 frames (float
  rounding).
- Option B leaves a documented quality gap between the two drivers on rate-mismatched devices.
- Risk: both adapters contain COM interop whose threading and buffer ownership must not change; the
  ring extraction must preserve each caller's existing lock discipline (ASIO locks outside its
  `*Locked` helpers; WASAPI locks inside `IngestPacket`/`ReadPlanar`).
