# ADR-0010: Recording fan-out — one encode for same-format dual-folder, and the failure model that governs it

- **Status:** **Accepted for the role split (§3) and the network deferral (§4). The `tee` adoption
  (§2) is BLOCKED by measurement — see §5, which is the STOP-AND-FLAG the NET-021 work order
  authorised. The two-child composite remains the shipping implementation.**
- **Deciders:** Odogwu (orchestration) on behalf of the project owner
- **Scope:** `Services/Sinks/CompositeRecordingSink.cs` + `CompositeRecordingSinkFactory`,
  `Services/Sinks/FfmpegProcessHost.cs` / `FfmpegPipeSink.cs` (argv plumbing only), and this record.
  **No code was changed for §2** — see §6.
- **Related:** ADR-0002 (source reconnect-with-backoff — the model §4 waits on), ADR-0005 (the arm's
  single gate and single finalize path), NET-004 (the writer loop, `MediaBufferPool`), NET-011
  (the composite this governs). Architecture-review candidate, scan #2, the final item.

## Context

NET-011 gave recording a second destination: one take, two folders. It did so with
`CompositeRecordingSink`, a copy-transparent fan-out that hands the same packet pointer to each
child in turn. The design is simple and its failure story is explicit — a child that cannot
initialize is DROPPED, a child that fails mid-take is detached exactly once, and the take continues
as long as one destination survives.

The cost is that **each destination runs its own encoder**. Two folders means two ffmpeg processes,
two x264 encodes, on the same machine that is simultaneously capturing, resampling audio and
muxing. For a hardware-encoded take that is wasteful; for a **CPU-encoded take it is a defect the
durability story cannot justify**: the second encode consumes exactly the CPU headroom the first one
needs, so the backup can cause the drops it was bought to protect against.

This ADR records the ruling that follows from that, and — because the ruling was to adopt a
different ffmpeg muxer — the empirical work that decides whether the muxer can do the job.

## Decision

### 1. Same-format dual-folder should be one encode, two outputs

When both destinations want the same container, encoder and quality — the common case, since the
backup folder inherits the primary's configuration — the second encode is pure waste. The intended
mechanism is ffmpeg's `tee` muxer: one `FfmpegProcessHost`, one encode, N output files.
`CompositeRecordingSink` is retained as the fallback for heterogeneous children, where one encoder
cannot serve both.

**Measured, this box, ffmpeg 9.0.1-essentials, 20 s of 720×30 `testsrc2`, `CpuX264` preset
`medium`:**

| topology | wall time |
| :--- | ---: |
| one encode, two outputs (`tee`) | **1 162 ms** |
| two encodes (the composite, today) | **2 323 ms** |
| one encode alone | 1 159 ms |

The saving is **1 161 ms, 49 %**, and `tee`'s cost is within 3 ms of a single encode — the second
output is effectively free. The premise of the ruling is confirmed: the fan-out is the expensive
part, not the writing. The duplicated outputs are **byte-identical** (same SHA-256, 7 845 885 bytes
each), which is also true for fragmented MP4 (144 963 bytes each).

### 2. The `tee` adoption is BLOCKED (§5) — do not land it as specified

The muxer cannot produce the output this app already writes, and its default failure behaviour is
worse than the model it would replace. The evidence is in §5 and it is not marginal.

### 3. The composite's role, recorded

- **`CompositeRecordingSink` stays** for heterogeneous children: copy-transparent fan-out,
  detach-once semantics, degrade-instead-of-stop when a destination survives (`HasSurvivingDestination`),
  and **drop-a-child-that-cannot-initialize** so an unprepared backup folder cannot reduce a take.
- It is the **reference failure model** for any fan-out mechanism: *a destination that fails is
  lost; the take is not.* Any replacement must meet it.
- The detach history is observable (`IRecordingSink.DetachedDestinationNames`, NET-020) and reaches
  the diagnostics bundle, so a silently-lost backup is diagnosable after the fact.

