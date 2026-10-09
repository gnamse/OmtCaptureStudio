# OMT Capture Studio — Coding Standards

This document is the **enforceable contract** for all C# code in this repository.
A code reviewer, CI gate, or future agent must be able to accept or reject a
change against these rules. When this file conflicts with a generic guideline
you've seen elsewhere, **this file wins** — it encodes constraints proven
necessary by bugs, deadlocks, and audit findings in this specific codebase.

Scope applies to the two projects in the solution:

- `OmtCaptureStudio/` — the Avalonia 11 Native AOT Windows desktop app.
- `OmtCaptureStudio.Tests/` — the headless end-to-end test executable
  (no unit-test framework; it is a console app that throws on failure).

Companion context: `CONTEXT.md` (domain vocabulary/seams), `README.md`
(build/deploy). Read both before making architectural choices.

---

## 1. Purpose

OMT Capture Studio is a **broadcast-grade recorder**: it must run 24/7 on
ob truck laptops, never freeze on a multi-hour capture, and ship as a single
self-contained native binary with **no .NET runtime on the target machine**.
That last requirement — Native AOT — is the single most consequential
constraint in this codebase. It silently breaks features that compile and run
fine under the host JIT. Every rule below either exists because E2E/soak tests
failed in the past, or because a Native AOT binary would fault at startup.

These standards exist to be **machine-checkable by a reviewer**, not aspirational.

---

## 2. Non-Negotiable Constraints

These are hard rules. A change that violates one does not get merged; it gets
reworked. Do not "soft-edit" the language here to fit a change you want.

### 2.1 Native AOT — no reflection, no MSIL metadata, no dynamic generation

Source of truth: `OmtCaptureStudio/OmtCaptureStudio.csproj`
(`<PublishAot>true</PublishAot>`, net8.0-windows, x64 only).

Native AOT compiles to native code. **Reflection over type metadata, IL
emission (`Expression` trees used as dynamic delegates), and runtime
`Assembly.Load` do not work the way they do under the JIT** — they either
return wrong results or fault. This is a release-blocking failure mode.

- **Forbidden:**
  - Calling into shipped third-party assemblies via reflection that AOT
    shared-generics/annotation can't include (no `DynamicMethod`, no
    runtime-generated delegates).
  - `Expression` compilation in a per-frame or per-message path.
  - Treating an AOT build as "just like JIT with a slower start".
- **Required:**
  - Prefer `[ObservableProperty]` / `[RelayCommand]` from
    `CommunityToolkit.Mvvm` (source generator, not magic).
  - Keep the MVVM layers free of reflection; use compiled bindings (see 3.1).
  - When you touch a generic over a value type in a hot loop, build and run
    the **actual AOT publish** before shipping (JIT-only testing misses it).

### 2.2 Static brushes for headless construction — single palette source

Source of truth: `OmtCaptureStudio/StudioPalette.cs` and commit
`07b9bf1` "fix(ui): harden accessibility and consolidate runtime palette".

The automated E2E suite (`OmtCaptureStudio.Tests/Program.cs`) constructs
`MainWindowViewModel`, `CaptureSession`, `AudioEngine`, etc. **entirely
headless — no Avalonia `Application` is ever created.** Instantiating a
`SolidColorBrush` from `Color.FromRgb` in a ViewModel or control needs a live
`Application` for resource/Dispatcher context, which headless tests don't have.
The project's fix: **all runtime brushes live as `static` members of
`StudioPalette`, so ViewModels and controls construct them without touching a
live application.**

- **Rule:** Any `IBrush`/`SolidColorBrush` needed by a ViewModel or a
  control's imperative code **must** be sourced from `StudioPalette`. Do
  **not** re-declare `Color.FromRgb` / `new SolidColorBrush(...)` in a
  `MainWindowViewModel`, `VideoViewportControl`, `VuMeterControl`, or any
  other code-behind/model file.
