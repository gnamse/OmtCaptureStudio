using System.Diagnostics;
using OmtCaptureStudio.Interop;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Clock the pacer reads and waits on. Production uses <see cref="StopwatchFrameClock"/>. Tests
/// pass a clock that advances without sleeping.
/// </summary>
public interface IFrameClock
{
    long Frequency { get; }

    long GetTimestamp();

    /// <summary>Block until <see cref="GetTimestamp"/> is at least <paramref name="deadlineTimestamp"/>.</summary>
    void WaitUntil(long deadlineTimestamp);
}

/// <summary>
/// Stopwatch clock. Coarse waits use a Windows high-resolution waitable timer when one can be
/// created; the last millisecond is a yield, then a short spin, so the wake-up quantisation of
/// <see cref="Thread.Sleep(int)"/> does not land a frame early or late.
/// One instance is owned by one pacing thread. It is not safe to wait on it from two threads.
/// </summary>
public sealed class StopwatchFrameClock : IFrameClock, IDisposable
{
    private IntPtr _timer;
    private int _timerUnavailable;
    private int _disposed;

    public long Frequency => Stopwatch.Frequency;

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public void WaitUntil(long deadlineTimestamp)
    {
        long frequency = Stopwatch.Frequency;
        if (frequency <= 0) return;

        long oneMillisecond = Math.Max(1, frequency / 1000);
        long spinFloor = Math.Max(1, frequency / 20_000);

        while (true)
        {
            long remaining = deadlineTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;

            if (remaining > oneMillisecond)
            {
                long coarse = remaining - oneMillisecond;
                if (!TryCoarseWait(coarse, frequency))
                {
                    int milliseconds = (int)Math.Min(int.MaxValue, coarse * 1000 / frequency);
                    if (milliseconds > 0) Thread.Sleep(milliseconds);
                    else Thread.Yield();
                }
            }
            else if (remaining > spinFloor)
            {
                Thread.Yield();
            }
            else
            {
                Thread.SpinWait(20);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IntPtr timer = Interlocked.Exchange(ref _timer, IntPtr.Zero);
        if (timer != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(timer);
        }
    }

    private bool TryCoarseWait(long ticks, long frequency)
    {
        if (!OperatingSystem.IsWindows() || Volatile.Read(ref _timerUnavailable) != 0 || ticks <= 0)
        {
            return false;
        }

        Int128 hundredNanoseconds128 = (Int128)ticks * 10_000_000 / frequency;
        if (hundredNanoseconds128 <= 0 || hundredNanoseconds128 > long.MaxValue) return false;
        long hundredNanoseconds = (long)hundredNanoseconds128;

        IntPtr timer = _timer;
        if (timer == IntPtr.Zero)
        {
            uint access = NativeMethods.TimerModifyState | NativeMethods.Synchronize;
            timer = NativeMethods.CreateWaitableTimerExW(
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.CreateWaitableTimerHighResolution,
                access);
            if (timer == IntPtr.Zero)
            {
                timer = NativeMethods.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0, access);
            }

            if (timer == IntPtr.Zero)
            {
                Volatile.Write(ref _timerUnavailable, 1);
                return false;
            }

            _timer = timer;
        }

        long due = -hundredNanoseconds;
        if (NativeMethods.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, 0) == 0)
        {
            return false;
        }

        NativeMethods.WaitForSingleObject(timer, NativeMethods.InfiniteWait);
        return true;
    }
}

/// <summary>
/// Blocks a capture/generator loop on an absolute frame schedule. The first call emits frame 0
/// immediately and anchors the origin. Later calls wait until <c>origin + n * period</c>, or skip
/// ahead when more than one frame late.
/// </summary>
public sealed class FramePacer : IDisposable
{
    private readonly RationalFrameRate _rate;
    private readonly IFrameClock _clock;
    private readonly bool _ownsClock;
    private long _origin;
    private long _nextIndex;
    private bool _armed;
    private int _disposed;

    public FramePacer(RationalFrameRate rate, IFrameClock? clock = null)
    {
        _rate = rate.IsValid ? rate : RationalFrameRate.FromInteger(30);
        if (clock is null)
        {
            _clock = new StopwatchFrameClock();
            _ownsClock = true;
        }
        else
        {
            _clock = clock;
        }
    }

    public RationalFrameRate Rate => _rate;

    /// <summary>The frame index to emit now. Blocks until that frame's deadline when the clock is early.</summary>
    public long WaitNext()
    {
        long now = _clock.GetTimestamp();
        if (!_armed)
        {
            _origin = now;
            _armed = true;
            _nextIndex = 1;
            return 0;
        }

        FrameScheduleDecision decision = FrameSchedule.Decide(_origin, now, _nextIndex, _rate, _clock.Frequency);
        long deadline = FrameSchedule.Deadline(_origin, decision.FrameIndex, _rate, _clock.Frequency);
        if (deadline > _clock.GetTimestamp())
        {
            _clock.WaitUntil(deadline);
        }

        _nextIndex = decision.FrameIndex + 1;
        return decision.FrameIndex;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsClock && _clock is IDisposable owned)
        {
            owned.Dispose();
        }
    }
}
