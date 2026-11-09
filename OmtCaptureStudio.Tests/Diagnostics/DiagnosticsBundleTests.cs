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
    /// <summary>
    /// NET-020: the diagnostics bundle. The readout is asserted PURE first (no filesystem at all), then
    /// collection across BOTH forensics locations, the global keep-last-N rule, the empty-artifact edge,
    /// and a write failure that must return a result rather than throw.
    /// </summary>
    static void TestDiagnosticsBundle()
    {
        Console.WriteLine("\n[Step 0.697] Verifying the diagnostics bundle (readout, dual-location, keep-last-N, empty edge, write failure)...");

        // ---- 1. THE READOUT IS PURE ------------------------------------------------------------------
        var live = new DiagnosticsSnapshot
        {
            AppVersion = "v9.9.9-test",
            FfmpegVersion = "ffmpeg version 9.0.1-test",
            Health = new RecordingHealthSnapshot
            {
                IsRecording = true,
                HasTake = true,
                SegmentIndex = 3,
                EncoderName = "h264_nvenc",
                AchievedBitrateMbps = 12.5,
                VideoFramesWritten = 1234,
                VideoFramesDropped = 7,
                AudioChunksDropped = 2,
                MediaDuration = TimeSpan.FromSeconds(61.5),
                AudioDuration = TimeSpan.FromSeconds(61.5),
                WallClockDuration = TimeSpan.FromSeconds(66),
                AvDriftRatio = 1.0004,
                DiagnosticTail = "frame= 1234 fps=60"
            },
            DetachedChildNames = new[] { "Backup/folder" },
            LogDirectory = "C:/x/logs",
            PrimaryLogDirectory = "C:/x/logs",
            DumpDirectory = "C:/x/crashdumps",
            PrimaryDumpDirectory = "C:/x/crashdumps",
            CrashReportPath = "C:/x/crashdumps/crash-1.dmp",
            GeneratedUtc = new DateTimeOffset(2026, 10, 18, 21, 30, 0, TimeSpan.Zero),
            OsDescription = "Microsoft Windows 11 Pro"
        };

        string text = DiagnosticsBundle.BuildDiagnosticsText(live);
        Expect(text.Contains("v9.9.9-test"), "The readout must carry the app version.");
        Expect(text.Contains("Microsoft Windows 11 Pro"), "The readout must carry the OS.");
        Expect(text.Contains("2026-10-18 21:30:00 UTC"), "The readout must carry the generation time.");
        Expect(text.Contains("h264_nvenc"), "The readout must carry the encoder actually used.");
        Expect(text.Contains("segment index : 3"), "The readout must carry the segment index.");
        Expect(text.Contains("video frames  : 1234 written, 7 dropped"), "The readout must carry both frame counters.");
        Expect(text.Contains("audio chunks  : 2 dropped"), "The readout must carry the dropped audio chunks.");
        Expect(text.Contains("12.50 Mbps"), "The readout must carry the achieved bitrate.");
        Expect(text.Contains("frame= 1234 fps=60"), "The readout must carry the FFmpeg diagnostic tail.");
        Expect(text.Contains("Backup/folder"), "The readout must name a detached destination.");
        Expect(text.Contains("status : available"), "A proven ffmpeg must read as available.");
        Expect(!text.Contains("UNAVAILABLE"), "An available ffmpeg must not report UNAVAILABLE.");

        var down = live with
        {
            IsRecordingUnavailable = true,
            RecordingUnavailableText = "ffmpeg was not found in the application directory or on PATH",
            FfmpegVersion = string.Empty,
            DetachedChildNames = Array.Empty<string>()
        };
        string downText = DiagnosticsBundle.BuildDiagnosticsText(down);
        Expect(downText.Contains("status : UNAVAILABLE"), "A failed preflight must read as UNAVAILABLE.");
        Expect(downText.Contains("ffmpeg was not found in the application directory"), "The unavailability reason must be spelled out.");
        Expect(downText.Contains("no destination was detached"), "No detach must say so rather than printing nothing.");
        Expect(DiagnosticsBundle.BuildDiagnosticsText(live) == text, "The readout must be pure - the same snapshot must yield the same text.");

        // ---- 2. COLLECTION: BOTH LOCATIONS, GLOBAL KEEP-LAST-N ---------------------------------------
        string root = Path.Combine(Path.GetTempPath(), "omt-diag-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string logs = Path.Combine(root, "logs");
        string oldLogs = Path.Combine(root, "readonly-run", "logs");
        string dumps = Path.Combine(root, "crashdumps");
        string oldDumps = Path.Combine(root, "readonly-run", "crashdumps");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(oldLogs);
        Directory.CreateDirectory(dumps);
        Directory.CreateDirectory(oldDumps);

        var stamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 7; i++) WriteTemp(Path.Combine(logs, $"2026-10-{i + 1:00}.log"), $"day {i}", stamp.AddDays(i));
        WriteTemp(Path.Combine(logs, "crash-0.log"), "boom", stamp.AddDays(30));
        WriteTemp(Path.Combine(logs, "crash-1.log"), "boom again", stamp.AddDays(31));
        for (int i = 0; i < 4; i++) WriteTemp(Path.Combine(dumps, $"crash-{i}.dmp"), "dump", stamp.AddDays(i));

        // A previous run fell back to LOCALAPPDATA while this one resolves to the app directory. Its
        // artifacts are the NEWEST and exist ONLY there - collecting just the active directory would
        // omit exactly what the operator is being asked to send.
        WriteTemp(Path.Combine(oldLogs, "2026-09-09.log"), "old day (newest of all)", stamp.AddDays(100));
        WriteTemp(Path.Combine(oldDumps, "crash-9.dmp"), "old dump (newest of all)", stamp.AddDays(200));

        var spread = live with
        {
            LogDirectory = logs,
            PrimaryLogDirectory = oldLogs,
            DumpDirectory = dumps,
            PrimaryDumpDirectory = oldDumps
        };

        var picked = DiagnosticsBundle.CollectArtifacts(spread);
        var names = picked.Select(a => a.EntryName).ToList();

        int daily = picked.Count(a => a.EntryName.StartsWith("logs/"));
        int crashLogs = picked.Count(a => a.EntryName.StartsWith("crashlogs/"));
        int dumpsPicked = picked.Count(a => a.EntryName.StartsWith("crashdumps/"));

        Expect(daily == DiagnosticsBundle.KeepLastLogs,
            $"Keep-last-N must be GLOBAL across locations: expected {DiagnosticsBundle.KeepLastLogs} daily logs, got {daily}.");
        Expect(names.Contains("logs/2026-09-09.log"),
            "A daily log that exists ONLY in the fallback location must still be collected.");
        Expect(!names.Contains("logs/2026-10-01.log"),
            "The OLDEST daily log must be dropped by the cap; the pick was not by recency.");
        Expect(crashLogs == 2, $"Both crash reports belong in the bundle (under the cap); got {crashLogs}.");
        Expect(dumpsPicked == DiagnosticsBundle.KeepLastDumps,
            $"Expected the newest {DiagnosticsBundle.KeepLastDumps} dumps, got {dumpsPicked}.");
        Expect(names.Contains("crashdumps/crash-9.dmp"),
            "A minidump that exists ONLY in the fallback location must still be collected.");
        Expect(picked.Count == names.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "Entry names must be unique - an archive with duplicate entries is not valid.");

        // ---- 3. A REAL ARCHIVE: diagnostics.txt is first and carries the readout ---------------------
        string outDir = Path.Combine(root, "out");
        string bundlePath = Path.Combine(outDir, DiagnosticsBundle.DefaultFileName(spread.GeneratedUtc));

        DiagnosticsBundleResult made = DiagnosticsBundle.Create(spread, bundlePath);
        Expect(made.Success, $"Creating the bundle must succeed; got '{made.Error}'.");
        Expect(File.Exists(bundlePath), "Create must have written the archive.");
        Expect(made.SizeBytes > 0, "The archive must not be empty.");
        Expect(made.Entries.Count == picked.Count + 1, "The archive must hold every artifact plus the readout.");
        Expect(made.Entries[0] == DiagnosticsBundle.DiagnosticsEntryName, "diagnostics.txt must be the FIRST entry.");

        using (var zip = ZipFile.OpenRead(bundlePath))
        {
            var entryNames = zip.Entries.Select(e => e.FullName).ToList();
            Expect(entryNames.Contains(DiagnosticsBundle.DiagnosticsEntryName), "The archive must contain diagnostics.txt.");
            Expect(entryNames.Contains("logs/2026-09-09.log"), "The archive must contain the fallback-location log.");
            Expect(entryNames.Contains("crashdumps/crash-9.dmp"), "The archive must contain the fallback-location dump.");

            string inside;
            using (var reader = new StreamReader(zip.GetEntry(DiagnosticsBundle.DiagnosticsEntryName)!.Open()))
            {
                inside = reader.ReadToEnd();
            }

            Expect(inside.Contains("v9.9.9-test"), "The bundled readout must carry the version.");
            Expect(inside.Contains("h264_nvenc"), "The bundled readout must carry the health snapshot.");
            Expect(inside.Contains("logs/2026-09-09.log"),
                "The bundled readout must list the artifacts actually in the archive.");
        }

        // ---- 4. THE EMPTY EDGE: no logs, no dumps, still a VALID bundle ------------------------------
        var barren = live with
        {
            LogDirectory = Path.Combine(root, "no-such-logs"),
            PrimaryLogDirectory = Path.Combine(root, "no-such-primary-logs"),
            DumpDirectory = Path.Combine(root, "no-such-dumps"),
            PrimaryDumpDirectory = Path.Combine(root, "no-such-primary-dumps"),
            CrashReportPath = null
        };
        Expect(DiagnosticsBundle.CollectArtifacts(barren).Count == 0, "A dir-less snapshot must collect nothing.");

        string emptyPath = Path.Combine(outDir, "empty.zip");
        DiagnosticsBundleResult empty = DiagnosticsBundle.Create(barren, emptyPath);
        Expect(empty.Success, $"A bundle with no artifacts must still succeed; got '{empty.Error}'.");
        Expect(empty.Entries.Count == 1 && empty.Entries[0] == DiagnosticsBundle.DiagnosticsEntryName,
            "An empty bundle must carry diagnostics.txt and nothing else.");
        Expect(File.Exists(emptyPath) && new FileInfo(emptyPath).Length > 0,
            "An empty bundle must still be a real, non-zero-byte archive.");

        using (var zip = ZipFile.OpenRead(emptyPath))
        {
            using var reader = new StreamReader(zip.GetEntry(DiagnosticsBundle.DiagnosticsEntryName)!.Open());
            string inside = reader.ReadToEnd();
            Expect(inside.Contains("no logs or crash artifacts were found"),
                "An empty bundle's readout must SAY it found nothing, not just omit the section.");
        }

        // ---- 5. A WRITE FAILURE RETURNS A RESULT, AND LEAVES NO HALF-WRITTEN ARCHIVE -----------------
        string blocker = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        string impossible = Path.Combine(blocker, "bundle.zip");
        DiagnosticsBundleResult failed = DiagnosticsBundle.Create(spread, impossible);
        Expect(!failed.Success, "A bundle that cannot be written must report failure, not throw.");
        Expect(!string.IsNullOrWhiteSpace(failed.Error), "A failed bundle must carry the reason.");
        Expect(failed.Summary.Contains("could not be created"), "The status line must say the bundle failed.");
        Expect(!File.Exists(impossible), "A failed bundle must not leave a file behind.");

        DiagnosticsBundleResult noPath = DiagnosticsBundle.Create(spread, "");
        Expect(!noPath.Success && noPath.Error != null, "An empty output path must fail cleanly, not throw.");

        // ---- 6. DEFAULTS -----------------------------------------------------------------------------
        Expect(DiagnosticsBundle.DefaultFileName(stamp).EndsWith(".zip"), "The default bundle name must be a zip.");
        Expect(DiagnosticsBundle.DefaultFileName(stamp) != DiagnosticsBundle.DefaultFileName(stamp.AddSeconds(1)),
            "The default name must be timestamped so repeat exports cannot overwrite each other.");
        Expect(DiagnosticsBundle.DefaultBundleDirectory(spread) == Path.Combine(root, "diagnostics"),
            $"The default bundle folder belongs beside the logs; got '{DiagnosticsBundle.DefaultBundleDirectory(spread)}'.");

        // ---- 7. END-TO-END THROUGH THE VIEW MODEL ----------------------------------------------------
        // The module is proven above; this proves the WIRING - the view model builds the snapshot from
        // the live surfaces, derives the destination, and writes a real archive. The forensics locations
        // are captured first and restored in a finally, so this step cannot leak state into the steps
        // that follow it.
        string[] logDirsBefore = CrashForensics.LogDirectories();
        string[] dumpDirsBefore = CrashForensics.DumpDirectories();
        string fallbackBefore = CrashForensics.FallbackRoot;

        string vmRoot = Path.Combine(root, "vm");
        string vmLogs = Path.Combine(vmRoot, "logs");
        string vmDumps = Path.Combine(vmRoot, "crashdumps");
        Directory.CreateDirectory(vmLogs);
        Directory.CreateDirectory(vmDumps);
        WriteTemp(Path.Combine(vmLogs, "2026-10-18.log"), "a live line", stamp.AddDays(300));
        WriteTemp(Path.Combine(vmDumps, "crash-1.dmp"), "a dump", stamp.AddDays(300));

        try
        {
            CrashForensics.ConfigureLocations(vmLogs, vmDumps, Path.Combine(vmRoot, "fallback"));

            using var vmSeam = BuildHeadlessSession();
            var vm = vmSeam.ViewModel!;

            Expect(vm.IsDiagnosticsBundleOffered, "One dump is present, so the next-launch offer must be ON.");
            Expect(vm.DiagnosticsBundleOfferText.Contains("1 crash dump"),
                $"The offer must state how many dumps are waiting; got '{vm.DiagnosticsBundleOfferText}'.");
            Expect(!vm.IsDiagnosticsBundleStatusVisible, "No bundle has been created yet, so no outcome line.");

            vm.CreateDiagnosticsBundleCommand.Execute(null);

            Expect(vm.IsDiagnosticsBundleStatusVisible, "Creating a bundle must publish the outcome line.");
            Expect(vm.DiagnosticsBundleStatusText.Contains("saved"),
                $"A successful export must read as saved; got '{vm.DiagnosticsBundleStatusText}'.");

            string expectedDir = Path.Combine(vmRoot, DiagnosticsBundle.BundleFolder);
            string[] written = Directory.Exists(expectedDir)
                ? Directory.GetFiles(expectedDir, "*.zip")
                : Array.Empty<string>();

            Expect(written.Length == 1, $"The view model must write exactly one archive; found {written.Length}.");
            if (written.Length == 1)
            {
                using var zip = ZipFile.OpenRead(written[0]);
                var inZip = zip.Entries.Select(e => e.FullName).ToList();
                Expect(inZip.Contains(DiagnosticsBundle.DiagnosticsEntryName),
                    "The view model's bundle must carry the generated readout.");
                Expect(inZip.Contains("logs/2026-10-18.log"),
                    "The view model's bundle must carry the log from the configured location.");
                Expect(inZip.Contains("crashdumps/crash-1.dmp"),
                    "The view model's bundle must carry the minidump from the configured location.");
            }
        }
        finally
        {
            CrashForensics.ConfigureLocations(logDirsBefore[0], dumpDirsBefore[0], fallbackBefore);
        }

        // ---- 8. THE DETACH CHAIN: sink -> service -> session -> the generated readout ---------------
        // Brief item 1 wants the composite's detach history in diagnostics.txt. This drives a REAL
        // composite until a child detaches and then walks the whole chain the bundle reads through.
        {
            const string ChainPrimary = "C:/take/chain.mp4";
            const string ChainBackup = "D:/take/chain.mp4";
            var chainConfig = new RecordingConfig { OutputDirectory = Path.GetTempPath(), FilenamePrefix = "DiagChain" };
            var chainFormat = new StreamFormat { Width = 320, Height = 240, FrameRate = 10.0, SampleRate = 48000, Channels = 2 };
            IntPtr chainFrame = System.Runtime.InteropServices.Marshal.AllocHGlobal(64);

            try
            {
                var keepsWriting = new ScriptedRecordingSink("primary");
                var goesAway = new ScriptedRecordingSink("backup")
                {
                    ThrowOnVideo = new IOException("the backup folder went away")
                };

                using var composite = new CompositeRecordingSink(
                    new CompositeChild("primary", keepsWriting, ChainPrimary),
                    new CompositeChild("backup", goesAway, ChainBackup));

                var chained = new StreamRecorderService(composite);
                using var chainedSession = new CaptureSession(new AudioEngine(), chained);

                // The honest default: nothing detached, and every layer says so rather than guessing.
                Expect(composite.DetachedDestinationNames.Count == 0,
                    "A fresh composite must report no detached destination.");
                Expect(chained.DetachedDestinationNames.Count == 0,
                    "The service must forward the empty list, not invent one.");
                Expect(chainedSession.DetachedDestinationNames.Count == 0,
                    "The session must forward the empty list too - a single/healthy take has detached nothing.");

                composite.Initialize(chainConfig, chainFormat, ChainPrimary);
                composite.WriteVideo(chainFrame, 64);   // the backup child throws -> it detaches

                Expect(composite.DetachedChildNames.Count == 1 && composite.DetachedChildNames[0] == "backup",
                    $"The failing child must be reported as detached; got [{string.Join(",", composite.DetachedChildNames)}].");

                var viaService = chained.DetachedDestinationNames;
                Expect(viaService.Count == 1 && viaService[0] == "backup",
                    $"StreamRecorderService must forward the composite's detach list unchanged; got [{string.Join(",", viaService)}].");

                var viaSession = chainedSession.DetachedDestinationNames;
                Expect(viaSession.Count == 1 && viaSession[0] == "backup",
                    $"CaptureSession must surface the composite's detach list - the bundle reaches it no other way; got [{string.Join(",", viaSession)}].");

                // ...and it survives all the way into the generated readout.
                string fanOutText = DiagnosticsBundle.BuildDiagnosticsText(live with { DetachedChildNames = viaSession });
                Expect(fanOutText.Contains("detached: backup"),
                    "A destination detached by the REAL composite must appear in the generated diagnostics.txt.");
                Expect(!fanOutText.Contains("no destination was detached"),
                    "The readout must not claim nothing detached while naming a detached destination.");

                // The DIM default is the OTHER half: a sink that cannot detach anything reports nothing.
                // Declared as the INTERFACE on purpose - a default interface member is only reachable
                // through the interface, never on the implementing class. Same for HasSurvivingDestination.
                IRecordingSink plainSink = new ScriptedRecordingSink("only-destination");
                using (plainSink)
                {
                    Expect(plainSink.DetachedDestinationNames.Count == 0,
                        "A single-destination sink must report no detached destination (the honest DIM default).");
                }

                // ...and the VIEW MODEL must CONSUME the chain rather than the empty stub it carried
                // before this pass. Build a view model over the chained session and read the archive it
                // actually writes - that is the only assertion that discriminates the wiring.
                string[] chainLogsBefore = CrashForensics.LogDirectories();
                string[] chainDumpsBefore = CrashForensics.DumpDirectories();
                string chainFallbackBefore = CrashForensics.FallbackRoot;

                string chainRoot = Path.Combine(root, "vm-chain");
                string chainLogs = Path.Combine(chainRoot, "logs");
                string chainDumps = Path.Combine(chainRoot, "crashdumps");
                Directory.CreateDirectory(chainLogs);
                Directory.CreateDirectory(chainDumps);

                try
                {
                    CrashForensics.ConfigureLocations(chainLogs, chainDumps, Path.Combine(chainRoot, "fallback"));

                    using var chainDiscovery = new OmtDiscoveryService();
                    using var chainVm = new MainWindowViewModel(chainedSession, chainDiscovery);
                    chainVm.CreateDiagnosticsBundleCommand.Execute(null);

                    string chainDir = Path.Combine(chainRoot, DiagnosticsBundle.BundleFolder);
                    string[] chainWritten = Directory.Exists(chainDir)
                        ? Directory.GetFiles(chainDir, "*.zip")
                        : Array.Empty<string>();
                    Expect(chainWritten.Length == 1,
                        $"The view model must write exactly one archive over the chained session; found {chainWritten.Length}.");

                    string chainReadout;
                    using (var zip = ZipFile.OpenRead(chainWritten[0]))
                    using (var reader = new StreamReader(zip.GetEntry(DiagnosticsBundle.DiagnosticsEntryName)!.Open()))
                    {
                        chainReadout = reader.ReadToEnd();
                    }

                    Expect(chainReadout.Contains("detached: backup"),
                        "The VIEW MODEL's bundle must carry the composite's detach history, not the empty stub " +
                        $"it returned before the completion pass. Readout tail: {chainReadout.Substring(Math.Max(0, chainReadout.Length - 220))}");
                    Expect(!chainReadout.Contains("no destination was detached"),
                        "The view model must not claim nothing detached while naming a detached destination.");
                }
                finally
                {
                    CrashForensics.ConfigureLocations(chainLogsBefore[0], chainDumpsBefore[0], chainFallbackBefore);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(chainFrame);
            }
        }

        try { Directory.Delete(root, recursive: true); } catch { }

        Console.WriteLine(" -> Readout, dual-location collection, global keep-last-N, the empty edge, a " +
                          "non-throwing write failure, a real export through the view model, and the full " +
                          "detach chain into diagnostics.txt all verified!");
    }

}
