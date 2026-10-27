namespace OmtCaptureStudio.Models;

public enum OutputContainerFormat
{
    MP4,
    MKV,
    MOV
}

public enum VideoEncoderChoice
{
    AutoHardware,
    NvidiaNvenc,
    IntelQuickSync,
    AmdAmf,
    CpuX264,
    ProRes
}

public enum QualityPreset
{
    Ultra,
    High,
    Medium,
    Low
}

public class RecordingConfig
{
    public OutputContainerFormat ContainerFormat { get; set; } = OutputContainerFormat.MP4;
    public VideoEncoderChoice EncoderChoice { get; set; } = VideoEncoderChoice.AutoHardware;
    public QualityPreset Quality { get; set; } = QualityPreset.High;
    public string OutputDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public string FilenamePrefix { get; set; } = "OMT_Capture";

    /// <summary>
    /// Input-loss watchdog window (ADR-0002): wall-clock seconds with no video frame while recording
    /// before the take is treated as interrupted. Operator-configurable; default 5 s. Applied per take
    /// when recording starts.
    /// </summary>
    public double InputLossThresholdSeconds { get; set; } = 5.0;

    /// <summary>
    /// Bounded grace period (ADR-0002): how long to keep attempting auto-reconnect after an outage
    /// before giving up and stopping the take. Default 30 s.
    /// </summary>
    public double InputRecoveryGraceSeconds { get; set; } = 30.0;

    // ================================================================================================
    // Scheduled / segmented capture (NET-008)
    // ================================================================================================

    /// <summary>
    /// Segment rollover by duration, in minutes. Once the active segment has been open this long it is
    /// finalized and the take continues into the next ..._partNN file. <c>0</c> = never roll by time.
    /// A rollover is a full Disarm+Arm cycle through the recording arm - there is no second gate
    /// beside it (ADR-0005).
    /// </summary>
    public double SegmentRolloverMinutes { get; set; }

    /// <summary>
    /// Segment rollover by on-disk size, in gigabytes (decimal GB, matching how drive sizes are
    /// quoted). An independent trigger from <see cref="SegmentRolloverMinutes"/>; whichever fires
    /// first rolls the segment. <c>0</c> = never roll by size.
    /// </summary>
    public double SegmentRolloverGigabytes { get; set; }

    /// <summary>
    /// Maximum take duration in minutes, summed across ALL segments. When reached the take stops
    /// (max-duration auto-stop). <c>0</c> = unlimited.
    /// </summary>
    public double MaxDurationMinutes { get; set; }

    /// <summary>
    /// Absolute UTC wall-clock instant at which a scheduled take should begin. <c>null</c> = begin as
    /// soon as the take is armed. When <see cref="ScheduledStartDelayMinutes"/> is also set the delay
    /// wins (it is the more specific instruction).
    /// </summary>
    public DateTime? ScheduledStartUtc { get; set; }

    /// <summary>
    /// "Start in N minutes" alternative to <see cref="ScheduledStartUtc"/>: a delay measured from the
    /// instant the schedule is resolved. <c>0</c> = no delay.
    /// </summary>
    public double ScheduledStartDelayMinutes { get; set; }

    /// <summary>
    /// Absolute UTC wall-clock instant at which a take should stop. <c>null</c> = no wall-clock stop
    /// (the operator stops it, or <see cref="MaxDurationMinutes"/> does).
    /// </summary>
    public DateTime? ScheduledStopUtc { get; set; }

    /// <summary>
    /// NET-016: "stop after N minutes" - the operator-facing, delay-based mirror of
    /// <see cref="ScheduledStartDelayMinutes"/>. Resolved ONCE into an absolute
    /// <see cref="ScheduledStopUtc"/> when the take is armed, exactly as a start delay is resolved into
    /// <see cref="ScheduledStartUtc"/>. <c>0</c> = no stop-after. This is what the picker binds to and
    /// what persists; the absolute instant is per-take and would go stale in the file.
    /// </summary>
    public double ScheduledStopAfterMinutes { get; set; }

    /// <summary>
    /// NET-011: write a SECOND copy of the take into this directory as well. <c>null</c>/empty = a
    /// single destination, exactly as before. When set, recording fans out through a composite sink:
    /// if the primary's volume fills or its transport fails, that destination is shed and the take
    /// carries on to the backup instead of stopping. The backup uses the same file name as the primary.
    /// </summary>
    public string? BackupOutputDirectory { get; set; }

