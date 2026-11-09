# OMT Capture Studio — Improvement Plan

**Status:** Living plan — Phases 0–4 complete and merged; CI green on Actions. Follow-up: `TreatWarningsAsErrors` on `chore/treat-warnings-as-errors` (clean Debug+Release baseline). Still optional: Core split, named workspace *types*, MF after ADR-0012 gates.

**Owner:** Gbolahan  
**Grounded against:** repo @ `main` / version `1.0.0.24` (security/broadcast + improvement plan line)  
**Companions:** `AGENTS.md`, `CODING_STANDARDS.md`, `CONTEXT.md`, `DESIGN.md`, `docs/adr/*` (recording evidence in ADR-0012)

---

## 1. Goals & non-goals

### Goals (“improved” means)

1. **Mergeable confidence without heroics** — every PR gets the same gate humans run today (`dotnet build` + headless suite + AOT smoke when relevant).
2. **Faster, safer change** — tests and `MainWindowViewModel` stop being single-file choke points; new work lands in named modules.
3. **Clone-to-green onboarding** — release/build scripts and living domain docs are trackable; payload/ffmpeg gaps are documented, not silent.
4. **Preserved product contracts** — Native AOT, hot-path hygiene, security bootstrap, broadcast resilience, throw-on-fail verification philosophy.
5. **Clear recording strategy** — decide FFmpeg subprocess vs Media Foundation sink with an ADR backed by spike evidence, not vibes.

### Non-goals (do not boil the ocean)

- Full rewrite / framework swap (stay Avalonia 11 / .NET 8 / win-x64 AOT).
- Replacing the custom suite with xUnit *as a prerequisite* for CI (CI first; harness migration is a later open decision).
- Process-isolating Capture vs Broadcast unless product forces it (open decision; default: keep one process).
- Shipping MF from the deleted spike (`spikes/mf-sinkwriter` removed after ADR-0012; evidence lives in that ADR — do not revive as production).
- Expanding codec decode surface for hostile peers without an explicit product call (PA16 stays refuse-by-default unless decided otherwise).
- “Perfect” layered architecture before quality gates exist.

---

## 2. Principles (preserve)

| Constraint | Source of truth | Implication for this plan |
|---|---|---|
| Native AOT — no reflection / dynamic IL in shipping paths | `CODING_STANDARDS.md` §2.1, `OmtCaptureStudio.csproj` `PublishAot` | Analyzers/CI must not introduce AOT-hostile patterns; AOT publish remains a release gate |
| Headless-constructible VMs + static `StudioPalette` | §2.2, `AGENTS.md` | VM splits stay constructible without Avalonia `Application` |
| Dispatcher-only UI mutation; hot-path no alloc churn | §2.3–2.4 | Decomposition must not move frame work onto UI thread or introduce per-frame `new`/LINQ |
| Idempotent disposal / event unhook | §2.5 | New types inherit the audit patterns already verified in `CODE_REVIEW_REPORT.md` |
| Seams over rewrites | `IMediaSource`, `IRecordingSink`, `CompositeRecordingSink`, ADRs 0001/0002/0005/0010/0011 | Prefer new sink/source behind interfaces |
| Throw-on-fail suite; soak when touching record/render | `AGENTS.md`, `OmtCaptureStudio.Tests` | Keep `--step` ids usable; E2E still records past known failure thresholds (e.g. 6s) |
| Security surface | native payload hash, frame guards (`OmtAudioFrameGuard` / `OmtVideoFrameGuard`), update SHA-256, FFmpeg preflight | CI must exercise security table (`SecurityRegressionTable`) |
| Spike discipline | ADR-0012 evidence table (former `spikes/mf-sinkwriter`) | Measure → ADR → implement; never merge spike code sideways; tree deleted |

---

## 3. Phased roadmap

### Phase 0 — Stabilize / DX (unblocks everything else)

**Objective:** A fresh clone + documented prerequisites can build, run the default suite, and find the living docs/scripts that today are gitignored or drifted.

