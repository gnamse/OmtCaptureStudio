using System;

namespace OmtCaptureStudio.Models;

/// <summary>
/// One immutable sample of the recording path's health (FEAT-07). The engine
/// (<see cref="Services.CaptureSession"/>) assembles it and the ViewModel projects it — the
/// aggregation lives here, never ad hoc in code-behind.
/// </summary>
public readonly record struct RecordingHealthSnapshot
{
    public bool IsRecording { get; init; }
    public bool HasTake { get; init; }
    public int SegmentIndex { get; init; }

    /// <summary>The encoder actually used (AutoHardware resolved to a concrete encoder).</summary>
    public string EncoderName { get; init; }

    /// <summary>Achieved output bitrate, derived from the output file-size delta over media time.</summary>
    public double AchievedBitrateMbps { get; init; }

    public long VideoFramesWritten { get; init; }

    /// <summary>Video frames the RECORDING transport discarded (not the receiver-side counter).</summary>
    public long VideoFramesDropped { get; init; }

    public long AudioChunksDropped { get; init; }

    /// <summary>Video track duration — frames / stamped frame rate (DEF-01: excludes dead air).</summary>
    public TimeSpan MediaDuration { get; init; }

    /// <summary>Audio track duration — PCM bytes / (rate × channels × 4).</summary>
    public TimeSpan AudioDuration { get; init; }

    /// <summary>Wall-clock span, which INCLUDES any input-loss dead air. Reported separately.</summary>
    public TimeSpan WallClockDuration { get; init; }

    public double AvDriftRatio { get; init; }

    /// <summary>True when the resample ratio is pinned against its 0.9/1.1 safety clamp.</summary>
    public bool AvDriftClamped { get; init; }

    /// <summary>Capped tail of the FFmpeg stderr stream, for the in-app diagnostics drawer.</summary>
    public string DiagnosticTail { get; init; }
}

/// <summary>
/// Why a recording stopped (ADR-0005).
///
/// <para>Every stop trigger routes through one finalize path, so the only thing that distinguishes
/// them is the reason — reported on <see cref="RecordingStopSummary"/> and surfaced in the stop
/// verdict. Before ADR-0005 the low-disk and sink-error paths stopped silently, with no summary at
/// all.</para>
/// </summary>
public enum RecordingStopReason
{
    /// <summary>The operator asked to stop (or disconnected / closed the session).</summary>
    Manual,

    /// <summary>Free space crossed the hard low-water mark; stopped to keep the file playable.</summary>
    LowDisk,

    /// <summary>The input-health watchdog hit an outage boundary while recording (ADR-0002).</summary>
    InputLoss,

    /// <summary>The recording transport failed; finalization is best-effort from whatever reached disk.</summary>
    SinkError,

    /// <summary>The session was disposed while recording.</summary>
    Dispose,

    /// <summary>
    /// A segment rollover boundary (NET-008): the segment finalized and the TAKE CONTINUED in the next
    /// …_partNN. The only reason that is a BOUNDARY rather than a stop - see
    /// <see cref="RecordingHealth.IsSegmentBoundary"/>.
    /// </summary>
    SegmentRollover,

    /// <summary>The take reached its configured maximum duration and stopped (NET-008).</summary>
    MaxDuration,

    /// <summary>The take reached its scheduled wall-clock stop time and stopped (NET-008).</summary>
    ScheduledStop
}

/// <summary>
/// Everything reported when a segment (or the whole take) stops. <see cref="MediaDuration"/> is the
/// media duration, NOT wall clock — the DEF-01 fix. Wall clock rides along separately and clearly
/// labelled, because it is the only figure that includes input-loss dead air.
/// </summary>
public sealed record RecordingStopSummary(
    string Path,
    TimeSpan MediaDuration,
    TimeSpan WallClockDuration,
    TimeSpan AudioDuration,
    TimeSpan FinalizeDuration,
    long FileSizeBytes,
    long VideoFramesWritten,
    long VideoFramesDropped,
    long AudioChunksDropped,
    int SegmentIndex,
    string EncoderName,
    string DiagnosticTail,
    RecordingStopReason Reason = RecordingStopReason.Manual)
{
    public RecordingHealthSnapshot ToHealthSnapshot() => new()
    {
        IsRecording = false,
        HasTake = false,
        SegmentIndex = SegmentIndex,
        EncoderName = EncoderName,
        DiagnosticTail = DiagnosticTail ?? string.Empty,
        AchievedBitrateMbps = MediaDuration.TotalSeconds > 0
            ? FileSizeBytes * 8.0 / 1_000_000.0 / MediaDuration.TotalSeconds
            : 0,
        VideoFramesWritten = VideoFramesWritten,
        VideoFramesDropped = VideoFramesDropped,
        AudioChunksDropped = AudioChunksDropped,
        MediaDuration = MediaDuration,
        AudioDuration = AudioDuration,
        WallClockDuration = WallClockDuration
    };
}

/// <summary>
/// Pure, headless analysis of a <see cref="RecordingHealthSnapshot"/>: warning thresholds, display
/// text, and the stop-summary verdict (FEAT-07). No I/O, no FFmpeg, no Avalonia — fully unit-testable
/// with synthetic snapshots.
/// </summary>
public static class RecordingHealth
{
    /// <summary>Recording-path frames dropped before the UI warns.</summary>
    public const long DropWarnThreshold = 30;

    /// <summary>|ratio − 1| beyond this is a drift warning (the clamp band is 0.9/1.1).</summary>
    public const double DriftWarnBand = 0.02;

