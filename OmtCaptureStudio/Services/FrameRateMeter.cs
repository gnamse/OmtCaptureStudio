using System;
using System.Diagnostics;
using System.Threading;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Measures the TRUE video delivery rate (frames/second) by timing frame arrivals
/// with a Stopwatch on the media thread.
///
/// The source-reported "FrameRate" is a nominal maximum (vMix "Maximum Frame Rate 30"
/// actually delivers ~29.2 fps). That measurement is a diagnostic — the sidecar and the
/// arm log — and is NOT the file's output rate. Stamping <c>-r</c> with the average since
/// Connect freezes a short-term count into a CFR timeline and the file runs short of wall
/// time (ADR-0014). The recorder conforms pictures to the announced ratio instead.
///
/// Cross-thread contract: <see cref="OnVideoFrame"/> runs on the media thread, while
/// <see cref="Fps"/>/<see cref="HasReliableEstimate"/> may be read on the UI thread at
/// recording start. All fields use Interlocked/Volatile for visibility; a slightly stale
/// estimate is harmless because the audio resampler continuously corrects residual drift.
/// </summary>
public sealed class FrameRateMeter
{
    private const int MinFramesForReliable = 30; // ~1s at 30 fps

    private long _frames;
    private long _startTick;

    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>Frames/second measured since <see cref="Reset"/>, or 0 before the first frame.</summary>
    public double Fps
    {
        get
        {
            long frames = Interlocked.Read(ref _frames);
            long start = Volatile.Read(ref _startTick);
            if (frames < 2 || start == 0) return 0.0;
            double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
            return elapsed > 0.001 ? frames / elapsed : 0.0;
        }
    }

    public bool HasReliableEstimate => Interlocked.Read(ref _frames) >= MinFramesForReliable && Fps > 1.0;

    /// <summary>
    /// The measured rate as a ratio. Known broadcast fractions are recovered (a delivery of ~59.94
    /// becomes 60000/1001) and a drifted rate is NOT collapsed to a nearby integer.
    /// </summary>
    public RationalFrameRate MeasuredRational => RationalFrameRate.FromDouble(Fps);

    public void OnVideoFrame()
    {
        if (Volatile.Read(ref _startTick) == 0)
        {
            Volatile.Write(ref _startTick, Stopwatch.GetTimestamp());
        }
        Interlocked.Increment(ref _frames);
    }

    public void Reset()
    {
        Volatile.Write(ref _startTick, 0);
        Interlocked.Exchange(ref _frames, 0);
    }
}
