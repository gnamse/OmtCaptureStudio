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
    /// <summary>A programmable fake transport: counts payloads, and can fail on demand.</summary>
    private sealed class FakePipe : IDisposable
    {
        public long BytesWritten;
        public int PacketsWritten;
        public int FailAfterPackets = -1;      // >= 0: throw on this packet index

        public PipeWriteResult Write(byte[] buffer, int length)
        {
            if (FailAfterPackets >= 0 && PacketsWritten >= FailAfterPackets)
            {
                throw new InvalidOperationException("simulated pipe failure");
            }
            Interlocked.Add(ref BytesWritten, length);
            PacketsWritten++;
            return PipeWriteResult.Written;
        }

        public void Dispose() { }
    }

    /// <summary>A deliberately broken writer loop: returns one packet twice.</summary>
    static void BorkedWriterLoop()
    {
        var queue = new BoundedMediaQueue<VideoFramePacket>(4, p => p.Release());
        queue.Enqueue(new VideoFramePacket(MediaBufferPool.Video.Rent(1000), 1000, MediaBufferPool.Video));

        while (queue.TryDequeue(out var packet))
        {
            packet.Release();
            packet.Release(); // the bug this detector exists to catch
        }

        queue.Dispose();
    }

    /// <summary>
    /// SEC-04 (R25/R26). Verified BY CONSEQUENCE, as security's re-audit requires: with no proven ffmpeg,
    /// the REAL recording path must refuse - and must never fall back to a bare "ffmpeg" resolved through
    /// the current directory and PATH (CWE-427). The positive direction is covered by the composite tests,
    /// which start a real take and would fail here if the sink could not reach a resolved binary.
    /// </summary>
    static void TestFfmpegSinkPathDiscipline()
    {
        Console.WriteLine("\n[Step 0.703] Verifying the ffmpeg execution sink never searches PATH (R25/R26, SEC-04b)...");

        // --- source shape: the fallback must not EXIST, and the two rules must be present -------------
        // NOTE: this step deliberately verifies BY CONSEQUENCE rather than by inspecting source text.
        // Security's re-audit asked for the sink's path selection to be driven end-to-end, and a
        // text-shape assertion is the weaker check anyway - it can pass on a file that merely MENTIONS the
        // right identifiers (this file's own comments do). The source is reported for the reader, not
        // asserted on.
        string? hostSource = FindRepoSource("OmtCaptureStudio/Services/Sinks/FfmpegProcessHost.cs");
        Console.WriteLine(hostSource is null
            ? "   [note] repo source not reachable (installed-binary run) - consequence checks below still run."
            : "   [note] sink source located; the bare-name fallback's absence is asserted by CONSEQUENCE below.");

        // --- the consequence, through the real recording path -----------------------------------------
        FfmpegPreflightResult none = FfmpegPreflight.Run(
            AppContext.BaseDirectory, fileExists: _ => false, versionProbe: _ => null, pathVariable: string.Empty);
        Expect(!none.IsAvailable, "A probe with no candidates must report ffmpeg unavailable.");
        Expect(FfmpegPreflight.ResolvedPath is null,
            "R26: a failed preflight must CLEAR the resolved path so a stale path can never be executed.");

        using (var session = new CaptureSession())
        {
            session.Connect(new SyntheticPatternSource());

            int ticks = 0;
            while (session.RecordingFramesWritten == 0 && ticks < 60) { Thread.Sleep(50); ticks++; }

            var cfg = new RecordingConfig
            {
                OutputDirectory = Path.Combine(Path.GetTempPath(), "omt-r25-probe"),
                FilenamePrefix = "R25"
            };

            Expect(!session.StartRecording(cfg),
                "R25/R26: with no PROVEN ffmpeg the take must NOT start - and must never fall back to a " +
                "PATH-resolved 'ffmpeg'. A take that starts here means the sink searched.");
        }

        // --- restore, so every step AFTER this one still has a proven binary ---------------------------
        FfmpegPreflight.Run(AppContext.BaseDirectory);
        Expect(FfmpegPreflight.ResolvedPath is not null,
            "The preflight must be re-established for the steps that follow.");

        Console.WriteLine(" -> no bare-name fallback in the sink, the preflight is the single resolution authority,");
        Console.WriteLine("    a failed preflight clears the path, and a real take is REFUSED rather than PATH-searched!");
    }

    /// <summary>
    /// NET-011 closing pass: the wiring. The selector must choose a composite only when a backup is
    /// configured; a REAL dual-destination take must leave both files on disk; and a backup that cannot
    /// start must leave the primary recording intact.
    /// </summary>
    static void TestCompositeRecordingWiring()
    {
        Console.WriteLine("\n[Step 0.690] Verifying composite recording wiring (dual destination end-to-end)...");

        var format = new StreamFormat { Width = 320, Height = 240, FrameRate = 10.0, SampleRate = 48000, Channels = 2 };

        // ---- 1. CONFIG PATH: a backup selects the composite; no backup leaves the single sink ---------
        var singleChosen = new ScriptedRecordingSink("single");
        var compositeChosen = new CompositeRecordingSink(
            new CompositeChild("primary", new ScriptedRecordingSink("primary"), "C:/t/a.mp4"),
            new CompositeChild("backup", new ScriptedRecordingSink("backup"), "D:/t/a.mp4"));

        var selector = new RecordingSinkSelector(
            singleFactory: (_, _) => singleChosen,
            compositeFactory: (_, _) => compositeChosen);

        var noBackup = new RecordingConfig { OutputDirectory = "C:/t" };
        Expect(!noBackup.HasBackupDestination, "A config with no backup folder must not report a fan-out.");
        Expect(selector.Initialize(noBackup, format, "C:/t/a.mp4"), "The selector must initialize the single sink.");
        Expect(!selector.IsComposite, "With NO backup configured the selector must choose the SINGLE sink.");
        Expect(singleChosen.InitCount == 1, "The single sink must be the one initialized.");

        var withBackup = new RecordingConfig { OutputDirectory = "C:/t", BackupOutputDirectory = "D:/t" };
        Expect(withBackup.HasBackupDestination, "A distinct backup folder must report a fan-out.");
        Expect(selector.Initialize(withBackup, format, "C:/t/a.mp4"), "The selector must initialize the composite.");
        Expect(selector.IsComposite, "With a backup configured the selector must choose the COMPOSITE sink.");
        Expect(compositeChosen.LiveChildCount == 2, $"Both destinations must be live; got {compositeChosen.LiveChildCount}.");

        // A backup that POINTS AT THE PRIMARY is not a fan-out - it would write the same file twice.
        var sameFolder = new RecordingConfig { OutputDirectory = "C:/t", BackupOutputDirectory = "C:/t/" };
        Expect(!sameFolder.HasBackupDestination,
            "A backup pointing at the primary folder must NOT count as a fan-out.");
        selector.Initialize(sameFolder, format, "C:/t/a.mp4");
        Expect(!selector.IsComposite, "A same-folder backup must fall back to the single sink.");

        // The arm's degrade decision must reach the inner sink unchanged through the selector. Both
        // directions are asserted with a NON-default answer, so forwarding cannot pass vacuously.
        singleChosen.Survives = true;
        selector.Initialize(noBackup, format, "C:/t/a.mp4");
        Expect(selector.HasSurvivingDestination,
            "The selector must forward the single sink's answer (deliberately set true) rather than its own.");

        selector.Initialize(withBackup, format, "C:/t/a.mp4");
        Expect(selector.IsComposite, "Re-initializing with a backup must select the composite again.");
        Expect(selector.HasSurvivingDestination == compositeChosen.HasSurvivingDestination,
            "The selector must forward the composite's answer for the take that is actually running.");

        // ---- 2. REAL DUAL-DESTINATION TAKE: both files land on disk ---------------------------------
        string sandbox = Path.Combine(Path.GetTempPath(), "omt_dual_" + Guid.NewGuid().ToString("N")[..8]);
        string primaryDir = Path.Combine(sandbox, "primary");
        string backupDir = Path.Combine(sandbox, "backup");
        Directory.CreateDirectory(primaryDir);

        try
        {
            using (var dualSession = new CaptureSession(new AudioEngine(), new StreamRecorderService(), ownsDependencies: true))
            {
                long frames = 0;
                dualSession.VideoFrameAvailable += (ptr, len, w, h, st, fps, ts) => Interlocked.Increment(ref frames);
                dualSession.Connect(new SyntheticPatternSource());

                int waitTicks = 0;
                while (frames < 5 && waitTicks < 30)
                {
                    Thread.Sleep(50);
                    waitTicks++;
                }

                Expect(frames > 0, "The dual-destination test needs a live synthetic feed.");

                var dualConfig = new RecordingConfig
                {
                    OutputDirectory = primaryDir,
                    FilenamePrefix = "DualDest",
                    BackupOutputDirectory = backupDir
                };

                Expect(dualSession.StartRecording(dualConfig), "A dual-destination take must start.");

                int recordTicks = 0;
                while (recordTicks < 50 && dualSession.RecordingFramesWritten < 20)
                {
                    Thread.Sleep(50);
                    recordTicks++;
                }

                dualSession.StopRecording();

                string[] primaryFiles = Directory.GetFiles(primaryDir, "DualDest*.mp4");
                string[] backupFiles = Directory.Exists(backupDir) ? Directory.GetFiles(backupDir, "DualDest*.mp4") : Array.Empty<string>();

                Expect(primaryFiles.Length == 1, $"The primary folder must hold exactly one file; got {primaryFiles.Length}.");
                Expect(backupFiles.Length == 1, $"The backup folder must hold exactly one file; got {backupFiles.Length}.");

                string primaryName = Path.GetFileName(primaryFiles[0]);
                string backupName = Path.GetFileName(backupFiles[0]);
                Expect(primaryName == backupName,
                    $"Both destinations must use the SAME file name; got '{primaryName}' vs '{backupName}'.");

                long primarySize = new FileInfo(primaryFiles[0]).Length;
                long backupSize = new FileInfo(backupFiles[0]).Length;
                Expect(primarySize > 0 && backupSize > 0,
                    $"Both files must carry data; got {primarySize} and {backupSize} bytes.");
                Expect(primarySize == backupSize || Math.Abs(primarySize - backupSize) <= Math.Max(primarySize, backupSize) * 4 / 10,
                    $"Both destinations encode the SAME frames, so their sizes must be close; got {primarySize} vs {backupSize}.");

                Console.WriteLine($" -> Dual destination wrote '{primaryName}' to both folders ({primarySize:N0} / {backupSize:N0} bytes)");
            }

            // ---- 3. A BACKUP THAT CANNOT START leaves the primary recording intact -------------------
            string blockedBackup = Path.Combine(sandbox, "blocked-backup");
            string blockedPrimary = Path.Combine(sandbox, "blocked-primary");
            Directory.CreateDirectory(blockedPrimary);
            File.WriteAllText(blockedBackup, "not a directory");

            using (var degradedSession = new CaptureSession(new AudioEngine(), new StreamRecorderService(), ownsDependencies: true))
            {
                long frames = 0;
                degradedSession.VideoFrameAvailable += (ptr, len, w, h, st, fps, ts) => Interlocked.Increment(ref frames);
                degradedSession.Connect(new SyntheticPatternSource());

                int waitTicks = 0;
                while (frames < 5 && waitTicks < 30)
                {
                    Thread.Sleep(50);
                    waitTicks++;
                }

                var degradedConfig = new RecordingConfig
                {
                    OutputDirectory = blockedPrimary,
                    FilenamePrefix = "Degraded",
                    BackupOutputDirectory = blockedBackup
                };

                Expect(degradedSession.StartRecording(degradedConfig),
                    "A take must still start when the PRIMARY is usable, even if the backup is not.");

                int recordTicks = 0;
                while (recordTicks < 50 && degradedSession.RecordingFramesWritten < 20)
                {
                    Thread.Sleep(50);
                    recordTicks++;
                }

                bool recordedSomething = degradedSession.RecordingFramesWritten > 0;
                degradedSession.StopRecording();

                string[] survived = Directory.GetFiles(blockedPrimary, "Degraded*.mp4");
                Expect(survived.Length == 1,
                    $"The primary must survive a backup that cannot start; got {survived.Length} file(s).");
                Expect(new FileInfo(survived[0]).Length > 0, "The surviving primary file must carry data.");
                Expect(!File.Exists(blockedBackup + ".mp4"), "The blocked backup path must not have produced a file.");
                Expect(recordedSomething,
                    $"The take must have written frames despite the unusable backup; wrote {degradedSession.RecordingFramesWritten}.");

                Console.WriteLine(" -> Unusable backup folder: the take still recorded to the primary and the backup left no file");
            }
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }

        Console.WriteLine(" -> A backup selects the composite, no backup keeps the single sink, both folders receive the same " +
                          "file name, and an unusable backup degrades instead of stopping the take!");
    }

    /// <summary>
    /// NET-011: the composite recording sink. Fan-out identity and the ledger proof that the fan-out
    /// layer adds no copies and no double ownership; detach-on-error with a surviving destination;
    /// all-children-failed as the real failure; exactly-once child disposal; and the arm's
    /// degrade-vs-stop decision.
    /// </summary>
    static void TestCompositeRecordingSink()
    {
        Console.WriteLine("\n[Step 0.689] Verifying composite recording sink (fan-out, detach, degrade vs stop)...");

        var config = new RecordingConfig { OutputDirectory = Path.GetTempPath(), FilenamePrefix = "CompositeTest" };
        var format = new StreamFormat { Width = 320, Height = 240, FrameRate = 10.0, SampleRate = 48000, Channels = 2 };
        const string PrimaryPath = "C:/take/a.mp4";
        const string BackupPath = "D:/take/a.mp4";
        IntPtr frame = System.Runtime.InteropServices.Marshal.AllocHGlobal(64);

        try
        {
            // ---- 1. FAN-OUT: identical packets, same pointer, and the composite rents NOTHING --------
            var childA = new ScriptedRecordingSink("primary", rentsPooledBuffer: true);
            var childB = new ScriptedRecordingSink("backup", rentsPooledBuffer: true);
            var composite = new CompositeRecordingSink(
                new CompositeChild("primary", childA, PrimaryPath),
                new CompositeChild("backup", childB, BackupPath));

            bool ledgerWasEnabled = MediaBufferPoolLedger.Enabled;
            MediaBufferPoolLedger.Enabled = true;
            MediaBufferPoolLedger.Reset();

            Expect(composite.Initialize(config, format, PrimaryPath),
                "The composite must initialize when its children do.");
            Expect(childA.InitCount == 1 && childB.InitCount == 1,
                "Initialize must reach EVERY child.");

            long rentsBeforeFanOut = MediaBufferPoolLedger.RentCount;
            for (int i = 0; i < 5; i++)
            {
                composite.WriteVideo(frame, 64);
                composite.WriteAudio(new byte[32], 0, 32);
            }

            Expect(childA.VideoFrames == 5 && childB.VideoFrames == 5,
                $"Both children must receive every frame; got {childA.VideoFrames} and {childB.VideoFrames}.");
            Expect(childA.AudioBytes == 160 && childB.AudioBytes == 160,
                $"Both children must receive every audio chunk; got {childA.AudioBytes} and {childB.AudioBytes}.");
            Expect(childA.LastVideoPointer == frame && childB.LastVideoPointer == frame,
                "Both children must be handed the SAME source pointer - the fan-out layer makes no copy of its own.");

            // 5 iterations x (1 video write + 1 audio write) = 10 leases PER CHILD, 20 across the two.
            // The point is that the fan-out layer itself contributes ZERO of them: the count is exactly
            // what the children would have rented had each been driven directly.
            long fanOutRents = MediaBufferPoolLedger.RentCount - rentsBeforeFanOut;
            Expect(fanOutRents == 20,
                $"Only the children may rent (10 leases each across 2 children = 20); fan-out added {fanOutRents}.");
            Expect(childA.Rents == childA.Returns && childB.Rents == childB.Returns,
                "Each child must return exactly what it rented - fan-out must not create double ownership.");
            Expect(MediaBufferPoolLedger.ActiveLeases == 0,
                $"No buffer may still be leased after fan-out; {MediaBufferPoolLedger.ActiveLeases} were.");
            Expect(MediaBufferPoolLedger.FaultCount == 0,
                $"Fan-out must not produce ledger faults: {string.Join(" | ", MediaBufferPoolLedger.Faults)}.");

            MediaBufferPoolLedger.Enabled = ledgerWasEnabled;

            // ---- 2. DETACH ON ERROR: the survivor carries on, the loss is reported ONCE -------------
            int reported = 0;
            string lastReport = string.Empty;
            composite.SinkError += msg => { reported++; lastReport = msg; };

            childB.ThrowOnVideo = new IOException("backup volume vanished");
            composite.WriteVideo(frame, 64);
            composite.WriteVideo(frame, 64);

            Expect(childA.VideoFrames == 7, $"The survivor must keep receiving frames; got {childA.VideoFrames}.");
            Expect(reported == 1, $"A detach must be reported exactly ONCE, not per write; got {reported}.");
            Expect(lastReport.Contains("backup"), $"The report must NAME the failing destination; got '{lastReport}'.");
            Expect(composite.LiveChildCount == 1, $"One destination must remain; got {composite.LiveChildCount}.");
            Expect(composite.HasSurvivingDestination,
                "A composite must report a surviving destination while one child remains.");
            Expect(childB.DisposeCount == 1, $"A detached child must be disposed exactly once; got {childB.DisposeCount}.");

            childB.RaiseError("late error from an already-detached child");
            Expect(reported == 1, $"An already-detached child must not report again; got {reported}.");

            // ---- 3. THE LAST CHILD: the real failure ------------------------------------------------
            childA.ThrowOnVideo = new IOException("primary volume failed too");
            composite.WriteVideo(frame, 64);

            Expect(!composite.HasSurvivingDestination,
                "With no destination left the composite must report NO survivor - the real failure.");
            Expect(reported == 2, $"The second detach must be reported too; got {reported}.");
            Expect(composite.LiveChildCount == 0, "No destinations may remain.");
            Expect(childA.DisposeCount == 1, "The last child must also be disposed exactly once.");

            // ---- 4. EXACTLY-ONCE DISPOSAL of the composite and its children -------------------------
            composite.Dispose();
            composite.Dispose();
            Expect(childA.DisposeCount == 1 && childB.DisposeCount == 1,
                $"Disposing the composite twice must dispose each child exactly once; got {childA.DisposeCount}/{childB.DisposeCount}.");

            // ---- 5. INITIALIZE degrades when one destination cannot start ---------------------------
            var goodInit = new ScriptedRecordingSink("primary");
            var badInit = new ScriptedRecordingSink("backup", initializeResult: false);
            using (var partial = new CompositeRecordingSink(
                new CompositeChild("primary", goodInit, "C:/take/b.mp4"),
                new CompositeChild("backup", badInit, "D:/take/b.mp4")))
            {
                Expect(partial.Initialize(config, format, "C:/take/b.mp4"),
                    "One destination failing to initialize must not fail the take - the other carries it.");
                Expect(partial.LiveChildCount == 1, "The failed destination must not count as live.");
                Expect(badInit.DisposeCount == 1, "A destination that failed to initialize must be let go exactly once.");
            }

            var deadOne = new ScriptedRecordingSink("p", initializeResult: false);
            var deadTwo = new ScriptedRecordingSink("b", initializeResult: false);
            using (var none = new CompositeRecordingSink(
                new CompositeChild("primary", deadOne, "C:/take/c.mp4"),
                new CompositeChild("backup", deadTwo, "D:/take/c.mp4")))
            {
                Expect(!none.Initialize(config, format, "C:/take/c.mp4"),
                    "With NO destination available the composite must fail - that is the real failure.");
            }

            // ---- 6. THE SHED API the arm uses when the primary's volume fills -----------------------
            var shedPrimary = new ScriptedRecordingSink("primary");
            var shedBackup = new ScriptedRecordingSink("backup");
            using (var shedding = new CompositeRecordingSink(
                new CompositeChild("primary", shedPrimary, "C:/full/a.mp4"),
                new CompositeChild("backup", shedBackup, "D:/roomy/a.mp4")))
            {
                shedding.Initialize(config, format, "C:/full/a.mp4");
                int shedReports = 0;
                shedding.SinkError += _ => shedReports++;

                Expect(shedding.TryShedFailedDestination("low disk", out string? survivor),
                    "With a survivor available the composite must shed the failing destination.");
                Expect(survivor == "D:/roomy/a.mp4",
                    $"The shed must report the SURVIVING path so the arm can follow it; got '{survivor}'.");
                Expect(shedding.LiveChildCount == 1 && shedding.HasSurvivingDestination,
                    "After a shed the composite must still report a survivor - the take continues.");
                Expect(shedReports == 1, $"A shed must be reported once; got {shedReports}.");

                Expect(!shedding.TryShedFailedDestination("low disk", out string? noSurvivor),
                    "With no survivor the composite must refuse the shed, so the arm runs its normal stop path.");
                Expect(noSurvivor == null, "A refused shed must not name a survivor.");
            }

            Expect(RecordingStorageGuard.DegradedMessage(1234, "C:/full/a.mp4", "D:/roomy/a.mp4").Contains("continuing the take"),
                "The degrade message must read as 'recording continues', distinct from the stop message.");

            // ---- 7. ARM: a viable sink degrades; a dead sink stops the take -------------------------
            var armSink = new ScriptedRecordingSink("composite-ish");
            using var arm = new RecordingArm(armSink);
            var armConfig = new RecordingConfig { OutputDirectory = Path.GetTempPath(), FilenamePrefix = "CompositeArm" };

            if (!arm.Arm(armConfig, format))
            {
                Console.WriteLine(" -> [SKIP] storage preflight refused the temp volume; the arm degrade-vs-stop assertions are skipped.");
            }
            else
            {
                int armErrors = 0;
                arm.RecordingError += _ => armErrors++;

                armSink.Survives = true;
                armSink.RaiseError("Recording destination 'backup' was detached: backup volume vanished");

                Expect(arm.IsRecording,
                    "A sink error while the transport is still viable must DEGRADE - the take keeps recording.");
                Expect(armErrors == 1, $"A degrade must still be surfaced to the operator; got {armErrors}.");

                armSink.Survives = false;
                armSink.RaiseError("all destinations gone");

                Expect(!arm.IsRecording,
                    "A sink error with nowhere left to write must STOP the take through the single finalize path.");
                Expect(armErrors == 2, $"Both faults must be reported; got {armErrors}.");
            }

            Console.WriteLine(" -> Fan-out is copy-transparent and the ledger balances; a failed destination detaches " +
                              "and reports once; the last one to go stops the take; children are disposed exactly once!");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(frame);
        }
    }

    /// <summary>
    /// NET-010 + the crash-forensics rider: the FFmpeg resolver and its warning surface, and the
    /// read-only-primary fallback for forensics. Every filesystem and process touch is injected, so
    /// this asserts the whole policy without a real ffmpeg, a real PATH or a read-only disk.
    /// </summary>
    static void TestFfmpegPreflightAndForensicsFallback()
    {
        Console.WriteLine("\n[Step 0.688] Verifying FFmpeg preflight + the forensics location fallback...");

        // ---- 1. Version-line parsing is pure ----------------------------------------------------
        string banner = "ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024 the FFmpeg developers";
        Expect(FfmpegPreflight.ParseVersion(banner) == "7.1-full_build-www.gyan.dev",
            $"The version token must be parsed out of the banner; got '{FfmpegPreflight.ParseVersion(banner)}'.");
        Expect(FfmpegPreflight.ParseVersion("ffmpeg version n7.0.2 Copyright (c) 2000-2024") == "n7.0.2",
            "A nightly-style version token must parse too.");
        Expect(FfmpegPreflight.ParseVersion("not an ffmpeg banner") == null,
            "A line with no version token must parse to null rather than guessing.");
        Expect(FfmpegPreflight.ParseVersion(null) == null, "A missing banner must parse to null.");
        Expect(FfmpegPreflight.ParseVersion("ffmpeg version    ") == null,
            "A banner with nothing after 'version' must parse to null.");

        // ---- 2. Resolver ORDER: application directory first, then PATH ---------------------------
        string appDir = @"C:\app";
        string appExe = Path.Combine(appDir, "ffmpeg.exe");
        string pathDir = @"C:\tools";
        string pathExe = Path.Combine(pathDir, "ffmpeg.exe");

        var probed = new List<string>();
        Func<string, string?> probe = exe =>
        {
            probed.Add(exe);
            return exe == appExe || exe == pathExe ? banner : null;
        };

        FfmpegPreflightResult both = FfmpegPreflight.Run(appDir,
            fileExists: f => f == appExe || f == pathExe, versionProbe: probe, pathVariable: pathDir);
        Expect(probed.Count > 0 && probed[0] == appExe,
            "The application directory must be probed FIRST - that is the documented resolution order.");
        Expect(both.IsAvailable && both.ExecutablePath == appExe,
            $"The app directory copy must be preferred over PATH; resolved '{both.ExecutablePath}'.");
        Expect(both.Version == "7.1-full_build-www.gyan.dev", "The parsed version must ride on the result.");
        Expect(both.VersionLine == banner, "The raw version line must be kept for the log.");

        FfmpegPreflightResult viaPath = FfmpegPreflight.Run(appDir,
            fileExists: f => f == pathExe, versionProbe: probe, pathVariable: pathDir);
        Expect(viaPath.IsAvailable && viaPath.ExecutablePath == pathExe,
            $"With no bundled binary the PATH copy must be used; resolved '{viaPath.ExecutablePath}'.");

        FfmpegPreflightResult none = FfmpegPreflight.Run(appDir,
            fileExists: _ => false, versionProbe: probe, pathVariable: pathDir);
        Expect(!none.IsAvailable, "Neither location present must report unavailable.");
        Expect(none.Detail.Contains(appExe) && none.Detail.Contains(pathExe),
            $"The failure detail must NAME the locations checked; got '{none.Detail}'.");

        FfmpegPreflightResult broken = FfmpegPreflight.Run(appDir,
            fileExists: _ => true, versionProbe: _ => null, pathVariable: pathDir);
        Expect(!broken.IsAvailable,
            "A binary that exists but cannot answer -version is NOT usable - it must not be accepted.");

        FfmpegPreflightResult throwing = FfmpegPreflight.Run(appDir,
            fileExists: _ => true,
            versionProbe: _ => throw new InvalidOperationException("boom"),
            pathVariable: null);
        Expect(!throwing.IsAvailable,
            "A probe that throws must be handled, not propagated - a broken binary must not stop startup.");

        FfmpegPreflightResult emptyPath = FfmpegPreflight.Run(appDir,
            fileExists: f => f == FfmpegPreflight.ExecutableName, versionProbe: probe, pathVariable: ";");
        Expect(!emptyPath.IsAvailable,
            "An empty PATH segment must not resolve a bare 'ffmpeg.exe' relative to the working directory.");

        // ---- 3. Absent binary -> persistent warning; available -> it CLEARS ----------------------
        using (var seam = BuildHeadlessSession())
        {
            var vm = seam.ViewModel!;

            vm.ApplyFfmpegPreflight(FfmpegPreflightResult.Missing(none.Detail));
            Expect(vm.IsRecordingUnavailable,
                "A failed preflight must raise the recording-unavailable warning - recording is the only thing affected.");
            Expect(vm.RecordingUnavailableText.Contains("Recording unavailable")
                   && vm.RecordingUnavailableText.Contains(appExe),
                $"The warning must name the locations checked; got '{vm.RecordingUnavailableText}'.");
            Expect(vm.StatusText == vm.RecordingUnavailableText,
                "The warning must also reach the status line, so it is persistent and not tooltip-only.");

            vm.ApplyFfmpegPreflight(FfmpegPreflightResult.Available(appExe, banner));
            Expect(!vm.IsRecordingUnavailable, "An available preflight must CLEAR the warning - never sticky.");
            Expect(vm.RecordingUnavailableText.Length == 0, "Clearing must empty the warning text.");
            Expect(vm.FfmpegVersion == "7.1-full_build-www.gyan.dev",
                $"The ViewModel must carry the resolved version; got '{vm.FfmpegVersion}'.");
            Expect(vm.RecordingHealthFfmpegText == "7.1-full_build-www.gyan.dev",
                "The health readout must project the resolved build - NET-010 provenance, live.");

            // The engine never sees the preflight; the ViewModel stamps provenance onto its snapshot.
            Expect(string.IsNullOrEmpty(seam.Session.GetRecordingHealthSnapshot().FfmpegVersion),
                "The engine's own snapshot carries no provenance - the ViewModel is what stamps it.");
            Expect(RecordingHealth.FfmpegText(default) == "—",
                "An empty snapshot must render as a dash, not a blank cell.");
        }

        // ---- 4. RIDER: an unwritable primary location falls back to LOCALAPPDATA ------------------
        string originalLogDir = CrashForensics.LogDir;
        string originalDumpDir = CrashForensics.DumpDir;
        string sandbox = Path.Combine(Path.GetTempPath(), "omt_pf_" + Guid.NewGuid().ToString("N")[..8]);
        string primaryLog = Path.Combine(sandbox, "app", "logs");
        string primaryDump = Path.Combine(sandbox, "app", "crashdumps");
        string fallbackRoot = Path.Combine(sandbox, "localappdata");
        string fallbackDump = Path.Combine(fallbackRoot, "crashdumps");

        try
        {
            CrashForensics.ConfigureLocations(primaryLog, primaryDump, fallbackRoot, writableProbe: _ => false);
            Expect(CrashForensics.IsUsingFallbackLocation,
                "An unwritable application directory must activate the LOCALAPPDATA fallback.");
            Expect(CrashForensics.DumpDir == fallbackDump,
                $"The fallback dump directory must sit under LOCALAPPDATA; got '{CrashForensics.DumpDir}'.");

            CrashForensics.RegisterStateProvider(
                () => CrashState.Unknown with { IsRecording = true, SegmentIndex = 3 });
            string? redirected = CrashForensics.HandleException(
                "Test.Redirected", new InvalidOperationException("redirected fault"), fatal: false);

            Expect(redirected != null, "The crash handler must still write a report from the fallback.");
            Expect(File.Exists(redirected!), $"The redirected report must exist at {redirected}.");
            Expect(File.ReadAllText(redirected!).Contains("Location    : forensics redirected"),
                "The report must SAY the location was redirected - a reader months later is otherwise misled.");

            // The launch notice must look in BOTH places: a previous run may have used the other one.
            Directory.CreateDirectory(primaryDump);
            File.WriteAllText(Path.Combine(primaryDump, "crash-2026-01-01_00-00-00.dmp"), "primary dump");
            File.WriteAllText(Path.Combine(fallbackDump, "crash-2026-01-02_00-00-00.dmp"), "fallback dump");

            Expect(CrashForensics.DumpDirectories().Length == 2,
                "The next-launch notice must inspect BOTH locations, or a dump from another run is hidden.");
            Expect(CrashForensics.CountDumps() == 2,
                $"The notice must count dumps across both locations; saw {CrashForensics.CountDumps()}.");
            Expect(CrashForensics.ReportPendingDumps() == 2,
                "ReportPendingDumps must report the combined count.");

            // A writable primary resolves back to the application directory (the portable layout).
            CrashForensics.ConfigureLocations(primaryLog, primaryDump, fallbackRoot, writableProbe: _ => true);
            Expect(!CrashForensics.IsUsingFallbackLocation,
                "A writable application directory must be used directly - the portable layout is preferred.");
            Expect(CrashForensics.DumpDir == primaryDump,
                "Resolving with a writable primary must target the application directory.");
        }
        finally
        {
            CrashForensics.RegisterStateProvider(null);
            CrashForensics.UseDirectories(originalLogDir, originalDumpDir);
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }

        Console.WriteLine(" -> Resolver order app-dir > PATH; version parsed; a missing binary warns and then clears; " +
                          "an unwritable primary falls back to LOCALAPPDATA and the notice counts both!");
    }

    /// <summary>
    /// NET-009: crash forensics. The handler is invoked directly - never a real crash - and its output
    /// asserted: the crash log must carry the exception AND the live recording state, keep-last-N must
    /// cap both artifacts, a failed dump must not mask the original exception, and the next-launch
    /// notice must count what is actually present.
    /// </summary>
    static void TestCrashForensics()
    {
        Console.WriteLine("\n[Step 0.687] Verifying crash forensics (crash log + state, keep-last-N, launch notice)...");

        string originalLogDir = CrashForensics.LogDir;
        string originalDumpDir = CrashForensics.DumpDir;

        string sandbox = Path.Combine(Path.GetTempPath(), "omt_crash_" + Guid.NewGuid().ToString("N")[..8]);
        string logsDir = Path.Combine(sandbox, "logs");
        string dumpsDir = Path.Combine(sandbox, "crashdumps");
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(dumpsDir);

        try
        {
            CrashForensics.UseDirectories(logsDir, dumpsDir);

            // ---- 1. A simulated handler invocation records the exception AND the live state ---------
            var liveState = new CrashState(
                IsRecording: true,
                RecordingPath: @"C:\takes\OMT_2026-10-16_part02.mp4",
                FramesWritten: 12345,
                SegmentIndex: 2,
                BroadcastState: "Broadcasting",
                MediaDuration: TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(3),
                EncoderName: "h264_nvenc",
                IsConnected: true);

            CrashForensics.RegisterStateProvider(() => liveState);
            string? reportPath = CrashForensics.HandleException(
                "Test.Handler", new InvalidOperationException("simulated fault"), fatal: false);

            Expect(reportPath != null && File.Exists(reportPath),
                "The crash handler must write a crash log and return its path.");

            string report = File.ReadAllText(reportPath!);
            Expect(report.Contains("Test.Handler"), "The crash log must name the handler that fired.");
            Expect(report.Contains("simulated fault"), "The crash log must carry the exception message.");
            Expect(report.Contains("Recording   : YES"),
                "The crash log must say whether a take was live - the part a stack trace cannot tell you.");
            Expect(report.Contains("OMT_2026-10-16_part02.mp4"), "The crash log must name the file being written.");
            Expect(report.Contains("Frames      : 12345"), "The crash log must carry the frames written.");
            Expect(report.Contains("Segment     : 2"), "The crash log must carry the segment index.");
            Expect(report.Contains("Broadcast   : Broadcasting"), "The crash log must carry the broadcast state.");
            Expect(report.Contains("Encoder     : h264_nvenc"), "The crash log must carry the resolved encoder.");
            Expect(report.Contains("(not written)"),
                "A non-fatal handler must record that no dump was taken rather than implying one was.");

            // ---- 2. A dump failure must not throw and must not mask the original exception ---------
            string blockedDumpDir = Path.Combine(sandbox, "blocked");
            File.WriteAllText(blockedDumpDir, "not a directory");
            CrashForensics.UseDirectories(logsDir, blockedDumpDir);

            string? masked = CrashForensics.HandleException(
                "Test.DumpFailure", new AccessViolationException("native fault"), fatal: true);

            Expect(masked != null && File.Exists(masked),
                "A dump failure must still produce the crash log.");
            Expect(!File.Exists(blockedDumpDir + ".dmp"),
                "The blocked dump directory must not have produced a dump.");
            Expect(File.ReadAllText(masked!).Contains("native fault"),
                "A dump failure must never mask the original exception.");

            CrashForensics.UseDirectories(logsDir, dumpsDir);

            // ---- 2b. A fatal fault on a WRITABLE directory must produce a REAL minidump -----------
            // This is the only coverage of the MiniDumpWriteDump P/Invoke: a wrong signature or a
            // missing dbghelp would otherwise fail silently in the field, which is the whole point of
            // the feature.
            string? withDump = CrashForensics.HandleException(
                "Test.RealDump", new AccessViolationException("native fault"), fatal: true);

            string[] producedDumps = Directory.GetFiles(dumpsDir, "crash-*.dmp");
            Expect(producedDumps.Length == 1,
                $"A fatal fault on a writable directory must write exactly one minidump; saw {producedDumps.Length}.");
            Expect(new FileInfo(producedDumps[0]).Length > 4096,
                $"The minidump must be a real dump, not an empty file; size={new FileInfo(producedDumps[0]).Length}.");
            Expect(withDump != null && !File.ReadAllText(withDump).Contains("(not written)"),
                "A fatal fault with a writable directory must record a real dump path in the report.");
            Console.WriteLine($" -> Real minidump written: {Path.GetFileName(producedDumps[0])}, {new FileInfo(producedDumps[0]).Length:N0} bytes");

            // ---- 3. keep-last-N over the DUMPS ------------------------------------------------------
            int seed = CrashForensics.KeepLastCount + 5;
            for (int i = 0; i < seed; i++)
            {
                string f = Path.Combine(dumpsDir, $"crash-2026-01-01_00-00-{i:00}.dmp");
                File.WriteAllText(f, "dump");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-60).AddSeconds(i));
            }

            CrashForensics.EnforceKeepLastCount();

            int dumpsLeft = Directory.GetFiles(dumpsDir, "crash-*.dmp").Length;
            Expect(dumpsLeft == CrashForensics.KeepLastCount,
                $"keep-last-N must cap the dump directory at {CrashForensics.KeepLastCount}; saw {dumpsLeft}.");
            Expect(File.Exists(Path.Combine(dumpsDir, $"crash-2026-01-01_00-00-{seed - 1:00}.dmp")),
                "keep-last-N must keep the NEWEST dumps.");
            Expect(!File.Exists(Path.Combine(dumpsDir, "crash-2026-01-01_00-00-00.dmp")),
                "keep-last-N must delete the OLDEST dumps.");

            // ---- 4. The crash LOG rotates on the same rule ------------------------------------------
            for (int i = 0; i < seed; i++)
            {
                string f = Path.Combine(logsDir, $"crash-2026-01-01_00-00-{i:00}.log");
                File.WriteAllText(f, "report");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-60).AddSeconds(i));
            }

            CrashForensics.EnforceKeepLastCount();
            int logsLeft = Directory.GetFiles(logsDir, "crash-*.log").Length;
            Expect(logsLeft == CrashForensics.KeepLastCount,
                $"The crash log must rotate on the same keep-last-N rule as the dumps; saw {logsLeft}.");

            // ---- 5. The next-launch notice --------------------------------------------------------
            Expect(CrashForensics.CountDumps() == CrashForensics.KeepLastCount,
                "The launch notice must count the dumps actually on disk.");
            Expect(CrashForensics.ReportPendingDumps() == CrashForensics.KeepLastCount,
                "ReportPendingDumps must report the number of dumps present.");

            // ---- 6. Clean install -> no notice -----------------------------------------------------
            string emptyDumps = Path.Combine(sandbox, "emptydumps");
            Directory.CreateDirectory(emptyDumps);
            CrashForensics.UseDirectories(logsDir, emptyDumps);
            Expect(CrashForensics.ReportPendingDumps() == 0,
                "With no dumps present the next-launch notice must stay silent.");

            // ---- 7. A faulting state provider must not stop the report ----------------------------
            CrashForensics.UseDirectories(logsDir, dumpsDir);
            CrashForensics.RegisterStateProvider(() => throw new InvalidOperationException("provider boom"));

            string? resilient = CrashForensics.HandleException(
                "Test.BadProvider", new Exception("fault"), fatal: false);
            Expect(resilient != null, "A throwing state provider must not stop the crash log being written.");
            Expect(File.ReadAllText(resilient!).Contains("Recording   : no"),
                "With no usable state the report must say so rather than inventing a live take.");

            // ---- 8. The handlers are actually WIRED --------------------------------------------------
            CrashForensics.Install();
            Expect(CrashForensics.IsInstalled,
                "Install() must wire AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException.");

            // ---- 9. The report builder is pure and deterministic -----------------------------------
            DateTime when = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            string a = CrashForensics.BuildCrashReport("S", "E", CrashState.Unknown, null, when);
            string b = CrashForensics.BuildCrashReport("S", "E", CrashState.Unknown, null, when);
            Expect(a == b, "The report builder must be deterministic for a fixed timestamp.");
            Expect(a.Contains("Dump        : (not written)"), "An absent dump must read as not written.");
        }
        finally
        {
            CrashForensics.RegisterStateProvider(null);
            CrashForensics.UseDirectories(originalLogDir, originalDumpDir);
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }

        Console.WriteLine(" -> Crash log carries the live recording state; keep-last-N caps logs and dumps; " +
                          "a failed dump never masks the exception; the launch notice counts what is present!");
    }

    /// <summary>
    /// NET-008: scheduled / segmented capture. Pure boundary and schedule arithmetic, a preset
    /// round-trip, and the rollover driven through the ARM on an injected clock - so the whole policy
    /// is asserted with zero real waits.
    /// </summary>
    static void TestScheduledSegmentedCapture()
    {
        Console.WriteLine("\n[Step 0.686] Verifying scheduled/segmented capture (rollover, max-duration, presets, schedule)...");

        long t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long tenMin = TimeSpan.FromMinutes(10).Ticks;

        // ---- 1. Rollover arithmetic is pure and BOUNDARY-EXACT --------------------------------
        var minutes = new RecordingConfig { SegmentRolloverMinutes = 10 };
        Expect(!RecordingBoundaries.RolloverDue(minutes, t0, 0, t0 + tenMin - 1),
            "A 10-minute rollover must NOT fire one tick before the boundary.");
        Expect(RecordingBoundaries.RolloverDue(minutes, t0, 0, t0 + tenMin),
            "A 10-minute rollover must fire exactly AT the boundary.");

        var bySize = new RecordingConfig { SegmentRolloverGigabytes = 2 };
        Expect(!RecordingBoundaries.RolloverDue(bySize, t0, 1_999_999_999, t0),
            "A 2 GB rollover must not fire one byte early.");
        Expect(RecordingBoundaries.RolloverDue(bySize, t0, 2_000_000_000, t0),
            "A 2 GB rollover must fire at exactly 2.0 decimal GB.");

        var bothTriggers = new RecordingConfig { SegmentRolloverMinutes = 60, SegmentRolloverGigabytes = 1 };
        Expect(RecordingBoundaries.RolloverDue(bothTriggers, t0, 1_000_000_000, t0),
            "With both triggers set, the segment must roll on whichever fires first (size here, 1 min in).");

        var allOff = new RecordingConfig();
        Expect(!RecordingBoundaries.RolloverDue(allOff, t0, long.MaxValue / 4, t0 + TimeSpan.FromDays(30).Ticks),
            "With both rollover triggers off, nothing may ever roll a segment.");

        // ---- 2. Priority: a rollover beats a take-level stop due at the same instant -----------
        var tie = new RecordingConfig { SegmentRolloverMinutes = 10, MaxDurationMinutes = 10 };
        Expect(RecordingBoundaries.Evaluate(tie, t0, t0, 0, t0 + tenMin) == RecordingBoundary.SegmentRollover,
            "When a rollover and the max duration fall due together the rollover must win, so the take " +
            "yields a full set of segments instead of stopping early.");

        // ---- 3. Max duration and scheduled stop ------------------------------------------------
        var maxOnly = new RecordingConfig { MaxDurationMinutes = 30 };
        Expect(RecordingBoundaries.Evaluate(maxOnly, t0, t0, 0, t0 + TimeSpan.FromMinutes(30).Ticks - 1) == RecordingBoundary.None,
            "One tick before the max duration the take must keep recording.");
        Expect(RecordingBoundaries.Evaluate(maxOnly, t0, t0, 0, t0 + TimeSpan.FromMinutes(30).Ticks) == RecordingBoundary.MaxDuration,
            "At the max duration the take must report MaxDuration.");

        // Max duration is measured from the TAKE start: a segment that began 20 min in has 10 min left.
        var spanning = new RecordingConfig { MaxDurationMinutes = 30, SegmentRolloverMinutes = 25 };
        long segStartAt20 = t0 + TimeSpan.FromMinutes(20).Ticks;
        Expect(RecordingBoundaries.Evaluate(spanning, t0, segStartAt20, 0, segStartAt20 + TimeSpan.FromMinutes(5).Ticks) == RecordingBoundary.None,
            "Max duration must be measured from the TAKE start, not the current segment: 25 min into a " +
            "30 min take with a fresh 25 min segment is still recording.");
        Expect(RecordingBoundaries.Evaluate(spanning, t0, segStartAt20, 0, t0 + TimeSpan.FromMinutes(30).Ticks) == RecordingBoundary.MaxDuration,
            "30 minutes into the TAKE, the max duration must fire even though the current segment is young.");

        long stopTicks = t0 + TimeSpan.FromMinutes(45).Ticks;
        var schedStop = new RecordingConfig { ScheduledStopUtc = new DateTime(stopTicks, DateTimeKind.Utc) };
        Expect(RecordingBoundaries.Evaluate(schedStop, t0, t0, 0, stopTicks - 1) == RecordingBoundary.None,
            "One tick before the scheduled stop the take must keep recording.");
        Expect(RecordingBoundaries.Evaluate(schedStop, t0, t0, 0, stopTicks) == RecordingBoundary.ScheduledStop,
            "At the scheduled wall-clock stop the take must report ScheduledStop.");

        // ---- 4. Scheduled START arithmetic -----------------------------------------------------
        var at0 = new DateTime(t0, DateTimeKind.Utc);
        var delayed = new RecordingConfig { ScheduledStartDelayMinutes = 5 };
        Expect(RecordingSchedule.ResolveStartUtc(delayed, at0) == at0.AddMinutes(5),
            "A 5-minute start delay must resolve to now + 5 minutes.");
        var absolute = new RecordingConfig { ScheduledStartUtc = new DateTime(stopTicks, DateTimeKind.Utc) };
        Expect(RecordingSchedule.ResolveStartUtc(absolute, at0) == new DateTime(stopTicks, DateTimeKind.Utc),
            "An absolute scheduled start must resolve to that instant.");

        var bothStarts = new RecordingConfig
        {
            ScheduledStartUtc = new DateTime(stopTicks, DateTimeKind.Utc),
            ScheduledStartDelayMinutes = 5
        };
        Expect(RecordingSchedule.ResolveStartUtc(bothStarts, at0) == at0.AddMinutes(5),
            "When a delay and an absolute instant are both set the DELAY wins (the more specific instruction).");

        Expect(!RecordingSchedule.StartReached(at0.AddMinutes(5), at0),
            "A start instant in the future must not read as reached.");
        Expect(RecordingSchedule.StartReached(at0.AddMinutes(5), at0.AddMinutes(5)),
            "A start instant exactly now must read as reached.");
        Expect(RecordingSchedule.StartReached(null, at0),
            "No scheduled instant at all means 'start now'.");

        // ---- 5. Preset round-trip --------------------------------------------------------------
        string mine = Path.Combine(Path.GetTempPath(), "mine");
        var cfg = new RecordingConfig { OutputDirectory = Path.Combine(Path.GetTempPath(), "preset-dest"), FilenamePrefix = "PFX" };
        foreach (RecordingPreset preset in RecordingPresets.BuiltIn)
        {
            RecordingPresets.Apply(preset, cfg);
            Expect(RecordingPresets.Matches(preset, cfg),
                $"Preset '{preset.Name}' must match the config it was just applied to.");

            RecordingPreset captured = RecordingPresets.Capture(cfg, preset.Name, preset.Description);
            Expect(captured.ContainerFormat == preset.ContainerFormat
                   && captured.EncoderChoice == preset.EncoderChoice
                   && captured.Quality == preset.Quality
                   && captured.SegmentRolloverMinutes.Equals(preset.SegmentRolloverMinutes)
                   && captured.SegmentRolloverGigabytes.Equals(preset.SegmentRolloverGigabytes)
                   && captured.MaxDurationMinutes.Equals(preset.MaxDurationMinutes),
                $"Preset '{preset.Name}' must survive an Apply then Capture round-trip on every axis.");
        }

        var noDest = new RecordingPreset("No destination", "", OutputContainerFormat.MP4, VideoEncoderChoice.AutoHardware, QualityPreset.High);
        var destCfg = new RecordingConfig { OutputDirectory = mine, FilenamePrefix = "MINE" };
        RecordingPresets.Apply(noDest, destCfg);
        Expect(destCfg.OutputDirectory == mine,
            "A preset carrying no destination must leave the operator's folder untouched - a preset must not " +
            "redirect output on a machine it was not authored on.");
        Expect(destCfg.FilenamePrefix == "MINE",
            "A preset carrying no prefix must leave the operator's prefix untouched.");
        Expect(RecordingPresets.Matches(noDest, new RecordingConfig()),
            "A destination-less preset must treat the folder as a wildcard: a default config on the same " +
            "codec axis DOES match it (Matches is not a destination assertion).");
        Expect(!RecordingPresets.Matches(noDest, new RecordingConfig { Quality = QualityPreset.Low }),
            "A config whose quality differs from the preset must NOT match it.");

        var setsRollover = new RecordingPreset("Rolls", "", OutputContainerFormat.MP4, VideoEncoderChoice.AutoHardware, QualityPreset.High)
        {
            SegmentRolloverMinutes = 10
        };
        RecordingPresets.Apply(setsRollover, destCfg);
        Expect(destCfg.SegmentRolloverMinutes.Equals(10d), "A preset that sets a rollover must write it.");
        RecordingPresets.Apply(noDest, destCfg);
        Expect(destCfg.SegmentRolloverMinutes.Equals(0d),
            "A preset with no rollover must CLEAR a rollover left by a previous preset rather than inherit it.");

        // ---- 6. The rollover through the ARM, on an injected clock -----------------------------
        long nowTicks = t0;
        var armCfg = new RecordingConfig
        {
            OutputDirectory = Path.GetTempPath(),
            FilenamePrefix = "RollTest",
            SegmentRolloverMinutes = 10
        };
        var format = new StreamFormat { Width = 320, Height = 240, FrameRate = 10.0, SampleRate = 48000, Channels = 2 };
        var armSink = new SignalRecordingSink();
        using var arm = new RecordingArm(armSink, ownsSink: false, utcNowTicks: () => nowTicks);

        if (!arm.Arm(armCfg, format))
        {
            Console.WriteLine(" -> [SKIP] storage preflight refused the temp volume; the arm-level rollover is skipped " +
                              "(the pure arithmetic above still covers the rollover policy).");
            return;
        }

        string firstPath = arm.CurrentRecordingPath!;
        long takeStart = arm.TakeStartUtcTicks;

        nowTicks = takeStart + tenMin - 1;
        Expect(arm.EvaluateBoundary(armCfg) == RecordingBoundary.None,
            "One tick before the rollover the arm must report no boundary.");
        Expect(arm.IsRecording, "No boundary due means the gate stays open.");

        nowTicks = takeStart + tenMin;
        Expect(arm.EvaluateBoundary(armCfg) == RecordingBoundary.SegmentRollover,
            "At the boundary the arm must report SegmentRollover.");

        int summaries = 0;
        arm.RecordingStopped += _ => summaries++;

        Expect(arm.RollSegment(armCfg, format, RecordingStopReason.SegmentRollover),
            "RollSegment must finalize the segment and arm the continuation.");
        Expect(summaries == 1,
            "A rollover must publish exactly one segment summary - it goes through the single finalize path.");
        Expect(arm.IsRecording, "After a rollover the take must still be recording (the take continues).");
        Expect(arm.CurrentSegmentIndex == 2,
            $"A rollover must advance to segment 2; saw {arm.CurrentSegmentIndex}.");
        Expect(arm.CurrentTakeBasePath == firstPath,
            "A rollover must keep the SAME take base path so the _partNN files sort together.");
        Expect(arm.CurrentRecordingPath != null
               && arm.CurrentRecordingPath != firstPath
               && arm.CurrentRecordingPath.Contains("_part02"),
            $"A rolled segment must be named _part02; saw {arm.CurrentRecordingPath}.");
        Expect(arm.TakeStartUtcTicks == takeStart,
            "A rollover must NOT reset the take clock - max duration is measured from the take start.");
        Expect(arm.SegmentStartUtcTicks == nowTicks,
            "A rollover must reset the SEGMENT clock so the new segment gets a full rollover budget.");
        Expect(arm.EvaluateBoundary(armCfg) == RecordingBoundary.None,
            "The rolled-in segment must start with a fresh rollover budget.");

        // Take-level max duration, once the segment clock is fresh.
        armCfg.MaxDurationMinutes = 15;
        nowTicks = takeStart + TimeSpan.FromMinutes(15).Ticks;
        Expect(arm.EvaluateBoundary(armCfg) == RecordingBoundary.MaxDuration,
            "15 minutes into the TAKE (5 minutes into segment 2) the arm must report MaxDuration.");

        // ... and the rollover still wins when both fall due.
        nowTicks = arm.SegmentStartUtcTicks + tenMin;
        Expect(arm.EvaluateBoundary(armCfg) == RecordingBoundary.SegmentRollover,
            "Through the arm, a rollover due at the same moment as the max duration still wins.");

        // ---- 7. Reason mapping: a rollover is named as a CONTINUATION, never as a stop ----------
        Expect(RecordingHealth.ReasonText(RecordingStopReason.SegmentRollover) == "segment complete: continuing",
            $"A rollover must be named as a continuation, not a stop; got '{RecordingHealth.ReasonText(RecordingStopReason.SegmentRollover)}'.");
        Expect(RecordingHealth.ReasonText(RecordingStopReason.MaxDuration) == "stopped: max duration",
            $"Max duration must be named; got '{RecordingHealth.ReasonText(RecordingStopReason.MaxDuration)}'.");
        Expect(RecordingHealth.ReasonText(RecordingStopReason.ScheduledStop) == "stopped: scheduled stop",
            $"Scheduled stop must be named; got '{RecordingHealth.ReasonText(RecordingStopReason.ScheduledStop)}'.");

        // A rollover is the ONLY boundary; every other reason is a genuine stop.
        Expect(RecordingHealth.IsSegmentBoundary(RecordingStopReason.SegmentRollover),
            "SegmentRollover is the one boundary reason.");
        foreach (RecordingStopReason stopReason in new[]
                 {
                     RecordingStopReason.Manual, RecordingStopReason.LowDisk, RecordingStopReason.InputLoss,
                     RecordingStopReason.SinkError, RecordingStopReason.Dispose,
                     RecordingStopReason.MaxDuration, RecordingStopReason.ScheduledStop
                 })
        {
            Expect(!RecordingHealth.IsSegmentBoundary(stopReason),
                $"{stopReason} is a genuine stop and must NOT be treated as a segment boundary.");
        }

        var rolloverSummary = new RecordingStopSummary(
            Path: Path.Combine(Path.GetTempPath(), "take_part01.mp4"),
            MediaDuration: TimeSpan.FromMinutes(15),
            WallClockDuration: TimeSpan.FromMinutes(15.01),
            AudioDuration: TimeSpan.FromMinutes(15),
            FinalizeDuration: TimeSpan.FromMilliseconds(42),
            FileSizeBytes: 484_030,
            VideoFramesWritten: 9000,
            VideoFramesDropped: 0,
            AudioChunksDropped: 0,
            SegmentIndex: 1,
            EncoderName: "h264_nvenc",
            DiagnosticTail: string.Empty,
            Reason: RecordingStopReason.SegmentRollover);

        string quiet = RecordingHealth.BuildSegmentContinuationText(rolloverSummary);
        Expect(quiet.Contains("Segment 1 complete") && quiet.Contains("continuing") && quiet.Contains("0.5 MB"),
            $"The continuation line must name the segment, say it is continuing and carry its size; got '{quiet}'.");
        Expect(!quiet.Contains("Stop verdict"),
            "A rollover continuation line must never read as a stop verdict.");
        Expect(RecordingHealth.BuildStopVerdict(rolloverSummary).Contains("segment complete: continuing"),
            "Used on a rollover summary, the verdict builder must still name it as a continuation.");

        // ---- 8. The rollover rendering path, end-to-end through the VIEWMODEL ------------------
        using (var vmSeam = BuildHeadlessSession())
        {
            var vm = vmSeam.ViewModel!;
            var vmSession = vmSeam.Session;

            long vmFrames = 0;
            vmSession.VideoFrameAvailable += (ptr, len, w, h, st, fps, ts) => Interlocked.Increment(ref vmFrames);
            vmSession.Connect(new SyntheticPatternSource());

            int vmWait = 0;
            while (vmFrames < 5 && vmWait < 20)
            {
                Thread.Sleep(50);
                vmWait++;
            }

            Expect(vmFrames > 0, "The ViewModel rollover test needs a live synthetic feed.");
            Expect(!vm.IsStopSummaryVisible, "A fresh ViewModel must start with no stop panel.");

            // A threshold low enough to fire inside this test, but reached through the SAME health tick
            // the running app uses - there is no test-only code path.
            var vmConfig = new RecordingConfig
            {
                OutputDirectory = Path.GetTempPath(),
                FilenamePrefix = "VmRoll",
                SegmentRolloverMinutes = 0.002
            };

            Expect(vmSession.StartRecording(vmConfig), "The ViewModel rollover test needs a live take.");
            Expect(vm.RecordButtonText.Contains("STOP"),
                $"A live take must read STOP; got '{vm.RecordButtonText}'.");

            int ticks = 0;
            while (!vm.IsSegmentContinuationVisible && ticks < 60)
            {
                Thread.Sleep(50);
                vmSession.CheckInputHealth();
                ticks++;
            }

            Expect(vm.IsSegmentContinuationVisible,
                "A rollover must raise the QUIET continuation notice on the ViewModel.");
            Expect(vm.SegmentContinuationText.Contains("complete") && vm.SegmentContinuationText.Contains("continuing"),
                $"The notice must read as a continuation; got '{vm.SegmentContinuationText}'.");
            Expect(!vm.IsStopSummaryVisible && string.IsNullOrEmpty(vm.StopSummaryText),
                "A rollover must NOT show the stop verdict panel - the take did not stop.");
            Expect(vm.RecordButtonText.Contains("STOP"),
                $"The record button must still read STOP after a rollover; got '{vm.RecordButtonText}'.");
            Expect(vmSession.IsRecording && vmSession.RecordingSegmentIndex >= 2,
                $"A rollover must leave the take recording in the next segment; segment={vmSession.RecordingSegmentIndex}.");
            Console.WriteLine($" -> ViewModel rollover rendering: quiet notice '{vm.SegmentContinuationText}', stop panel hidden, take live in segment {vmSession.RecordingSegmentIndex}");

            vmSession.StopRecording();

            int clearWait = 0;
            while (vm.SegmentContinuationText.Length != 0 && clearWait < 20)
            {
                Thread.Sleep(25);
                clearWait++;
            }

            Expect(vm.SegmentContinuationText.Length == 0,
                "A genuine stop must clear the rollover notice.");
        }

        Console.WriteLine(" -> Rollover arithmetic boundary-exact (10 min / 2 GB); take clock survives the rollover; " +
                          "presets round-trip; schedule arithmetic pure-clock; arm rolled segment 1 -> 2 as _part02 " +
                          "through the single finalize path!");
    }

    /// <summary>
    /// The exactly-once return invariant, made OBSERVABLE. A double return or a pool mix-up is a
    /// silent leak, not a crash, so structure alone cannot prove it: the ledger is armed here and the
    /// tests assert lease balance across the real queue code paths (drop, drain, dispose, consumer),
    /// and assert that the detector FIRES on a deliberately broken loop.
    /// </summary>
    static void TestMediaBufferPool()
    {
        Console.WriteLine("\n[Step 0.683] Verifying MediaBufferPool (exactly-once return, ledger, pool identity)...");

        // ---- Pool identity travels with the packet, and video frames still pool in the 16 MB pool.
        // This is the regression that matters: routing video to ArrayPool<byte>.Shared would silently
        // stop pooling 1080p frames (8.29 MB > its 1 MB ceiling) and reintroduce the LOH churn
        // LargeArrayPool exists to prevent — with no error, just churn.
        byte[] firstFrame = MediaBufferPool.Video.Rent(8_290_000);
        if (firstFrame.Length < 8_290_000) throw new Exception($"The video pool must serve an 8.29 MB frame; got {firstFrame.Length}.");
        MediaBufferPool.Video.Return(firstFrame);
        byte[] secondFrame = MediaBufferPool.Video.Rent(8_290_000);
        if (!ReferenceEquals(firstFrame, secondFrame))
        {
            throw new Exception("Video frames must POOL (same array back), not reallocate per frame — the LargeArrayPool contract.");
        }
        MediaBufferPool.Video.Return(secondFrame);

        byte[] firstChunk = MediaBufferPool.Audio.Rent(4096);
        MediaBufferPool.Audio.Return(firstChunk);
        byte[] secondChunk = MediaBufferPool.Audio.Rent(4096);
        if (!ReferenceEquals(firstChunk, secondChunk)) throw new Exception("Audio chunks must pool on the shared pool.");
        MediaBufferPool.Audio.Return(secondChunk);

        if (ReferenceEquals(MediaBufferPool.Video, MediaBufferPool.Audio))
        {
            throw new Exception("Video and audio must be distinct pools — that asymmetry is the whole reason the packet carries its owner.");
        }

        // ---- Lease balance across the REAL queue paths, with the ledger armed.
        MediaBufferPoolLedger.Reset();
        MediaBufferPoolLedger.Enabled = true;
        try
        {
            var videoQueue = new BoundedMediaQueue<VideoFramePacket>(4, p => p.Release());
            var audioQueue = new BoundedMediaQueue<AudioDataPacket>(3, p => p.Release());

            // Phase A: normal enqueue → consumer dequeue → release (the writer-loop path).
            for (int i = 0; i < 3; i++)
            {
                videoQueue.Enqueue(new VideoFramePacket(MediaBufferPool.Video.Rent(1000), 1000, MediaBufferPool.Video));
            }
            int consumed = 0;
            while (videoQueue.TryDequeue(out var packet)) { packet.Release(); consumed++; }
            if (consumed != 3) throw new Exception($"Consumer path consumed {consumed}/3.");

            // Phase B: overflow — the drop-oldest path returns the evicted buffer.
            for (int i = 0; i < 6; i++)
            {
                videoQueue.Enqueue(new VideoFramePacket(MediaBufferPool.Video.Rent(1000), 1000, MediaBufferPool.Video));
            }
            if (videoQueue.DroppedCount != 2) throw new Exception($"Expected 2 drops into capacity 4; got {videoQueue.DroppedCount}.");
            while (videoQueue.TryDequeue(out var packet)) { packet.Release(); }

            // Phase C: enqueue while adding is complete — dropped immediately, still returned once.
            videoQueue.CompleteAdding();
            videoQueue.Enqueue(new VideoFramePacket(MediaBufferPool.Video.Rent(1000), 1000, MediaBufferPool.Video));
            if (videoQueue.DroppedCount != 3) throw new Exception("An enqueue after CompleteAdding must count as a drop.");

            // Phase D: disposal drains whatever is still queued through the drop callback.
            for (int i = 0; i < 2; i++)
            {
                audioQueue.Enqueue(new AudioDataPacket(MediaBufferPool.Audio.Rent(512), 512, MediaBufferPool.Audio));
            }
            audioQueue.Dispose();
            videoQueue.Dispose();

            if (MediaBufferPoolLedger.ActiveLeases != 0)
            {
                throw new Exception($"Lease leak: {MediaBufferPoolLedger.ActiveLeases} buffer(s) never returned across drop/drain/dispose/consumer paths.");
            }
            if (MediaBufferPoolLedger.FaultCount != 0)
            {
                throw new Exception($"Ledger faults on the healthy paths: {string.Join(" | ", MediaBufferPoolLedger.Faults)}");
            }
            if (MediaBufferPoolLedger.RentCount != MediaBufferPoolLedger.ReturnCount)
            {
                throw new Exception($"Rent/return imbalance: {MediaBufferPoolLedger.RentCount} vs {MediaBufferPoolLedger.ReturnCount}.");
            }
            Console.WriteLine($" -> Lease balance across drop/drain/complete/dispose/consumer paths: {MediaBufferPoolLedger.RentCount} rents, {MediaBufferPoolLedger.ReturnCount} returns, 0 leaks, 0 faults.");

            // ---- The detector must FIRE, or it proves nothing. A deliberately broken loop.
            MediaBufferPoolLedger.Reset();
            BorkedWriterLoop();
            if (MediaBufferPoolLedger.FaultCount == 0)
            {
                throw new Exception("The double-return detector did NOT fire on a deliberately broken loop — the invariant is unobservable.");
            }
            if (!MediaBufferPoolLedger.Faults[0].Contains("not on loan"))
            {
                throw new Exception($"The double-return fault should say the buffer was not on loan; got: {MediaBufferPoolLedger.Faults[0]}");
            }
            Console.WriteLine($" -> Broken loop detected: \"{MediaBufferPoolLedger.Faults[0]}\"");

            // ---- And the silent one: a pool mix-up.
            MediaBufferPoolLedger.Reset();
            byte[] mixed = MediaBufferPool.Video.Rent(2000);
            MediaBufferPool.Audio.Return(mixed); // wrong pool: silent leak in the old code
            if (MediaBufferPoolLedger.FaultCount == 0 || !MediaBufferPoolLedger.Faults[0].Contains("WRONG POOL"))
            {
                throw new Exception("A pool mix-up must be reported as WRONG POOL, not leaked silently.");
            }
            Console.WriteLine($" -> Pool mix-up detected: \"{MediaBufferPoolLedger.Faults[0]}\"");

            if (MediaBufferPoolLedger.ActiveLeases != 1)
            {
                throw new Exception("A wrong-pool return must leave the lease open (it did not actually return it).");
            }
        }
        finally
        {
            MediaBufferPoolLedger.Enabled = false;
            MediaBufferPoolLedger.Reset();
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Exactly-once return is now observable (lease ledger, drop/drain/dispose paths, broken-loop detector)!");
        Console.ResetColor();
    }

    /// <summary>
    /// NET-004: the ONE writer loop, driven headlessly at line rate by a fake consumer. The Slice-A
    /// detectors are wired through the NEW loop, so a double-return introduced by the extraction would
    /// fail here rather than reach the gate.
    /// </summary>
    static void TestPipeWriterLoop()
    {
        Console.WriteLine("\n[Step 0.684] Verifying PipeWriterLoop<T> (line rate, exactly-once return, error funnel)...");

        MediaBufferPoolLedger.Reset();
        MediaBufferPoolLedger.Enabled = true;
        try
        {
            // ---- 1. Line rate: concurrent enqueue while the loop drains. Capacity is deliberately
            // small so the drop-oldest path runs concurrently with the consumer path.
            var fake = new FakePipe();
            var queue = new BoundedMediaQueue<VideoFramePacket>(16, p => p.Release());
            int connected = 0, dequeued = 0;
            var loop = new PipeWriterLoop<VideoFramePacket>(
                queue,
                connect: () => null,
                write: fake.Write,
                isCancelled: () => false,
                reportError: msg => throw new Exception($"Unexpected error report on the healthy path: {msg}"),
                errorPrefix: "FFmpeg video",
                onConnected: () => connected++,
                onDequeued: () => Interlocked.Increment(ref dequeued));

            const int total = 200;
            var thread = new System.Threading.Thread(loop.Run) { IsBackground = true };
            thread.Start();
            for (int i = 0; i < total; i++)
            {
                queue.Enqueue(new VideoFramePacket(MediaBufferPool.Video.Rent(512), 512, MediaBufferPool.Video));
            }
            queue.CompleteAdding();
            if (!thread.Join(5000)) throw new Exception("The loop did not drain and exit.");
            queue.Dispose();

            if (connected != 1) throw new Exception($"onConnected must fire exactly once; got {connected}.");
            if (loop.WrittenCount != fake.PacketsWritten) throw new Exception("WrittenCount must match the transport's count.");
            long accounted = fake.PacketsWritten + queue.DroppedCount;
            if (accounted != total)
            {
                throw new Exception($"Every packet must be written or dropped exactly once: written {fake.PacketsWritten} + dropped {queue.DroppedCount} != {total}.");
            }
            if (dequeued != fake.PacketsWritten)
            {
                throw new Exception($"onDequeued fired {dequeued} times but {fake.PacketsWritten} packets were written — a dequeued packet must always be written or the loop must report.");
            }
            if (!loop.HasExited) throw new Exception("A drained loop must report HasExited.");
            if (MediaBufferPoolLedger.ActiveLeases != 0)
            {
                throw new Exception($"LEASE LEAK through the new loop: {MediaBufferPoolLedger.ActiveLeases} buffer(s) never returned.");
            }
            if (MediaBufferPoolLedger.FaultCount != 0)
            {
                throw new Exception($"Ledger faults through the new loop: {string.Join(" | ", MediaBufferPoolLedger.Faults)}");
            }
            if (fake.BytesWritten != fake.PacketsWritten * 512L) throw new Exception("Payload byte accounting is off.");
            Console.WriteLine($" -> Line rate: {fake.PacketsWritten} written + {queue.DroppedCount} dropped = {total}; {MediaBufferPoolLedger.RentCount} rents / {MediaBufferPoolLedger.ReturnCount} returns, 0 leaks.");

            // ---- 2. Every failure path must still return the packet it was holding, report ONCE, and exit.
            foreach (var (name, write, connect, expected) in new (string, Func<byte[], int, PipeWriteResult>, Func<string?>, string)[]
            {
                ("disconnected", (b, l) => PipeWriteResult.Disconnected, () => null, "FFmpeg video pipe disconnected unexpectedly."),
                ("write threw", (b, l) => throw new InvalidOperationException("boom"), () => null, "FFmpeg video write error: boom"),
                ("handshake refused", (b, l) => PipeWriteResult.Written, () => "pipe failed to connect.", "FFmpeg video pipe failed to connect."),
                ("handshake threw", (b, l) => PipeWriteResult.Written, () => throw new InvalidOperationException("dead"), "FFmpeg video pipe handshake error: dead"),
            })
            {
                MediaBufferPoolLedger.Reset();
                var failQueue = new BoundedMediaQueue<VideoFramePacket>(8, p => p.Release());
                for (int i = 0; i < 4; i++)
                {
                    failQueue.Enqueue(new VideoFramePacket(MediaBufferPool.Video.Rent(512), 512, MediaBufferPool.Video));
                }
                failQueue.CompleteAdding();

                var errors = new List<string>();
                PipeWrite writer = (b, l) =>
                {
                    try { return write(b, l); }
                    catch (InvalidOperationException ex) { throw new InvalidOperationException(ex.Message); }
                };
                PipeConnect connector = () => connect();

                var failLoop = new PipeWriterLoop<VideoFramePacket>(
                    failQueue, connector, writer, () => false, errors.Add, "FFmpeg video");
                failLoop.Run(); // same thread: the loop must return promptly, not spin

                if (errors.Count != 1) throw new Exception($"[{name}] the funnel must report EXACTLY once; got {errors.Count}.");
                if (errors[0] != expected) throw new Exception($"[{name}] message drifted: got \"{errors[0]}\", expected \"{expected}\".");
                if (!failLoop.HasExited) throw new Exception($"[{name}] the loop must exit after a failure.");

                failQueue.Dispose(); // the drain releases whatever the loop left behind
                if (MediaBufferPoolLedger.ActiveLeases != 0)
                {
                    throw new Exception($"[{name}] LEASE LEAK: {MediaBufferPoolLedger.ActiveLeases} buffer(s) stranded.");
                }
                if (MediaBufferPoolLedger.FaultCount != 0)
                {
                    throw new Exception($"[{name}] ledger faults: {string.Join(" | ", MediaBufferPoolLedger.Faults)}");
                }
                Console.WriteLine($" -> [{name}] one report (\"{errors[0]}\"), loop exited, 0 leases stranded.");
            }

            // ---- 3. The audio prefix produces the historic audio wording from the same loop.
            MediaBufferPoolLedger.Reset();
            var audioQueue = new BoundedMediaQueue<AudioDataPacket>(4, p => p.Release());
            var audioErrors = new List<string>();
            // One packet queued, so the loop actually reaches the transport and takes the
            // disconnected path. (With an empty, still-open queue the loop correctly waits instead.)
            audioQueue.Enqueue(new AudioDataPacket(MediaBufferPool.Audio.Rent(256), 256, MediaBufferPool.Audio));
            var audioLoop = new PipeWriterLoop<AudioDataPacket>(
                audioQueue, () => null, (b, l) => PipeWriteResult.Disconnected, () => false, audioErrors.Add, "FFmpeg audio");
            audioLoop.Run();
            if (audioErrors.Count != 1 || audioErrors[0] != "FFmpeg audio pipe disconnected unexpectedly.")
            {
                throw new Exception($"The audio prefix must produce the audio message; got \"{(audioErrors.Count > 0 ? audioErrors[0] : "<none>")}\".");
            }
            audioQueue.Dispose();

            // ---- 4. Cancellation leaves the queue to release its own backlog (no leak either way).
            MediaBufferPoolLedger.Reset();
            var cancelQueue = new BoundedMediaQueue<VideoFramePacket>(4, p => p.Release());
            cancelQueue.Enqueue(new VideoFramePacket(MediaBufferPool.Video.Rent(256), 256, MediaBufferPool.Video));
            var cancelLoop = new PipeWriterLoop<VideoFramePacket>(
                cancelQueue, () => null, (b, l) => PipeWriteResult.Written, () => true, _ => { }, "FFmpeg video");
            cancelLoop.Run();
            cancelQueue.Dispose();
            if (MediaBufferPoolLedger.ActiveLeases != 0) throw new Exception("A cancelled loop must not strand leases (the queue drain owns them).");
        }
        finally
        {
            MediaBufferPoolLedger.Enabled = false;
            MediaBufferPoolLedger.Reset();
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> One writer loop verified: exactly-once return on every path, single funnel report, historic wording preserved!");
        Console.ResetColor();
    }

    /// <summary>
    /// NET-004 AC3: the encoder×quality → argv table and the AutoHardware resolution rule are pure, so
    /// they are asserted as a table — no ffmpeg spawn, no GPU, no registry. The detector is injected, and
    /// the test proves it is NOT consulted when a concrete encoder is requested.
    /// </summary>
    static void TestFfmpegArguments()
    {
        Console.WriteLine("\n[Step 0.685] Verifying FfmpegArguments (encoder×quality matrix, injected detector)...");

        // --- Quality → CRF: the four presets, exact values. ---
        var crfExpect = new (QualityPreset Quality, int Crf)[]
        {
            (QualityPreset.Ultra, 16),
            (QualityPreset.High, 20),
            (QualityPreset.Medium, 24),
            (QualityPreset.Low, 28),
        };
        foreach (var (quality, expected) in crfExpect)
        {
            int actual = FfmpegArguments.QualityToCrf(quality);
            if (actual != expected)
            {
                throw new Exception($"QualityToCrf({quality}) = {actual}, expected {expected}.");
            }
        }

        // --- Video argv: every concrete encoder × every quality. ProRes ignores quality. ---
        // The expected token is the codec selector; the CRF token is appended for CRF encoders only.
        var encoders = new (VideoEncoderChoice Encoder, string CodecToken, string? CrfFlag)[]
        {
            (VideoEncoderChoice.NvidiaNvenc,    "h264_nvenc",  "-cq"),
            (VideoEncoderChoice.IntelQuickSync, "h264_qsv",    "-global_quality"),
            (VideoEncoderChoice.AmdAmf,         "h264_amf",    null),   // AMF uses a quality knob, not CRF
            (VideoEncoderChoice.ProRes,         "prores_ks",   null),   // profile-fixed, no CRF
            (VideoEncoderChoice.CpuX264,        "libx264",     "-crf"),
        };

        int cells = 0;
        foreach (var (encoder, codecToken, crfFlag) in encoders)
        {
            foreach (var (quality, crf) in crfExpect)
            {
                string args = FfmpegArguments.VideoCodecArguments(encoder, quality);
                cells++;

                if (!args.Contains($"-c:v {codecToken}", StringComparison.Ordinal))
                {
                    throw new Exception($"[{encoder}/{quality}] argv must select {codecToken}; got \"{args}\".");
                }

                if (crfFlag != null)
                {
                    if (!args.Contains($"{crfFlag} {crf}", StringComparison.Ordinal))
                    {
                        throw new Exception($"[{encoder}/{quality}] argv must carry {crfFlag} {crf}; got \"{args}\".");
                    }
                }
                else if (encoder == VideoEncoderChoice.ProRes)
                {
                    // ProRes must be quality-independent: the same argv for all four presets.
                    if (args != FfmpegArguments.VideoCodecArguments(encoder, QualityPreset.Ultra))
                    {
                        throw new Exception($"[ProRes/{quality}] ProRes must not vary with quality; got \"{args}\".");
                    }
                }
            }
        }

        // --- Audio argv: PCM only for MOV+ProRes, AAC everywhere else. ---
        if (FfmpegArguments.AudioCodecArguments(OutputContainerFormat.MOV, VideoEncoderChoice.ProRes) != "-c:a pcm_s24le ")
        {
            throw new Exception("MOV+ProRes must use PCM audio.");
        }
        if (!FfmpegArguments.AudioCodecArguments(OutputContainerFormat.MOV, VideoEncoderChoice.CpuX264).Contains("aac", StringComparison.Ordinal))
        {
            throw new Exception("MOV+x264 must use AAC audio.");
        }
        if (!FfmpegArguments.AudioCodecArguments(OutputContainerFormat.MKV, VideoEncoderChoice.ProRes).Contains("aac", StringComparison.Ordinal))
        {
            throw new Exception("MKV+ProRes must NOT use PCM (the PCM rule is MOV-specific).");
        }

        // --- AutoHardware resolution: the detector is consulted ONLY for AutoHardware. ---
        int detectorCalls = 0;
        VideoEncoderChoice Detector() { detectorCalls++; return VideoEncoderChoice.NvidiaNvenc; }

        VideoEncoderChoice resolved = FfmpegArguments.Resolve(VideoEncoderChoice.AutoHardware, Detector);
        if (resolved != VideoEncoderChoice.NvidiaNvenc)
        {
            throw new Exception($"AutoHardware must resolve to the detected encoder; got {resolved}.");
        }
        if (detectorCalls != 1)
        {
            throw new Exception($"The detector must be called exactly once for AutoHardware; got {detectorCalls}.");
        }

        VideoEncoderChoice passthrough = FfmpegArguments.Resolve(VideoEncoderChoice.CpuX264, Detector);
        if (passthrough != VideoEncoderChoice.CpuX264)
        {
            throw new Exception($"A concrete encoder must pass through unchanged; got {passthrough}.");
        }
        if (detectorCalls != 1)
        {
            throw new Exception($"The detector must NOT be consulted for a concrete encoder; calls rose to {detectorCalls}.");
        }

        // --- Structural: the moved logic really left the host. ---
        string? hostPath = FindRepoSource(Path.Combine("OmtCaptureStudio", "Services", "Sinks", "FfmpegProcessHost.cs"));
        if (hostPath != null)
        {
            string hostText = File.ReadAllText(hostPath);
            if (hostText.Contains("GetVideoCodecArguments", StringComparison.Ordinal))
            {
                throw new Exception("GetVideoCodecArguments must be gone from FfmpegProcessHost (moved to FfmpegArguments).");
            }
            if (hostText.Contains("private static VideoEncoderChoice ResolveEncoder", StringComparison.Ordinal))
            {
                throw new Exception("ResolveEncoder must be gone from FfmpegProcessHost (moved to FfmpegArguments.Resolve).");
            }
            if (!hostText.Contains("DetectHardwareEncoderFromRegistry", StringComparison.Ordinal))
            {
                throw new Exception("The registry probe must remain in the host as the default detector.");
            }
        }
        else
        {
            Console.WriteLine(" [SKIP] FfmpegProcessHost.cs not reachable from the test binary — structural check skipped.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($" -> Encoder×quality matrix: {cells} argv cells asserted; quality→CRF 16/20/24/28; ProRes quality-independent; PCM only for MOV+ProRes; detector called only for AutoHardware; host no longer owns the table!");
        Console.ResetColor();
    }
}