| Workstream | Concrete tasks | Effort |
|---|---|---|
| **0.1 Gitignore / tracking** | Edit `.gitignore`: stop ignoring `CONTEXT.md`, root launch/build bats that operators need (`Launch-OmtCaptureStudio.bat`, `Build-AOT.bat`), and `scripts/**` (esp. `scripts/build-release.ps1`). Keep ignoring `tools/ffmpeg.exe`, `NativePayload/`, `Publish_AOT/`, `Releases/`, `*.log`, `diag*.txt`. Add a tracked `scripts/README.md` stub listing required local binaries. | S (0.5–1 d) |
| **0.2 Onboarding truth** | Update `README.md` Quick Start: explicit “populate `OmtCaptureStudio/NativePayload/win-x64/` + `tools/ffmpeg.exe`” checklist (already partially there — make it a numbered gate). Fix `CONTEXT.md` WPF/NAudio leftovers → Avalonia / Vortice XAudio2. Fix stale comments in `CaptureSession.cs` / `OmtReceiverService.cs` (“WPF WriteableBitmap”). | S (0.5 d) |
| **0.3 Living inventory** | Add short “Known gaps” section to `AGENTS.md` pointing at this plan + ADR number gaps (0004, 0007–0009 missing). Do **not** invent missing ADRs yet. | S (<0.5 d) |

**Acceptance criteria**

- [x] `git check-ignore -v CONTEXT.md scripts/build-release.ps1` no longer says ignored (after intentional re-add).
- [x] README clone path lists native payload + ffmpeg as hard prerequisites before “it will die at startup / release refuse”.
- [x] No WPF/NAudio claims remain in `CONTEXT.md` for current stack.

**Completed:** 2026-11-08 — `.gitignore` no longer blanks `CONTEXT.md` / `*.bat` / `*.ps1`; `scripts/README.md` stub added; README step 0 gate; CONTEXT + CaptureSession/OmtReceiver comments cleared of WPF/NAudio; `AGENTS.md` Known gaps.

**Dependencies / risks:** Re-adding ignored files may surface local-only edits — review before first commit. Do not commit `ffmpeg.exe` or native DLLs.

---

### Phase 1 — Quality gates (highest leverage)

**Objective:** Automated, repeatable gate matching what `AGENTS.md` already documents manually.

| Workstream | Concrete tickets | Effort |
|---|---|---|
| **1.1 CI workflow** | Add `.github/workflows/ci.yml` (or Azure DevOps if preferred — default GitHub Actions): restore → `dotnet build OmtCaptureStudio.sln -c Release -p:Platform=x64` → `dotnet run --project OmtCaptureStudio.Tests -c Release -p:Platform=x64` (default suite, no soak). Fail on non-zero. Cache NuGet. Document that headless DXGI/GDI paths already soft-fail (see prior OMT-11/12 remediations). | M (1–2 d) **Done 2026-11-08** (skeleton: workflow + `scripts/ci-prerequisites.ps1`; suite green on runners still blocked on 1.2 artifacts) |
| **1.2 CI prerequisites** | Decide how runners get native libs + ffmpeg: (a) checked-in CI cache artifact / secret-stored zip, or (b) job step that fails with clear message if missing. Prefer (a) for green main. Suite already calls `FfmpegPreflight.Run` at head of `RunDefaultSuite`. | M (1 d) + infra — **Done 2026-11-09**: provision before build + public portable release zip fallback (`CI_PREREQ_ARCHIVE_URL` optional override) |
| **1.3 Optional AOT job** | Nightly or `workflow_dispatch`: `dotnet publish OmtCaptureStudio/OmtCaptureStudio.csproj -c Release -r win-x64 /p:PublishAot=true`. Not required on every PR initially (slow). | S–M **Done 2026-11-09** (`.github/workflows/aot-nightly.yml` — schedule + `workflow_dispatch`; publish smoke only, no artifact upload) |
| **1.4 Directory.Build.props + editorconfig** | Root `Directory.Build.props`: `Nullable`, `TreatWarningsAsErrors` (start Warning-level treat carefully — may need baseline), `LangVersion`, shared `Platform=x64` notes. `.editorconfig` for C# style aligned with existing code. Optional: `EnableNETAnalyzers` + a small allowlist; **do not** enable rules that fight AOT or CommunityToolkit generators without a dry-run. | M (1–2 d) **Done 2026-11-08** (props + editorconfig); **`TreatWarningsAsErrors` ON 2026-11-09** after 0-warning Debug+Release\|x64 baseline (`CODING_STANDARDS.md` §3.1) |
| **1.5 Test harness split (mechanical)** | Extract `RunDefaultSuite` steps from `OmtCaptureStudio.Tests/Program.cs` into files by domain, keeping the **same** console host + `Expect` + `--step` switch. Keep CLI modes (`--diag-*`, `--real-sender-soak`, `--step`) in thin `Program.cs`. | L — **Done 2026-11-09:** `Program.cs` is router-only (~400 lines: `Main` + `RunDefaultSuite` + `RunSingleStep`). Domains under `Capture/`, `Operator/`, `Diagnostics/`, `Recording/`, `Security/`, `Broadcast/`, `Audio/`, `UiShell/`, `Support/`. |
| **1.6 VM decomposition (continue)** | Carve `ViewModels/MainWindowViewModel.cs` along existing `#region`s and mode shell (ADR-0011): Capture/Broadcast workspace + chrome slices; keep facade bindings. Preserve headless ctor + palette rules. | L (ongoing) — **Third cut 2026-11-09:** `MainWindowViewModel.CaptureWorkspace.cs` + `BroadcastWorkspace.cs` partials (target/audio/ASIO population + host-broadcast arm); prior AppModeShell / BroadcastChrome / RecordingChrome remain. Separate named workspace *types* (composition) still optional Phase 2 polish. |

