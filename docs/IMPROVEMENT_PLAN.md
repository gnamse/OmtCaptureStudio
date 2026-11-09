# OMT Capture Studio — Improvement Plan

**Status:** Living plan — Phase 0 complete; Phase 1.1–1.4 done; 1.5 domain extracts + 1.6 chrome slices + 1.3 nightly AOT landed (2026-11-09); suite green on Actions still needs owner artifact secrets  

**Owner:** Gbolahan  
**Grounded against:** repo @ `feature/media-foundation-sink` / version `1.0.0.24` (main carries security/broadcast line)  
**Companions:** `AGENTS.md`, `CODING_STANDARDS.md`, `CONTEXT.md`, `DESIGN.md`, `docs/adr/*`, `spikes/mf-sinkwriter/README.md`

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
- Shipping the MF spike as-is (`spikes/mf-sinkwriter` remains throwaway until an ADR greenlights a sink).
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
| Spike discipline | `spikes/mf-sinkwriter/README.md` | Measure → ADR → implement; never merge spike code sideways |

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
| **1.2 CI prerequisites** | Decide how runners get native libs + ffmpeg: (a) checked-in CI cache artifact / secret-stored zip, or (b) job step that fails with clear message if missing. Prefer (a) for green main. Suite already calls `FfmpegPreflight.Run` at head of `RunDefaultSuite`. | M (1 d) + infra — **Scaffolding done 2026-11-08** (`ci-provision-artifacts.ps1` + workflow secret hooks); **owner must set `CI_PREREQ_ARCHIVE_URL` (or split URLs) for green suite on Actions** |
| **1.3 Optional AOT job** | Nightly or `workflow_dispatch`: `dotnet publish OmtCaptureStudio/OmtCaptureStudio.csproj -c Release -r win-x64 /p:PublishAot=true`. Not required on every PR initially (slow). | S–M **Done 2026-11-09** (`.github/workflows/aot-nightly.yml` — schedule + `workflow_dispatch`; publish smoke only, no artifact upload) |
| **1.4 Directory.Build.props + editorconfig** | Root `Directory.Build.props`: `Nullable`, `TreatWarningsAsErrors` (start Warning-level treat carefully — may need baseline), `LangVersion`, shared `Platform=x64` notes. `.editorconfig` for C# style aligned with existing code. Optional: `EnableNETAnalyzers` + a small allowlist; **do not** enable rules that fight AOT or CommunityToolkit generators without a dry-run. | M (1–2 d) **Done 2026-11-08** (props + editorconfig landed; `TreatWarningsAsErrors` deferred — policy paragraph in `CODING_STANDARDS.md` §3.1) |
| **1.5 Test harness split (mechanical)** | Extract `RunDefaultSuite` steps from `OmtCaptureStudio.Tests/Program.cs` (~9.0k lines today) into files by domain, keeping the **same** console host + `Expect` + `--step` switch (`RunSingleStep` cases `0`…`0.69x`). Suggested first cuts mirroring suite order + existing extract `SecurityRegressionTable.cs`: `Tests/Recording/*.cs`, `Tests/Security/*.cs`, `Tests/Broadcast/*.cs`, `Tests/Audio/*.cs`, `Tests/UiShell/*.cs`, `Tests/Support/Expect.cs`. Keep CLI modes (`--diag-*`, `--real-sender-soak`, `--step`) in thin `Program.cs`. | L (3–5 d), incremental PRs — **Major cut 2026-11-09:** Recording / Security / Broadcast / Audio partials + `Support/FindRepoSource` + `Support/ScriptedRecordingSink`; `Program.cs` ~3.5k (router + remaining Ui/Capture/diag/soak still inline) |
| **1.6 VM decomposition (continue)** | Carve `ViewModels/MainWindowViewModel.cs` (~3.0k lines) along existing `#region`s and mode shell (ADR-0011): e.g. `CaptureWorkspaceViewModel`, `BroadcastWorkspaceViewModel`, `RecordingChromeViewModel`, keep facade or thin shell VM for bindings in `MainWindow.axaml` / mode views. Preserve headless ctor + palette rules. Drive with existing `TestMainWindowViewModel` + `TestAppModeShell`. | L (ongoing; first slice 2–3 d) — **Second cut 2026-11-09:** `MainWindowViewModel.BroadcastChrome.cs` + `RecordingChrome.cs` partials (row projection / state projection / advanced disclosure); named workspace VMs still pending |

