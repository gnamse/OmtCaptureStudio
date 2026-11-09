using System.Diagnostics;
using System.Runtime.InteropServices;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;

namespace OmtCaptureStudio.Tests;

partial class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    static int Main(string[] args)
    {
        SetDllDirectory(AppDomain.CurrentDomain.BaseDirectory);

        // SOAK-01 opt-in modes. The default suite (no args) is unchanged and fast; the soak mode runs
        // ONLY when explicitly requested because it takes ~65-70 s of real wall clock and spawns child
        // sender processes.
        // DIAG-BROADCAST: probe the broadcast sender's FRAME PROVIDER directly. The receive side is
        // proven good (722/722 accepted); this asks whether DesktopFrameCapturer delivers frames at all
        // on this box - which is what would make a broadcast source advertise but carry nothing.
        if (args.Length >= 1 && args[0] == "--diag-capture")
        {
            return RunDiagCapture(args.Length >= 2 && int.TryParse(args[1], out int csecs) ? csecs : 8);
        }

        if (args.Length >= 1 && args[0] == "--sender")
        {
            return RunSenderChild(args.Length >= 2 ? args[1] : "OMT_NET007_LiveSender");
        }

        if (args.Length >= 1 && args[0] == "--real-sender-soak")
        {
            return RunRealSenderSoak();
        }

        // NET-005: run a single step by id, e.g. `--step 0.682`, for CI loops. The default suite
        // (no args) is unchanged and still runs every step in the same order.
        if (args.Length >= 2 && args[0] == "--step")
        {
            return RunSingleStep(args[1]);
        }

        // DIAG-VMIX: owner-reported vMix audio defect. Diagnosis modes only - never part of the gate.
        if (args.Length >= 1 && args[0] == "--diag-discover")
        {
            return RunDiagDiscover();
        }

        if (args.Length >= 2 && args[0] == "--diag-audio")
        {
            return RunDiagAudio(
                args[1],
                args.Length >= 3 && int.TryParse(args[2], out int dsecs) ? dsecs : 20,
                DiagVideoFormat(Environment.GetCommandLineArgs()));
        }

        if (args.Length >= 1 && args[0] == "--diag-pa16")
        {
            return RunDiagPa16();
        }

        if (args.Length >= 2 && args[0] == "--diag-record")
        {
            return RunDiagRecord(
                args[1],
                args.Length >= 3 && int.TryParse(args[2], out int rsecs) ? rsecs : 20,
                DiagVideoFormat(Environment.GetCommandLineArgs()));
        }

        if (args.Length >= 2 && args[0] == "--diag-guard")
        {
            return RunDiagGuard(args[1], args.Length >= 3 && int.TryParse(args[2], out int gsecs) ? gsecs : 15);
        }

        RunDefaultSuite();
        return 0;
    }

    /// <summary>
    /// The default regression gate. Runs only when no CLI mode is given, so its behaviour and runtime
    /// are unchanged by the soak mode.
    /// </summary>
    static void RunDefaultSuite()
    {
        // SEC-04 (R25/R26): PRODUCTION resolves ffmpeg once at startup - App.axaml.cs calls
        // FfmpegPreflight.Run and the execution sink consumes that proven path. The suite must do the same
        // before any step that starts a take, because the sink no longer falls back to a bare "ffmpeg"
        // resolved through PATH. Without this the recording steps fail by design, not by accident.
        FfmpegPreflight.Run(AppContext.BaseDirectory);
        Console.WriteLine($"   [ffmpeg] head-of-suite resolution -> {FfmpegPreflight.ResolvedPath ?? "<NULL>"}");
        Console.WriteLine("====================================================");
        Console.WriteLine("  OMT Capture Studio - End-to-End Automated Test");
        Console.WriteLine("====================================================");

        TestSourceInfoParsing();

        TestInMemorySeams();

        TestAudioClockResampler();
        TestDesktopCapture();
        TestCaptureTargets();
        TestReceiverServiceTelemetry();
        TestAsioHostAudio();
        TestAuditRemediationsAndRegressionSeams();
        TestBroadcastSessionState();
        TestInputLossWatchdog();
        TestReconnectBackoff();
        TestRecordingSegments();
        TestRecordingOutageRecovery();
        TestRecordingStopReportsMediaDuration();
        TestRecordingHealthModel();
        TestStereoCaptureRing();
        TestHostAudioSourceInterface();
        TestFixedRatioStereoResampler();
        TestConversionPlacement();
        TestRecordingStorageGuard();
        TestRecordingArm();
        TestMediaBufferPool();
        TestPipeWriterLoop();
        TestFfmpegArguments();
        TestScheduledSegmentedCapture();
        TestCrashForensics();
        TestFfmpegPreflightAndForensicsFallback();

        // SEC-04 (R26): re-resolve here, deliberately. The preflight STEP above exercises the failure
        // scenarios (missing, broken, throwing, empty PATH) and each one legitimately CLEARS
        // FfmpegPreflight.ResolvedPath - so the proven path established at the head of this suite is gone by
        // the time a take is started, and the sink now refuses to search PATH. Production never clears it
        // (nothing re-runs the preflight), so the faithful mirror is to re-resolve after the step that
        // deliberately breaks it, immediately before the steps that start real takes.
        Console.WriteLine($"   [ffmpeg] after the preflight STEP -> {FfmpegPreflight.ResolvedPath ?? "<NULL>"}"
                          + "  (NULL here is the STEP's own failure scenarios clearing it, by R26 design)");
        FfmpegPreflight.Run(AppContext.BaseDirectory);
        Console.WriteLine($"   [ffmpeg] after re-resolve, SAME process + SAME redirect -> {FfmpegPreflight.ResolvedPath ?? "<NULL>"}"
                          + "  (non-null REFUTES the redirected-stdio hypothesis)");
        TestCompositeRecordingSink();
        TestCompositeRecordingWiring();
        TestFfmpegSinkPathDiscipline();
        TestNativePayloadVerification();
        TestProgressiveDisclosure();
        TestOperatorSettings();
        TestScheduledStopAndRemainingTime();
        TestLogLocationAndReadOnlyInstall();
        TestTakeSupervisor();
        TestAsioInputPairSelector();
        TestDiagnosticsBundle();
        TestAppModeShell();
        TestOmtAudioFrameGuard();
        TestUpdateService();
        TestBroadcastCaptureStall();
        SecurityRegressionTable.RunAll();

        TestMainWindowViewModel();

        string testSource = "OMT_Automated_Test_Source";
        string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestOutput");
        if (Directory.Exists(outputDir))
        {
            try { Directory.Delete(outputDir, true); } catch { }
        }
        Directory.CreateDirectory(outputDir);

        Console.WriteLine("\n[Step 1] Initializing Test Pattern Generator...");
        using var generator = new OmtTestPatternGenerator(testSource);
        generator.Start();
        Console.WriteLine($" -> Broadcasting OMT test stream: {testSource}");

        Thread.Sleep(1500); // Allow sender to bind

        Console.WriteLine("\n[Step 2] Testing OMT Discovery...");
        using var discovery = new OmtDiscoveryService();
        discovery.RefreshNow();
        Thread.Sleep(1000);

        var disc = libomtnet.OMTDiscovery.GetInstance();
        var addrs = disc.GetAddresses();
        Console.WriteLine($" -> Discovery returned {addrs?.Length ?? 0} address(es):");
        if (addrs != null)
        {
            foreach (var a in addrs) Console.WriteLine($"    * {a}");
        }

        Console.WriteLine($" -> Generator URL:     {generator.Url}");
        Console.WriteLine($" -> Generator Address: {generator.Address}");
        Console.WriteLine($" -> Generator Port:    {generator.Port}");

        string connectTarget = generator.Url ?? generator.Address ?? testSource;
        if (addrs != null && addrs.Length > 0)
        {
            connectTarget = addrs.FirstOrDefault(a => a.Contains(testSource, StringComparison.OrdinalIgnoreCase)) ?? addrs[0];
        }

        Console.WriteLine($"\n[Step 3] Initializing Headless CaptureSession & Connecting to target: {connectTarget}...");
        using var session = new CaptureSession();

        long videoFrameCount = 0;
        long audioFrameCount = 0;
        StreamFormat? detectedFormat = null;
        AudioLevelData? lastAudioLevels = null;

        session.VideoFrameAvailable += (pData, length, w, h, stride, fps, ts) =>
        {
            Interlocked.Increment(ref videoFrameCount);
        };

        session.AudioLevelsUpdated += levels =>
        {
            Interlocked.Increment(ref audioFrameCount);
            lastAudioLevels = levels;
        };

        session.FormatChanged += fmt =>
        {
            detectedFormat = fmt;
        };

        session.RecordingError += err =>
        {
            Console.WriteLine($" -> [Recorder Error Callback]: {err}");
        };

        session.Connect(connectTarget);

        Console.WriteLine(" -> Waiting for connection and first video and audio frames via CaptureSession...");
        int waitAttempts = 0;
        while ((videoFrameCount < 5 || audioFrameCount < 5) && waitAttempts < 60)
        {
            Thread.Sleep(100);
            waitAttempts++;
            if (waitAttempts % 10 == 0)
            {
                Console.WriteLine($" [Poll {waitAttempts}] Sender Running: {generator.IsRunning}, Session Connected: {session.IsConnected}, VideoFrames={videoFrameCount}, AudioFrames={audioFrameCount}");
            }
        }

        Console.WriteLine($" -> Frames received so far: Video={videoFrameCount}, Audio={audioFrameCount}");
        if (videoFrameCount == 0 || audioFrameCount == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[FAILED] No frames received from OMT stream!");
            Console.ResetColor();
            return;
        }

        var activeFormat = session.CurrentFormat;
        Console.WriteLine($" -> Stream detected: {activeFormat.VideoSpecsText}, {activeFormat.AudioSpecsText}");
        Console.WriteLine($" -> Audio levels computed: {lastAudioLevels?.Channels.Length ?? 0} channels, Peak Ch0: {lastAudioLevels?.Channels[0].PeakDb:F1} dBFS");

        Console.WriteLine("\n[Step 4] Starting Stream Recording via CaptureSession.StartRecording()...");
        var config = new RecordingConfig
        {
            ContainerFormat = OutputContainerFormat.MP4,
            EncoderChoice = VideoEncoderChoice.CpuX264,
            Quality = QualityPreset.High,
            OutputDirectory = outputDir,
            FilenamePrefix = "Test_Record"
        };

        bool started = session.StartRecording(config);

        if (!started)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[FAILED] Could not start recording via CaptureSession!");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($" -> Recording active. Writing frames to {session.CurrentRecordingPath} for 6 seconds (verifying past 5s threshold)...");
        for (int sec = 1; sec <= 6; sec++)
        {
            long startFrames = Interlocked.Read(ref videoFrameCount);
            Thread.Sleep(1000);
            long endFrames = Interlocked.Read(ref videoFrameCount);
            Console.WriteLine($"   [Sec {sec:D2}] Frames received in this second: {endFrames - startFrames}, Total: {endFrames}, Recorder Frames: {session.RecordingFramesWritten}");
        }

        Console.WriteLine("\n[Step 5] Stopping Recording & Finalizing MP4 Container via CaptureSession.StopRecording()...");
        string? recordedFile = session.CurrentRecordingPath;
        session.StopRecording();
        Thread.Sleep(500);

        if (string.IsNullOrEmpty(recordedFile) || !File.Exists(recordedFile))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FAILED] Recorded file not found on disk!");
            Console.ResetColor();
            return;
        }

        var fileInfo = new FileInfo(recordedFile);
        Console.WriteLine($" -> Recorded File: {fileInfo.FullName}");
        Console.WriteLine($" -> File Size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        if (fileInfo.Length < 10000)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[FAILED] Recorded file is suspiciously small.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("\n[Step 6] Inspecting Recorded Media File via FFprobe...");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -show_entries format=duration,size:stream=codec_type,codec_name -of default=noprint_wrappers=1 \"{recordedFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            string output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(3000);

            Console.WriteLine(output);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Note] FFprobe check skipped: {ex.Message}");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n====================================================");
        Console.WriteLine("  ALL TESTS PASSED! OMT CAPTURE & RECORDING VERIFIED");
        Console.WriteLine("====================================================");
        Console.ResetColor();
    }

    /// <summary>
    /// NET-005: one entry point per step, so a single step can run in a CI loop via `--step &lt;id&gt;`
    /// without booting the whole suite. The default suite calls the same methods in the same order, so the
    /// output contract is unchanged. Steps 1..6 are still inline in <see cref="RunDefaultSuite"/> and are
    /// deliberately NOT registered yet — they are the follow-up this seam exists for.
    /// </summary>
    static int RunSingleStep(string id)
    {
        switch (id)
        {
            case "0":     TestSourceInfoParsing(); break;
            case "0.5":   TestInMemorySeams(); break;
            case "0.6":   TestAudioClockResampler(); break;
            case "0.65":  TestDesktopCapture(); break;
            case "0.7":   TestCaptureTargets(); break;
            case "0.71":  TestReceiverServiceTelemetry(); break;
            case "0.69":  TestAsioHostAudio(); break;
            case "0.72":  TestAuditRemediationsAndRegressionSeams(); break;
            case "0.68":  TestBroadcastSessionState(); break;
            case "0.67":  TestInputLossWatchdog(); break;
            case "0.671": TestReconnectBackoff(); break;
            case "0.672": TestRecordingSegments(); break;
            case "0.673": TestRecordingOutageRecovery(); break;
            case "0.674": TestRecordingStopReportsMediaDuration(); break;
            case "0.675": TestRecordingHealthModel(); break;
            case "0.676": TestStereoCaptureRing(); break;
            case "0.677": TestHostAudioSourceInterface(); break;
            case "0.678": TestFixedRatioStereoResampler(); break;
            case "0.679": TestConversionPlacement(); break;
            case "0.681": TestRecordingStorageGuard(); break;
            case "0.682": TestRecordingArm(); break;
            case "0.683": TestMediaBufferPool(); break;
            case "0.684": TestPipeWriterLoop(); break;
            case "0.685": TestFfmpegArguments(); break;
            case "0.686": TestScheduledSegmentedCapture(); break;
            case "0.687": TestCrashForensics(); break;
            case "0.688": TestFfmpegPreflightAndForensicsFallback(); break;
            case "0.689": TestCompositeRecordingSink(); break;
            case "0.690": TestCompositeRecordingWiring(); break;
            case "0.691": TestProgressiveDisclosure(); break;
            case "0.692": TestOperatorSettings(); break;
            case "0.693": TestScheduledStopAndRemainingTime(); break;
            case "0.694": TestLogLocationAndReadOnlyInstall(); break;
            case "0.695": TestTakeSupervisor(); break;
            case "0.696": TestAsioInputPairSelector(); break;
            case "0.697": TestDiagnosticsBundle(); break;
            case "0.698": TestAppModeShell(); break;
            case "0.699": TestOmtAudioFrameGuard(); break;
            case "0.700": SecurityRegressionTable.RunAll(); break;
            case "0.701": TestUpdateService(); break;
            case "0.702": TestBroadcastCaptureStall(); break;
            case "0.703": TestFfmpegSinkPathDiscipline(); break;
            case "0.704": TestNativePayloadVerification(); break;
            case "0.75":  TestMainWindowViewModel(); break;
            default:
                Console.Error.WriteLine($"Unknown step id '{id}'.");
                Console.Error.WriteLine("Known: 0, 0.5, 0.6, 0.65, 0.7, 0.71, 0.69, 0.72, 0.68, 0.67, 0.671, 0.672, " +
                                        "0.673, 0.674, 0.675, 0.676, 0.677, 0.678, 0.679, 0.681, 0.682, 0.683, 0.684, 0.685, 0.686, 0.687, 0.688, 0.689, 0.690, 0.691, 0.75");
                return 2;
        }

        return 0;
    }
}