**Acceptance criteria**

- [x] PR CI red/green without manual local ritual for build + default suite. *(green on Actions 2026-11-09 via public portable release zip fallback)*
- [x] `Program.cs` is a router (<~400 lines target after split); step ids still work (`--step 0.682` etc.). *(~400 lines: Main + RunDefaultSuite + RunSingleStep; domains extracted incl. Capture/Operator/Diagnostics/soak/diag CLI)*
- [x] At least one VM slice extracted with tests green and AOT publish still succeeding. *(AppModeShell + BroadcastChrome + RecordingChrome + CaptureWorkspace + BroadcastWorkspace partials; nightly AOT workflow landed — full AOT publish not re-run locally this pass)*
- [x] `Directory.Build.props` + `.editorconfig` present; build warning policy documented in `CODING_STANDARDS.md` (one paragraph).

**1.1 landed (2026-11-08):** `.github/workflows/ci.yml` on `windows-latest` — NuGet cache via `setup-dotnet`, Release\|x64 restore/build, `scripts/ci-prerequisites.ps1`, stage `tools/ffmpeg.exe` beside the test host, default suite via `dotnet run` (no soak). Does not commit binaries; never depended on the MF spike (now deleted).

**1.5–1.6 complete for Phase 1 structural goals (2026-11-09):** Test domains under `OmtCaptureStudio.Tests/{Capture,Operator,Diagnostics,Recording,Security,Broadcast,Audio,UiShell,Support}`; `Program.cs` router ~400 lines. VM partials: AppModeShell, BroadcastChrome, RecordingChrome, CaptureWorkspace, BroadcastWorkspace (facade bindings unchanged). `.github/workflows/aot-nightly.yml`. Actions suite green. Optional later: true composed Capture/Broadcast workspace *types* (Phase 2.3), not required for 1.6 acceptance.

**Dependencies / risks:** CI without native payload/ffmpeg will false-red. Splitting tests without preserving step order will break muscle memory / any external scripts. TreatWarningsAsErrors can stall — phase in.

---

### Phase 2 — Structure (optional Core; seams first)

**Objective:** Reduce coupling so UI and engine evolve independently — only after gates exist.

