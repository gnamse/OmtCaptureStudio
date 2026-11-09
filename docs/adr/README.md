# Architecture Decision Records

Index of ADRs for OMT Capture Studio. Start here when you need the *why* behind
mode shell, recording arm, sinks, or security-adjacent policy.

Living improvement work (phased roadmap, open product calls) lives in
[`../IMPROVEMENT_PLAN.md`](../IMPROVEMENT_PLAN.md). Coding rules:
[`../../CODING_STANDARDS.md`](../../CODING_STANDARDS.md).

## How to use this index

| Looking for… | Read |
|---|---|
| Mode picker / Capture vs Broadcast shell | [0011](0011-mode-based-shell.md) |
| Recording start/stop gate + finalize ownership | [0005](0005-recording-arm.md) |
| Dual-destination / fan-out recording | [0010](0010-recording-fan-out.md) |
| What happens when input dies mid-take | [0002](0002-input-loss-recording-policy.md) |
| Broadcast live/select state machine | [0001](0001-broadcast-session-state.md) |
| Settings persistence / quarantine | [0006](0006-settings-persistence.md) |
| Host ↔ OMT audio rate conversion | [0003](0003-audio-rate-conversion.md) |
| FFmpeg vs Media Foundation recording engine | *not authored yet — Phase 3; see IMPROVEMENT_PLAN* |
| PA16 / hostile-peer decode policy | *not authored yet — Phase 3* |

Security bootstrap (native payload hash), frame guards, and FFmpeg preflight are
enforced in code + the headless suite (`SecurityRegressionTable`); they are not
a numbered ADR. Recording sink selection for production takes is
`RecordingSinkSelector.ForProduction()` behind `IRecordingSink`.

## Numbered series

| ADR | Title | Status |
|---|---|---|
| [0001](0001-broadcast-session-state.md) | Broadcast mode transitions owned by headless `BroadcastSessionState` | Accepted |
| [0002](0002-input-loss-recording-policy.md) | Input-loss recording policy | Accepted |
| [0003](0003-audio-rate-conversion.md) | Audio rate conversion | Accepted |
| **0004** | — | **Missing** — leave vacant until a real decision needs the slot |
| [0005](0005-recording-arm.md) | Recording arm (gate + finalize ownership) | Accepted |
| [0006](0006-settings-persistence.md) | Settings persistence | Accepted |
| **0007** | — | **Missing** — leave vacant |
| **0008** | — | **Missing** — leave vacant |
| **0009** | — | **Missing** — leave vacant |
| [0010](0010-recording-fan-out.md) | Recording fan-out / composite sink | Accepted |
| [0011](0011-mode-based-shell.md) | Mode-based application shell (picker → Capture \| Broadcast) | Accepted |

Do **not** invent stub ADRs for gaps 0004 / 0007–0009. Fill a number only when
shipping a real decision (see `AGENTS.md` Known gaps).

## Authoring a new ADR

1. Use the next free number after the highest accepted ADR, **or** intentionally
   fill a reserved gap when that topic historically belonged there.
2. Keep scope to one decision; link companions (`CODING_STANDARDS.md`, spike
   READMEs) rather than pasting them.
3. Add a row to this index in the same PR.
