using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Xml.Linq;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Audio;
using OmtCaptureStudio.Services.Sinks;
using OmtCaptureStudio.Services.Sources;
using OmtCaptureStudio.Services.Forensics;
using OmtCaptureStudio.ViewModels;

namespace OmtCaptureStudio.Tests;

partial class Program
{
    static void TestLogLocationAndReadOnlyInstall()
    {
        Console.WriteLine("\n[Step 0.694] Verifying LogLocation (one ladder) + the read-only-install crash link...");

        string root = Path.Combine(Path.GetTempPath(), "omt-logloc-" + Guid.NewGuid().ToString("N"));
        string primary = Path.Combine(root, "app");
        string fallback = Path.Combine(root, "localappdata");
        Directory.CreateDirectory(primary);
        Directory.CreateDirectory(fallback);

        try
        {
            string primaryLogs = Path.Combine(primary, "logs");
            string primaryDumps = Path.Combine(primary, "crashdumps");

            // ---- 1. THE LADDER AS PURE POLICY (injected probe) ----------------------------------
            LogLocationResult writable = LogLocation.Resolve(primaryLogs, primaryDumps, fallback, _ => true);
            Expect(!writable.UsingFallback,
                "A writable primary must be used as-is - the portable ZIP keeps its own logs and forensics.");
            Expect(writable.LogDirectory == primaryLogs && writable.DumpDirectory == primaryDumps,
                "A writable primary must place logs AND dumps in the application directory.");

            LogLocationResult readOnly = LogLocation.Resolve(primaryLogs, primaryDumps, fallback, _ => false);
            Expect(readOnly.UsingFallback, "A read-only primary must redirect.");
            Expect(readOnly.LogDirectory == Path.Combine(fallback, "logs"),
                "LOG LINES must follow the same ladder as the dumps - ONE policy, not two. This is the finding.");
            Expect(readOnly.DumpDirectory == Path.Combine(fallback, "crashdumps"),
                "and the dumps follow the same ladder.");
            Expect(readOnly.PrimaryLogDirectory == primaryLogs && readOnly.PrimaryDumpDirectory == primaryDumps,
                "The result must still report where the primary WOULD have been, so the redirect can be explained.");

            // ---- 2. END TO END: a read-only install must not lose the lines that led to the crash --
            CrashForensics.ConfigureLocations(primaryLogs, primaryDumps, fallback, writableProbe: _ => false);

            Expect(CrashForensics.IsUsingFallbackLocation,
                "A read-only primary must put forensics in the fallback.");
            Expect(AppLogger.LogDir == Path.Combine(fallback, "logs"),
                "AppLogger MUST follow the same resolution. Before NET-017 it stayed pinned at the app " +
                "directory, so on a read-only install every log line was written somewhere it could not be.");

            AppLogger.LogInfo("NET-017 probe: the line BEFORE the fault");
            AppLogger.Flush();

            string? reportPath = CrashForensics.HandleException(
                "NET-017 probe", new InvalidOperationException("NET-017 simulated fault"), fatal: false);

            Expect(reportPath != null && File.Exists(reportPath), "The crash report must be written.");
            Expect(Path.GetDirectoryName(reportPath) == Path.Combine(fallback, "logs"),
                "The crash report must land in the FALLBACK when the primary is read-only.");

            AppLogger.LogInfo("NET-017 probe: the line AFTER the fault");
            AppLogger.Flush();

            // THE ASSERTION THAT CLOSES THE FINDING: the report is SURROUNDED by its log lines.
            string[] daily = Directory.GetFiles(Path.Combine(fallback, "logs"), "????-??-??.log");
            Expect(daily.Length == 1,
                $"Exactly one daily log must exist in the fallback; found {daily.Length}.");

            string logText = File.ReadAllText(daily[0]);
            Expect(logText.Contains("the line BEFORE the fault"),
                "The log lines written BEFORE the fault must be in the fallback - losing exactly these is " +
                "what the read-only-install defect did.");
            Expect(logText.Contains("the line AFTER the fault"),
                "Log lines must keep flowing to the fallback after the fault as well.");
            Expect(Path.GetDirectoryName(daily[0]) == Path.GetDirectoryName(reportPath),
                "The crash report must sit in the SAME directory as the lines that led to it - the " +
                "report-to-surrounding-lines link that the defect broke.");

            // THE DEFECT, ASSERTED ABSENT: nothing was written where it could not be written.
            bool primaryHoldsLogs = Directory.Exists(primaryLogs) && Directory.GetFiles(primaryLogs).Length > 0;
            Expect(!primaryHoldsLogs,
                "The read-only primary must hold NO log lines at all - not a directory of silent losses.");

            // ---- 3. THE SAME SEAM MOVES THE LOGGING SIDE (one seam, two consumers) ---------------
            string appLogs = Path.Combine(primary, "logsApp");
            AppLogger.ConfigureLocation(appLogs);
            Expect(AppLogger.LogDir == appLogs,
                "The AppLogger location must be settable through the shared policy, not pinned at type init.");
            AppLogger.LogInfo("NET-017 probe: writable primary line");
            AppLogger.Flush();
            Expect(Directory.GetFiles(appLogs, "????-??-??.log").Length == 1,
                "With a writable location, log lines must land there instead.");
        }
        finally
        {
            // Restore production resolution so later steps and the app are unaffected by this probe.
            CrashForensics.ConfigureLocations(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crashdumps"),
                LogLocation.FallbackRoot);

            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine(" -> ONE ladder: a read-only install redirects log lines AND crash artifacts together, so " +
                          "the crash report is surrounded by the lines that led to it - the original defect " +
                          "asserted absent (the primary holds no log lines at all)!");
    }

    static void TestScheduledStopAndRemainingTime()
    {
        Console.WriteLine("\n[Step 0.693] Verifying STOP AFTER resolution + the remaining-time readout...");

        var now = new DateTime(2026, 10, 18, 3, 0, 0, DateTimeKind.Utc);
        long nowTicks = now.Ticks;
        long takeStart = nowTicks;
        long segmentStart = nowTicks;
        TimeSpan M(double m) => TimeSpan.FromMinutes(m);

        // ---- 1. STOP-AFTER RESOLUTION MIRRORS THE START DELAY ------------------------------------
        Expect(RecordingSchedule.ResolveStopUtc(new RecordingConfig { ScheduledStopAfterMinutes = 90 }, now)
                   == now.AddMinutes(90),
            "A stop-after delay must resolve to an absolute instant measured from the arm, exactly " +
            "like the start delay resolves ScheduledStartUtc.");

        Expect(RecordingSchedule.ResolveStopUtc(new RecordingConfig(), now) is null,
            "No stop-after and no absolute instant must resolve to null - an unbounded take.");

        Expect(RecordingSchedule.ResolveStopUtc(
                   new RecordingConfig { ScheduledStopUtc = now.AddMinutes(20) }, now) == now.AddMinutes(20),
            "With no delay set, an absolute stop instant must be honoured as-is.");

        Expect(RecordingSchedule.ResolveStopUtc(
                   new RecordingConfig { ScheduledStopAfterMinutes = 90, ScheduledStopUtc = now.AddMinutes(5) },
                   now) == now.AddMinutes(90),
            "A stop-after delay must WIN over an absolute instant, mirroring the start rule - the delay " +
            "is the more specific instruction.");

        // ---- 2. THE NEAREST TIME BOUNDARY --------------------------------------------------------
        // max length only
        RecordingBoundaryEta? eta = RecordingBoundaries.NearestTimeBoundary(
            new RecordingConfig { MaxDurationMinutes = 120 }, takeStart, segmentStart,
            nowTicks + M(39).Ticks);
        Expect(eta is { Boundary: RecordingBoundary.MaxDuration },
            "With only a max length set, the nearest boundary must be MaxDuration.");
        Expect(eta!.Value.Remaining == M(81),
            $"Remaining must be max-minus-elapsed (120-39=81); got {eta.Value.Remaining}.");

        // scheduled stop NEARER than max length
        eta = RecordingBoundaries.NearestTimeBoundary(
            new RecordingConfig { MaxDurationMinutes = 120, ScheduledStopUtc = now.AddMinutes(20) },
            takeStart, segmentStart, nowTicks + M(1).Ticks);
        Expect(eta is { Boundary: RecordingBoundary.ScheduledStop },
            "A scheduled stop nearer than the max length must win the readout - the operator needs the " +
            "boundary that actually happens first.");
        Expect(eta!.Value.Remaining == M(19), $"Remaining must be to the stop instant; got {eta.Value.Remaining}.");

        // max length NEARER than the scheduled stop (the comparison must go both ways).
        // 19 min elapsed, max 30 -> 11 min left; the stop is 120 min out, so MaxDuration is nearest.
        eta = RecordingBoundaries.NearestTimeBoundary(
            new RecordingConfig { MaxDurationMinutes = 30, ScheduledStopUtc = now.AddMinutes(120) },
            takeStart, segmentStart, nowTicks + M(19).Ticks);
        Expect(eta is { Boundary: RecordingBoundary.MaxDuration },
            "With the max length nearer, MaxDuration must win instead - the switch must be symmetric.");
        Expect(eta!.Value.Remaining == M(11),
            $"Remaining must be 30-19=11 min; got {eta.Value.Remaining}.");

        // rollover nearest (segment clock, not take clock)
        eta = RecordingBoundaries.NearestTimeBoundary(
            new RecordingConfig { MaxDurationMinutes = 120, SegmentRolloverMinutes = 15 },
            takeStart, segmentStart, nowTicks + M(12).Ticks);
        Expect(eta is { Boundary: RecordingBoundary.SegmentRollover },
            "A rollover due sooner than the max length must win the readout.");
        Expect(eta!.Value.Remaining == M(3), $"Remaining must be 15-12=3 min; got {eta.Value.Remaining}.");

        // THE TWO CLOCKS ARE INDEPENDENT. Same config, two different elapsed measurements - this is the
        // split NET-008's boundary arithmetic makes (the take clock survives a rollover; the segment
        // clock does not), and getting it wrong is how a countdown silently lies.
        var dualClock = new RecordingConfig { MaxDurationMinutes = 60, SegmentRolloverMinutes = 30 };

        eta = RecordingBoundaries.NearestTimeBoundary(
            dualClock, takeStart - M(40).Ticks, segmentStart, nowTicks);
        Expect(eta is { Boundary: RecordingBoundary.MaxDuration } && eta!.Value.Remaining == M(20),
            "The TAKE clock must be measured from the take start: 40 min into a 60 min max leaves 20 min, " +
            "even though the segment was just restarted.");

        eta = RecordingBoundaries.NearestTimeBoundary(
            dualClock, takeStart - M(40).Ticks, segmentStart - M(25).Ticks, nowTicks);
        Expect(eta is { Boundary: RecordingBoundary.SegmentRollover } && eta!.Value.Remaining == M(5),
            "The SEGMENT clock must be measured from the segment start: 25 min into a 30 min segment " +
            "leaves 5 min, and that now beats the take's 20 min.");

        // ---- 3. NOTHING BOUNDED -> NO READOUT ----------------------------------------------------
        Expect(RecordingBoundaries.NearestTimeBoundary(new RecordingConfig(), takeStart, segmentStart, nowTicks) is null,
            "An unbounded take must produce NO readout, rather than a countdown to nothing.");

        Expect(RecordingBoundaries.NearestTimeBoundary(
                   new RecordingConfig { SegmentRolloverGigabytes = 2 }, takeStart, segmentStart, nowTicks) is null,
            "A SIZE-ONLY rollover must have no countdown: it depends on the achieved bitrate, so any " +
            "time we printed would be a guess dressed up as a measurement.");

        // ---- 4. OVERDUE CLAMPS TO ZERO, NEVER NEGATIVE -------------------------------------------
        eta = RecordingBoundaries.NearestTimeBoundary(
            new RecordingConfig { MaxDurationMinutes = 10 }, takeStart, segmentStart, nowTicks + M(25).Ticks);
        Expect(eta!.Value.Remaining == TimeSpan.Zero,
            $"An overdue boundary must clamp to zero, never report negative time; got {eta.Value.Remaining}.");

        eta = RecordingBoundaries.NearestTimeBoundary(
            new RecordingConfig { ScheduledStopUtc = now.AddMinutes(-5) }, takeStart, segmentStart, nowTicks);
        Expect(eta!.Value.Remaining == TimeSpan.Zero,
            "A scheduled stop already in the past must clamp to zero too.");

        // ---- 5. THE STOP-AFTER CHOICE SURVIVES A RELAUNCH -----------------------------------------
        string root = Path.Combine(Path.GetTempPath(), "omt-stopafter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            var store = new SettingsStore(settingsPath);
            Expect(store.Save(SettingsStore.FromConfig(new RecordingConfig { ScheduledStopAfterMinutes = 60 }, null)),
                "Saving a stop-after choice must succeed.");

            var reloadStore = new SettingsStore(settingsPath);
            OperatorSettings reloaded = reloadStore.Load();
            Expect(reloaded.ScheduledStopAfterMinutes == 60,
                "The stop-after CHOICE must persist - the operator must not re-pick it every launch.");
            Expect(SettingsStore.ToConfig(reloaded).ScheduledStopAfterMinutes == 60,
                "The persisted stop-after choice must map back onto the config.");

            using (var seam = BuildHeadlessSession())
            {
                var vm = seam.ViewModel!;
                vm.AttachSettings(new SettingsStore(Path.Combine(root, "vm.json"), logError: _ => { }), reloaded);
                Expect(vm.SelectedStopAfter?.Value == 60,
                    "The VM must map the restored stop-after onto a real picker option (1 h).");

                vm.SelectedStopAfter = MainWindowViewModel.StopAfterOptions.First(o => o.Value == 480);
                Expect(File.Exists(Path.Combine(root, "vm.json")),
                    "Changing STOP AFTER must persist immediately.");
                Expect(File.ReadAllText(Path.Combine(root, "vm.json")).Contains("480"),
                    "The written file must carry the new stop-after choice.");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine(" -> STOP AFTER resolves like START IN (delay wins over absolute, null = unbounded); the " +
                          "readout picks the nearest clock-driven boundary, stays silent when nothing is bounded, " +
                          "refuses to guess a size-based rollover, clamps instead of going negative, and the " +
                          "choice survives a relaunch!");
    }

    static void TestOperatorSettings()
    {
        Console.WriteLine("\n[Step 0.692] Verifying operator settings persistence (load/save/validate/fallback)...");

        string root = Path.Combine(Path.GetTempPath(), "omt-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "settings.json");

        try
        {
            // ---- 1. MISSING FILE: a first run is all-defaults, and nothing is quarantined. ----------
            var store = new SettingsStore(settingsPath);
            OperatorSettings first = store.Load();
            Expect(store.LastOutcome == SettingsLoadOutcome.Missing,
                "A missing settings file must report Missing.");
            Expect(first.OutputDirectory is null && first.SelectedPreset is null,
                "A missing file must leave every field unset, so defaults are in force.");
            Expect(!File.Exists(settingsPath + ".corrupt"),
                "A missing file must NOT be quarantined - there is nothing to quarantine.");

            // ---- 2. ROUND TRIP: config -> JSON -> load -> identical values. --------------------------
            string destination = Path.Combine(root, "takes");
            string backupDir = Path.Combine(root, "backup");
            Directory.CreateDirectory(destination);
            Directory.CreateDirectory(backupDir);

            var original = new RecordingConfig
            {
                ContainerFormat = OutputContainerFormat.MKV,
                EncoderChoice = VideoEncoderChoice.NvidiaNvenc,
                Quality = QualityPreset.Ultra,
                OutputDirectory = destination,
                BackupOutputDirectory = backupDir,
                FilenamePrefix = "MORNING_SHOW",
                InputLossThresholdSeconds = 7.5,
                InputRecoveryGraceSeconds = 45,
                SegmentRolloverMinutes = 15,
                SegmentRolloverGigabytes = 2,
                MaxDurationMinutes = 120,
                ScheduledStartDelayMinutes = 5,
                ScheduledStopUtc = new DateTime(2026, 10, 17, 3, 30, 0, DateTimeKind.Utc)
            };

            // NET-026: persist a NON-DEFAULT engine, so a silent revert to the default cannot pass.
            original.SinkEngine = SinkEngine.MediaFoundation;

            Expect(store.Save(SettingsStore.FromConfig(original, "Long event - MKV")),
                "Saving settings to a writable directory must succeed.");
            Expect(File.Exists(settingsPath), "Save must create the settings file.");

            var reloadStore = new SettingsStore(settingsPath);
            OperatorSettings reloaded = reloadStore.Load();
            Expect(SettingsStore.ToConfig(new OperatorSettings()).SinkEngine == SinkEngine.Ffmpeg,
                "NET-026: an absent SinkEngine must fail safe to the FFmpeg default.");
            Expect(SettingsStore.ToConfig(new OperatorSettings { SinkEngine = "not-an-engine" }).SinkEngine == SinkEngine.Ffmpeg,
                "NET-026: an unparseable SinkEngine must fail safe to the FFmpeg default.");

            Expect(reloadStore.LastOutcome == SettingsLoadOutcome.Loaded,
                "A well-formed file must load as Loaded.");

            RecordingConfig rt = SettingsStore.ToConfig(reloaded);
            Expect(rt.ContainerFormat == original.ContainerFormat, "Round trip: container format.");
            Expect(rt.EncoderChoice == original.EncoderChoice, "Round trip: encoder choice.");
            Expect(rt.SinkEngine == SinkEngine.MediaFoundation,
                $"NET-026: the recording engine must survive a save/load round trip; got {rt.SinkEngine}.");

            // ====================================================================================
            // REVIEW-028 finding 5.3 (HIGH): AtomicFile was UNTESTED. It is the one write path
            // behind settings.json (SettingsStore.cs:144), and ADR-0006's guarantee is that a
            // settings write must NEVER truncate settings.json into an unreadable file. These
            // assertions pin the real contract: the destination is only ever the OLD complete
            // contents or the NEW complete contents - never a hybrid, never partial.
            // ====================================================================================
            string atomicDir = Path.Combine(root, "atomicfile");
            Directory.CreateDirectory(atomicDir);
            string atomicPath = Path.Combine(atomicDir, "settings.json");

            // 1. create path (destination does not exist yet -> File.Move)
            AtomicFile.WriteAllText(atomicPath, "v1");
            Expect(File.ReadAllText(atomicPath) == "v1",
                "AtomicFile must create the destination with the exact contents given.");

            // 2. replace path (destination exists -> File.Replace)
            AtomicFile.WriteAllText(atomicPath, "v2");
            Expect(File.ReadAllText(atomicPath) == "v2",
                "AtomicFile must REPLACE an existing destination, not append or truncate-partially.");

            // 3. a leftover .tmp from an interrupted earlier write must be ignored by the next save
            string staleTemp = atomicPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(staleTemp, "GARBAGE-FROM-A-DEAD-WRITE");
            AtomicFile.WriteAllText(atomicPath, "v3");
            Expect(File.ReadAllText(atomicPath) == "v3",
                "AtomicFile must ignore a stale sibling .tmp left by an interrupted write.");

            // 4. INTERRUPTION: hold the destination so the final step of the write cannot complete.
            //    The helper writes to a temp first, so the destination must still hold the PREVIOUS
            //    COMPLETE content - this is the 'never a hybrid' property, and the reason the helper
            //    exists rather than a plain File.WriteAllText.
            bool surfacedFailure = false;
            using (var hold = new FileStream(atomicPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try
                {
                    AtomicFile.WriteAllText(atomicPath, "SHOULD-NEVER-LAND");
                }
                catch
                {
                    surfacedFailure = true;   // the helper must rethrow, not silently succeed
                }
            }

            Expect(surfacedFailure,
                "AtomicFile must SURFACE a failed write; swallowing it would report success for a write that did not land.");
            Expect(File.ReadAllText(atomicPath) == "v3",
                $"After a failed write the destination must still be the previous COMPLETE contents (never a hybrid); got '{File.ReadAllText(atomicPath)}'.");

            // 5. this block leaves NO temp debris. The planted stale .tmp is removed here on purpose:
            //    a later assertion in this step requires a save to leave no temp behind, and a test
            //    that litters its shared sandbox would fail someone else's guard rather than its own.
            if (File.Exists(staleTemp)) File.Delete(staleTemp);
            string[] leftovers = Directory.GetFiles(atomicDir, "*.tmp");
            Expect(leftovers.Length == 0,
                $"Neither a successful nor a failed AtomicFile write may leave temp debris; found {leftovers.Length}: {string.Join(", ", leftovers)}");

            // 6. REVIEW-028-FIX-2b (finding 5.3, HIGH): DISCRIMINATING child-process Process.Kill() harness.
            //    Reuses the NET-025 child/kill pattern:
            //    - Positive control (--atomicfile-write): 20 iterations of spawning a child writer loop,
            //      waiting for "atomicfile-child: flowing", hard-killing via Process.Kill(entireProcessTree: true),
            //      and asserting the destination is ALWAYS the exact complete OLD or exact complete NEW payload
            //      (never 0-byte truncated, never partial, never a hybrid).
            //    - Negative control (--atomicfile-write-plain): runs the SAME child/kill loop against bare
            //      File.WriteAllText (which opens with FileMode.Create, truncating the file to 0 bytes before
            //      writing). Proves the harness discriminates by asserting that bare File.WriteAllText
            //      genuinely goes RED (leaves a 0-byte or partial/truncated file on disk under Process.Kill).
            {
                string selfExe = Environment.ProcessPath ?? string.Empty;
                Expect(selfExe.Length > 0, "REVIEW-028-FIX-2b: could not locate test executable for child-process AtomicFile harness.");

                string killDir = Path.Combine(atomicDir, "kill-harness");
                Directory.CreateDirectory(killDir);
                string posTarget = Path.Combine(killDir, "positive-settings.json");

                string prevToken = "ITER_00";
                string prevExpected = ExpandAtomicHarnessPayload(prevToken);
                AtomicFile.WriteAllText(posTarget, prevExpected);

                const int PositiveIterations = 20;
                int sawOldCount = 0;
                int sawNewCount = 0;

                for (int iter = 1; iter <= PositiveIterations; iter++)
                {
                    string nextToken = $"ITER_{iter:D2}";
                    string nextExpected = ExpandAtomicHarnessPayload(nextToken);

                    var psi = new ProcessStartInfo(selfExe)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add("--atomicfile-write");
                    psi.ArgumentList.Add(posTarget);
                    psi.ArgumentList.Add(nextToken);

                    using (var child = Process.Start(psi))
                    {
                        Expect(child is not null, $"REVIEW-028-FIX-2b: --atomicfile-write child failed to start on iter {iter}.");
                        string? ready = child!.StandardOutput.ReadLine();
                        Expect(ready is not null && ready.Contains("flowing", StringComparison.Ordinal),
                            $"REVIEW-028-FIX-2b: --atomicfile-write child never reported flowing on iter {iter} (got '{ready}').");

                        // Kill immediately when "flowing" arrives so the kill lands inside the 32 MB write window.
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(10_000);
                    }

                    Expect(File.Exists(posTarget),
                        $"REVIEW-028-FIX-2b (iter {iter}): destination disappeared after killing --atomicfile-write child!");
                    string onDisk = File.ReadAllText(posTarget);
                    bool isOld = string.Equals(onDisk, prevExpected, StringComparison.Ordinal);
                    bool isNew = string.Equals(onDisk, nextExpected, StringComparison.Ordinal);
                    Expect(isOld || isNew,
                        $"REVIEW-028-FIX-2b (iter {iter}): AtomicFile destination was neither complete OLD ({prevExpected.Length} B) " +
                        $"nor complete NEW ({nextExpected.Length} B); got {onDisk.Length} B (head='{onDisk[..Math.Min(32, onDisk.Length)]}').");

                    if (isNew)
                    {
                        sawNewCount++;
                        prevToken = nextToken;
                        prevExpected = nextExpected;
                    }
                    else
                    {
                        sawOldCount++;
                    }
                }

                // Clean up any sibling .tmp left by the hard-killed child processes before the end-of-step .tmp guard
                foreach (string tmp in Directory.GetFiles(killDir, "*.tmp"))
                {
                    try { File.Delete(tmp); } catch { }
                }

                // Negative control: bare File.WriteAllText (--atomicfile-write-plain) under the exact same kill loop.
                // Must genuinely go RED (produce a 0-byte truncated or partial/corrupt file that is neither complete OLD nor complete NEW).
                string negTarget = Path.Combine(killDir, "negative-plain.json");
                string negSeedToken = "PLAIN_00";
                string negSeedExpected = ExpandAtomicHarnessPayload(negSeedToken);
                File.WriteAllText(negTarget, negSeedExpected);

                bool negativeControlCaughtCorruption = false;
                int negDetectedIter = 0;
                int negCorruptLen = -1;

                for (int iter = 1; iter <= 20; iter++)
                {
                    string nextToken = $"PLAIN_{iter:D2}";
                    string nextExpected = ExpandAtomicHarnessPayload(nextToken);

                    var psi = new ProcessStartInfo(selfExe)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add("--atomicfile-write-plain");
                    psi.ArgumentList.Add(negTarget);
                    psi.ArgumentList.Add(nextToken);

                    using (var child = Process.Start(psi))
                    {
                        Expect(child is not null, "REVIEW-028-FIX-2b: --atomicfile-write-plain child failed to start.");
                        string? ready = child!.StandardOutput.ReadLine();
                        Expect(ready is not null && ready.Contains("flowing", StringComparison.Ordinal),
                            "REVIEW-028-FIX-2b: --atomicfile-write-plain child never reported flowing.");

                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(10_000);
                    }

                    string onDisk = File.Exists(negTarget) ? File.ReadAllText(negTarget) : string.Empty;
                    bool isOld = string.Equals(onDisk, negSeedExpected, StringComparison.Ordinal);
                    bool isNew = string.Equals(onDisk, nextExpected, StringComparison.Ordinal);
                    if (!isOld && !isNew)
                    {
                        negativeControlCaughtCorruption = true;
                        negDetectedIter = iter;
                        negCorruptLen = onDisk.Length;
                        break;
                    }

                    negSeedExpected = onDisk;
                }

                Expect(negativeControlCaughtCorruption,
                    "REVIEW-028-FIX-2b negative control failed to go RED: --atomicfile-write-plain (bare File.WriteAllText) " +
                    "survived 20 mid-write process kills without truncating or corrupting the destination, so the harness is not discriminating.");

                try { Directory.Delete(killDir, recursive: true); } catch { }

                Console.WriteLine($" -> [AtomicFile kill harness] positive control GREEN across {PositiveIterations}/20 kills " +
                                  $"(complete OLD={sawOldCount}, complete NEW={sawNewCount}, 0 hybrids); " +
                                  $"negative control (--atomicfile-write-plain) went RED on iter {negDetectedIter} " +
                                  $"(truncated to {negCorruptLen:N0} B vs {prevExpected.Length:N0} B expected)!");
            }

            Expect(rt.Quality == original.Quality, "Round trip: quality.");
            Expect(rt.OutputDirectory == original.OutputDirectory, "Round trip: destination.");
            Expect(rt.BackupOutputDirectory == original.BackupOutputDirectory, "Round trip: backup destination.");
            Expect(rt.FilenamePrefix == original.FilenamePrefix, "Round trip: filename prefix.");
            Expect(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0,
                "A successful settings save must not leave its temp file behind.");

            // Filename stem: path traversal and quote-breakout fall back to the default. A normal name does not.
            Expect(RecordingFileNames.SanitizePrefix("MORNING_SHOW") == "MORNING_SHOW",
                "A plain prefix must be kept.");
            Expect(RecordingFileNames.SanitizePrefix("Morning Show") == "Morning Show",
                "A space is a legal Windows file name and must be kept.");
            Expect(RecordingFileNames.SanitizePrefix(@"..\..\Windows\System32\cmd") == RecordingFileNames.DefaultPrefix,
                "A traversing prefix must fall back to the default stem.");
            Expect(RecordingFileNames.SanitizePrefix("foo\" -filter evil") == RecordingFileNames.DefaultPrefix,
                "A prefix with a quote must not be usable as a filename stem.");
            Expect(RecordingFileNames.SanitizePrefix("C:evil") == RecordingFileNames.DefaultPrefix,
                "A drive-qualified prefix must fall back to the default stem.");
            Expect(RecordingFileNames.TakeFileName(@"..\evil", new DateTime(2026, 1, 2, 3, 4, 5), ".mp4")
                   == "OMT_Capture_2026-01-02_03-04-05.mp4",
                "The arm's file name must use the sanitised stem.");
            Expect(SettingsStore.ToConfig(new OperatorSettings { FilenamePrefix = @"..\..\secret" }).FilenamePrefix
                   == RecordingFileNames.DefaultPrefix,
                "Load must sanitise the prefix, not copy it through.");
            Expect(rt.InputLossThresholdSeconds == original.InputLossThresholdSeconds,
                "Round trip: ADR-0002 input-loss threshold (a field the UI does not expose).");
            Expect(rt.InputRecoveryGraceSeconds == original.InputRecoveryGraceSeconds,
                "Round trip: ADR-0002 reconnect grace period.");
            Expect(rt.SegmentRolloverMinutes == original.SegmentRolloverMinutes, "Round trip: rollover minutes.");
            Expect(rt.SegmentRolloverGigabytes == original.SegmentRolloverGigabytes, "Round trip: rollover GB.");
            Expect(rt.MaxDurationMinutes == original.MaxDurationMinutes, "Round trip: max duration.");
            Expect(rt.ScheduledStartDelayMinutes == original.ScheduledStartDelayMinutes, "Round trip: start delay.");
            Expect(rt.ScheduledStopUtc == original.ScheduledStopUtc, "Round trip: scheduled stop instant.");
            Expect(reloaded.SelectedPreset == "Long event - MKV",
                "Round trip: the selected preset name.");
            Expect(rt.HasBackupDestination,
                "Round trip: the backup destination is still recognised as a real fan-out.");

            // ENUMS ARE NAMES, not ordinals - reordering an enum in code must not reinterpret the file.
            string json = File.ReadAllText(settingsPath);
            Expect(json.Contains("\"MKV\"") && json.Contains("\"NvidiaNvenc\"") && json.Contains("\"Ultra\""),
                "Enums must persist as NAMES, so reordering an enum in code cannot silently " +
                "reinterpret an operator's saved choice.");

            // ---- 3. VANISHED DRIVE: that FIELD falls back; the rest of the file is honoured. --------
            var vanishedStore = new SettingsStore(
                Path.Combine(root, "vanished.json"),
                directoryExists: existing => existing == destination,
                logInfo: _ => { });

            Expect(vanishedStore.Save(new OperatorSettings
                {
                    ContainerFormat = "MOV",
                    Quality = "Low",
                    OutputDirectory = @"Z:\gone\takes",
                    BackupOutputDirectory = @"Z:\gone\backup",
                    SelectedPreset = "Low disk - x264 MP4"
                }),
                "Saving must succeed even when a path inside it is invalid.");

            OperatorSettings recovered = vanishedStore.Load();
            Expect(recovered.OutputDirectory is null,
                "A destination on a vanished drive must fall back to that field's default.");
            Expect(recovered.BackupOutputDirectory is null,
                "A backup destination on a vanished drive must fall back to that field's default.");
            Expect(recovered.Quality == "Low" && recovered.ContainerFormat == "MOV",
                "A vanished path must cost that field ONLY - the rest of the file must be honoured.");
            Expect(recovered.SelectedPreset == "Low disk - x264 MP4",
                "A vanished path must not cost the operator the preset either.");
            Expect(SettingsStore.ToConfig(recovered).OutputDirectory == new RecordingConfig().OutputDirectory,
                "The fallen-back destination must be the config's own default, not an empty string.");

            // The usability rule itself: existing folder OK, not-yet-created folder OK while its DRIVE
            // exists, vanished drive NOT OK.
            Expect(SettingsStore.IsUsablePath(destination, existing => existing == destination),
                "An existing folder is usable.");
            string notYetCreated = Path.Combine(root, "not-yet-created");
            string? notYetRoot = Path.GetPathRoot(notYetCreated);
            Expect(SettingsStore.IsUsablePath(notYetCreated, existing => existing == notYetRoot),
                "A folder that does not exist YET is usable while its DRIVE does - the recorder creates it. " +
                "Falling back just because the operator has not made the subfolder yet would be obnoxious.");
            Expect(!SettingsStore.IsUsablePath(@"Z:\gone", _ => false),
                "A vanished drive is NOT usable - that is the 3 a.m. case this module exists for.");
            Expect(!SettingsStore.IsUsablePath("", _ => true) && !SettingsStore.IsUsablePath(null, _ => true),
                "A blank path is never usable.");

            // ---- 4. CORRUPT FILE: all defaults, and the evidence is KEPT. ---------------------------
            File.WriteAllText(settingsPath, "{ this is not json");
            var corruptStore = new SettingsStore(settingsPath);
            OperatorSettings afterCorrupt = corruptStore.Load();
            Expect(corruptStore.LastOutcome == SettingsLoadOutcome.Corrupt,
                "An unparseable file must report Corrupt.");
            Expect(afterCorrupt.OutputDirectory is null && afterCorrupt.SelectedPreset is null,
                "A corrupt file must fall back to ALL defaults rather than a partial restore.");
            Expect(File.Exists(settingsPath + ".corrupt"),
                "The corrupt file must be KEPT (renamed .corrupt) for forensics, never deleted.");
            Expect(File.ReadAllText(settingsPath + ".corrupt").Contains("this is not json"),
                "The quarantined file must still hold the original bytes.");
            Expect(!File.Exists(settingsPath),
                "The corrupt file is MOVED, so startup is unblocked on the next run.");

            // A SECOND corruption must not destroy the first file's evidence.
            File.WriteAllText(settingsPath, "{{ also broken");
            new SettingsStore(settingsPath).Load();
            Expect(File.Exists(settingsPath + ".corrupt.1"),
                "A second corruption must quarantine to .corrupt.1 rather than overwrite the first evidence.");

            // A literal null document is valid JSON but useless settings - corruption, not a crash.
            File.WriteAllText(settingsPath, "null");
            var nullStore = new SettingsStore(settingsPath);
            Expect(nullStore.Load().OutputDirectory is null
                   && nullStore.LastOutcome == SettingsLoadOutcome.Corrupt,
                "A literal null document must be treated as corrupt rather than throwing.");

            // ---- 5. WRITE FAILURE: reported, logged, never thrown. ----------------------------------
            var logged = new List<string>();
            var unwritable = new SettingsStore(
                Path.Combine(root, "family", "\0invalid", "settings.json"),
                logError: logged.Add);

            Expect(!unwritable.Save(reloaded),
                "A write that cannot land must REPORT failure rather than throwing - a settings write " +
                "must never be able to interrupt the operator.");
            Expect(logged.Count > 0,
                "A failed write must be LOGGED, so the field problem is diagnosable.");

            TestRecordingAudioSettingsMigration(root);

            // ---- 6. THE VIEWMODEL APPLIES RESTORED VALUES, AND WRITES ONLY ON CHANGE. ---------------
            using (var seam = BuildHeadlessSession())
            {
                var vm = seam.ViewModel!;
                string vmPath = Path.Combine(root, "vm.json");
                vm.AttachSettings(new SettingsStore(vmPath, logError: _ => { }), reloaded);

                Expect(vm.SelectedContainerFormat == original.ContainerFormat,
                    "The VM must apply the restored container format.");
                Expect(vm.SelectedEncoderChoice == original.EncoderChoice,
                    "The VM must apply the restored encoder choice.");
                Expect(vm.SelectedQualityPreset == original.Quality, "The VM must apply the restored quality.");
                Expect(vm.OutputDirectory == original.OutputDirectory,
                    "The VM must apply the restored destination.");
                Expect(vm.BackupOutputDirectory == original.BackupOutputDirectory,
                    "The VM must apply the restored backup destination.");
                Expect(vm.SelectedRecordingPreset == "Long event - MKV",
                    "The VM must restore the preset NAME, not re-apply the preset over the restored axes.");
                Expect(vm.SelectedRolloverMinutes?.Value == 15,
                    "The VM must map the restored rollover onto a real picker option.");
                Expect(vm.SelectedMaxDuration?.Value == 120,
                    "The VM must map the restored max duration onto a real picker option.");
                Expect(!File.Exists(vmPath),
                    "RESTORING must not write a file - nothing has changed yet.");

                // A committed change now writes immediately.
                vm.SelectedQualityPreset = QualityPreset.Low;
                Expect(File.Exists(vmPath), "A committed change must write the settings file immediately.");
                string written = File.ReadAllText(vmPath);
                Expect(written.Contains("\"Low\""), "The written file must carry the new value.");
                Expect(written.Contains("takes"),
                    "The written file must still carry the destination the operator had set.");

                // Changing the destination writes too (a field that had no change hook before NET-015).
                vm.OutputDirectory = Path.Combine(root, "other-takes");
                Expect(File.ReadAllText(vmPath).Contains("other-takes"),
                    "A destination change must persist - the folder is the field the operator most needs back.");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine(" -> Settings persist across a relaunch: round trip exact, a corrupt file is quarantined " +
                          "with its evidence kept, a vanished drive costs only that field, a failed write is " +
                          "logged not thrown, and restoring is silent while a real change writes immediately!");
    }

    /// <summary>
    /// ADR-0014 / ADR-0006 version 4: a file that never mentions the recording audio route stays on
    /// OMT source audio and a 0 ms offset. A stored host input and offset survive a reload, and an
    /// absurd offset is clamped. The host-feed pull is an injected clock, not a sleep.
    /// </summary>
    static void TestRecordingAudioSettingsMigration(string root)
    {
        Console.WriteLine("\n[Step 0.692b] Verifying recording audio route migration, offset, and host-feed pull...");

        string legacyPath = Path.Combine(root, "v3.json");
        File.WriteAllText(legacyPath, """
            {
              "Version": 3,
              "ContainerFormat": "MP4",
              "Quality": "High"
            }
            """);
        RecordingConfig legacy = SettingsStore.ToConfig(new SettingsStore(legacyPath).Load());
        Expect(legacy.AudioRoute == RecordingAudioRoute.OmtSource,
            "A version 3 file must keep OMT source audio.");
        Expect(legacy.AudioVideoOffsetMilliseconds == 0, "A version 3 file must keep a 0 ms offset.");

        var host = new RecordingConfig
        {
            OutputDirectory = root,
            AudioRoute = RecordingAudioRoute.HostInput,
            HostAudioMode = HostAudioCaptureMode.Microphone.ToString(),
            HostAudioDeviceId = "mic-1",
            HostAudioAsioChannel = 2,
            AudioVideoOffsetMilliseconds = -250
        };
        string hostPath = Path.Combine(root, "host.json");
        var hostStore = new SettingsStore(hostPath);
        Expect(hostStore.Save(SettingsStore.FromConfig(host, "Custom")), "Saving a host recording route must succeed.");
        string saved = File.ReadAllText(hostPath);
        Expect(saved.Contains("\"HostInput\"", StringComparison.Ordinal), "The route must persist as its name.");
        RecordingConfig round = SettingsStore.ToConfig(new SettingsStore(hostPath).Load());
        Expect(round.AudioRoute == RecordingAudioRoute.HostInput, "The host route must reload.");
        Expect(round.HostAudioMode == "Microphone", $"The host mode must reload. Got {round.HostAudioMode}.");
        Expect(round.HostAudioDeviceId == "mic-1", "The device id must reload.");
        Expect(round.HostAudioAsioChannel == 2, "The ASIO channel must reload.");
        Expect(round.AudioVideoOffsetMilliseconds == -250, "The offset must reload.");

        RecordingConfig wild = SettingsStore.ToConfig(new OperatorSettings { AudioVideoOffsetMilliseconds = 9_999_999 });
        Expect(wild.AudioVideoOffsetMilliseconds == AudioOffset.MaxMilliseconds,
            "An absurd offset must clamp rather than shift by hours.");
        Expect(SettingsStore.ToConfig(new OperatorSettings { RecordingAudioRoute = "HostInput" }).AudioRoute
               == RecordingAudioRoute.OmtSource,
            "A host route with no mode must fall back to OMT rather than guess a device.");

        Expect(AudioOffset.FramesFor(100, 48000) == 4800, "100 ms at 48 kHz is 4800 frames.");
        Expect(AudioOffset.FramesFor(-20, 48000) == -960, "A negative offset advances audio.");
        AudioOffsetStep delay = AudioOffset.Step(remainingFrames: 100, availableFrames: 40);
        Expect(delay.SilenceFrames == 100 && delay.KeepFrames == 40 && delay.Remaining == 0,
            "A positive offset emits the silence debt and keeps the samples.");
        AudioOffsetStep advance = AudioOffset.Step(remainingFrames: -30, availableFrames: 10);
        Expect(advance.SkipFrames == 10 && advance.KeepFrames == 0 && advance.Remaining == -20,
            "A negative offset drops samples until the debt is paid.");
        AudioOffsetStep done = AudioOffset.Step(advance.Remaining, 25);
        Expect(done.SkipFrames == 20 && done.KeepFrames == 5 && done.Remaining == 0,
            "The remainder of a negative offset is paid on the next chunk.");

        const long frequency = 10_000_000;
        long now = 1_000;
        int pulls = 0;
        int samples = 0;
        float firstLeft = 0;
        float firstRight = 0;
        var feed = new RecordingHostAudioFeed(
            new ScriptedPlanarSource(),
            (buffer, count) =>
            {
                pulls++;
                samples += count / 8;
                firstLeft = BitConverter.ToSingle(buffer, 0);
                firstRight = BitConverter.ToSingle(buffer, 4);
            },
            () => now,
            frequency);
        Expect(feed.PullDue(now) == 0, "The first pull anchors the clock and sends nothing.");
        now += frequency / 2;
        int got = feed.PullDue(now);
        Expect(got == 24000, $"Half a second at 48 kHz is 24000 samples. Got {got}.");
        Expect(pulls == 1 && samples == 24000, "The callback must see the samples that were due.");
        Expect(firstLeft == 0.25f && firstRight == -0.5f,
            $"Interleaved audio must be left then right. Got {firstLeft}, {firstRight}.");

        // REVIEW-028 finding 5.2 (MEDIUM): direct RecordingAudioChoice contract coverage (Omt singleton + SameAs identity).
        var omtChoice = RecordingAudioChoice.Omt;
        Expect(omtChoice.Route == RecordingAudioRoute.OmtSource && omtChoice.Host is null,
            "RecordingAudioChoice.Omt must represent the OMT source audio route with no host device.");
        Expect(omtChoice.SameAs(new RecordingAudioChoice("Any OMT label", RecordingAudioRoute.OmtSource, null)),
            "Two OmtSource choices must match under SameAs regardless of label.");
        var hostMicA = new RecordingAudioChoice("Mic 1", RecordingAudioRoute.HostInput,
            new HostAudioCaptureOption(HostAudioCaptureMode.Microphone, "Mic 1", "dev-a", null, 0));
        var hostMicAUpper = new RecordingAudioChoice("Mic 1 copy", RecordingAudioRoute.HostInput,
            new HostAudioCaptureOption(HostAudioCaptureMode.Microphone, "Mic 1 copy", "DEV-A", null, 0));
        var hostMicPair2 = new RecordingAudioChoice("Mic 1 ch 3/4", RecordingAudioRoute.HostInput,
            new HostAudioCaptureOption(HostAudioCaptureMode.Microphone, "Mic 1 ch 3/4", "dev-a", null, 2));
        Expect(hostMicA.SameAs(hostMicAUpper), "RecordingAudioChoice.SameAs must compare DeviceId case-insensitively.");
        Expect(!hostMicA.SameAs(hostMicPair2), "RecordingAudioChoice.SameAs must distinguish ASIO input channel pairs.");
        Expect(!hostMicA.SameAs(omtChoice) && !hostMicA.SameAs(null), "RecordingAudioChoice.SameAs must reject different routes and null.");
        Expect(feed.PullDue(now) == 0, "The same timestamp must not send the samples again.");
        feed.Dispose();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Recording audio route migrates, the offset math holds, and the host feed pulls by the clock.");
        Console.ResetColor();
    }

    private sealed class ScriptedPlanarSource : IHostAudioSource
    {
        public int SampleRate => 48000;
        public int Channels => 2;
        public bool IsHardwareActive => true;
        public int DeviceSampleRate => 48000;
        public bool IsResamplingRequired => false;
        public string? RateNegotiationNote => null;
        public int InputLatencyFrames => 0;
        public string? LastError => null;

        public void FillPlanarAudio(Span<float> planarDestination, int samplesPerChannel)
        {
            for (int i = 0; i < samplesPerChannel; i++) planarDestination[i] = 0.25f;
            for (int i = 0; i < samplesPerChannel; i++) planarDestination[samplesPerChannel + i] = -0.5f;
        }

        public void Dispose() { }
    }

}