| Workstream | Concrete tickets | Effort |
|---|---|---|
| **2.1 Core project decision spike** | Timebox: list types with **zero** Avalonia references (`CaptureSession`, sinks, sources, guards, forensics, settings). Propose `OmtCaptureStudio.Core` classlib (AOT-compatible) referenced by app + tests. **Ship only if** move is mechanical and does not drag UI types. | M (1–2 d spike) → L if execute — **NO-GO 2026-11-09** (see outcome below) |
| **2.2 Recording sink selector** | Document / harden the selection point for sinks (spike notes `RecordingSinkSelector.Initialize` — verify actual type name in tree; today production is FFmpeg pipe via `StreamRecorderService` / `FfmpegPipeSink`). Keep `IRecordingSink` as the only integration surface for MF later. | S — **Done 2026-11-09** |
| **2.3 Continue VM + view alignment** | Mode views already exist (`Views/CaptureModeView`, `BroadcastModeView`, `ModeSelectionView`). Bind extracted VMs; avoid second mega-VM. | M — **Done 2026-11-09** (facade bindings + view comments; named workspace *types* still deferred) |

**Acceptance criteria**

- [x] Written go/no-go for Core split (if go: project in `.sln`, tests reference Core, AOT publish green).
- [x] No circular project refs; CODING_STANDARDS scope list updated.

**2.1 Core go/no-go (2026-11-09) — NO-GO / defer**

Inventory: `OmtCaptureStudio/Services/**` and `Models/**` have **zero** `using Avalonia` references — engines, sinks, sources, guards, forensics, and settings are Avalonia-free on paper. Still **do not ship** `OmtCaptureStudio.Core` this pass:

1. Core csproj/AOT-trim churn still needs an owner appetite check (CI is now green, so that prior gate is cleared).
2. Move is not purely mechanical: app csproj owns Vortice + `libomtnet` refs, embedded `NativePayload`, and AOT publish roots — a split would re-home trim/rd.xml and test project refs without a vertical slice plan.
3. Safer subset landed instead: harden `RecordingSinkSelector` as the sole production `FfmpegPipeSink` construction point behind `IRecordingSink` (Phase 3 MF plugs factories here, not a Core rewrite).

Revisit only when owner confirms appetite for csproj churn (open decision table still defaults to Later).

**2.2 landed:** `RecordingSinkSelector` (+ `CompositeRecordingSinkFactory`) extracted to `Services/Sinks/RecordingSinkSelector.cs`; `ForProduction()` documented as sole shipping `new FfmpegPipeSink()` site; forwards `DetachedDestinationNames` (NET-020 production gap closed); suite step `0.690` asserts selector detach forward. No MF code in Services; no FFmpeg-vs-MF product call.

**2.3 landed:** Mode views continue to bind the `MainWindowViewModel` facade (AppModeShell / workspace / chrome partials). View summaries state "do not invent a second Capture/Broadcast VM." Named composed workspace *types* remain optional polish — not required for Phase 2 acceptance.

**Dependencies / risks:** Premature Core split churns csproj/AOT trim without CI → defer if Phase 1 incomplete. ADR-0010 `tee` work remains **measurement-blocked** — do not couple to Core.

---

### Phase 3 — Product / recording strategy

**Objective:** Resolve the FFmpeg process dependency and hostile-peer product policy with ADRs.

