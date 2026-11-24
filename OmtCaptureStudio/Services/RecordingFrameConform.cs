using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// How many pictures to hand the encoder for one decision.
/// <see cref="DuplicateCount"/> of <see cref="EmitCount"/> repeat the previous picture.
/// <see cref="DropCount"/> is 1 when this arrival is early and the current slot is already filled.
/// </summary>
public readonly record struct RecordingConformDecision(int EmitCount, int DuplicateCount, int DropCount);

/// <summary>
/// CFR conform math for the recording pipe. FFmpeg stamps rawvideo as frame-index / rate, so the
/// file's duration is the written count divided by the output rate. The output rate is the stream's
/// announced ratio (30/1, 30000/1001, …), never a rate measured at arm time.
///
/// <para>The target written count at <paramref name="dueIndex"/> is <c>dueIndex + 1</c>: frame 0 is
/// due at the origin (<see cref="FrameSchedule.IndexAt"/> returns 0 there) and
/// <c>IndexAt(Deadline(n)) == n</c>, so at the deadline of frame n the file should contain n+1
/// pictures. An arrival that shows up before the next deadline is dropped. Slots that passed with
/// no new picture are filled by duplicating the last one, which is what keeps a slow sender from
/// making the file run short of wall time.</para>
///
/// <para>A stop does not call this for a gap with no arrival. Filling a multi-second hole at
/// finalize would stitch input-loss dead air into the file (ADR-0002, DEF-01). While frames are
/// arriving, each arrival catches the file up to "now".</para>
/// </summary>
public static class RecordingFrameConform
{
    public static long DueIndex(long originTick, long nowTick, RationalFrameRate rate, long frequency)
        => FrameSchedule.IndexAt(originTick, nowTick, rate, frequency);

    public static RecordingConformDecision Decide(long framesWritten, long dueIndex, bool frameArrived)
    {
        if (framesWritten < 0) framesWritten = 0;
        if (dueIndex < 0) dueIndex = 0;

        long target = dueIndex + 1;
        if (framesWritten >= target)
        {
            return frameArrived
                ? new RecordingConformDecision(0, 0, 1)
                : default;
        }

        if (!frameArrived && framesWritten == 0)
        {
            return default;
        }

        long emit = target - framesWritten;
        if (emit > int.MaxValue) emit = int.MaxValue;
        long duplicates = frameArrived ? emit - 1 : emit;
        if (duplicates < 0) duplicates = 0;
        return new RecordingConformDecision((int)emit, (int)duplicates, 0);
    }
}
