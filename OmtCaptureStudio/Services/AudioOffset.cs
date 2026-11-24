namespace OmtCaptureStudio.Services;

/// <summary>
/// One step of a manual A/V offset applied to already-resampled audio (ADR-0014).
/// Positive <see cref="SilenceFrames"/> delays the audio (the same effect as a positive
/// ffmpeg <c>-itsoffset</c> on the audio input). Negative debt drops samples so the audio
/// moves earlier. The resampler does not see this shift, so a large offset cannot pin its clamp.
/// </summary>
public readonly record struct AudioOffsetStep(int SilenceFrames, int SkipFrames, int KeepFrames, int Remaining);

public static class AudioOffset
{
    public const int MaxMilliseconds = 120_000;

    public static int FramesFor(int milliseconds, int sampleRate)
    {
        if (sampleRate <= 0 || milliseconds == 0) return 0;
        int ms = Math.Clamp(milliseconds, -MaxMilliseconds, MaxMilliseconds);
        long frames = (long)ms * sampleRate / 1000;
        if (frames > int.MaxValue) return int.MaxValue;
        if (frames < int.MinValue) return int.MinValue;
        return (int)frames;
    }

    public static AudioOffsetStep Step(int remainingFrames, int availableFrames)
    {
        if (availableFrames < 0) availableFrames = 0;
        if (remainingFrames > 0)
        {
            return new AudioOffsetStep(remainingFrames, 0, availableFrames, 0);
        }

        if (remainingFrames < 0)
        {
            int drop = Math.Min(availableFrames, -remainingFrames);
            return new AudioOffsetStep(0, drop, availableFrames - drop, remainingFrames + drop);
        }

        return new AudioOffsetStep(0, 0, availableFrames, 0);
    }
}