### 4. Network children are explicitly DEFERRED

Do **not** add a network destination to a fan-out until the child failure policy distinguishes
**transient** from **terminal**:

- **transient** — retry with backoff, reusing ADR-0002's reconnect ladder rather than inventing a
  second one;
- **terminal** — detach, per the composite's existing semantics.

The reason is asymmetric damage. A network destination detached on its first Wi-Fi blip silently
ends the offsite copy for the rest of the take — the operator believes they have an offsite copy and
does not. That is **worse than having no offsite copy**, because it is indistinguishable from
success. A local-folder destination has no transient class worth modelling: a folder that is gone is
gone.

Note that the `tee` path's fifo recovery (§5.6) is a *partial* answer to exactly this problem and is
recorded as evidence that a retry/recovery model is expressible here — not as a substitute for a
deliberate transient/terminal policy.

## 5. The empirical findings (the STOP-AND-FLAG)

All measurements are against the real ffmpeg on this box
(`9.0.1-essentials_build-www.gyan.dev`), with `libx264` and a synthetic `testsrc2` source. Every
claim below has a control: the *same* configuration written as a single output, which passes.

### 5.1 `-map` is mandatory (closed — a fact, not a blocker)

`tee` represents no particular output format, so ffmpeg cannot auto-select streams. Without `-map`
the muxer fails with `Output file does not contain any stream`, exit 127, both outputs empty. This
matches the ffmpeg documentation verbatim ("all streams intended for output must be specified using
`-map`"). Any implementation must emit `-map 0:v -map 1:a` explicitly.

### 5.2 BLOCKER — `empty_moov` through a `tee` slave produces an UNPLAYABLE file

The app writes MP4/MOV as `-movflags +frag_keyframe+empty_moov`. That is a deliberate, long-documented
crash-tolerance decision: a fragmented index is online from the first fragment, so a mid-take crash
still leaves a salvageable file and Stop finalizes in ~O(1).

Written through a `tee` slave, that configuration yields a file that **cannot be decoded**:

| | tee slave | single-output control |
| :--- | :--- | :--- |
| bytes | 96 829 | 96 832 |
| top-level boxes | `ftyp moov(704) moof mdat mfra` | `ftyp moov(741) moof mdat mfra` |
| `ffprobe` | **`Invalid data found when processing input`** | `duration=2.000000`, 20 frames |
| decode | `missing picture in access unit` / `No start code is found` | clean |

The box layout is identical, so the flags **did** apply. The `moov` is **37 bytes shorter** — the
H.264 extradata (SPS/PPS). `tee` writes the empty `moov` without the codec extradata, and the result
is unplayable. This is not a probe artefact: the only difference between the two rows is `tee`.

It is consistent with the documented caveat that "some encoders may need different options depending
on the output format; the auto-detection of this can not work with the tee muxer" — the mov muxer's
normal extradata handling does not survive the tee path.

**The flag cannot be plumbed either way.** Passed globally (`-movflags +frag_keyframe+empty_moov`
before `-f tee`) it is silently ignored by the slaves: the resulting file is byte-size identical to
a run with no `movflags` at all (96 751 bytes). Passed per-slave it corrupts the file. There is no
form that works.

Consequence: adopting `tee` for MP4/MOV (two of the app's three containers) requires **dropping
`empty_moov`**, i.e. giving up crash salvageability on dual-folder takes. That is a behavioural
regression in the recorded-file story, not an argv change, and it is out of this work order's scope.

### 5.3 BLOCKER — any slave failure aborts the ENTIRE muxer

The composite's defining property is that a failed destination is *lost while the take survives*.
`tee`'s default is the opposite:

```
[tee @ …] Slave '[f=mp4]nope/out.mp4': error opening: No such file or directory
[tee @ …] Slave muxer #1 failed, aborting.
[out#0/tee @ …] Could not write header (incorrect codec parameters ?): No such file or directory
```

exit **127**, and the *primary* is left as a **261-byte stub** — not a truncated recording, an
unusable one. With `-use_fifo 1` alone it is still fatal: exit 127, primary 9 521 bytes / 0.100 s.

One bad path does not degrade the fan-out; it **destroys both copies**. That is strictly worse than
the two-child composite on the single most likely real failure (an absent or unmounted backup
volume), which the composite handles by dropping the child and recording the whole take.

### 5.4 The mitigation exists, but is entirely non-default

```
-use_fifo 1 -fifo_options attempt_recovery=1:recover_any_error=1:max_recovery_attempts=2:recovery_wait_time=1:drop_pkts_on_overflow=1
```

With that option set and a permanently dead second slave: **exit 0**, primary **96 751 bytes,
duration 2.000000, decode-clean** — the good destination records the complete take and the bad one
is tolerated. With *both* slaves dead it correctly aborts (`All tee outputs failed.`). Note
`drop_pkts_on_overflow` is documented as "drop packets on fifo queue overflow **not to block
encoder**", which is the answer to the "one bad path stalling the muxer" risk named in the work
order.

So the behaviour the work order wanted — *the take continues if the primary output still writes* —
**is achievable, but is not the default, and is invisible in the exit code**: ffmpeg returned 0
while printing the slave failure to stderr. Detection therefore has to come from the existing stderr
funnel (`DiagnosticTail` / the sink error path), never from the exit status.

### 5.5 MKV needs `-flags +global_header`, and containers cannot be mixed

- Matroska slaves fail at header write without it (`error writing header: Invalid data found`,
  exit 183, 291-byte file); with `-flags +global_header` the output is clean (96 548 bytes, 20
  frames). Per-container special-casing is unavoidable.
- A single `tee` **cannot mix containers**: an mp4 slave beside an mkv slave aborts the muxer and
  leaves the mp4 slave as a 764-byte stub. Heterogeneous destinations must keep the composite.

### 5.6 What was NOT measured

A **mid-stream** slave failure was not induced — this box offers no way to fail one destination's
writes partway through a take without a genuinely full volume (the app's low-disk guard exists
precisely because that state is hard to fake). The open-time failure in §5.3/§5.4 is measured; the
mid-stream case is **inferred** from `recover_any_error=1` existing for it, and is flagged as
inference rather than evidence. Given §5.2 blocks the adoption regardless, it was not worth
manufacturing.

## Consequences

- **Nothing ships from §1–§2 this pass.** The two-child composite remains the implementation, and
  the `-movflags +frag_keyframe+empty_moov` crash-tolerance property is preserved on dual-folder
  takes because no tee path was introduced.
- The **cost finding stands on its own** and is recorded for whoever revisits this: a same-format
  fan-out costs one extra full encode today, measured at 49 % of the work.
- **The composite's failure model is now the documented reference** (§3), and any future fan-out
  mechanism must be measured against it, not assumed to match it.
- **Network destinations remain deferred** (§4) with a stated reason, so "why is there no offsite
  option?" has an answer in the record.

### To unblock §2, one of these must change

1. **ffmpeg fixes the `tee` + `empty_moov` extradata loss.** Then re-run §5.2's table; if the `moov`
   sizes match and both decode clean, the remaining work is the fifo option set (§5.4), the MKV
   header flag (§5.5) and explicit `-map` (§5.1).
2. **The owner accepts dropping `empty_moov` on dual-folder MP4/MOV takes** — i.e. accepts that a
   mid-take crash on a dual-folder take may leave an unplayable file where a single-destination take
   would have been salvageable. This is a product decision, not an engineering one.
3. **A future release ships a ffmpeg the check passes on** and the fan-out is revisited then.

Until one of those happens, a same-format dual-folder take keeps costing two encodes and keeps
gaining the failure isolation that makes the backup worth having.
