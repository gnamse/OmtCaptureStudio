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