**Acceptance criteria**

- [ ] PR CI red/green without manual local ritual for build + default suite. *(1.1 workflow + 1.2 provision hooks exist; clean runners stay prereq-red until owner secrets populate artifacts.)*
- [ ] `Program.cs` is a router (<~400 lines target after split); step ids still work (`--step 0.682` etc.). *(partial: Recording/Security/Broadcast/Audio/UiShell extracted; Program ~3.5k — Capture/desktop, TakeSupervisor, OperatorSettings, Diagnostics, soak/diag CLI remain)*
- [x] At least one VM slice extracted with tests green and AOT publish still succeeding. *(AppModeShell + BroadcastChrome + RecordingChrome partials; nightly AOT workflow landed — full AOT publish not re-run locally this pass)*
- [x] `Directory.Build.props` + `.editorconfig` present; build warning policy documented in `CODING_STANDARDS.md` (one paragraph).

**1.1 landed (2026-11-08):** `.github/workflows/ci.yml` on `windows-latest` — NuGet cache via `setup-dotnet`, Release\|x64 restore/build, `scripts/ci-prerequisites.ps1`, stage `tools/ffmpeg.exe` beside the test host, default suite via `dotnet run` (no soak). Does not commit binaries; does not touch `spikes/mf-sinkwriter`.

**1.2–1.6 + 1.3 progress (2026-11-09):** Domain test partials under `Tests/{Recording,Security,Broadcast,Audio,UiShell,Support}`; VM `BroadcastChrome` / `RecordingChrome` partials; `.github/workflows/aot-nightly.yml`. **Owner still owes** `CI_PREREQ_ARCHIVE_URL` (or split URLs) for Actions suite green. Remaining 1.5: Capture/desktop + operator/settings/diagnostics/soak into domains until `Program.cs` is router-only. Remaining 1.6: named Capture/Broadcast workspace VMs (bindings stay on facade).

**Dependencies / risks:** CI without native payload/ffmpeg will false-red. Splitting tests without preserving step order will break muscle memory / any external scripts. TreatWarningsAsErrors can stall — phase in.

---

### Phase 2 — Structure (optional Core; seams first)

**Objective:** Reduce coupling so UI and engine evolve independently — only after gates exist.

| Workstream | Concrete tickets | Effort |
|---|---|---|
| **2.1 Core project decision spike** | Timebox: list types with **zero** Avalonia references (`CaptureSession`, sinks, sources, guards, forensics, settings). Propose `OmtCaptureStudio.Core` classlib (AOT-compatible) referenced by app + tests. **Ship only if** move is mechanical and does not drag UI types. | M (1–2 d spike) → L if execute |
| **2.2 Recording sink selector** | Document / harden the selection point for sinks (spike notes `RecordingSinkSelector.Initialize` — verify actual type name in tree; today production is FFmpeg pipe via `StreamRecorderService` / `FfmpegPipeSink`). Keep `IRecordingSink` as the only integration surface for MF later. | S |
| **2.3 Continue VM + view alignment** | Mode views already exist (`Views/CaptureModeView`, `BroadcastModeView`, `ModeSelectionView`). Bind extracted VMs; avoid second mega-VM. | M |

**Acceptance criteria**

- [ ] Written go/no-go for Core split (if go: project in `.sln`, tests reference Core, AOT publish green).
- [ ] No circular project refs; CODING_STANDARDS scope list updated.

**Dependencies / risks:** Premature Core split churns csproj/AOT trim without CI → defer if Phase 1 incomplete. ADR-0010 `tee` work remains **measurement-blocked** — do not couple to Core.

---

### Phase 3 — Product / recording strategy

**Objective:** Resolve the FFmpeg process dependency and hostile-peer product policy with ADRs.