| Workstream | Concrete tickets | Effort |
|---|---|---|
| **3.1 ADR: FFmpeg vs Media Foundation** | New ADR (next free number; note gap series 0004/0007–0009 — either fill intentionally or skip with “reserved/withdrawn” stubs). Consume former `spikes/mf-sinkwriter` evidence: BGRA path OK, fragmented `Fmpeg4` required for crash parity with ADR-0002, audio AAC path still incomplete, HW encode identity unmeasured, installer size win (~102 MB ffmpeg). Decision options: (A) stay FFmpeg, harden; (B) MF primary + FFmpeg fallback; (C) MF-only after audio/HW/crash gates. | M — **Done 2026-11-09** → **(A)** in `docs/adr/0012-recording-engine-strategy.md` |
| **3.2 If MF proceeds** | Implement `MediaFoundationRecordingSink : IRecordingSink` behind flag; port suite steps that assert preflight/R25/R26 only when FFmpeg path selected; do not revive deleted spike into `Services/`. Measure HW MFT + multi-hour soak before flipping default. | L — **Deferred** (ADR-0012 gates unmet: HW identity, multi-hour soak, ProRes/MKV matrix, PCM16 convert cost) |
| **3.3 PA16 / hostile peer policy** | Product decision ticket: keep refuse+name (`OmtAudioFrameGuard`, suite asserts) **or** add PA16 decode. Document operator-visible messaging. No silent reinterpretation (already forbidden by tests). | S — **Done 2026-11-09** → **refuse** (ADR-0012 §3 + CONTEXT / CODING_STANDARDS §4.4) |
| **3.4 ADR-0010 tee follow-through** | Only after measurement unlock in ADR-0010 §5; else leave composite two-child as shipping. | blocked |

**Acceptance criteria**

- [x] Accepted ADR for recording engine strategy linked from `CONTEXT.md` / `AGENTS.md`. *(ADR-0012; indexed in `docs/adr/README.md`)*
- [x] PA16 policy recorded (product + test expectation one-liner in CODING_STANDARDS or CONTEXT). *(refuse-by-name; suite rows remain load-bearing)*
- [x] No production dependency on `spikes/` project. *(tree deleted 2026-11-09; evidence in ADR-0012; never referenced by shipping csproj)*

**Completed:** 2026-11-09 — ADR-0012 **(A) stay FFmpeg** as production default; MF option (B) only after explicit gates; (C) NO-GO. Spike evidence table copied into the ADR; **`spikes/mf-sinkwriter` deleted** (and empty `spikes/` removed) after owner go-ahead. PA16 refuse documented. No MF code in `Services/`.

**Phase 3 follow-ups done:** spike delete + pointer updates; Actions suite green. Optional later: reopen MF (B) only after ADR-0012 gates; Core with owner appetite; composed workspace *types*.

**Dependencies / risks:** MF without fragmented MP4 regresses crash forensics story. Dual-engine increases test matrix — gate with feature flag. Reopen 3.2 only when ADR-0012 go/no-go table is green.

---

### Phase 4 — Polish / docs

**Objective:** Living docs match the shipping app; ADR index is navigable.

| Workstream | Concrete tickets | Effort |
|---|---|---|
| **4.1 ADR index** | `docs/adr/README.md` listing 0001–0011 with status; mark missing numbers reserved/missing. | S — **Done 2026-11-09** |
| **4.2 DESIGN / README drift** | Pass for leftover dual-mode shell language vs ADR-0011 picker; screenshot paths already OK under `docs/images/`. | S — **Done 2026-11-09** (`DESIGN.md` §5.2/§5.5/components; README already picker-first) |
| **4.3 Release checklist** | Short `docs/RELEASE_CHECKLIST.md`: version.txt bump, `scripts/build-release.ps1`, SHA-256, installer (`installer/OmtCaptureStudio.iss`), suite + optional AOT. | S — **Done 2026-11-09** |
| **4.4 Retire stale audit noise** | Keep `CODE_REVIEW_REPORT.md` as historical; point new work at this plan + ADRs. | S — **Done 2026-11-09** |

**Acceptance criteria**

- [x] New contributor can find mode shell, recording arm, and security gates from ADR index alone.
- [x] Release steps are copy-pasteable without tribal knowledge.

**Completed:** 2026-11-09 — `docs/adr/README.md` (topic → ADR map + gap series); `DESIGN.md` aligned to ADR-0011 (no dual-mode Broadcast toggle narrative); `docs/RELEASE_CHECKLIST.md`; `CODE_REVIEW_REPORT.md` historical banner; `AGENTS.md` Known gaps points at ADR index + release checklist.

---

## 4. Priority matrix

