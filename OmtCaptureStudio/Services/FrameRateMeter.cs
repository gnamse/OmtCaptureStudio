using System;
using System.Diagnostics;
using System.Threading;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Measures the TRUE video delivery rate (frames/second) by timing frame arrivals
/// with a Stopwatch on the media thread.
///
/// The source-reported "FrameRate" is a nominal maximum (vMix "Maximum Frame Rate 30"
/// actually delivers ~29.2 fps). If FFmpeg is stamped with that nominal rate while the
/// source delivers a different true rate, the video track drifts against the audio and
/// players periodically re-sync ("skipping"). This meter feeds the true rate to FFmpeg's
/// <c>-r</c> and to the audio resampler's reference, so neither stream is pitch-shifted.
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
