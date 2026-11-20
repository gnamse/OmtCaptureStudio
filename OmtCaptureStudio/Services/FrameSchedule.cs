using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// One pacing decision. <see cref="WaitTicks"/> is how long the clock should block before emitting
/// <see cref="FrameIndex"/>. <see cref="SkippedFrames"/> is non-zero when the producer was more than
/// one frame late and the schedule jumped ahead instead of bursting the missed frames.
/// </summary>
public readonly record struct FrameScheduleDecision(long FrameIndex, long SkippedFrames, long WaitTicks);

/// <summary>
/// Absolute frame schedule. The deadline of frame <c>n</c> is <c>origin + n * period</c>, computed
/// in integer ticks as <c>origin + floor(n * frequency * denominator / numerator)</c>. Summing
/// rounded sleeps is deliberately not used: that error accumulates over a multi-hour take.
/// </summary>
public static class FrameSchedule
{
    public static long Deadline(long originTick, long frameIndex, RationalFrameRate rate, long frequency)
    {
        if (frameIndex <= 0 || !rate.IsValid || frequency <= 0) return originTick;
        Int128 ticks = (Int128)frameIndex * frequency * rate.Denominator / rate.Numerator;
        return originTick + (long)ticks;
    }

    /// <summary>
    /// The latest frame whose deadline is at or before <paramref name="nowTick"/>. Inverts
    /// <see cref="Deadline"/>, including the floor truncation, so <c>IndexAt(Deadline(n)) == n</c>.
    /// </summary>
    public static long IndexAt(long originTick, long nowTick, RationalFrameRate rate, long frequency)
    {
        if (nowTick <= originTick || !rate.IsValid || frequency <= 0) return 0;

        Int128 elapsed = nowTick - originTick;
        Int128 numerator = rate.Numerator;
        Int128 denominator = (Int128)frequency * rate.Denominator;
        // Largest n with floor(n * frequency * denominator / numerator) <= elapsed.
        Int128 index = ((elapsed + 1) * numerator - 1) / denominator;
        if (index <= 0) return 0;
        if (index > long.MaxValue) return long.MaxValue;
        return (long)index;
    }

    /// <summary>
    /// Decides the next frame to emit. When <paramref name="nowTick"/> has reached the deadline of
    /// the frame after <paramref name="nextIndex"/>, the producer is a full frame late (or more)
    /// and the schedule skips ahead to the frame that is due now. A lateness under one frame emits
    /// the pending frame immediately, once — it does not schedule a catch-up burst.
    /// </summary>
    public static FrameScheduleDecision Decide(
        long originTick,
        long nowTick,
        long nextIndex,
        RationalFrameRate rate,
        long frequency)
    {
        if (nextIndex < 0) nextIndex = 0;
        if (!rate.IsValid || frequency <= 0)
        {
            return new FrameScheduleDecision(nextIndex, 0, 0);
        }

        long followingDeadline = Deadline(originTick, nextIndex + 1, rate, frequency);
        if (nowTick >= followingDeadline)
        {
            long index = IndexAt(originTick, nowTick, rate, frequency);
            if (index < nextIndex) index = nextIndex;
            return new FrameScheduleDecision(index, index - nextIndex, 0);
        }

        long deadline = Deadline(originTick, nextIndex, rate, frequency);
        long wait = deadline > nowTick ? deadline - nowTick : 0;
        return new FrameScheduleDecision(nextIndex, 0, wait);
    }
}