| Workstream | Concrete tickets | Effort |
|---|---|---|
| **3.1 ADR: FFmpeg vs Media Foundation** | New ADR (next free number; note gap series 0004/0007–0009 — either fill intentionally or skip with “reserved/withdrawn” stubs). Consume `spikes/mf-sinkwriter/README.md`: BGRA path OK, fragmented `Fmpeg4` required for crash parity with ADR-0002, audio AAC path still incomplete, HW encode identity unmeasured, installer size win (~102 MB ffmpeg). Decision options: (A) stay FFmpeg, harden; (B) MF primary + FFmpeg fallback; (C) MF-only after audio/HW/crash gates. | M (1–2 d writing + owner call) |
| **3.2 If MF proceeds** | Implement `MediaFoundationRecordingSink : IRecordingSink` behind flag; port suite steps that assert preflight/R25/R26 only when FFmpeg path selected; keep spike throwaway. Measure HW MFT + multi-hour soak before flipping default. | L (1–3 w) |
| **3.3 PA16 / hostile peer policy** | Product decision ticket: keep refuse+name (`OmtAudioFrameGuard`, suite asserts) **or** add PA16 decode. Document operator-visible messaging. No silent reinterpretation (already forbidden by tests). | S decision; M if decode |
| **3.4 ADR-0010 tee follow-through** | Only after measurement unlock in ADR-0010 §5; else leave composite two-child as shipping. | blocked |

**Acceptance criteria**

- [ ] Accepted ADR for recording engine strategy linked from `CONTEXT.md` / `AGENTS.md`.
- [ ] PA16 policy recorded (product + test expectation one-liner in CODING_STANDARDS or CONTEXT).
- [ ] No production dependency on `spikes/` project.

**Dependencies / risks:** MF without fragmented MP4 regresses crash forensics story. Dual-engine increases test matrix — gate with feature flag.

---

### Phase 4 — Polish / docs

**Objective:** Living docs match the shipping app; ADR index is navigable.

| Workstream | Concrete tickets | Effort |
|---|---|---|
| **4.1 ADR index** | `docs/adr/README.md` listing 0001–0011 with status; mark missing numbers reserved/missing. | S |
| **4.2 DESIGN / README drift** | Pass for leftover dual-mode shell language vs ADR-0011 picker; screenshot paths already OK under `docs/images/`. | S |
| **4.3 Release checklist** | Short `docs/RELEASE_CHECKLIST.md`: version.txt bump, `scripts/build-release.ps1`, SHA-256, installer (`installer/OmtCaptureStudio.iss`), suite + optional AOT. | S |
| **4.4 Retire stale audit noise** | Keep `CODE_REVIEW_REPORT.md` as historical; point new work at this plan + ADRs. | S |

**Acceptance criteria**

- [ ] New contributor can find mode shell, recording arm, and security gates from ADR index alone.
- [ ] Release steps are copy-pasteable without tribal knowledge.

---

## 4. Priority matrix

| Priority | Item | Phase |
|---|---|---|
| **Must** | CI build + default test suite | 1 |
| **Must** | Fix `.gitignore` overreach (CONTEXT.md, scripts, needed bats) | 0 |
| **Must** | Preserve AOT / security / throw-on-fail philosophy while changing structure | all |
| **Must** | Recording strategy ADR (FFmpeg vs MF) before MF production work | 3 |
| **Should** | Split `OmtCaptureStudio.Tests/Program.cs` by domain | 1 |
| **Should** | Continue `MainWindowViewModel` decomposition along ADR-0011 modes | 1–2 |
| **Should** | `Directory.Build.props` + `.editorconfig` + analyzer dry-run | 1 |
| **Should** | Doc drift cleanup (WPF/NAudio, ADR index) | 0 / 4 |
| **Could** | Extract `OmtCaptureStudio.Core` | 2 |
| **Could** | MF sink implementation / retire ffmpeg.exe from installer | 3 |
| **Could** | PA16 decode support | 3 |
| **Could** | Capture/Broadcast process isolation | open — not scheduled |
| **Could** | Migrate harness to xUnit | open — not required for CI |

