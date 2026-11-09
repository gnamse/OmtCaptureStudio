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
    // ============================================================================================
    // SOAK-01: --real-sender-soak / --sender — real out-of-process ADR-0002 soak (ported from QA)
    // ============================================================================================
    // The injected-clock tests prove the POLICY; only a real sender killed mid-take proves the
    // ON-DISK outcome (segment finalized at the boundary, _partNN resume, grace expiry, decode-clean).

    /// <summary>
    /// Child mode: broadcast an OMT test-pattern stream and block until the orchestrator kills this
    /// process. Prints exactly one parseable line, then sleeps forever.
    /// </summary>
    static int RunSenderChild(string sourceName)
    {
        using var gen = new OmtTestPatternGenerator(sourceName);
        gen.Start();
        Thread.Sleep(500);
        Console.WriteLine($"SENDER_READY|SourceName={sourceName}|Address={gen.Address}|Url={gen.Url}|Port={gen.Port}|PID={Environment.ProcessId}");
        Console.Out.Flush();

        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    static Process SpawnSender(string exePath, string sourceName, out string address)
    {
        address = string.Empty;
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--sender \"{sourceName}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Process proc = Process.Start(psi) ?? throw new Exception("Failed to start the sender child process.");
        string? line = proc.StandardOutput.ReadLine();
        if (line == null || !line.StartsWith("SENDER_READY|", StringComparison.Ordinal))
        {
            string err = proc.StandardError.ReadToEnd();
            throw new Exception($"Sender child did not report ready. Line='{line}', Err='{err}', Exe='{exePath}'");
        }

        foreach (string part in line.Split('|').Skip(1))
        {
            string[] kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "Address")
            {
                address = kv[1];
            }
        }

        Console.WriteLine($"[{Now()}] Spawned sender PID={proc.Id}: '{sourceName}' address='{address}'");
        return proc;
    }

    static string ResolveDiscoveredAddress(string sourceName, string fallback)
    {
        var disc = libomtnet.OMTDiscovery.GetInstance();
        for (int i = 0; i < 30; i++)
        {
            string[] addrs = disc.GetAddresses() ?? Array.Empty<string>();
            string? match = addrs.FirstOrDefault(a => a.Contains(sourceName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                Console.WriteLine($"[{Now()}] Discovery resolved '{sourceName}' -> '{match}'");
                return match;
            }
            Thread.Sleep(200);
        }

        Console.WriteLine($"[{Now()}] Discovery did not list '{sourceName}' yet; using fallback '{fallback}'");
        return fallback;
    }

    /// <summary>
    /// SOAK-01 orchestrator. Runs two real end-to-end scenarios against child sender processes:
    /// Run 1 kills the sender mid-take and restarts it inside the grace window; Run 2 never brings it
    /// back, so the grace window must expire. Every produced segment is fully decoded.
    /// </summary>
    static int RunRealSenderSoak()
    {
        string appHost = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OmtCaptureStudio.Tests.exe");
        string exePath = File.Exists(appHost) ? appHost : (Environment.ProcessPath ?? appHost);

        string baseOutDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SoakOutput");
        if (Directory.Exists(baseOutDir))
        {
            try { Directory.Delete(baseOutDir, true); } catch { }
        }
        Directory.CreateDirectory(baseOutDir);

        Console.WriteLine("====================================================");
        Console.WriteLine("  [SOAK-01] Real-Sender Soak: input loss -> finalize -> _partNN resume");
        Console.WriteLine("====================================================");
        Console.WriteLine($"Executable : {exePath}");
        Console.WriteLine($"Output Dir : {baseOutDir}");

        // ---------------------------------------------------- RUN 1: recover inside the grace window
        Console.WriteLine("\n--------------------------------------------------------------------------------");
        Console.WriteLine("  RUN 1: mid-recording signal loss + sender restart within the grace window");
        Console.WriteLine("--------------------------------------------------------------------------------");

        string run1Dir = Path.Combine(baseOutDir, "Run1_Recovery");

        // NET-011: Run 1 records THROUGH A COMPOSITE - the same take also lands in a backup folder - so
        // the soak exercises the dual-destination transport over a real network feed with a real outage
        // and segment resume, not just the headless unit path.
        string run1BackupDir = Path.Combine(baseOutDir, "Run1_Recovery_Backup");
        Directory.CreateDirectory(run1Dir);
        string source1 = $"OMT_SOAK_Recovery_{Environment.ProcessId}";

        string seg1Path = string.Empty;
        string seg2Path = string.Empty;
        Process? sender1 = null;
        try
        {
            sender1 = SpawnSender(exePath, source1, out string address1);
            Thread.Sleep(1500);
            string target1 = ResolveDiscoveredAddress(source1, address1);

            using var session = new CaptureSession();
            using var discovery = new OmtDiscoveryService();
            using var vm = new MainWindowViewModel(session, discovery, ownsDependencies: false);

            long videoFrames = 0;
            bool inputLost = false;
            bool inputRecovered = false;

            session.VideoFrameAvailable += (p, l, w, h, s, fps, ts) => Interlocked.Increment(ref videoFrames);
            session.RecordingStarted += path =>
                Console.WriteLine($"[{Now()}] EVENT RecordingStarted segment={session.RecordingSegmentIndex}: {Path.GetFileName(path)}");
            session.RecordingStopped += summary =>
                Console.WriteLine($"[{Now()}] EVENT RecordingStopped segment={summary.SegmentIndex} media={summary.MediaDuration.TotalSeconds:F3}s wall={summary.WallClockDuration.TotalSeconds:F3}s size={summary.FileSizeBytes:N0}");
            session.InputLost += () =>
            {
                inputLost = true;
                Console.WriteLine($"[{Now()}] EVENT InputLost | VM.State={vm.BroadcastState} banner={vm.IsInputLossBannerVisible}");
            };
            session.InputRecovered += () =>
            {
                inputRecovered = true;
                Console.WriteLine($"[{Now()}] EVENT InputRecovered | VM.State={vm.BroadcastState} segment={session.RecordingSegmentIndex}");
            };

            vm.SelectedSource = OmtSourceInfo.Parse(target1);
            vm.ToggleConnectCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            WaitUntil(() => Interlocked.Read(ref videoFrames) >= 60 && session.CurrentFormat.HasVideo, 15_000, "live frames from the sender");
            Console.WriteLine($"[{Now()}] Stream locked: {session.CurrentFormat.VideoSpecsText}, {session.CurrentFormat.AudioSpecsText}");

            var config1 = new RecordingConfig
            {
                ContainerFormat = OutputContainerFormat.MP4,
                EncoderChoice = VideoEncoderChoice.CpuX264,
                Quality = QualityPreset.High,
                OutputDirectory = run1Dir,
                BackupOutputDirectory = run1BackupDir,
                FilenamePrefix = "SOAK_Run1",
                InputLossThresholdSeconds = 5.0,
                InputRecoveryGraceSeconds = 30.0
            };

            if (!session.StartRecording(config1))
            {
                throw new Exception("Run 1: StartRecording failed.");
            }
            seg1Path = session.CurrentRecordingPath ?? string.Empty;
            if (session.RecordingSegmentIndex != 1)
            {
                throw new Exception($"Run 1: expected to open segment 1, got {session.RecordingSegmentIndex}.");
            }

            Console.WriteLine($"[{Now()}] Recording segment 1 for ~6 s...");
            var sw1 = Stopwatch.StartNew();
            while (sw1.ElapsedMilliseconds < 6000)
            {
                Thread.Sleep(200);
                session.CheckInputHealth();
            }

            Console.WriteLine($"[{Now()}] KILLING sender PID={sender1.Id} mid-take (RecFrames={session.RecordingFramesWritten})...");
            sender1.Kill(entireProcessTree: true);
            sender1.WaitForExit(3000);
            sender1.Dispose();
            sender1 = null;

            WaitUntil(() => session.IsInputLost, 25_000, "the input-loss boundary", () => session.CheckInputHealth());

            if (!inputLost) throw new Exception("Run 1: InputLost was never raised.");
            if (session.IsRecording) throw new Exception("Run 1: the take must be finalized at the outage boundary.");
            if (!File.Exists(seg1Path)) throw new Exception($"Run 1: finalized segment 1 is missing: {seg1Path}");
            Console.WriteLine($"[{Now()}] Boundary handled; segment 1 finalized: {Path.GetFileName(seg1Path)}");

            Console.WriteLine($"[{Now()}] Leaving the sender offline ~4.5 s to exercise backoff reconnects...");
            var swDead = Stopwatch.StartNew();
            while (swDead.ElapsedMilliseconds < 4500)
            {
                Thread.Sleep(100);
                session.CheckInputHealth();
            }

            Console.WriteLine($"[{Now()}] Restarting the sender inside the 30 s grace window...");
            sender1 = SpawnSender(exePath, source1, out _);

            WaitUntil(() => !session.IsInputLost && session.IsRecording, 30_000, "auto-reconnect and _partNN resume", () => session.CheckInputHealth());

            seg2Path = session.CurrentRecordingPath ?? string.Empty;
            if (session.RecordingSegmentIndex != 2)
            {
                throw new Exception($"Run 1: expected segment 2 after resume, got {session.RecordingSegmentIndex}.");
            }
            if (!seg2Path.EndsWith("_part02.mp4", StringComparison.Ordinal))
            {
                throw new Exception($"Run 1: the resumed segment must be named …_part02.mp4, got '{seg2Path}'.");
            }
            if (!inputRecovered) throw new Exception("Run 1: InputRecovered was never raised.");
            Console.WriteLine($"[{Now()}] Resumed into segment 2: {Path.GetFileName(seg2Path)}");

            Console.WriteLine($"[{Now()}] Recording segment 2 for ~6 s...");
            var sw2 = Stopwatch.StartNew();
            while (sw2.ElapsedMilliseconds < 6000)
            {
                Thread.Sleep(200);
                session.CheckInputHealth();
            }

            session.StopRecording();
            vm.ToggleConnectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        }
        finally
        {
            if (sender1 != null)
            {
                try { if (!sender1.HasExited) sender1.Kill(entireProcessTree: true); } catch { }
                sender1.Dispose();
            }
        }

        VerifyDecodeClean(seg1Path, "Run 1 segment 1");
        VerifyDecodeClean(seg2Path, "Run 1 segment 2");
        if (string.Equals(seg1Path, seg2Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("Run 1: the resumed segment must be a different file from segment 1.");
        }

        // NET-011: the backup destination must hold the SAME take - both segments, same file names -
        // and both copies must decode clean. A duplicate that is silently truncated or missing is a
        // worse failure than no backup at all, so this is asserted rather than assumed.
        string backupSeg1 = Path.Combine(run1BackupDir, Path.GetFileName(seg1Path));
        string backupSeg2 = Path.Combine(run1BackupDir, Path.GetFileName(seg2Path));
        if (!File.Exists(backupSeg1))
        {
            throw new Exception($"Dual destination: the backup folder is missing segment 1 ('{backupSeg1}').");
        }
        if (!File.Exists(backupSeg2))
        {
            throw new Exception($"Dual destination: the backup folder is missing the resumed segment ('{backupSeg2}').");
        }
        VerifyDecodeClean(backupSeg1, "Run 1 backup segment 1");
        VerifyDecodeClean(backupSeg2, "Run 1 backup segment 2");
        Console.WriteLine($"[{Now()}] Dual destination: both segments decode clean in the primary AND the backup folder.");

        // ---------------------------------------------------- RUN 2: grace window expires
        Console.WriteLine("\n--------------------------------------------------------------------------------");
        Console.WriteLine("  RUN 2: permanent sender loss (full 30 s grace window expiry)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        string run2Dir = Path.Combine(baseOutDir, "Run2_GraceExpiry");
        Directory.CreateDirectory(run2Dir);
        string source2 = $"OMT_SOAK_NoReturn_{Environment.ProcessId}";

        Process? sender2 = null;
        try
        {
            sender2 = SpawnSender(exePath, source2, out string address2);
            Thread.Sleep(1500);
            string target2 = ResolveDiscoveredAddress(source2, address2);

            using var session2 = new CaptureSession();
            using var discovery2 = new OmtDiscoveryService();
            using var vm2 = new MainWindowViewModel(session2, discovery2, ownsDependencies: false);

            long videoFrames2 = 0;
            bool recoveryFailed = false;

            session2.VideoFrameAvailable += (p, l, w, h, s, fps, ts) => Interlocked.Increment(ref videoFrames2);
            session2.RecordingStopped += summary =>
                Console.WriteLine($"[{Now()}] [Run 2] EVENT RecordingStopped segment={summary.SegmentIndex} media={summary.MediaDuration.TotalSeconds:F3}s wall={summary.WallClockDuration.TotalSeconds:F3}s");
            session2.InputRecoveryFailed += () =>
            {
                recoveryFailed = true;
                Console.WriteLine($"[{Now()}] [Run 2] EVENT InputRecoveryFailed | VM.State={vm2.BroadcastState} banner={vm2.IsInputLossBannerVisible}");
            };

            vm2.SelectedSource = OmtSourceInfo.Parse(target2);
            vm2.ToggleConnectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            WaitUntil(() => Interlocked.Read(ref videoFrames2) >= 60 && session2.CurrentFormat.HasVideo, 15_000, "[Run 2] live frames from the sender");

            var config2 = new RecordingConfig
            {
                ContainerFormat = OutputContainerFormat.MP4,
                EncoderChoice = VideoEncoderChoice.CpuX264,
                Quality = QualityPreset.High,
                OutputDirectory = run2Dir,
                FilenamePrefix = "SOAK_Run2",
                InputLossThresholdSeconds = 5.0,
                InputRecoveryGraceSeconds = 30.0
            };

            if (!session2.StartRecording(config2))
            {
                throw new Exception("[Run 2] StartRecording failed.");
            }

            Console.WriteLine($"[{Now()}] [Run 2] Recording ~5 s, then killing the sender permanently (RecFrames={session2.RecordingFramesWritten})...");
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(200);
                session2.CheckInputHealth();
            }

            sender2.Kill(entireProcessTree: true);
            sender2.WaitForExit(3000);
            sender2.Dispose();
            sender2 = null;

            WaitUntil(() => recoveryFailed, 50_000, "[Run 2] the 30 s grace-window expiry", () => session2.CheckInputHealth());

            if (session2.IsRecording) throw new Exception("[Run 2] the take must be stopped once the grace window expires.");
            if (session2.IsInputHealthWatched) throw new Exception("[Run 2] the watchdog must be disarmed once the take has stopped.");
            Console.WriteLine($"[{Now()}] [Run 2] Grace expired cleanly; VM.State={vm2.BroadcastState} banner={vm2.IsInputLossBannerVisible}");

            string[] run2Files = Directory.GetFiles(run2Dir);
            if (run2Files.Length != 1)
            {
                throw new Exception($"[Run 2] expected exactly one finalized segment, found {run2Files.Length}: {string.Join(", ", run2Files.Select(Path.GetFileName))}");
            }

            vm2.ToggleConnectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            VerifyDecodeClean(run2Files[0], "[Run 2] segment 1");
        }
        finally
        {
            if (sender2 != null)
            {
                try { if (!sender2.HasExited) sender2.Kill(entireProcessTree: true); } catch { }
                sender2.Dispose();
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n====================================================");
        Console.WriteLine("  SOAK PASSED: boundary finalize, _partNN resume,");
        Console.WriteLine("  grace expiry and decode-clean output verified.");
        Console.WriteLine("====================================================");
        Console.ResetColor();
        return 0;
    }

}
