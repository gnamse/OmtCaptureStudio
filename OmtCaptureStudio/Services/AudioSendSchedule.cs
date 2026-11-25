using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// How many audio samples a desktop-sender loop pass owes the wire. The count is the
/// samples of every frame up to and including the one just emitted, minus what was already
/// sent. A <see cref="FrameSchedule"/> skip therefore still ships the audio for the pictures
/// it did not draw (ADR-0014).
/// </summary>
public static class AudioSendSchedule
{
    public static int SamplesDue(
        RationalFrameRate rate,
        int sampleRate,
        long emittedFrameIndexInclusive,
        long samplesAlreadySent)
    {
        if (!rate.IsValid || sampleRate <= 0 || emittedFrameIndexInclusive < 0) return 0;

        long due = rate.SamplesUntil(emittedFrameIndexInclusive + 1, sampleRate);
        long delta = due - samplesAlreadySent;
        if (delta <= 0) return 0;
        if (delta > int.MaxValue) return int.MaxValue;
        return (int)delta;
    }
}