---

## 5. Suggested sequencing

### This week (execute in order)

1. ~~**Phase 0.1–0.2** — gitignore + README/CONTEXT truth~~ **Done 2026-11-08** (0.3 Known gaps also landed).
2. ~~**Phase 1.1 skeleton** — CI YAML that builds; tolerate missing payload with explicit fail message if artifact not ready yet.~~ **Done 2026-11-08**.
3. ~~**Phase 1.5 first cut** — extract `Expect` + UiShell domain~~ **Done 2026-11-08**.
4. ~~**Phase 1.6 first VM slice** — AppModeShell partial~~ **Done 2026-11-08**.
5. ~~**Phase 1.4** — `Directory.Build.props` + `.editorconfig`~~ **Done 2026-11-08** (`TreatWarningsAsErrors` deferred).
6. ~~**Phase 1.2 scaffolding** — provision script + secret hooks~~ **Done 2026-11-08**; **owner action:** set `CI_PREREQ_ARCHIVE_URL` (or split URLs) for Actions suite green.
7. ~~**1.5 domain extracts** (Recording/Security/Broadcast/Audio)~~ **Done 2026-11-09** (major cut; Capture/operator/diag remain).
8. ~~**1.3 nightly AOT**~~ **Done 2026-11-09**.
9. ~~**1.6 chrome slices** (Broadcast/Recording)~~ **Done 2026-11-09**.
10. **Next:** finish 1.5 router (`Program.cs` ≤ ~400); named workspace VMs; **owner:** set CI artifact secrets; MF vs FFmpeg decision meeting (read spike README — do not invent ADR).

### Next month

1. CI green with cached native+ffmpeg artifacts (owner secrets).
2. Finish mechanical test split; `Program.cs` router-only.
3. Named Capture/Broadcast workspace VMs behind facade bindings.
4. ADR for recording engine + PA16 policy written and accepted.
5. Core split go/no-go; if go, one vertical move (sinks+sources) not a big-bang.
6. Phase 4 ADR index + release checklist.

---

## 6. Open decisions

| Decision | Options | Recommendation (default) | Needed from owner |
|---|---|---|---|
| **FFmpeg vs MF** | Stay / MF+fallback / MF-only | Stay FFmpeg until audio+HW+crash gates in ADR; keep spike evidence | Strategy call |
| **Test harness vs xUnit** | Keep console throw-on-fail / migrate | **Keep** harness; CI invokes `dotnet run`; migrate only if IDE/explorer pain dominates | Only if CI friction |
| **Core project split** | Now / later / never | **Later** (Phase 2), after CI + test split | Confirm appetite for csproj churn |
| **CONTEXT.md tracking** | Track in git / generate / personal-only | **Track** (remove from `.gitignore`) — AGENTS already requires it | Confirm no secrets in file |
| **Capture vs Broadcast process isolation** | One process (today) / two processes | **Stay one process** unless stability data demands isolation | Only if multi-hour broadcast faults require it |
| **PA16** | Refuse (current) / decode | **Refuse** until a real sender requirement appears | Product |

---

## 7. Success metrics

| Metric | Baseline (verified) | Target |
|---|---|---|
| CI on default branch / PRs | **1.1 workflow + 1.2 provision hooks** (prereq-red until secrets) | Required check: build + default suite green (needs owner artifact URL) |
| Test entrypoint size | `Program.cs` ~3.5k after Recording/Security/Broadcast/Audio extract (was ~9.0k) | Router ≤ ~400 lines; domains in separate files |
| Shell VM size | `MainWindowViewModel.cs` ~2.3k + AppModeShell/BroadcastChrome/RecordingChrome partials | No single VM file > ~1k without active split plan; mode concerns separated |
| Onboarding | Tracked scripts + CONTEXT | Tracked scripts + CONTEXT; README prerequisites unambiguous |
| Repo standards files | **`Directory.Build.props` + `.editorconfig` present** | Present; documented in CODING_STANDARDS |
| Recording strategy | Spike only on `feature/media-foundation-sink` | Accepted ADR; production still behind `IRecordingSink` |
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