    /// <summary>
    /// True when a backup destination is configured AND differs from the primary - i.e. when recording
    /// should fan out to two destinations. A backup pointing at the primary folder is not a fan-out.
    /// </summary>
    public bool HasBackupDestination
    {
        get
        {
            string? backup = BackupOutputDirectory?.Trim();
            string? primary = OutputDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(backup) || string.IsNullOrWhiteSpace(primary)) return false;

            return !string.Equals(
                backup.TrimEnd('\\', '/'), primary.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A plain value copy, used by the preset picker. The preset types are deliberately separate from
    /// this class so a preset can be applied without mutating a live config in place.
    /// </summary>
    public RecordingConfig Clone() => new()
    {
        ContainerFormat = ContainerFormat,
        EncoderChoice = EncoderChoice,
        Quality = Quality,
        OutputDirectory = OutputDirectory,
        FilenamePrefix = FilenamePrefix,
        InputLossThresholdSeconds = InputLossThresholdSeconds,
        InputRecoveryGraceSeconds = InputRecoveryGraceSeconds,
        SegmentRolloverMinutes = SegmentRolloverMinutes,
        SegmentRolloverGigabytes = SegmentRolloverGigabytes,
        MaxDurationMinutes = MaxDurationMinutes,
        ScheduledStartUtc = ScheduledStartUtc,
        ScheduledStartDelayMinutes = ScheduledStartDelayMinutes,
        ScheduledStopUtc = ScheduledStopUtc,
        ScheduledStopAfterMinutes = ScheduledStopAfterMinutes,
        BackupOutputDirectory = BackupOutputDirectory
    };
}

/// <summary>
/// Which recording boundary the active take has reached (NET-008). Evaluated on the existing UI health
/// tick by pure timestamp/size comparison - no clock reads, no allocation, no I/O.
/// </summary>
public enum RecordingBoundary
{
    /// <summary>Nothing due; keep recording.</summary>
    None,

    /// <summary>
    /// The active segment hit its rollover limit. The take CONTINUES: finalize this segment and arm
    /// the next ..._partNN through the arm's single finalize path.
    /// </summary>
    SegmentRollover,

    /// <summary>The take hit <see cref="RecordingConfig.MaxDurationMinutes"/>. The take ends.</summary>
    MaxDuration,

    /// <summary>The take hit <see cref="RecordingConfig.ScheduledStopUtc"/>. The take ends.</summary>
    ScheduledStop
}

/// <summary>
/// The boundary arithmetic for scheduled/segmented capture, as PURE functions of
/// (config, state, now). Nothing here reads a clock, touches a file or allocates, so the whole policy
/// is asserted headlessly with a local clock and zero real waits - the same shape as the storage guard.
/// </summary>
public static class RecordingBoundaries
{
    /// <summary>
    /// Evaluates every boundary in priority order. Rollover is checked FIRST: a segment that must roll
    /// over does so before the take-level stop is considered, so a take configured to roll every
    /// 10 min with a 60 min maximum yields six segments rather than stopping early.
    /// </summary>
    public static RecordingBoundary Evaluate(
        RecordingConfig config,
        long takeStartUtcTicks,
        long segmentStartUtcTicks,
        long segmentBytes,
        long nowUtcTicks)
    {
        if (RolloverDue(config, segmentStartUtcTicks, segmentBytes, nowUtcTicks))
        {
            return RecordingBoundary.SegmentRollover;
        }

        if (MaxDurationReached(config, takeStartUtcTicks, nowUtcTicks))
        {
            return RecordingBoundary.MaxDuration;
        }

        if (ScheduledStopReached(config, nowUtcTicks))
        {
            return RecordingBoundary.ScheduledStop;
        }

        return RecordingBoundary.None;
    }

    /// <summary>
    /// True when the active segment has reached EITHER rollover limit. The two are independent
    /// triggers: setting one is the common case; setting both rolls on whichever fires first.
    /// </summary>
    public static bool RolloverDue(RecordingConfig config, long segmentStartUtcTicks, long segmentBytes, long nowUtcTicks)
    {
        if (config.SegmentRolloverMinutes > 0)
        {
            double minutes = TimeSpan.FromTicks(nowUtcTicks - segmentStartUtcTicks).TotalMinutes;
            if (minutes >= config.SegmentRolloverMinutes) return true;
        }

        if (config.SegmentRolloverGigabytes > 0)
        {
            double gigabytes = segmentBytes / 1_000_000_000.0;
            if (gigabytes >= config.SegmentRolloverGigabytes) return true;
        }

        return false;
    }

    /// <summary>True when the take has run for <see cref="RecordingConfig.MaxDurationMinutes"/>.</summary>
    public static bool MaxDurationReached(RecordingConfig config, long takeStartUtcTicks, long nowUtcTicks)
        => config.MaxDurationMinutes > 0
           && TimeSpan.FromTicks(nowUtcTicks - takeStartUtcTicks).TotalMinutes >= config.MaxDurationMinutes;

    /// <summary>True when the wall-clock scheduled stop instant has arrived.</summary>
    public static bool ScheduledStopReached(RecordingConfig config, long nowUtcTicks)
        => config.ScheduledStopUtc is { } stop && nowUtcTicks >= stop.Ticks;

    /// <summary>
    /// NET-016: the nearest boundary that has a CLOCK, and the time left until it. Pure - the caller
    /// supplies <paramref name="nowUtcTicks"/>, so the whole readout is assertable with a local clock and
    /// no waits.
    ///
    /// <para>Only time-driven boundaries are candidates. <see cref="RecordingConfig.SegmentRolloverGigabytes"/>
    /// is deliberately EXCLUDED: a size rollover depends on the achieved bitrate, so it has no honest
    /// "ends in" - presenting one would be a guess dressed up as a countdown.</para>
    ///
    /// <para>Returns <c>null</c> when nothing is bounded, so the readout disappears rather than counting
    /// down to nothing. A boundary already PASSED clamps to zero rather than reporting negative time - the
    /// stop is imminent either way, and a negative countdown reads as a bug.</para>
    /// </summary>
    public static RecordingBoundaryEta? NearestTimeBoundary(
        RecordingConfig config,
        long takeStartUtcTicks,
        long segmentStartUtcTicks,
        long nowUtcTicks)
    {
        RecordingBoundary nearest = RecordingBoundary.None;
        TimeSpan remaining = TimeSpan.Zero;
        bool found = false;

        void Consider(RecordingBoundary boundary, TimeSpan candidate)
        {
            if (candidate < TimeSpan.Zero) candidate = TimeSpan.Zero;
            if (found && candidate >= remaining) return;

            found = true;
            nearest = boundary;
            remaining = candidate;
        }

        if (config.MaxDurationMinutes > 0)
        {
            Consider(
                RecordingBoundary.MaxDuration,
                TimeSpan.FromMinutes(config.MaxDurationMinutes)
                    - TimeSpan.FromTicks(nowUtcTicks - takeStartUtcTicks));
        }

        if (config.ScheduledStopUtc is { } stop)
        {
            Consider(
                RecordingBoundary.ScheduledStop,
                new DateTime(stop.Ticks, DateTimeKind.Utc) - new DateTime(nowUtcTicks, DateTimeKind.Utc));
        }

        if (config.SegmentRolloverMinutes > 0)
        {
            Consider(
                RecordingBoundary.SegmentRollover,
                TimeSpan.FromMinutes(config.SegmentRolloverMinutes)
                    - TimeSpan.FromTicks(nowUtcTicks - segmentStartUtcTicks));
        }

        return found ? new RecordingBoundaryEta(nearest, remaining) : null;
    }
}

/// <summary>
/// NET-016: which time-driven boundary is coming next, and how long until it. A value type, so the UI
/// tick can project it without allocating.
/// </summary>
public readonly record struct RecordingBoundaryEta(RecordingBoundary Boundary, TimeSpan Remaining);

/// <summary>
/// Scheduled-start arithmetic, also pure. It lives beside <see cref="RecordingBoundaries"/> because
/// the two are the same policy read from opposite ends of a take.
/// </summary>
public static class RecordingSchedule
{
    /// <summary>
    /// Resolves when an armed schedule should actually begin. A start DELAY wins over an absolute
    /// instant when both are set, and <c>null</c> means "start now". Pure: the caller supplies
    /// <paramref name="nowUtc"/>.
    /// </summary>
    public static DateTime? ResolveStartUtc(RecordingConfig config, DateTime nowUtc)
    {
        if (config.ScheduledStartDelayMinutes > 0)
        {
            return nowUtc.AddMinutes(config.ScheduledStartDelayMinutes);
        }

        return config.ScheduledStartUtc;
    }

    /// <summary>True when a resolved start instant has arrived. A null instant means "start now".</summary>
    public static bool StartReached(DateTime? resolvedStartUtc, DateTime nowUtc)
        => resolvedStartUtc is not { } start || nowUtc >= start;

    /// <summary>
    /// Resolves when an armed take should actually stop (NET-016) - the exact mirror of
    /// <see cref="ResolveStartUtc"/>. A stop-after delay wins over an absolute instant, and <c>null</c>
    /// means "no scheduled stop". Pure: the caller supplies <paramref name="nowUtc"/>.
    /// </summary>
    public static DateTime? ResolveStopUtc(RecordingConfig config, DateTime nowUtc)
    {
        if (config.ScheduledStopAfterMinutes > 0)
        {
            return nowUtc.AddMinutes(config.ScheduledStopAfterMinutes);
        }

        return config.ScheduledStopUtc;
    }
}
