# ADR-0014: Record on the wall clock, not a rate frozen at arm time

- **Status:** Accepted
- **Deciders:** Field failure on a Waves LV1 broadcast → capture take (h264_qsv, ~1h50m)
- **Scope:** Recording video conform, audio resample reference, desktop-sender audio cadence,
  capture-ring backlog, take file identity, recording audio source and manual A/V offset
- **Related:** ADR-0002 (do not stitch an input-loss hole into the file), ADR-0003 (one capture
  ring), ADR-0005 (the arm still owns the clock and the single finalize path), ADR-0006
  (settings; new fields are nullable and default to today's behaviour), ADR-0012 (the pipe is
  still FFmpeg; `-r` is the announced ratio)

## Context

A capture-mode file of a 30 fps OMT desktop sender was stamped `3221/125` (25.768 fps) because
`FrameRateMeter` averages every frame since Connect and `CaptureSession` froze that number into
ffmpeg `-r` at arm time. Rawvideo pictures are timestamped as index/rate, so a sender that actually
delivered ~24.47 fps made the file ~350 s short of wall time and the burned-in clock drifted.

The audio resampler then enslaved output samples to `videoFrames * 48000 / rate`. The sender was
also short of real-time audio: each loop pass shipped one frame of samples, skipped frames dropped
theirs, and `StereoCaptureRing` trimmed anything past 120 ms down to 60 ms. The ratio pinned on the
0.9/1.1 clamp (~1.1, about −1.65 semitones) and the mix landed ~60 s behind the picture, with
~14% of the source missing in small gaps. The filename was `DateTime.Now` at arm, ~80 s before the
first picture was written, and the container had no `creation_time`.

## Decision

1. **Video is CFR at the announced ratio** (`RationalFrameRate` on the stream: 25/1, 30/1,
   30000/1001, …). On each arrival a pure conformer (`RecordingFrameConform`) duplicates the last
   picture or drops the arrival so the written count matches elapsed wall time at that rate. The
   meter stays a diagnostic. A stop does **not** invent pictures across a silent hole — that would
   undo ADR-0002 / DEF-01. While pictures are arriving, each one catches the file up.
2. **Audio length follows the wall clock, not the video frame count.** The resampler's reference
   is elapsed time versus samples received. The 0.9/1.1 band remains a safety clamp for a wild
   measurement. If the ratio stays pinned for more than a few seconds, that is logged once and the
   pinned duration is on the stop summary. A one- or two-percent clock skew is the correction the
   resampler is for; it does not pitch-shift the mix to chase a wrong frame rate.
3. **The desktop sender ships audio by elapsed schedule time.** A loop pass sends every sample due
   since the previous send, including pictures `FrameSchedule` skipped. The capture ring holds a
   2 s buffer and trims only past a 1 s backlog, and logs when it does.
4. **The file's name is the first picture actually written.** The arm creates a pending name, and
   renames after finalize (the encoder has the file open during the take). ffmpeg gets
   `-metadata creation_time` as a UTC ISO-8601 timestamp of encoder start. A JSON sidecar beside
   the file records arm time, first video time, first audio time, the configured rate, the measured
   input rate, duplicate and drop counts, audio clamp time, and the source name.
5. **The recorded audio source is a setting.** OMT source audio stays the default. A local
   WASAPI/ASIO input uses the existing host-audio sources. A manual A/V offset in milliseconds
   delays or advances the recorded audio by sample shift (the same effect as `-itsoffset` on the
   audio input, and it applies to whichever sink is selected). Both persist. Absent fields keep
   today's behaviour.

## Consequences

- Duplicate/drop counts and clamp time are part of `RecordingStopSummary` and the disarm log.
- ADR-0005's arm order is unchanged. ADR-0005's old "stamp the measured fps" sentence is not.
- ADR-0003's 120 ms ring cap is relaxed to a 1 s backlog. The drop-to-half rule still applies once
  a take is genuinely that far behind, and the trim is logged.
- Headless tests drive the conformer, the sender sample count, and the resampler with injected
  clocks. No test sleeps to prove the math.
