# AGENTS.md — for AI agents working in this repo

Read this before touching anything. It is brief on purpose; the detail lives in
the documents it points to.

## Must-read first
- **`CODING_STANDARDS.md`** — the enforceable contract for all C# work. Non-
  negotiable constraints: Native AOT (no reflection/dynamic IL), single static
  brush palette (`StudioPalette.cs`) for headless-constructible ViewModels,
  Dispatcher-only UI mutation, no allocation churn in the per-frame hot path
  (bounded queues + ArrayPool, **return every buffer exactly once**), idempotent
  disposal.
- **`CONTEXT.md`** — domain vocabulary and architectural seams (CaptureSession,
  IMediaSource, IRecordingSink, RecordingConfig, ...).
- **`README.md`** — build/run/deploy.

## The one rule above all
**Never guess at a failure mode.** This is a broadcast-grade multi-hour
recorder; every fix has burned this project before. When a change touches the
recording/render path, verify past the known failure threshold (the E2E records
6s explicitly to exceed a 5s bug), not a 1-second smoke. When two sources
disagree about a defect, read the actual source and reconcile before patching.

## Build & test
```bash
dotnet build OmtCaptureStudio.sln -c Debug
cd OmtCaptureStudio.Tests && dotnet run -c Debug -p:Platform=x64
```
The test suite is a single headless console app that throws on failure — no
Avalonia `Application` is created, so ViewModels/engines must stay
headless-constructible. Native AOT is release; run an actual AOT publish before
shipping reflection-sensitive or generic-hot-loop changes.

## Commit style
Conventional Commits, lowercase scope: `fix(recording):`, `fix(build):`,
`fix(ui):`, `feat(core):`, `refactor(...):`, `docs:`, `chore:`, `test:`.

## Known gaps
Living improvement work lives in **`docs/IMPROVEMENT_PLAN.md`** (phases, acceptance
criteria, open decisions). Navigable ADR index: **`docs/adr/README.md`**. Release
operator sequence: **`docs/RELEASE_CHECKLIST.md`**. Do not invent missing ADRs —
the numbered series has gaps by design for now:

- Present: `docs/adr/0001`, `0002`, `0003`, `0005`, `0006`, `0010`, `0011`, **`0012`**
  (recording engine stays FFmpeg; MF gated; PA16 refuse)
- Missing / not authored yet: **0004**, **0007**, **0008**, **0009** (leave vacant
  until a real decision needs that slot; do not stub speculative ADRs)
- Recording: production remains `RecordingSinkSelector` → `FfmpegPipeSink`; do **not**
  merge `spikes/mf-sinkwriter` into `Services/` until ADR-0012 gates flip. PA16 stays
  refuse-by-name (`OmtAudioFrameGuard`) until a real sender requires decode.

`CODE_REVIEW_REPORT.md` is a **historical** Oct 2026 audit — point new work at the
plan + ADR index, not at extending that report.