- Adding a brush: add it to `StudioPalette` (comment the matching `Studio*`
  token in `App.axaml` where one exists), then reference it. Declarative XAML
  uses `App.axaml` `Studio*` resources; imperative code uses `StudioPalette`.
- A background brush for a hot render (e.g. viewport letterbox on
  `StudioPalette.Backdrop`) must be a **`private static` field** captured once,
  not allocated in `Render()`.

### 2.3 Thread affinity — UI mutations only via the Dispatcher

Source of truth: `OmtCaptureStudio/ViewModels/MainWindowViewModel.cs`
(`PostToUIThread`, `Dispatcher.UIThread.CheckAccess()/Post`) and
`OmtCaptureStudio/Controls/VideoViewportControl.cs`.

The audio, network, and recording threads are **not** the UI thread. Any
mutation of an `[ObservableProperty]`, a control's visual state, or an
Automation/accessibility live region must be marshaled to the UI thread.

- Use the established `PostToUIThread(Action, DispatcherPriority?)` pattern.
- **Never** block the UI thread waiting on media work, and **never** touch
  view-model observable state directly from a `VideoFrameAvailable` /
  `AudioLevelsUpdated` / telemetry callback. Throttle HUD updates (see 2.4).
- `volatile bool` and `Interlocked` are acceptable for cross-thread flags
  (`_isLive`, `_isSafeExitConfirmed`, `_renderPending`), not for composite
  state that needs the UI thread.

### 2.4 No allocation churn in the per-frame hot path

Source of truth: `OmtCaptureStudio/Controls/VideoViewportControl.cs`
(`UpdateFrame`/`ProcessPendingFrame` — ping-pong byte buffers, frame-drop
guard `_renderPending`, stride-aware `Buffer.MemoryCopy`) and
`OmtCaptureStudio/Services/Sinks/FfmpegPipeSink.cs`
(`ArrayPool<byte>.Shared.Rent/Return`, bounded queues) with
`BoundedMediaQueue.cs`.

The video path runs at line rate (1920×1080 @ 60 fps = 60 frames/sec). **Short-lived
allocations here are a frame-drop / OOM / freeze hazard** that has already
burned this project.

- **Forbidden in the per-frame path** (`UpdateFrame`, `WriteVideo`,
  `WriteAudio`, `IngestAudioFrame`, `OnSourceVideoFrameReceived`):
  - `new byte[...]` for payloads — rent from `ArrayPool<byte>.Shared` and
    return via the queue's drop/finalize callback. **Return every rented
    buffer exactly once** (drop callback, `Dispose` drain, and the worker
    loop). A double `Return` to `ArrayPool` is memory corruption — see the
    fix history; this was a P0 finding.
  - LINQ, `string` building, reflection, or logging **per frame**.
  - Unbounded queues. Bound producer queues (`MaxQueuedVideoFrames=128`,
    `MaxQueuedAudioChunks=512`) and drop-oldest on overflow.
- Keep the double-buffer pattern: staging under `_stagingLock`, swap and render
  under `_bitmapLock`, frame-drop guard via `Interlocked` (at most one pending
  UI render dispatch). Only reallocate the `WriteableBitmap` on dimension change.
- **Do not fix buffer churn without keeping the drop guard.** Removing
  `_renderPending` reintroduces dispatcher queue backlog → UI freeze.

### 2.5 Disposal / event-hook hygiene

Every `IDisposable` engine and view model must be acyclic, idempotent, and not
leak subscriptions. The E2E test constructs and disposes these headlessly, and
disposes them **twice** (`Program.cs` calls `vm.Dispose(); vm.Dispose();`).

- Unhook every event in a mirror method (`UnhookEvents()`), clear delegate
  fields to `null`, stop timers, and dispose owned dependencies only under an
  `_ownsDependencies` flag.
- Guard with `_disposed`; make `Dispose()` safe to call twice.
- Constructor-inject dependencies with clear `ownsDependencies` semantics so
  tests can hand in stubs (`NullRecordingSink`, synthetic sources).

---

## 3. Style & Structure

### 3.1 Project settings — keep them

Preserve; do not weaken:

- `net8.0-windows`, `Nullable=enable`, `ImplicitUsings=enable`.
- `AllowUnsafeBlocks=true` and x64-only. Do not add x86/AnyCPU to the solution
  config matrix.
- `AvaloniaUseCompiledBindingsByDefault=true` — keep compiled bindings on.
- Naming: file-scoped namespaces, `_camelCase` private fields, `PascalCase`
  public members, `I*` interfaces (`IMediaSource`, `IRecordingSink`,
  `IAudioMonitor`), `*Service` for orchestrators.
- Folder layering matches namespace: `Services/`, `Services/Sources/`,
  `Services/Sinks/`, `Models/`, `ViewModels/`, `Controls/`. A new media source
  goes in `Services/Sources/`, a new output transport in `Services/Sinks/`.

### 3.2 MVVM with CommunityToolkit.Mvvm

- ViewModels derive from `ObservableObject`; use `[ObservableProperty]` on
  `_camelCase` fields and `[RelayCommand]` on private methods.
- Use `[RelayCommand(AllowConcurrentExecutions = false)]` for toggle/task
  commands that must not run re-entrantly (`ToggleConnectAsync`,
  `ToggleRecordAsync`, `StopAndExitAsync`, `SetTestSignalActiveAsync`).
- The **ViewModel must be constructible headlessly** (§2.2 / §2.3). The
  `DispatcherTimer` for UI ticks is created inside a `try/catch` so headless
  construction without an Avalonia dispatcher still succeeds — keep that.
- Expose seams for headless testing: `PickFolderAsync` is a settable
  `Func<string?, Task<string?>>?` the E2E substitutes to simulate a folder
  picker without UI.

### 3.3 Hot-path style

- Pin buffers with `fixed`, copy with `Buffer.MemoryCopy`/`Buffer.BlockCopy`,
  use `unsafe` where interop requires it. Keep pointer code strictly inside the
  hot-path files.
- Prefer `ArrayPool<byte>.Shared` for payload buffers; **return exactly once**.

### 3.4 Logging

Use `AppLogger` (`LogInfo`/`LogError`) — async, rolling, daily file logger —
for operational messages. Never call it **per frame**. Use `Debug.WriteLine`
(not `Console`) for developer-only diagnostics in controls.

### 3.5 Git / commits

The repo follows Conventional Commits: `fix(build):`, `fix(recording):`,
`fix(ui):`, `feat(core):`, `refactor(...):`, `docs:`, `chore:`, `test:`.
Lead with a lowercase scope matching the subsystem.

---

## 4. Testing Expectations

The suite is a **single headless console executable**
(`OmtCaptureStudio.Tests/Program.cs`) that constructs real engines and
ViewModels **with no Avalonia UI**, and throws an `Exception` on failure. No
xUnit/NUnit framework; keep it that way unless added deliberately.

### 4.1 Must stay runnable, must stay headless

- The E2E must construct `MainWindowViewModel`, `CaptureSession`,
  `AudioEngine`, `StreamRecorderService` with **no live Avalonia Application**.
  Any change requiring a running app is a hard regression.
- Keep `SetDllDirectory(AppDomain...BaseDirectory)` before `CaptureSession`
  so native `libomtnet.dll`/`libomt.dll` resolve.
- Seam preference order (mirroring `Program.cs`):
  1. In-memory seams (`SyntheticPatternSource` + `NullRecordingSink`).
  2. ViewModel state/command verification over injected session + discovery.
  3. Real local `OmtTestPatternGenerator` → `CaptureSession` → real
     `FfmpegPipeSink` writing MP4, ffprobe-verified on disk.
- No test requiring a real GPU, real OMT camera, or CI-box hardware encoder,
  unless conditionally skipped with a message.

### 4.2 Soak / long-duration seams