| Priority | Item | Phase |
|---|---|---|
| **Must** | CI build + default test suite | 1 |
| **Must** | Fix `.gitignore` overreach (CONTEXT.md, scripts, needed bats) | 0 |
| **Must** | Preserve AOT / security / throw-on-fail philosophy while changing structure | all |
| **Must** | Recording strategy ADR (FFmpeg vs MF) before MF production work | 3 — **met** (ADR-0012) |
| **Should** | Split `OmtCaptureStudio.Tests/Program.cs` by domain | 1 |
| **Should** | Continue `MainWindowViewModel` decomposition along ADR-0011 modes | 1–2 |
| **Should** | `Directory.Build.props` + `.editorconfig` + analyzer dry-run | 1 |
| **Should** | Doc drift cleanup (WPF/NAudio, ADR index) | 0 / 4 |
| **Could** | Extract `OmtCaptureStudio.Core` | 2 |
| **Could** | MF sink implementation / retire ffmpeg.exe from installer | 3 — deferred (ADR-0012 gates) |
| **Could** | PA16 decode support | 3 — refuse until product requires (ADR-0012 §3) |
| **Could** | Capture/Broadcast process isolation | open — not scheduled |
| **Could** | Migrate harness to xUnit | open — not required for CI |

---

## 5. Suggested sequencing

### This week (execute in order)

1. ~~**Phase 0.1–0.2** — gitignore + README/CONTEXT truth~~ **Done 2026-11-08** (0.3 Known gaps also landed).
2. ~~**Phase 1.1 skeleton** — CI YAML that builds; tolerate missing payload with explicit fail message if artifact not ready yet.~~ **Done 2026-11-08**.
3. ~~**Phase 1.5 first cut** — extract `Expect` + UiShell domain~~ **Done 2026-11-08**.
4. ~~**Phase 1.6 first VM slice** — AppModeShell partial~~ **Done 2026-11-08**.
5. ~~**Phase 1.4** — `Directory.Build.props` + `.editorconfig`~~ **Done 2026-11-08**; ~~`TreatWarningsAsErrors`~~ **ON 2026-11-09** (clean baseline).
6. ~~**Phase 1.2 scaffolding** — provision script + secret hooks~~ **Done 2026-11-08**; Actions suite green via public portable release zip fallback (secrets optional).
7. ~~**1.5 domain extracts** (Recording/Security/Broadcast/Audio)~~ **Done 2026-11-09**.
8. ~~**1.3 nightly AOT**~~ **Done 2026-11-09**.
9. ~~**1.6 chrome slices** (Broadcast/Recording)~~ **Done 2026-11-09**.
10. ~~**1.5 router finish** + ~~**1.6 Capture/Broadcast workspace partials**~~ **Done 2026-11-09** (`Program.cs` ~400).
11. ~~**Phase 2** (Core NO-GO + sink selector harden + mode-view facade notes)~~ **Done 2026-11-09**.
12. ~~**Phase 4** (ADR index, DESIGN drift, release checklist, historical audit pointer)~~ **Done 2026-11-09**.
13. ~~**Phase 3** (ADR-0012 stay-FFmpeg + PA16 refuse)~~ **Done 2026-11-09**.
14. ~~Spike delete after ADR-0012~~ **Done 2026-11-09** (`spikes/mf-sinkwriter` + empty `spikes/` removed).
15. ~~**CI suite green + merge to main**~~ **Done 2026-11-09**.
16. ~~**`TreatWarningsAsErrors`**~~ **Done 2026-11-09** (`chore/treat-warnings-as-errors`).
17. **Optional next:** named Capture/Broadcast workspace *types*; Core split (owner appetite); MF (B) only after ADR-0012 gates; ADR-0010 tee when unlocked.

### Next month

1. ~~CI green with native+ffmpeg artifacts~~ **Done** (public portable release zip fallback).
2. ~~ADR for recording engine + PA16 policy written and accepted (Phase 3).~~ **Done** (ADR-0012).
3. ~~`TreatWarningsAsErrors`~~ **Done** (clean Debug+Release baseline).
4. If owner reopens MF after gates: `MediaFoundationRecordingSink : IRecordingSink` behind flag via `RecordingSinkSelector` factories — build from ADR-0012 evidence; do not revive the deleted spike tree.
5. Revisit Core split only with owner appetite; if go, one vertical move (sinks+sources) not a big-bang.
6. Optional: named Capture/Broadcast workspace VM *types* behind facade bindings.

