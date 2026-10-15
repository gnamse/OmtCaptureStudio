using System;
using System.IO;
using System.Threading;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Edge result of one <see cref="InputHealthWatchdog.Poll"/>: only a transition is reported,
/// so the caller reacts once per boundary instead of every poll.
/// </summary>
public enum InputHealthTransition
{
    None,
    Lost,
    Restored
}

/// <summary>
/// Recording-level input-health watchdog (ADR-0002). While armed, it is notified on every
/// received video frame and raises <see cref="InputHealthTransition.Lost"/> once when no frame has
/// arrived for the configured threshold — measured in wall-clock absence of frames, not queue depth.
/// <para>
/// This is deliberately DISTINCT from the FFmpeg stall watchdog in
/// <c>Services/Sinks/FfmpegProcessHost.cs</c> (<c>StallThresholdMs</c>): that one fires only when data
/// is queued but the pipe stops draining. A dead feed leaves the queue EMPTY, which the stall
/// watchdog reads as idle — exactly the blind spot this watchdog closes.
/// </para>
/// <para>
/// Headless and allocation-free on the frame path: <see cref="NotifyVideoFrame"/> only exchanges a
/// <see cref="long"/> timestamp (no allocation, no locking), so it is safe to call from the media
/// thread once per frame. The clock is injected so the headless suite can drive time directly
/// (no real 5 s sleeps).
/// </para>
/// </summary>
public sealed class InputHealthWatchdog
{
    /// <summary>Default input-loss threshold: 5 s with no video frame while recording (ADR-0002).</summary>
    public const long DefaultThresholdMs = 5000;

    private readonly Func<long> _clock;
    private long _lastActivityMs;
    private volatile bool _armed;
    private volatile bool _lost;

    public InputHealthWatchdog(long thresholdMs = DefaultThresholdMs, Func<long>? clock = null)
    {
        ThresholdMs = thresholdMs > 0 ? thresholdMs : DefaultThresholdMs;
        _clock = clock ?? (static () => Environment.TickCount64);
    }

    /// <summary>Wall-clock window with no frame that counts as an input loss. Operator-configurable.</summary>
    public long ThresholdMs { get; set; }

    public bool IsArmed => _armed;

    public bool IsLost => _lost;

    /// <summary>
    /// Starts watching: activity is timed from now. Arming happens only while a take is in progress
    /// on a network source, so in-process sources (test signal, host capture) are never watched.
    /// </summary>
    public void Arm()
    {
        Interlocked.Exchange(ref _lastActivityMs, _clock());
        _lost = false;
        _armed = true;
    }

    public void Disarm()
    {
        _armed = false;
        _lost = false;
    }

    /// <summary>
    /// Called from the media thread for every received video frame. Allocation-free; no-op when
    /// disarmed. Restore is detected by the next <see cref="Poll"/>, keeping this hot path minimal.
    /// </summary>
    public void NotifyVideoFrame()
    {
        if (_armed)
        {
            Interlocked.Exchange(ref _lastActivityMs, _clock());
        }
    }

    /// <summary>
    /// Edge-detecting health check. Returns <see cref="InputHealthTransition.Lost"/> once when the
    /// frame-absence window is first exceeded, and <see cref="InputHealthTransition.Restored"/> once
    /// when frames become recent again; <see cref="InputHealthTransition.None"/> otherwise.
    /// </summary>
    public InputHealthTransition Poll()
    {
        if (!_armed)
        {
            return InputHealthTransition.None;
        }

        long sinceLastFrameMs = _clock() - Interlocked.Read(ref _lastActivityMs);

        if (!_lost && sinceLastFrameMs >= ThresholdMs)
        {
            _lost = true;
            return InputHealthTransition.Lost;
        }

        if (_lost && sinceLastFrameMs < ThresholdMs)
        {
            _lost = false;
            return InputHealthTransition.Restored;
        }

        return InputHealthTransition.None;
    }
}

/// <summary>
/// Deterministic exponential reconnect backoff (no jitter, so it is assertable in the headless
/// suite): 250 ms, 500 ms, 1 s, 2 s, 4 s, then capped at 5 s. Prevents a rapid-reconnect storm
/// (ADR-0002 decision 2) while still recovering a transient blip quickly.
/// </summary>
public static class ReconnectBackoff
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>Delay before the attempt with the given 1-based index.</summary>
    public static TimeSpan DelayFor(int attempt)
    {
        if (attempt < 1)
        {
            return TimeSpan.Zero;
        }

        double ms = InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return ms >= MaxDelay.TotalMilliseconds ? MaxDelay : TimeSpan.FromMilliseconds(ms);
    }
}

/// <summary>
/// Sequential segment naming for a multi-segment take (ADR-0002 decision 3). Segment 1 is the
/// take's base file; every later segment suffixes the stem with <c>_partNN</c> so an input-loss
/// gap is always an explicit, named discontinuity on disk and never an invisible stitch.
/// </summary>
public static class RecordingSegments
{
    public static string SegmentFilePath(string takeBasePath, int segmentIndex)
    {
        if (segmentIndex <= 1)
        {
            return takeBasePath;
        }

        string directory = Path.GetDirectoryName(takeBasePath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(takeBasePath);
        string extension = Path.GetExtension(takeBasePath);
        return Path.Combine(directory, $"{stem}_part{segmentIndex:00}{extension}");
    }
}
