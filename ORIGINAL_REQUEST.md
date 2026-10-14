# Original User Request

## 2026-10-13T19:33:12Z

# Teamwork Project Prompt

> Requested team: Small, focused team

This is a single self-contained fix; keep it small and focused. Conduct a comprehensive audit of all work completed so far in OMT Capture Studio (`c:\AI\OMT`), covering the MVVM UI refactor, media capture/audio pipeline, FFmpeg recording sink, logging, and Native AOT build pipeline, and remediate any defects, coding standard violations, or test coverage gaps found.

Working directory: c:\AI\OMT\
Integrity mode: demo

## Verification Resources
- [CODING_STANDARDS.md](file:///c:/AI/OMT/CODING_STANDARDS.md) — enforceable C# and Native AOT architectural contract (static `StudioPalette`, headless-constructible ViewModels, `Dispatcher.UIThread`-only UI mutations, zero-allocation hot paths with exact-once `ArrayPool` buffer returns, idempotent disposal).
- [CONTEXT.md](file:///c:/AI/OMT/CONTEXT.md) — domain vocabulary and subsystem seams.
- [OmtCaptureStudio.Tests/Program.cs](file:///c:/AI/OMT/OmtCaptureStudio.Tests/Program.cs) — headless console test suite.

## Requirements

### R1. Full-Codebase Architecture & Standards Audit
Review all subsystems changed so far—including `MainWindowViewModel`, `MainWindow.axaml(.cs)`, `CaptureSession`, `AudioEngine` (`XAudio2Monitor`, `AudioInterleaver`, `AudioLevelCalculator`), `FfmpegPipeSink` (`BoundedMediaQueue`, `FfmpegProcessHost`), `AppLogger`, and release scripts—against `CODING_STANDARDS.md` and `CONTEXT.md`. Produce a structured audit report at `c:\AI\OMT\CODE_REVIEW_REPORT.md` documenting findings by severity, architectural conformance, thread/memory safety analysis, and verification evidence.

### R2. Defect Remediation & Hardening
Fix any bugs, race conditions, resource/buffer leaks, AOT/trimming hazards, or `CODING_STANDARDS.md` violations uncovered during the audit. Ensure all ViewModels and services remain strictly headless-constructible and idempotent on disposal.

### R3. Test Coverage Expansion
Add regression and edge-case tests in `OmtCaptureStudio.Tests` covering any newly identified failure modes or previously untested seams uncovered during the review.

## Acceptance Criteria

### Audit Report Deliverable
- [ ] `c:\AI\OMT\CODE_REVIEW_REPORT.md` exists and contains a complete subsystem-by-subsystem assessment against `CODING_STANDARDS.md`, a table of all findings with severity and resolution status, and before/after verification results.

### Standards & Code Quality
- [ ] Zero reflection-based bindings or dynamic IL generation exist in `OmtCaptureStudio`; all XAML bindings use compiled bindings (`x:DataType` / `CompiledBinding`).
- [ ] Every `ArrayPool<byte>.Shared.Rent` call across the capture, audio, and recording pipelines has a guaranteed, single-execution `Return` path under both normal and exception/shutdown flows.

### Build, Native AOT, and Test Gates
- [ ] `dotnet build OmtCaptureStudio.sln -c Release` completes with 0 warnings and 0 errors.
- [ ] `dotnet run --project OmtCaptureStudio.Tests -c Release` completes with a 100% pass rate (including all new regression tests).
- [ ] `dotnet publish OmtCaptureStudio\OmtCaptureStudio.csproj -c Release -r win-x64 /p:PublishAot=true` succeeds cleanly.
