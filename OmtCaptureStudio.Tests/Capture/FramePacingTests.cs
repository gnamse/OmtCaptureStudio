using System.Runtime.InteropServices;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Sinks;

namespace OmtCaptureStudio.Tests;

partial class Program
{
    static void TestFramePacing()
    {
        Console.WriteLine("\n[Step 0.705] Verifying rational frame rates and the absolute frame schedule...");

        RationalFrameRate ntsc = RationalFrameRate.FromDouble(59.94);
        Expect(ntsc.Numerator == 60000 && ntsc.Denominator == 1001,
            $"59.94 must be 60000/1001, got {ntsc}.");
        Expect(RationalFrameRate.FromDouble(29.97) == RationalFrameRate.Ntsc2997,
            "29.97 must be 30000/1001.");
        Expect(RationalFrameRate.FromDouble(23.976) == RationalFrameRate.Ntsc23976,
            "23.976 must be 24000/1001.");
        Expect(RationalFrameRate.FromDouble(30.0) == RationalFrameRate.FromInteger(30),
            "An integer double must stay N/1, not a reduced thousandths fraction.");
        Expect(RationalFrameRate.FromInteger(60).Numerator == 60
               && RationalFrameRate.FromInteger(60).Denominator == 1,
            "The historical integer setting 60 must remain 60/1.");

        RationalFrameRate drifted = RationalFrameRate.FromDouble(29.226);
        Expect(drifted != RationalFrameRate.Ntsc2997 && drifted != RationalFrameRate.FromInteger(30)
               && drifted != RationalFrameRate.FromInteger(29),
            $"A measured 29.226 must not snap to 29.97, 30, or 29; got {drifted}.");
        Expect(Math.Abs(drifted.FramesPerSecond - 29.226) < 0.001,
            $"The approximation of 29.226 drifted too far: {drifted.FramesPerSecond}.");

        RationalFrameRate storedInteger = RationalFrameRate.FromStored(legacyFramesPerSecond: 24, numerator: null, denominator: null);
        Expect(storedInteger == RationalFrameRate.FromInteger(24),
            "A legacy integer setting must migrate to N/1.");
        RationalFrameRate storedRatio = RationalFrameRate.FromStored(60, 60000, 1001);
        Expect(storedRatio == RationalFrameRate.Ntsc5994,
            "A stored ratio must win over a stale integer in the same file.");
        Expect(!RationalFrameRate.FromStored(null, null, null).IsValid,
            "A file that never set a rate must stay unset so the caller can apply 30/1.");

        const long frequency = 10_000_000;
        const long origin = 1_000_000;
        RationalFrameRate ntscRate = RationalFrameRate.Ntsc5994;
        long oneThousandAndOneSeconds = FrameSchedule.Deadline(origin, 60000, ntscRate, frequency) - origin;
        Expect(oneThousandAndOneSeconds == frequency * 1001,
            $"60000 frames of 60000/1001 must be exactly 1001 seconds. Got {oneThousandAndOneSeconds} ticks, expected {frequency * 1001}.");

        long oneSecondAt30 = FrameSchedule.Deadline(origin, 30, RationalFrameRate.FromInteger(30), frequency) - origin;
        Expect(oneSecondAt30 == frequency,
            $"30 frames at 30/1 must be exactly one second. Got {oneSecondAt30} ticks.");

        long previous = origin;
        long floorPeriod = frequency * 1001 / 60000;
        for (long n = 1; n <= 5000; n++)
        {
            long deadline = FrameSchedule.Deadline(origin, n, ntscRate, frequency);
            long gap = deadline - previous;
            if (gap != floorPeriod && gap != floorPeriod + 1)
            {
                throw new Exception($"NTSC gap at frame {n} was {gap} ticks; expected {floorPeriod} or {floorPeriod + 1}.");
            }

            if (FrameSchedule.IndexAt(origin, deadline, ntscRate, frequency) != n)
            {
                throw new Exception($"IndexAt(Deadline({n})) must be {n} so the truncated deadline still names that frame.");
            }

            previous = deadline;
        }

        RationalFrameRate thirty = RationalFrameRate.FromInteger(30);
        for (long n = 1; n <= 300; n++)
        {
            long deadline = FrameSchedule.Deadline(0, n, thirty, frequency);
            if (FrameSchedule.IndexAt(0, deadline, thirty, frequency) != n)
            {
                throw new Exception($"30 fps IndexAt(Deadline({n})) was not {n}.");
            }
        }

        long late = FrameSchedule.Deadline(0, 7, thirty, frequency);
        FrameScheduleDecision skipped = FrameSchedule.Decide(0, late, nextIndex: 5, thirty, frequency);
        Expect(skipped.FrameIndex == 7 && skipped.SkippedFrames == 2 && skipped.WaitTicks == 0,
            $"Late by two frames must skip ahead once, not burst. Got index {skipped.FrameIndex}, skipped {skipped.SkippedFrames}, wait {skipped.WaitTicks}.");

        long halfLate = (FrameSchedule.Deadline(0, 5, thirty, frequency) + FrameSchedule.Deadline(0, 6, thirty, frequency)) / 2;
        FrameScheduleDecision hold = FrameSchedule.Decide(0, halfLate, nextIndex: 5, thirty, frequency);
        Expect(hold.FrameIndex == 5 && hold.SkippedFrames == 0 && hold.WaitTicks == 0,
            "Less than one frame late must emit the pending frame once, with no wait and no skip.");

        long earlyNow = FrameSchedule.Deadline(0, 4, thirty, frequency);
        FrameScheduleDecision early = FrameSchedule.Decide(0, earlyNow, nextIndex: 5, thirty, frequency);
        long expectedWait = FrameSchedule.Deadline(0, 5, thirty, frequency) - earlyNow;
        Expect(early.FrameIndex == 5 && early.SkippedFrames == 0 && early.WaitTicks == expectedWait,
            $"An early clock must wait until the absolute deadline. Got wait {early.WaitTicks}, expected {expectedWait}.");

        var clock = new ManualFrameClock { Frequency = frequency, Now = origin };
        using (var pacer = new FramePacer(thirty, clock))
        {
            for (int i = 0; i <= 30; i++)
            {
                long index = pacer.WaitNext();
                if (index != i)
                {
                    throw new Exception($"Pacer emitted frame {index} where frame {i} was due.");
                }
            }
        }

        Expect(clock.Now == origin + frequency,
            $"After frames 0..30 at 30 fps the injected clock must sit exactly one second past the origin. Now={clock.Now}, expected {origin + frequency}.");

        clock.Now = origin;
        using (var skipping = new FramePacer(thirty, clock))
        {
            Expect(skipping.WaitNext() == 0, "The first paced frame is immediate.");
            clock.Now = FrameSchedule.Deadline(origin, 10, thirty, frequency);
            long jumped = skipping.WaitNext();
            Expect(jumped == 10, $"A clock that jumps ten frames must skip to 10, not burst 1..10. Got {jumped}.");
            Expect(clock.Now == FrameSchedule.Deadline(origin, 10, thirty, frequency),
                "A late skip must not wait.");
        }

        long sampleSum = 0;
        RationalFrameRate rate = RationalFrameRate.Ntsc5994;
        int maxSamples = rate.MaxSamplesPerFrame(48000);
        for (long frame = 0; frame < 60000; frame++)
        {
            int samples = rate.SamplesForFrame(frame, 48000);
            if (samples < 800 || samples > 801 || samples > maxSamples)
            {
                throw new Exception($"60000/1001 frame {frame} produced {samples} samples; expected 800 or 801, max {maxSamples}.");
            }

            sampleSum += samples;
        }

        Expect(sampleSum == 48000L * 1001,
            $"60000 NTSC frames must carry exactly 48000*1001 samples. Got {sampleSum}.");
        Expect(RationalFrameRate.FromInteger(30).SamplesForFrame(0, 48000) == 1600
               && RationalFrameRate.FromInteger(60).SamplesForFrame(7, 48000) == 800,
            "Integer rates must keep the historical samples-per-frame (1600 at 30, 800 at 60).");

        var past = new StopwatchFrameClock();
        long already = past.GetTimestamp() - past.Frequency;
        past.WaitUntil(already);
        past.Dispose();
        past.Dispose();

        string root = Path.Combine(Path.GetTempPath(), "omt-fps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string legacyPath = Path.Combine(root, "legacy.json");
            File.WriteAllText(legacyPath, """
                {
                  "Version": 1,
                  "BroadcastFrameRate": 24
                }
                """);
            var legacyStore = new SettingsStore(legacyPath);
            OperatorSettings legacy = legacyStore.Load();
            Expect(legacyStore.LastOutcome == SettingsLoadOutcome.Loaded, "A version 1 file with an integer fps must still load.");
            Expect(legacy.BroadcastFrameRateNumerator == 24 && legacy.BroadcastFrameRateDenominator == 1,
                $"Integer 24 must migrate to 24/1. Got {legacy.BroadcastFrameRateNumerator}/{legacy.BroadcastFrameRateDenominator}.");
            Expect(SettingsStore.ReadBroadcastFrameRate(legacy) == RationalFrameRate.FromInteger(24),
                "The reader must arm the migrated integer.");

            string ratioPath = Path.Combine(root, "ratio.json");
            File.WriteAllText(ratioPath, """
                {
                  "Version": 1,
                  "BroadcastFrameRate": 60,
                  "BroadcastFrameRateNumerator": 60000,
                  "BroadcastFrameRateDenominator": 1001
                }
                """);
            OperatorSettings ratio = new SettingsStore(ratioPath).Load();
            Expect(ratio.BroadcastFrameRateNumerator == 60000 && ratio.BroadcastFrameRateDenominator == 1001,
                "The ratio must win over a stale integer 60.");
            Expect(ratio.BroadcastFrameRate is null,
                "A fractional rate must not keep a rounded integer that a later load could prefer.");

            string roundTripPath = Path.Combine(root, "round.json");
            var roundStore = new SettingsStore(roundTripPath);
            Expect(roundStore.Save(SettingsStore.FromConfig(
                    new RecordingConfig(),
                    "Custom",
                    broadcastFrameRate: RationalFrameRate.Ntsc5994)),
                "Saving a fractional broadcast rate must succeed.");
            OperatorSettings round = new SettingsStore(roundTripPath).Load();
            Expect(SettingsStore.ReadBroadcastFrameRate(round) == RationalFrameRate.Ntsc5994,
                "60000/1001 must survive a save/load round trip.");
            string json = File.ReadAllText(roundTripPath);
            Expect(json.Contains("60000", StringComparison.Ordinal) && json.Contains("1001", StringComparison.Ordinal),
                "The file must store the ratio, not a rounded double.");

            OperatorSettings absent = new SettingsStore(Path.Combine(root, "missing.json")).Load();
            Expect(SettingsStore.ReadBroadcastFrameRate(absent) == RationalFrameRate.FromInteger(30),
                "A file with no frame rate must keep the historical 30 fps default.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        var argFormat = new StreamFormat
        {
            Width = 1920,
            Height = 1080,
            FrameRate = 59.94,
            FrameRateNumerator = 60000,
            FrameRateDenominator = 1001,
            SampleRate = 48000,
            Channels = 2
        };
        string[] argv = FfmpegArguments.BuildArgumentList(
            new RecordingConfig(),
            argFormat,
            "out.mp4",
            "pipeV",
            null,
            VideoEncoderChoice.CpuX264);
        Expect(argv.Contains("60000/1001"),
            "ffmpeg -r must receive the exact ratio, not a rounded decimal.");
        Expect(FfmpegArguments.FormatRate(new StreamFormat { FrameRate = 59.94 }) == "59.94",
            "A legacy double with no ratio must stay the invariant-culture decimal.");
        Expect(new FrameRateMeter().MeasuredRational.IsValid == false,
            "A meter with no frames must not invent a rate.");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Rational rates, absolute deadlines, skip-ahead, sample distribution, and settings migration verified!");
        Console.ResetColor();

        TestRecordingFrameConform();
        TestAudioSendByElapsedTime();
    }

    /// <summary>
    /// ADR-0014: audio owed by one sender pass is every sample up to the emitted frame, so a
    /// skip-ahead still carries the audio for the pictures it did not draw. Injected counts only.
    /// </summary>
    static void TestAudioSendByElapsedTime()
    {
        Console.WriteLine("\n[Step 0.705c] Verifying audio-by-elapsed-time sender math...");

        RationalFrameRate thirty = RationalFrameRate.FromInteger(30);
        Expect(AudioSendSchedule.SamplesDue(thirty, 48000, emittedFrameIndexInclusive: 0, samplesAlreadySent: 0) == 1600,
            "The first 30 fps frame owes 1600 samples at 48 kHz.");

        long sent = 0;
        for (long frame = 0; frame < 30; frame++)
        {
            int due = AudioSendSchedule.SamplesDue(thirty, 48000, frame, sent);
            Expect(due == 1600, $"Frame {frame} on time must owe exactly 1600 samples, got {due}.");
            sent += due;
        }

        Expect(sent == 48000, $"Thirty frames must be exactly one second of audio. Got {sent}.");

        // The pacer emitted frame 4, then the next pass jumped to frame 10.
        long afterFour = thirty.SamplesUntil(5, 48000);
        int skipped = AudioSendSchedule.SamplesDue(thirty, 48000, emittedFrameIndexInclusive: 10, samplesAlreadySent: afterFour);
        int expectedSpan = 0;
        for (long frame = 5; frame <= 10; frame++)
        {
            expectedSpan += thirty.SamplesForFrame(frame, 48000);
        }

        Expect(skipped == expectedSpan,
            $"A skip from frame 5 through 10 must owe those frames' samples ({expectedSpan}), got {skipped}.");
        Expect(skipped > thirty.SamplesForFrame(10, 48000),
            "A skip must send more than the single frame that was drawn.");

        RationalFrameRate ntsc = RationalFrameRate.Ntsc5994;
        long ntscSent = 0;
        for (long frame = 0; frame < 60000; frame++)
        {
            int due = AudioSendSchedule.SamplesDue(ntsc, 48000, frame, ntscSent);
            if (due != ntsc.SamplesForFrame(frame, 48000))
            {
                throw new Exception($"NTSC frame {frame} owed {due}, expected {ntsc.SamplesForFrame(frame, 48000)}.");
            }

            ntscSent += due;
        }

        Expect(ntscSent == 48000L * 1001,
            $"Sending one NTSC frame at a time must still total 48000*1001 samples. Got {ntscSent}.");

        // One jump across the whole NTSC second-and-a-bit owes the same total.
        int jumped = AudioSendSchedule.SamplesDue(ntsc, 48000, emittedFrameIndexInclusive: 59999, samplesAlreadySent: 0);
        Expect(jumped == 48000 * 1001,
            $"A single pass that lands on frame 59999 owes the whole NTSC span. Got {jumped}.");

        Expect(AudioSendSchedule.SamplesDue(thirty, 48000, 3, samplesAlreadySent: thirty.SamplesUntil(4, 48000)) == 0,
            "Nothing is owed when the samples for that frame were already sent.");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Sender audio follows elapsed schedule time, including frames the pacer skipped.");
        Console.ResetColor();
    }

    /// <summary>
    /// ADR-0014: the recorder's duplicate/drop decision, driven by an injected clock. No sleeps.
    /// A 24.5 fps arrival stream into a 30/1 file must finish within one frame of wall time.
    /// </summary>
    static void TestRecordingFrameConform()
    {
        Console.WriteLine("\n[Step 0.705b] Verifying recording CFR conform (duplicate / drop, no sleeps)...");

        const long frequency = 10_000_000;
        RationalFrameRate thirty = RationalFrameRate.FromInteger(30);

        RecordingConformDecision first = RecordingFrameConform.Decide(0, dueIndex: 0, frameArrived: true);
        Expect(first.EmitCount == 1 && first.DuplicateCount == 0 && first.DropCount == 0,
            $"The opening picture is one emit and not a duplicate. Got emit {first.EmitCount} dup {first.DuplicateCount} drop {first.DropCount}.");

        long early = FrameSchedule.Deadline(0, 1, thirty, frequency) - 1;
        Expect(RecordingFrameConform.DueIndex(0, early, thirty, frequency) == 0,
            "A timestamp before the next deadline is still slot 0.");
        RecordingConformDecision dropped = RecordingFrameConform.Decide(1, dueIndex: 0, frameArrived: true);
        Expect(dropped.EmitCount == 0 && dropped.DropCount == 1,
            "An arrival before the next slot is a drop, not a second picture.");

        long atOne = FrameSchedule.Deadline(0, 1, thirty, frequency);
        Expect(RecordingFrameConform.DueIndex(0, atOne, thirty, frequency) == 1, "IndexAt(Deadline(1)) is 1.");
        RecordingConformDecision onTime = RecordingFrameConform.Decide(1, dueIndex: 1, frameArrived: true);
        Expect(onTime.EmitCount == 1 && onTime.DuplicateCount == 0 && onTime.DropCount == 0,
            "The picture that lands on the next deadline is written once.");

        RecordingConformDecision behind = RecordingFrameConform.Decide(2, dueIndex: 10, frameArrived: true);
        Expect(behind.EmitCount == 9 && behind.DuplicateCount == 8 && behind.DropCount == 0,
            $"Eight missed slots are duplicates and the new picture fills the current one. Got emit {behind.EmitCount} dup {behind.DuplicateCount}.");

        RecordingConformDecision quiet = RecordingFrameConform.Decide(2, dueIndex: 10, frameArrived: false);
        Expect(quiet.EmitCount == 9 && quiet.DuplicateCount == 9,
            "A catch-up with no new picture is entirely duplicates.");
        Expect(RecordingFrameConform.Decide(0, dueIndex: 4, frameArrived: false).EmitCount == 0,
            "There is nothing to repeat before the first picture.");

        // 10 seconds of a 24.5 fps sender into a 30/1 file. Fully caught up at each arrival,
        // the written count stays within one frame of elapsed * rate.
        long written = 0;
        long drops = 0;
        long dups = 0;
        long origin = 1_000_000;
        double inputFps = 24.5;
        int arrivals = (int)(10 * inputFps);
        for (int n = 0; n < arrivals; n++)
        {
            long now = origin + (long)(n * frequency / inputFps);
            long due = RecordingFrameConform.DueIndex(origin, now, thirty, frequency);
            RecordingConformDecision step = RecordingFrameConform.Decide(written, due, frameArrived: true);
            written += step.EmitCount;
            dups += step.DuplicateCount;
            drops += step.DropCount;
            double elapsed = (now - origin) / (double)frequency;
            double target = elapsed * thirty.FramesPerSecond;
            if (Math.Abs(written - (target + 1)) > 1.0)
            {
                throw new Exception(
                    $"At arrival {n} ({elapsed:F3}s) written {written} is more than one frame from wall*rate+1 ({target + 1:F2}).");
            }
        }

        Expect(dups > 0, "A 24.5 fps sender into 30/1 must duplicate, or the file runs short.");
        Expect(drops == 0, "A slower-than-rate sender must not drop pictures.");
        long end = origin + (long)((arrivals - 1) * frequency / inputFps);
        double endElapsed = (end - origin) / (double)frequency;
        double endTarget = endElapsed * 30.0 + 1.0;
        Expect(Math.Abs(written - endTarget) <= 1.0,
            $"After 10s of 24.5 fps, written {written} must be within one frame of {endTarget:F2}.");
        Console.WriteLine($" -> 24.5 into 30/1 over {endElapsed:F2}s: written {written}, dups {dups}, drops {drops}.");

        // Faster than the file rate: extras are drops, and the file does not run long.
        written = 0;
        drops = 0;
        for (int n = 0; n < 600; n++)
        {
            long now = origin + (long)(n * frequency / 60.0);
            long due = RecordingFrameConform.DueIndex(origin, now, thirty, frequency);
            RecordingConformDecision step = RecordingFrameConform.Decide(written, due, frameArrived: true);
            written += step.EmitCount;
            drops += step.DropCount;
        }

        Expect(drops > 200, $"A 60 fps sender into 30/1 must drop about half the arrivals; dropped {drops}.");
        double fastElapsed = 599 * (1.0 / 60.0);
        Expect(Math.Abs(written - (fastElapsed * 30.0 + 1.0)) <= 1.0,
            $"A fast sender must not make the file longer than wall time. Written {written}.");

        // The arm applies the same decision. Two arrivals in one slot: one write. A late jump: duplicates.
        var sink = new SignalRecordingSink();
        long clock = 5_000_000;
        var arm = new RecordingArm(sink, mediaTimestamp: () => clock, mediaFrequency: frequency);
        var config = new RecordingConfig { OutputDirectory = Path.GetTempPath(), FilenamePrefix = "Conform" };
        var format = new StreamFormat
        {
            Width = 16,
            Height = 16,
            FrameRate = 30,
            FrameRateNumerator = 30,
            FrameRateDenominator = 1,
            SampleRate = 48000,
            Channels = 2
        };
        if (!arm.Arm(config, format))
        {
            Console.WriteLine(" -> [SKIP] storage preflight refused the temp volume; the arm wiring check is skipped.");
        }
        else
        {
            var frame = Marshal.AllocHGlobal(16);
            try
            {
                arm.WriteConformedVideoFrame(frame, 16);
                arm.WriteConformedVideoFrame(frame, 16);
                Expect(sink.VideoFramesReceived == 1, $"Two arrivals at the origin must write one picture; wrote {sink.VideoFramesReceived}.");
                Expect(arm.PaceFramesDropped == 1, "The second arrival in the same slot is a pace drop.");

                clock = FrameSchedule.Deadline(5_000_000, 8, thirty, frequency);
                arm.WriteConformedVideoFrame(frame, 16);
                Expect(arm.VideoFramesDuplicated >= 7,
                    $"A jump of eight frames must duplicate the gap. Dups {arm.VideoFramesDuplicated}.");
                Expect(sink.VideoFramesReceived == 1 + 8,
                    $"Written pictures must be the opening frame plus eight. Got {sink.VideoFramesReceived}.");
            }
            finally
            {
                Marshal.FreeHGlobal(frame);
                arm.Dispose();
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Recording CFR conform: drops when early, duplicates when late, stays within one frame of wall time.");
        Console.ResetColor();
    }

    sealed class ManualFrameClock : IFrameClock
    {
        public long Frequency { get; init; } = 10_000_000;
        public long Now;

        public long GetTimestamp() => Now;

        public void WaitUntil(long deadlineTimestamp)
        {
            if (deadlineTimestamp > Now) Now = deadlineTimestamp;
        }
    }
}