---

## 6. Open decisions

| Decision | Options | Recommendation (default) | Needed from owner |
|---|---|---|---|
| **FFmpeg vs MF** | Stay / MF+fallback / MF-only | **Stay FFmpeg** (ADR-0012 accepted 2026-11-09); MF (B) only after gates; (C) NO-GO; spike deleted | Reopen (B) only if gates green |
| **Test harness vs xUnit** | Keep console throw-on-fail / migrate | **Keep** harness; CI invokes `dotnet run`; migrate only if IDE/explorer pain dominates | Only if CI friction |
| **Core project split** | Now / later / never | **Later / NO-GO this pass** (Phase 2.1 written 2026-11-09) — revisit after CI green | Confirm appetite for csproj churn |
| **CONTEXT.md tracking** | Track in git / generate / personal-only | **Track** (remove from `.gitignore`) — AGENTS already requires it | Confirm no secrets in file |
| **Capture vs Broadcast process isolation** | One process (today) / two processes | **Stay one process** unless stability data demands isolation | Only if multi-hour broadcast faults require it |
| **PA16** | Refuse (current) / decode | **Refuse** (ADR-0012 §3) until a real sender requirement appears | Only if a peer requires decode |

---

## 7. Success metrics

| Metric | Baseline (verified) | Target |
|---|---|---|
| CI on default branch / PRs | **Green on Actions** (build + default suite; portable zip fallback) | Keep required check green; secrets remain optional overrides |
| Test entrypoint size | `Program.cs` ~400 router (was ~9.0k → ~3.5k → ~400) | Router ≤ ~400 lines; domains in separate files — **met** |
| Shell VM size | `MainWindowViewModel.cs` ~2.2k + AppModeShell/BroadcastChrome/RecordingChrome/CaptureWorkspace/BroadcastWorkspace partials | No single VM file > ~1k without active split plan; mode concerns separated — further split optional |
| Onboarding | Tracked scripts + CONTEXT | Tracked scripts + CONTEXT; README prerequisites unambiguous |
| Repo standards files | **`Directory.Build.props` + `.editorconfig` present** | Present; documented in CODING_STANDARDS |
| Recording strategy | **ADR-0012 accepted** — stay FFmpeg; spike deleted; evidence in ADR | Production still `RecordingSinkSelector` → `FfmpegPipeSink`; MF only after gates |
| Regressions | Manual `dotnet run` suite | Same suite in CI; security table still runs (`SecurityRegressionTable.RunAll`) |
| Doc drift | WPF/NAudio strings in CONTEXT / comments | Zero false stack claims |

**Plan worked when:** a new clone + documented binaries + CI badge is enough for Gbolahan (or an agent following `AGENTS.md`) to change a sink or VM with confidence, without opening a 9k-line test file to find the right assertion.

---

## Ticket cheat-sheet (copy into issues)

- `dx(gitignore):` track CONTEXT.md + scripts; stop ignoring operator bats  
- `docs(readme):` native payload + ffmpeg clone checklist; kill WPF/NAudio drift  
- `ci:` Release build + default `OmtCaptureStudio.Tests` gate  
- `ci:` native payload + ffmpeg runner provisioning  
- `build:` Directory.Build.props + editorconfig (+ analyzer dry-run)  
- `test:` extract Expect + Recording/* from Program.cs (preserve `--step` ids)  
- `test:` extract Broadcast/* + Audio/* + UiShell/*  
- `refactor(ui):` extract BroadcastWorkspaceViewModel from MainWindowViewModel  
- `refactor(ui):` extract Capture/Recording chrome VM  
- `docs(adr):` FFmpeg vs Media Foundation recording engine  
- `docs(adr):` PA16 / hostile peer decode policy  
- `spike(arch):` Core project go/no-go  
- `docs(adr):` index + reserved gap numbers  

---

*End of plan. Update this file when a phase completes or an open decision closes.*