The recording path has burned the project with multi-minute stoppage and
live-preview freeze bugs. Keep a soak-style verification: `FinalizeSink` must
finalize a container cleanly so captures past the ~5s threshold are valid. Any
change to `FfmpegPipeSink`, `FfmpegProcessHost`, or `VideoViewportControl`
must be validated with a run **exceeding the previous failure threshold**, not
a 1-second smoke. The test records 6 seconds specifically to verify past the
5-second threshold — keep such explicit durations.

### 4.3 What a failure must look like

Fail hard with `throw new Exception("<Step> failed: <what and got>");` and a
console banner. Do not swallow and continue when a hard invariant is unmet.

---

## 5. Review Checklist

A reviewer must confirm **all** of the following before merge. Any "No" blocks.

**Native AOT / runtime**
- [ ] No reflection, `Expression`-tree-as-delegate, `DynamicMethod`, or
      `Assembly.Load` added in a layer that ships.
- [ ] Generic/reflection-sensitive changes validated with an actual
      `dotnet publish ... /p:PublishAot=true` build.
- [ ] `Nullable`, `ImplicitUsings`, `AllowUnsafeBlocks`, x64-only, and
      `AvaloniaUseCompiledBindingsByDefault=true` left intact.

**Palette / brushes**
- [ ] No `Color.FromRgb` / `new SolidColorBrush` outside `StudioPalette`.
- [ ] New brushes are static members of `StudioPalette`, commented with their
      matching `Studio*` token when one exists.
- [ ] Hot-render brushes cached in a `private static` field.

**Threading**
- [ ] All `[ObservableProperty]`/visual/accessibility mutations go through the
      Dispatcher (`PostToUIThread`), not directly from media callbacks.
- [ ] No device/UI blocking a media thread; no `Task.Run` running UI-affinity
      work off-thread.
- [ ] Cross-thread flags use `volatile`/`Interlocked`.

**Hot path / allocation**
- [ ] Per-frame path uses `ArrayPool` + bounded queues; no `new byte[...]`,
      LINQ, string-building, or logging per frame.
- [ ] **Every rented buffer returned exactly once** (no double `Return`).
- [ ] Frame-drop guard (`_renderPending`) and ping-pong double buffer retained.

**Lifecycle / disposal**
- [ ] New `IDisposable` types dispose idempotently, unhook events, and honor
      `_ownsDependencies`; no leaked subscriptions.
- [ ] ViewModel remains constructible headlessly.

**Testing**
- [ ] E2E stays headless and passes; new state/commands covered via seam-
      injected dependencies or the local pattern-generator path.
- [ ] Recording/render-path changes verified beyond the known failure
      threshold (soak), not a 1s smoke.
- [ ] Tests fail hard with descriptive exceptions.

**Style / structure**
- [ ] File-scoped namespaces; correct folder/namespace; MVVM conventions §3;
      Conventional-Commit message matches scope.

---

## Appendix — key files

| Concern | File |
| --- | --- |
| Single palette source | `OmtCaptureStudio/StudioPalette.cs` |
| Headless MVVM VM | `OmtCaptureStudio/ViewModels/MainWindowViewModel.cs` |
| Hot rendering path | `OmtCaptureStudio/Controls/VideoViewportControl.cs` |
| VU meter control | `OmtCaptureStudio/Controls/VuMeterControl.axaml.cs` |
| Recording transport (pipes, pool, bounded queue) | `OmtCaptureStudio/Services/Sinks/FfmpegPipeSink.cs`, `BoundedMediaQueue.cs`, `FfmpegProcessHost.cs` |
| Session engine (headless, polymorphic source) | `OmtCaptureStudio/Services/CaptureSession.cs` |
| Recording orchestration | `OmtCaptureStudio/Services/StreamRecorderService.cs` |
| Async logger | `OmtCaptureStudio/AppLogger.cs` |
| Headless E2E suite | `OmtCaptureStudio.Tests/Program.cs` |
| Build / AOT publish / release | `scripts/build-release.ps1`, `installer/OmtCaptureStudio.iss` |
| Project settings | `OmtCaptureStudio/OmtCaptureStudio.csproj`, `OmtCaptureStudio.Tests/*.csproj` |