    /// <summary>Video/audio duration may differ by this much before parity is called out.</summary>
    public const double AvParityToleranceSeconds = 0.25;

    /// <summary>Friendly name for a concrete (already AutoHardware-resolved) encoder choice.</summary>
    public static string EncoderDisplayName(VideoEncoderChoice encoder) => encoder switch
    {
        VideoEncoderChoice.NvidiaNvenc => "NVENC (NVIDIA)",
        VideoEncoderChoice.IntelQuickSync => "QSV (Intel)",
        VideoEncoderChoice.AmdAmf => "AMF (AMD)",
        VideoEncoderChoice.ProRes => "ProRes",
        VideoEncoderChoice.CpuX264 => "x264 (CPU)",
        _ => "Auto"
    };

    public static bool IsDropWarning(RecordingHealthSnapshot s) =>
        s.VideoFramesDropped >= DropWarnThreshold;

    public static bool IsDriftWarning(RecordingHealthSnapshot s) =>
        s.AvDriftClamped || Math.Abs(s.AvDriftRatio - 1.0) > DriftWarnBand;

    public static string EncoderText(RecordingHealthSnapshot s) =>
        string.IsNullOrWhiteSpace(s.EncoderName) ? "—" : s.EncoderName;

    public static string BitrateText(RecordingHealthSnapshot s) =>
        s.IsRecording ? $"{s.AchievedBitrateMbps:F1} Mbps" : "—";

    public static string DropsText(RecordingHealthSnapshot s) =>
        $"{s.VideoFramesDropped:N0}v / {s.AudioChunksDropped:N0}a";

    public static string DriftText(RecordingHealthSnapshot s) =>
        s.AvDriftRatio > 0 ? $"{s.AvDriftRatio:F4}{(s.AvDriftClamped ? " (clamped)" : string.Empty)}" : "—";

    /// <summary>True when the video and audio track durations agree within tolerance.</summary>
    public static bool IsAvParityOk(RecordingHealthSnapshot s)
    {
        if (s.AudioDuration <= TimeSpan.Zero || s.MediaDuration <= TimeSpan.Zero)
        {
            return true; // a missing track cannot be out of parity with anything
        }

        return Math.Abs(s.MediaDuration.TotalSeconds - s.AudioDuration.TotalSeconds) <= AvParityToleranceSeconds;
    }

    /// <summary>
    /// The one-line stop verdict: video/audio duration parity, recording-path drops, and finalize
    /// time. Uses the media duration (DEF-01), so an input-loss outage does not inflate it.
    /// </summary>
    public static string BuildStopVerdict(RecordingStopSummary summary)
    {
        RecordingHealthSnapshot s = summary.ToHealthSnapshot();
        double delta = Math.Abs(s.MediaDuration.TotalSeconds - s.AudioDuration.TotalSeconds);
        string parity = IsAvParityOk(s) ? "parity OK" : "PARITY MISMATCH";

        return $"Stop verdict ({ReasonText(summary.Reason)}): video {s.MediaDuration.TotalSeconds:F2}s vs audio {s.AudioDuration.TotalSeconds:F2}s " +
               $"(delta {delta:F2}s, {parity}) | drops {VideoDropsText(s)} | finalize {summary.FinalizeDuration.TotalMilliseconds:F0} ms | " +
               $"segment {summary.SegmentIndex} | {EncoderText(s)} @ {s.AchievedBitrateMbps:F1} Mbps | " +
               $"file {summary.FileSizeBytes / (1024.0 * 1024.0):F1} MB";
    }

    /// <summary>
    /// True when this reason is a segment BOUNDARY rather than a take stop: the segment finalized and
    /// the take CONTINUED in the next …_partNN. Only a rollover qualifies (NET-008). Callers use this
    /// to give a boundary its own quiet rendering instead of the stop verdict.
    /// </summary>
    public static bool IsSegmentBoundary(RecordingStopReason reason)
        => reason == RecordingStopReason.SegmentRollover;

    /// <summary>
    /// The quiet one-line notice for a rollover boundary (NET-008). An unattended take rolling over
    /// every 15 minutes must NOT flash an alarming stop verdict each time - the take did not stop - so
    /// a boundary renders as this continuation line instead of <see cref="BuildStopVerdict"/>. Pure,
    /// so the UI and the tests agree by construction.
    /// </summary>
    public static string BuildSegmentContinuationText(RecordingStopSummary summary)
        => $"Segment {summary.SegmentIndex} complete \u2014 continuing " +
           $"({summary.FileSizeBytes / (1024.0 * 1024.0):F1} MB)";

    /// <summary>Operator-facing name for why a recording stopped.</summary>
    public static string ReasonText(RecordingStopReason reason) => reason switch
    {
        RecordingStopReason.SegmentRollover => "segment complete: continuing",
        RecordingStopReason.LowDisk => "stopped: low disk",
        RecordingStopReason.InputLoss => "stopped: input lost",
        RecordingStopReason.SinkError => "stopped: transport failure",
        RecordingStopReason.MaxDuration => "stopped: max duration",
        RecordingStopReason.ScheduledStop => "stopped: scheduled stop",
        RecordingStopReason.Dispose => "stopped: session closed",
        _ => "stopped by operator"
    };

    /// <summary>Wall-clock span for the log line — the ONLY figure that includes outage dead air.</summary>
    public static string WallClockNote(RecordingStopSummary summary) =>
        $"wall-clock {summary.WallClockDuration.TotalSeconds:F2}s (includes input-loss dead air)";

    private static string VideoDropsText(RecordingHealthSnapshot s) =>
        $"{s.VideoFramesDropped:N0} video";
}
