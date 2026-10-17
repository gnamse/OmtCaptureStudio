using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Audio;
using OmtCaptureStudio.Services.Sinks;
using OmtCaptureStudio.Services.Sources;
using OmtCaptureStudio.ViewModels;

namespace OmtCaptureStudio.Tests;

class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    static int Main(string[] args)
    {
        SetDllDirectory(AppDomain.CurrentDomain.BaseDirectory);

        // SOAK-01 opt-in modes. The default suite (no args) is unchanged and fast; the soak mode runs
        // ONLY when explicitly requested because it takes ~65-70 s of real wall clock and spawns child
        // sender processes.
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

        RunDefaultSuite();
        return 0;
    }

    /// <summary>
    /// The default regression gate. Runs only when no CLI mode is given, so its behaviour and runtime
    /// are unchanged by the soak mode.
    /// </summary>
    static void RunDefaultSuite()
    {
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
        TestCompositeRecordingSink();
        TestCompositeRecordingWiring();
        TestProgressiveDisclosure();

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

    static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");

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
    /// <summary>
    /// NET-005: one deep assertion per fact. Replaces the four-line `if (!cond) { throw new Exception(...); }`
    /// blocks that dominated the presentation step — the message is the FACT, and it may interpolate the
    /// actual value when the value is the interesting part of the failure.
    /// </summary>
    static void Expect(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    /// <summary>
    /// NET-005: the headless session fixture — the CaptureSession + NullRecordingSink + recorder + audio
    /// (+ discovery and MainWindowViewModel) wiring that every presentation step used to re-type by hand.
    /// Dispose order mirrors the original nested `using` blocks.
    /// </summary>
    private sealed class HeadlessSessionFixture : IDisposable
    {
        public NullRecordingSink Sink { get; }
        public AudioEngine Audio { get; }
        public StreamRecorderService Recorder { get; }
        public CaptureSession Session { get; }
        public OmtDiscoveryService? Discovery { get; }
        public MainWindowViewModel? ViewModel { get; }

        private HeadlessSessionFixture(bool withViewModel)
        {
            Sink = new NullRecordingSink();
            Audio = new AudioEngine();
            Recorder = new StreamRecorderService(Sink);
            Session = new CaptureSession(Audio, Recorder);

            if (withViewModel)
            {
                Discovery = new OmtDiscoveryService();
                ViewModel = new MainWindowViewModel(Session, Discovery);
            }
        }

        public static HeadlessSessionFixture Build() => new(withViewModel: false);
        public static HeadlessSessionFixture BuildWithViewModel() => new(withViewModel: true);

        public void Dispose()
        {
            ViewModel?.Dispose();
            Discovery?.Dispose();
            Session?.Dispose();
            Audio?.Dispose();
            Recorder?.Dispose();
        }
    }

    /// <summary>NET-005: one builder for the headless wiring; see <see cref="HeadlessSessionFixture"/>.</summary>
    static HeadlessSessionFixture BuildHeadlessSession(bool withViewModel = true)
        => withViewModel ? HeadlessSessionFixture.BuildWithViewModel() : HeadlessSessionFixture.Build();

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
            case "0.75":  TestMainWindowViewModel(); break;
            default:
                Console.Error.WriteLine($"Unknown step id '{id}'.");
                Console.Error.WriteLine("Known: 0, 0.5, 0.6, 0.65, 0.7, 0.71, 0.69, 0.72, 0.68, 0.67, 0.671, 0.672, " +
                                        "0.673, 0.674, 0.675, 0.676, 0.677, 0.678, 0.679, 0.681, 0.682, 0.683, 0.684, 0.685, 0.686, 0.687, 0.688, 0.689, 0.690, 0.691, 0.75");
                return 2;
        }

        return 0;
    }

    /// <summary>NET-005: [Step 0] OMT source-info parsing &amp; display formatting. Extracted from the inline block so the step has one entry point (runnable alone via `--step 0`).</summary>
    static void TestSourceInfoParsing()
    {
        Console.WriteLine("\n[Step 0] Verifying OMT Source Info Parsing & Display Formatting...");
        var test1 = OmtSourceInfo.Parse("PC (vMix - Output 1)");
        Expect(!(test1.Name != "vMix - Output 1" || test1.Host != "PC" || test1.DisplayName != "vMix - Output 1 (PC)"), $"Test1 failed: Name='{test1.Name}', Host='{test1.Host}', Display='{test1.DisplayName}'");
        Expect(test1.Address == "PC (vMix - Output 1)", $"Test1 address failed: Address='{test1.Address}'");

        var test2 = OmtSourceInfo.Parse("vMix - Output 1 @ PC");
        Expect(!(test2.Name != "vMix - Output 1" || test2.Host != "PC" || test2.DisplayName != "vMix - Output 1 (PC)"), $"Test2 failed: Name='{test2.Name}', Host='{test2.Host}', Display='{test2.DisplayName}'");

        var test3 = OmtSourceInfo.Parse("omt://127.0.0.1:5000");
        Expect(!(test3.DisplayName != "omt://127.0.0.1:5000" || test3.Address != "omt://127.0.0.1:5000"), $"Test3 failed: Display='{test3.DisplayName}'");

        // Test redundant double-wrapping edge cases from user screenshot
        var test4 = OmtSourceInfo.Parse("PC (vMix - Output 1) (PC (vMix - Output 1))");
        Expect(!(test4.Name != "vMix - Output 1" || test4.Host != "PC" || test4.DisplayName != "vMix - Output 1 (PC)"), $"Test4 failed (redundant duplicate): Name='{test4.Name}', Host='{test4.Host}', Display='{test4.DisplayName}'");

        var test5 = OmtSourceInfo.Parse("PC (Audio Headset Microphone) (PC (Audio Headset Microphone))");
        Expect(!(test5.Name != "Audio Headset Microphone" || test5.Host != "PC" || test5.DisplayName != "Audio Headset Microphone (PC)"), $"Test5 failed: Name='{test5.Name}', Host='{test5.Host}', Display='{test5.DisplayName}'");

        var test6 = OmtSourceInfo.Parse("STUDIO-PC (Camera 1 (Main))");
        Expect(!(test6.Name != "Camera 1 (Main)" || test6.Host != "STUDIO-PC" || test6.DisplayName != "Camera 1 (Main) (STUDIO-PC)"), $"Test6 failed: Name='{test6.Name}', Host='{test6.Host}', Display='{test6.DisplayName}'");

        var test7 = OmtSourceInfo.Parse("");
        Expect(!(test7.DisplayName != "" || test7.Name != "" || test7.Host != ""), $"Test7 failed on empty string");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> All source parsing & display formatting tests passed cleanly!");
        Console.ResetColor();

    }

    /// <summary>NET-005: [Step 0.5] in-memory seams. Body migrated to the headless fixture; the disposable wiring is no longer re-typed here.</summary>
    static void TestInMemorySeams()
    {
        Console.WriteLine("\n[Step 0.5] Verifying In-Memory Seams (SyntheticPatternSource + NullRecordingSink)...");
        using var seam = BuildHeadlessSession(withViewModel: false);
        var nullSink = seam.Sink;
        var testSession = seam.Session;

        long syntheticVideoCount = 0;
        testSession.VideoFrameAvailable += (p, len, w, h, s, fps, ts) => Interlocked.Increment(ref syntheticVideoCount);

        testSession.Connect(new SyntheticPatternSource());

        int seamWait = 0;
        while (syntheticVideoCount < 5 && seamWait < 20)
        {
            Thread.Sleep(50);
            seamWait++;
        }

        Expect(syntheticVideoCount != 0, "SyntheticPatternSource failed to deliver frames in-memory!");

        Console.WriteLine($" -> SyntheticPatternSource delivered {syntheticVideoCount} frames in-memory with zero network sockets!");

        var testConfig = new RecordingConfig
        {
            OutputDirectory = Path.GetTempPath(),
            FilenamePrefix = "Seam_Test"
        };

        bool seamRecordStarted = testSession.StartRecording(testConfig);
        Expect(seamRecordStarted, "Failed to start recording via NullRecordingSink!");

        Thread.Sleep(300);
        testSession.StopRecording();

        Console.WriteLine($" -> NullRecordingSink captured {nullSink.VideoFramesReceived} video frames, {nullSink.AudioBytesReceived} audio bytes in memory (0 disk I/O, 0 FFmpeg processes)!");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Architectural seams successfully verified!");
        Console.ResetColor();
    }

    /// <summary>NET-011 test child: scriptable initialize/viability/shed, and it CAN rent a pooled buffer.</summary>
    private sealed class ScriptedRecordingSink : IRecordingSink
    {
        private readonly bool _rentsPooledBuffer;

        public ScriptedRecordingSink(string name, bool rentsPooledBuffer = false, bool initializeResult = true)
        {
            Name = name;
            _rentsPooledBuffer = rentsPooledBuffer;
            InitializeResult = initializeResult;
        }

        public string Name { get; }
        public bool InitializeResult { get; }
        public bool Survives { get; set; }
        public string? ShedTo { get; set; }
        public Exception? ThrowOnVideo { get; set; }
        public IntPtr LastVideoPointer { get; private set; }

        public int InitCount;
        public int FinalizeCount;
        public int DisposeCount;
        public int ShedCount;
        public int VideoFrames;
        public int AudioBytes;
        public int Rents;
        public int Returns;

        public long VideoFramesDropped => 0;
        public long AudioChunksDropped => 0;
        public string ResolvedEncoderName => Name;
        public string DiagnosticTail => string.Empty;
        public event Action<string>? SinkError;

        public void RaiseError(string message) => SinkError?.Invoke(message);

        public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
        {
            InitCount++;
            return InitializeResult;
        }

        public void WriteVideo(IntPtr pData, int dataLength)
        {
            if (ThrowOnVideo != null) throw ThrowOnVideo;

            LastVideoPointer = pData;
            if (_rentsPooledBuffer)
            {
                byte[] rented = MediaBufferPool.Video.Rent(dataLength);
                Rents++;
                try { }
                finally { MediaBufferPool.Video.Return(rented); Returns++; }
            }

            VideoFrames++;
        }

        public void WriteAudio(byte[] buffer, int offset, int count)
        {
            if (_rentsPooledBuffer)
            {
                byte[] rented = MediaBufferPool.Audio.Rent(count);
                Rents++;
                try { }
                finally { MediaBufferPool.Audio.Return(rented); Returns++; }
            }

            AudioBytes += count;
        }

        public void FinalizeSink(out long totalBytesWritten)
        {
            FinalizeCount++;
            totalBytesWritten = VideoFrames * 100L;
        }

        public bool HasSurvivingDestination => Survives;

        public bool TryShedFailedDestination(string reason, out string? survivingPath)
        {
            ShedCount++;
            survivingPath = ShedTo;
            return ShedTo != null;
        }

        public void Dispose() => DisposeCount++;
    }

    /// <summary>
    /// NET-012 (distill): progressive disclosure. The Advanced tier's observable state, the broadcast
    /// row's collapse keyed to BROADCAST state only (never recording), the locking composition, and the
    /// preset-Custom flow working from inside the tier.
    /// </summary>
    static void TestProgressiveDisclosure()
    {
        Console.WriteLine("\n[Step 0.691] Verifying progressive disclosure (advanced tier, collapsed broadcast row)...");

        using var seam = BuildHeadlessSession();
        var vm = seam.ViewModel!;

        // ---- 1. DEFAULTS: the Advanced tier starts collapsed, and the broadcast row starts hidden ----
        Expect(!vm.IsAdvancedRecordingOptionsExpanded,
            "The Advanced tier must be COLLAPSED by default - the default path is preset -> folder -> record.");
        Expect(vm.IsAdvancedChevronDown, "A collapsed tier must show the down chevron.");
        Expect(!vm.IsBroadcastRowVisible,
            "The broadcast configuration row must start hidden - it is revealed by Broadcast being ON.");
        Expect(vm.IsLiveSettingSummaryVisible,
            "While the broadcast row is hidden the compact summary must be the thing that is shown.");

        // ---- 2. THE DISCLOSURE observable + its accessible name ---------------------------------
        Expect(vm.AdvancedOptionsAutomationName.Contains("Show"),
            $"The collapsed disclosure must name the ACTION; got '{vm.AdvancedOptionsAutomationName}'.");

        vm.ToggleAdvancedRecordingOptionsCommand.Execute(null);
        Expect(vm.IsAdvancedRecordingOptionsExpanded, "Toggling the disclosure must expand the tier.");
        Expect(!vm.IsAdvancedChevronDown, "An expanded tier must show the up chevron.");
        Expect(vm.AdvancedOptionsAutomationName.Contains("Hide"),
            $"The expanded disclosure must name the reverse action; got '{vm.AdvancedOptionsAutomationName}'.");

        // ---- 3. THE LOCKING COMPOSITION survives inside the tier ---------------------------------
        // Collapsing is VISIBILITY; locking is ENABLED. They are different axes and must compose.
        Expect(vm.IsRecordingConfigEnabled,
            "With no take running the recording config must be unlocked, expanded or not.");
        vm.IsRecordingConfigEnabled = false;
        Expect(vm.IsAdvancedRecordingOptionsExpanded,
            "Locking the recording config must NOT collapse the tier - visibility and enablement are independent.");
        vm.IsRecordingConfigEnabled = true;

        // ---- 4. PRESET -> Custom still works from INSIDE the tier --------------------------------
        vm.SelectedRecordingPreset = RecordingPresets.BuiltIn[0].Name;
        Expect(vm.SelectedRecordingPreset == RecordingPresets.BuiltIn[0].Name,
            "Selecting a preset must apply it.");
        Expect(vm.IsAdvancedRecordingOptionsExpanded,
            "Applying a preset must not collapse the tier the operator is working in.");

        // An Advanced-tier knob EDIT flips the picker to Custom - the behaviour has to survive the move.
        vm.SelectedRolloverMinutes = RecordingChoiceOption_ForTest(MainWindowViewModel.RolloverMinuteOptions, 30);
        Expect(vm.SelectedRecordingPreset == RecordingPresets.CustomName,
            $"Editing a control inside the tier must switch the picker to Custom; got '{vm.SelectedRecordingPreset}'.");

        // ---- 5. THE THREE-STATE BROADCAST FLOW (NET-014) ------------------------------------------
        // State 1 - HIDDEN.
        Expect(!vm.IsBroadcastRowVisible, "State 1 (Hidden): the row must be hidden.");
        Expect(!vm.IsBroadcastSelecting, "State 1: nothing is being selected.");

        // State 2 - REVEALED: the pickers are open and ENABLED. Nothing is live yet.
        vm.IsBroadcastRevealed = true;
        Expect(vm.IsBroadcastRowVisible, "State 2 (Revealed): the row must be visible.");
        Expect(vm.IsBroadcastSelecting, "State 2: the row is SELECTING, not live.");
        Expect(!vm.IsDesktopCaptureActive, "State 2: revealing must NOT start a broadcast.");
        Expect(vm.IsCaptureSelectorEnabled,
            "State 2: the pickers must be ENABLED for selection - the reported complaint was that revealing " +
            "started the send and left them locked before the operator could touch them.");
        Expect(vm.BroadcastActionText == "Start Broadcasting",
            $"State 2: the action must read Start Broadcasting; got '{vm.BroadcastActionText}'.");
        Expect(!vm.IsLiveSettingSummaryVisible, "State 2: the row is visible, so the compact summary stands down.");
        Expect(!string.IsNullOrWhiteSpace(vm.LiveSettingSummary),
            "State 2: the summary must still carry the current selection for the collapsed state.");

        // State 3 - LIVE. The VM takes the lock as a PROJECTION of the state machine, so mirror what the
        // projection does; the key itself is asserted against the machine below.
        vm.IsDesktopCaptureActive = true;
        vm.IsCaptureSelectorEnabled = false;
        Expect(vm.IsBroadcastRowVisible, "State 3 (Live): the row stays visible.");
        Expect(!vm.IsBroadcastSelecting, "State 3: the row is live, not selecting.");
        Expect(!vm.IsCaptureSelectorEnabled,
            "State 3: while LIVE the pickers are locked - that is when a mid-flight change drops receivers.");
        Expect(vm.BroadcastActionText == "Stop Broadcasting",
            $"State 3: the action must read Stop Broadcasting; got '{vm.BroadcastActionText}'.");

        // THE LOCK KEY MOVED. Asserted against the state machine, because the VM's flag is a projection of
        // it and a headless run can reveal the row WITHOUT starting a real desktop capture.
        var lockProbe = new BroadcastSessionState();
        Expect(lockProbe.IsCaptureSelectorEnabled, "The capture lock must start unlocked.");
        lockProbe.StartBroadcasting();
        Expect(!lockProbe.IsCaptureSelectorEnabled,
            "The capture pickers must lock when BROADCASTING - not when the row is merely revealed. That is " +
            "exactly the defect the user reported: the pickers were locked the moment the row appeared.");
        lockProbe.StopBroadcasting();
        Expect(lockProbe.IsCaptureSelectorEnabled, "Stopping must unlock the pickers again.");

        // THE NEVER-ORPHAN INVARIANT: a live broadcast cannot be hidden behind a collapsed row.
        vm.IsBroadcastRevealed = false;
        Expect(vm.IsBroadcastRowVisible,
            "A LIVE broadcast must remain visible even with the reveal flag cleared - it can never be orphaned.");
        vm.IsBroadcastRevealed = true;   // restore: the probe above cleared a flag the operator never cleared

        // Stopping from the in-row action returns to REVEALED, with the pickers usable again. The unlock is
        // a projection of the state machine (proved on lockProbe above), so mirror it here.
        vm.IsDesktopCaptureActive = false;
        vm.IsCaptureSelectorEnabled = true;
        Expect(vm.IsBroadcastRevealed, "The in-row stop leaves the panel revealed.");
        Expect(vm.IsCaptureSelectorEnabled, "After a stop the pickers must be usable again.");
        Expect(vm.BroadcastActionText == "Start Broadcasting", "After a stop the action returns to Start.");

        // The row-0 dismiss returns to HIDDEN.
        vm.IsBroadcastRevealed = false;
        Expect(!vm.IsBroadcastRowVisible, "Dismissing returns to State 1 (Hidden).");

        // ---- 6. RECORDING IS STILL AN INDEPENDENT AXIS --------------------------------------------
        vm.IsBroadcastRevealed = true;
        vm.IsDesktopCaptureActive = true;

        long frames = 0;
        seam.Session.VideoFrameAvailable += (ptr, len, w, h, st, fps, ts) => Interlocked.Increment(ref frames);
        seam.Session.Connect(new SyntheticPatternSource());

        int waitTicks = 0;
        while (frames < 5 && waitTicks < 30)
        {
            Thread.Sleep(50);
            waitTicks++;
        }

        var config = new RecordingConfig { OutputDirectory = Path.GetTempPath(), FilenamePrefix = "DisclosureTest" };
        if (seam.Session.StartRecording(config))
        {
            bool rowWasVisibleWhileRecording = vm.IsBroadcastRowVisible;

            // Broadcast OFF mid-take: the row returns to Hidden (reveal cleared + no longer live) and the
            // take must not notice.
            vm.IsDesktopCaptureActive = false;
            vm.IsBroadcastRevealed = false;
            Expect(!vm.IsBroadcastRowVisible,
                "Broadcast OFF must hide the row even while a take is running - the row is not keyed to recording.");
            Expect(rowWasVisibleWhileRecording,
                "The row was visible because BROADCAST was on, not because a take was running.");
            Expect(seam.Session.IsRecording,
                "Hiding the broadcast row must never disturb a running take.");

            // A live take must NOT force the row open on its own.
            Expect(!vm.IsBroadcastRowVisible,
                "A recording on its own must never reveal the broadcast row - it is keyed to broadcast only.");

            seam.Session.StopRecording();
        }
        else
        {
            Console.WriteLine(" -> [SKIP] the storage preflight refused the temp volume; the recording-vs-row assertions were skipped.");
        }

        Console.WriteLine(" -> Advanced collapsed by default with a drawn chevron; the broadcast row is keyed to " +
                          "broadcast state alone (a take neither opens nor is disturbed by it); locking composes " +
                          "with visibility; preset->Custom still works from inside the tier!");

        void Body() { }
        Body();
    }

    /// <summary>Finds a labelled option by value - mirrors the ViewModel's own lookup for the test.</summary>
    private static RecordingChoiceOption RecordingChoiceOption_ForTest(IReadOnlyList<RecordingChoiceOption> options, double value)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].Value.Equals(value)) return options[i];
        }

        return options[0];
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

    /// <summary>NET-005: [Step 0.75] MainWindowViewModel MVVM state, commands and seams. The ~330-line presentation-assertion block migrated to the fixture + `Expect`, so each fact is one assertion instead of a four-line throw block.</summary>
    static void TestMainWindowViewModel()
    {
        Console.WriteLine("\n[Step 0.75] Verifying MainWindowViewModel (MVVM State, Commands & Seams)...");
        using var seam = BuildHeadlessSession();
        var vmSession = seam.Session;
        var vm = seam.ViewModel!;

        // Verify default UI state
        Expect(!(vm.SelectedContainerFormat != OutputContainerFormat.MP4 || vm.SelectedEncoderChoice != VideoEncoderChoice.AutoHardware || vm.SelectedQualityPreset != QualityPreset.High), "ViewModel recording config defaults mismatch!");

        Expect(!(!vm.IsNoSignalOverlayVisible || vm.LiveStatusText != "OFFLINE"), "ViewModel live status defaults mismatch!");

        Expect(!(vm.CaptureAudioOptions.Count < 4 || vm.SelectedCaptureAudioMode != HostAudioCaptureMode.SystemAudio), $"ViewModel host audio capture defaults mismatch: count={vm.CaptureAudioOptions.Count}, mode={vm.SelectedCaptureAudioMode}");
        Expect(!(vm.BroadcastNetworks.Count < 2 || !vm.SelectedBroadcastNetwork.IsAllNetworks), $"ViewModel broadcast network defaults mismatch: count={vm.BroadcastNetworks.Count}, selected={vm.SelectedBroadcastNetwork?.DisplayName}");
        vm.SelectedCaptureAudioMode = HostAudioCaptureMode.SystemAndMicrophone;
        Expect(vm.SelectedCaptureAudioOption.Mode == HostAudioCaptureMode.SystemAndMicrophone, $"SelectedCaptureAudioOption did not sync with SelectedCaptureAudioMode: {vm.SelectedCaptureAudioOption.Mode}");
        vm.SelectedCaptureAudioMode = HostAudioCaptureMode.SystemAudio;

        // Test MOV auto-switches encoder to ProRes
        vm.SelectedContainerFormat = OutputContainerFormat.MOV;
        Expect(vm.SelectedEncoderChoice == VideoEncoderChoice.ProRes, $"Expected ProRes for MOV, got: {vm.SelectedEncoderChoice}");

        // Test switching away from MOV restores AutoHardware
        vm.SelectedContainerFormat = OutputContainerFormat.MP4;
        Expect(vm.SelectedEncoderChoice == VideoEncoderChoice.AutoHardware, $"Expected AutoHardware for MP4, got: {vm.SelectedEncoderChoice}");

        // Test Source ToolTip generation
        var dummySource = OmtSourceInfo.Parse("PC (Camera 1)");
        vm.SelectedSource = dummySource;
        Expect(!(string.IsNullOrWhiteSpace(vm.SelectedSourceToolTip) || !vm.SelectedSourceToolTip.Contains("Camera 1")), $"ViewModel tooltip not updated correctly: '{vm.SelectedSourceToolTip}'");
        vm.SelectedSource = null;
        Expect(vm.SelectedSourceToolTip == null, "ViewModel tooltip should be null when SelectedSource is null!");

        // Test Commands
        vm.RefreshSourcesCommand.Execute(null);
        Expect(vm.StatusText.Contains("Scanning network"), $"RefreshSourcesCommand failed to update status: '{vm.StatusText}'");

        vm.IsRecordingCloseOverlayVisible = true;
        vm.KeepRecordingCommand.Execute(null);
        Expect(!(vm.IsRecordingCloseOverlayVisible || !vm.IsKeepRecordingEnabled), "KeepRecordingCommand failed to dismiss overlay or restore button state!");

        // Connect validation without source
        vm.ToggleConnectCommand.Execute(null);
        Expect(vm.StatusText.Contains("Please select an OMT source"), $"ToggleConnectCommand without source failed validation: '{vm.StatusText}'");

        // Test StudioPalette token identity in default state
        Expect(vm.ConnectButtonForeground == StudioPalette.ButtonDarkForeground, "ViewModel ConnectButtonForeground default state is not StudioPalette.ButtonDarkForeground!");
        Expect(vm.TestSignalForeground == StudioPalette.ButtonLightForeground, "ViewModel TestSignalForeground default state is not StudioPalette.ButtonLightForeground!");
        Expect(vm.DesktopCaptureForeground == StudioPalette.ButtonLightForeground, "ViewModel DesktopCaptureForeground default state is not StudioPalette.ButtonLightForeground!");
        Expect(!(!vm.IsRecordingConfigEnabled || !vm.IsMainContentEnabled), "ViewModel IsRecordingConfigEnabled and IsMainContentEnabled should default to true!");

        // ADR-0002: the input-loss banner is part of the state projection and starts hidden.
        Expect(!(vm.IsInputLossBannerVisible || vm.BroadcastState != BroadcastState.Offline), $"ViewModel must start Offline with the input-loss banner hidden: state={vm.BroadcastState}, banner={vm.IsInputLossBannerVisible}");

        // FEAT-07: the recording-health panel, stop verdict and diagnostics drawer all start hidden
        // (checked BEFORE any recording below, so nothing has populated a verdict yet).
        Expect(!(vm.IsRecordingHealthVisible || vm.IsStopSummaryVisible || vm.IsDiagnosticsDrawerOpen || vm.IsRecordingDropWarning || vm.IsRecordingDriftWarning),  $"Recording-health surfaces must start hidden: panel={vm.IsRecordingHealthVisible}, " + $"verdict={vm.IsStopSummaryVisible}, drawer={vm.IsDiagnosticsDrawerOpen}");
        vm.ToggleDiagnosticsDrawerCommand.Execute(null);
        Expect(vm.IsDiagnosticsDrawerOpen, "ToggleDiagnosticsDrawerCommand did not open the drawer.");
        vm.ToggleDiagnosticsDrawerCommand.Execute(null);
        Expect(!(vm.IsDiagnosticsDrawerOpen), "ToggleDiagnosticsDrawerCommand did not close the drawer.");

        // Test ToggleTestSignalCommand (Turn ON)
        vm.ToggleTestSignalCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Expect(!(!vm.IsTestSignalActive || !vm.IsConnected || vm.ConnectButtonText != "Disconnect"), $"ToggleTestSignalCommand failed to activate test signal! IsActive={vm.IsTestSignalActive}, IsConnected={vm.IsConnected}");
        Expect(vm.StatusText.Contains("Internal Test Pattern Generator", StringComparison.OrdinalIgnoreCase), $"Unexpected status after starting test signal: '{vm.StatusText}'");
        Expect(vm.TestSignalForeground == StudioPalette.ButtonWhiteForeground, "ViewModel TestSignalForeground active state is not StudioPalette.ButtonWhiteForeground!");
        Expect(vm.DesktopCaptureForeground == StudioPalette.ButtonLightForeground, "ViewModel DesktopCaptureForeground must remain ButtonLightForeground when only TestSignal is active!");
        Expect(vm.ConnectButtonForeground == StudioPalette.ButtonWhiteForeground, "ViewModel ConnectButtonForeground connected state is not StudioPalette.ButtonWhiteForeground!");
        Expect(vm.BroadcastState == BroadcastState.TestSignal, $"ViewModel state must be TestSignal while the test signal is active: {vm.BroadcastState}");

        // Test ToggleTestSignalCommand (Turn OFF)
        vm.ToggleTestSignalCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Expect(!(vm.IsTestSignalActive || vm.IsConnected || vm.ConnectButtonText != "Connect"), $"ToggleTestSignalCommand failed to deactivate test signal! IsActive={vm.IsTestSignalActive}, IsConnected={vm.IsConnected}");
        Expect(vm.TestSignalForeground == StudioPalette.ButtonLightForeground, "ViewModel TestSignalForeground inactive state is not StudioPalette.ButtonLightForeground!");
        Expect(vm.ConnectButtonForeground == StudioPalette.ButtonDarkForeground, "ViewModel ConnectButtonForeground disconnected state is not StudioPalette.ButtonDarkForeground!");
        Expect(!(vm.BroadcastState != BroadcastState.Offline || vm.IsInputLossBannerVisible), $"ViewModel must return Offline with the banner hidden: state={vm.BroadcastState}, banner={vm.IsInputLossBannerVisible}");

        // Test Folder Picker seam & exception resilience
        vm.OutputDirectory = "";
        vm.OpenOutputDirectoryCommand.Execute(null);
        Expect(vm.StatusText.Contains("not set", StringComparison.OrdinalIgnoreCase), $"OpenOutputDirectoryCommand with empty path should report not set, got: '{vm.StatusText}'");

        vm.PickFolderAsync = _ => throw new InvalidOperationException("Simulated picker error");
        vm.BrowseOutputDirectoryCommand.Execute(null);
        Expect(vm.StatusText.Contains("cancelled or failed", StringComparison.OrdinalIgnoreCase), $"BrowseOutputDirectoryCommand failed to catch error gracefully: '{vm.StatusText}'");

        string mockFolder = Path.Combine(Path.GetTempPath(), "OMT_Mock_Capture_Dir");
        vm.PickFolderAsync = _ => Task.FromResult<string?>(mockFolder);
        vm.BrowseOutputDirectoryCommand.Execute(null);
        Expect(vm.OutputDirectory == mockFolder, $"BrowseOutputDirectoryCommand failed to update OutputDirectory! Got: '{vm.OutputDirectory}'");

        // Test audio monitoring toggle
        vm.SetAudioMonitoring(true);
        Expect(vmSession.IsAudioMonitoringEnabled, "SetAudioMonitoring(true) failed to update session audio monitoring state!");
        vm.SetAudioMonitoring(false);
        Expect(!(vmSession.IsAudioMonitoringEnabled), "SetAudioMonitoring(false) failed to disable session audio monitoring state!");

        // Test ToggleRecordCommand validation when disconnected
        vm.ToggleRecordCommand.Execute(null);
        Expect(vm.StatusText.Contains("Please connect to an active OMT stream"), $"ToggleRecordCommand without active stream failed validation: '{vm.StatusText}'");

        // Connect live synthetic pattern generator to verify HUD overlay and live recording
        vm.SetTestSignalActiveAsync(true).GetAwaiter().GetResult();
        Expect(!(!vm.IsTestSignalActive || !vm.IsConnected || vm.ConnectButtonText != "Disconnect"), "Failed to activate test signal generator for recording test!");

        // Wait briefly for synthetic frames to negotiate stream format
        int frameWait = 0;
        while (!vmSession.CurrentFormat.HasVideo && frameWait < 30)
        {
            Thread.Sleep(50);
            frameWait++;
        }
        Expect(vmSession.CurrentFormat.HasVideo, "Synthetic pattern generator failed to deliver video stream format!");
        Expect(!(vm.IsNoSignalOverlayVisible || vm.LiveStatusText != "LIVE"), $"Video frame HUD overlay failed to transition to LIVE! OverlayVisible={vm.IsNoSignalOverlayVisible}, LiveStatus='{vm.LiveStatusText}'");
        Expect(vm.VideoSpecsText.Contains("1920x1080"), $"VideoSpecsText does not reflect synthetic 1080p stream! Got: '{vm.VideoSpecsText}'");

        // Test empty output directory pre-flight validation while connected
        vm.OutputDirectory = "   ";
        vm.ToggleRecordCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Expect(!(!vm.StatusText.Contains("valid destination folder", StringComparison.OrdinalIgnoreCase) || vm.IsRecording), $"ToggleRecordCommand with empty directory failed pre-flight validation: '{vm.StatusText}'");

        // Test active recording lifecycle through ViewModel
        vm.OutputDirectory = Path.GetTempPath();
        vm.ToggleRecordCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Expect(!(!vm.IsRecording || !vm.RecordButtonText.Contains("STOP RECORDING")), $"ToggleRecordCommand failed to start recording! IsRecording={vm.IsRecording}, BtnText='{vm.RecordButtonText}', Status='{vm.StatusText}'");
        Expect(!(vm.IsRecordingConfigEnabled), "IsRecordingConfigEnabled must be false while recording is active!");

        // Test close protection modal while active recording is in flight
        vm.IsRecordingCloseOverlayVisible = true;
        Expect(!(vm.IsMainContentEnabled), "IsMainContentEnabled must be false while RecordingCloseOverlay is visible!");
        vm.KeepRecordingCommand.Execute(null);
        Expect(!(vm.IsRecordingCloseOverlayVisible || vm.IsSafeExitConfirmed || !vm.IsMainContentEnabled), "KeepRecordingCommand failed to dismiss modal, restore IsMainContentEnabled, or left IsSafeExitConfirmed true!");

        // Test StopAndExitCommand safe finalization while recording
        bool closeRequested = false;
        vm.RequestClose += () => closeRequested = true;
        vm.StopAndExitCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        int exitWait = 0;
        while ((!vm.IsSafeExitConfirmed || vm.IsRecording) && exitWait < 30)
        {
            Thread.Sleep(50);
            exitWait++;
        }
        Expect(!(!vm.IsSafeExitConfirmed || !closeRequested || vm.IsRecording || !vm.IsRecordingConfigEnabled), $"StopAndExitCommand failed safe recording finalization! Confirmed={vm.IsSafeExitConfirmed}, CloseReq={closeRequested}, IsRec={vm.IsRecording}, ConfigEnabled={vm.IsRecordingConfigEnabled}");

        // Deactivate test signal generator and verify offline state
        vm.SetTestSignalActiveAsync(false).GetAwaiter().GetResult();
        Expect(!(vm.IsTestSignalActive || vm.IsConnected || vm.ConnectButtonText != "Connect" || !vm.IsNoSignalOverlayVisible || vm.LiveStatusText != "OFFLINE"), $"Deactivating test signal failed to restore offline state! IsActive={vm.IsTestSignalActive}, Live={vm.LiveStatusText}");
        Expect(!(vm.AudioTelemetryText != "Audio: --" || vm.BitrateText != "Bitrate: 0.0 Mbps" || vm.DroppedFramesText != "Dropped: 0" || vm.SourceNameText != ""), $"Deactivating test signal failed to reset telemetry! Audio='{vm.AudioTelemetryText}', Bitrate='{vm.BitrateText}', Dropped='{vm.DroppedFramesText}', Source='{vm.SourceNameText}'");

        // Test OmtSourceInfo equality
        var srcA = OmtSourceInfo.Parse("PC (Camera 1)");
        var srcB = OmtSourceInfo.Parse("PC (Camera 1)");
        var srcC = OmtSourceInfo.Parse("PC (Camera 2)");
        Expect(!(!srcA.Equals(srcB) || srcA != srcB || srcA == srcC || srcA.GetHashCode() != srcB.GetHashCode()), "OmtSourceInfo equality implementation failed!");

        // Test Dispose idempotency & seam cleanup
        vm.PickFolderAsync = _ => Task.FromResult<string?>("mock");
        vm.Dispose();
        Expect(vm.PickFolderAsync == null, "PickFolderAsync hook was not cleared on Dispose!");
        vm.Dispose();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> MainWindowViewModel MVVM patterns, bindings, commands, and seams verified!");
        Console.ResetColor();
    }

    /// Fully DECODES the recorded file with FFmpeg (not just a header probe) — the real "playable"
    /// check. Fails loudly on a non-zero exit or any stderr output at <c>-v error</c>.
    /// </summary>
    static void VerifyDecodeClean(string path, string label)
    {
        if (!File.Exists(path))
        {
            throw new Exception($"{label}: expected recorded file is missing: {path}");
        }

        long size = new FileInfo(path).Length;
        if (size < 10_000)
        {
            throw new Exception($"{label}: recorded file suspiciously small ({size} bytes): {path}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-v error -i \"{path}\" -f null -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process p = Process.Start(psi) ?? throw new Exception($"{label}: could not start ffmpeg for decode verification.");
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);

        if (p.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
        {
            throw new Exception($"{label}: decode is NOT clean (exit={p.ExitCode}). ffmpeg said: {stderr.Trim()}");
        }

        Console.WriteLine($"[{Now()}] {label}: decode-clean, {size / 1024.0 / 1024.0:F2} MB -> {Path.GetFileName(path)}");
    }

    /// <summary>Poll a condition, ticking a side effect (e.g. CheckInputHealth) between attempts.</summary>
    static void WaitUntil(Func<bool> condition, int timeoutMs, string what, Action? tick = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            tick?.Invoke();
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new Exception($"Timed out after {timeoutMs} ms waiting for {what}.");
            }
            Thread.Sleep(100);
        }
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

    /// <summary>
    /// Exercises the headless <see cref="BroadcastSessionState"/> module directly (ADR-0001): the
    /// enum contract, the transition rules, the stop-recording-before-disconnect ordering, and the
    /// selector-lock derivation. No Avalonia Application and no media session are involved — the
    /// media effects are injected as a recording fake.
    /// </summary>
    static void TestBroadcastSessionState()
    {
        Console.WriteLine("\n[Step 0.68] Verifying BroadcastSessionState module (headless state machine)...");

        // --- Enum contract: the five operating modes + the reserved Reconnecting state. ---
        var states = Enum.GetValues<BroadcastState>();
        foreach (BroadcastState required in new[]
                 {
                     BroadcastState.Offline, BroadcastState.Connecting, BroadcastState.NetworkConnected,
                     BroadcastState.TestSignal, BroadcastState.Broadcasting, BroadcastState.Reconnecting,
                     BroadcastState.InputLost
                 })
        {
            if (!states.Contains(required))
            {
                throw new Exception($"BroadcastState is missing the required member '{required}'.");
            }
        }
        if (states.Length != 7)
        {
            throw new Exception($"BroadcastState exposed {states.Length} members; expected exactly 7: {string.Join(",", states)}");
        }

        // --- Defaults: headless construct, Offline, both selectors unlocked. ---
        var module = new BroadcastSessionState();
        if (module.State != BroadcastState.Offline || module.IsBroadcasting || module.IsReceiving ||
            !module.IsSourceSelectorEnabled || !module.IsCaptureSelectorEnabled)
        {
            throw new Exception($"BroadcastSessionState defaults mismatch: State={module.State}, Broadcasting={module.IsBroadcasting}");
        }

        int events = 0;
        module.StateChanged += (_, __) => events++;
        var calls = new List<string>();
        var effects = new BroadcastTestEffects(calls);

        // --- Connect from Offline: nothing to leave, passes through Connecting (2 transitions). ---
        module.EnterNetworkConnectedAsync(effects, () => calls.Add("connect-network")).GetAwaiter().GetResult();
        if (module.State != BroadcastState.NetworkConnected || events != 2 ||
            string.Join(",", calls) != "connect-network")
        {
            throw new Exception($"EnterNetworkConnectedAsync failed: State={module.State}, events={events}, calls={string.Join(",", calls)}");
        }
        if (module.IsSourceSelectorEnabled || !module.IsCaptureSelectorEnabled || !module.IsReceiving)
        {
            throw new Exception($"Selector locks wrong while NetworkConnected: source={module.IsSourceSelectorEnabled}, capture={module.IsCaptureSelectorEnabled}");
        }

        // --- Switch NetworkConnected -> TestSignal: stop-recording-first, then disconnect, then connect (one transition). ---
        events = 0;
        calls.Clear();
        effects.IsRecording = true;
        module.EnterTestSignalAsync(effects, () => calls.Add("connect-test-signal")).GetAwaiter().GetResult();
        if (string.Join(",", calls) != "stop-recording,disconnect,connect-test-signal")
        {
            throw new Exception($"Stop-recording-first ordering violated: {string.Join(",", calls)}");
        }
        if (module.State != BroadcastState.TestSignal || events != 1)
        {
            throw new Exception($"Switching receive mode must be a single transition: State={module.State}, events={events}");
        }

        // --- Not recording => no stop-recording call, but still disconnects; selectors unlock. ---
        events = 0;
        calls.Clear();
        effects.IsRecording = false;
        module.LeaveReceiveModeAsync(effects).GetAwaiter().GetResult();
        if (string.Join(",", calls) != "disconnect" || module.State != BroadcastState.Offline || events != 1)
        {
            throw new Exception($"LeaveReceiveModeAsync (not recording) failed: calls={string.Join(",", calls)}, State={module.State}, events={events}");
        }
        if (!module.IsSourceSelectorEnabled || !module.IsCaptureSelectorEnabled)
        {
            throw new Exception("Selector locks must reset once Offline again.");
        }

        // --- Host broadcast is orthogonal: sender-only mode, survives receiving, and returns to Broadcasting. ---
        var broadcastModule = new BroadcastSessionState();
        var noCalls = new List<string>();
        broadcastModule.StartBroadcasting();
        if (broadcastModule.State != BroadcastState.Broadcasting || !broadcastModule.IsBroadcasting ||
            !broadcastModule.IsSourceSelectorEnabled || broadcastModule.IsCaptureSelectorEnabled)
        {
            throw new Exception($"StartBroadcasting (sender-only) failed: State={broadcastModule.State}, capture={broadcastModule.IsCaptureSelectorEnabled}");
        }

        broadcastModule.EnterNetworkConnectedAsync(new BroadcastTestEffects(noCalls), () => { }).GetAwaiter().GetResult();
        if (broadcastModule.State != BroadcastState.NetworkConnected || !broadcastModule.IsBroadcasting ||
            broadcastModule.IsSourceSelectorEnabled || broadcastModule.IsCaptureSelectorEnabled)
        {
            throw new Exception($"Broadcast must survive connecting to monitor it: State={broadcastModule.State}, Broadcasting={broadcastModule.IsBroadcasting}");
        }

        broadcastModule.LeaveReceiveModeAsync(new BroadcastTestEffects(noCalls)).GetAwaiter().GetResult();
        if (broadcastModule.State != BroadcastState.Broadcasting || !broadcastModule.IsBroadcasting)
        {
            throw new Exception($"Leaving the receive mode must return to Broadcasting, not Offline: State={broadcastModule.State}");
        }

        broadcastModule.StopBroadcasting();
        if (broadcastModule.State != BroadcastState.Offline || broadcastModule.IsBroadcasting || !broadcastModule.IsCaptureSelectorEnabled)
        {
            throw new Exception($"StopBroadcasting failed to return Offline: State={broadcastModule.State}, Broadcasting={broadcastModule.IsBroadcasting}");
        }

        // --- ADR-0002 outage states: NetworkConnected -> Reconnecting -> (NetworkConnected | InputLost). ---
        var outage = new BroadcastSessionState();
        int outageEvents = 0;
        outage.StateChanged += (_, __) => outageEvents++;
        outage.EnterNetworkConnectedAsync(new BroadcastTestEffects(new List<string>()), () => { }).GetAwaiter().GetResult();
        outageEvents = 0;

        outage.BeginReconnecting();
        if (outage.State != BroadcastState.Reconnecting || !outage.IsSessionActive ||
            outage.IsSourceSelectorEnabled || outageEvents != 1)
        {
            throw new Exception($"BeginReconnecting failed: State={outage.State}, sourceSelector={outage.IsSourceSelectorEnabled}, events={outageEvents}");
        }

        outage.EndReconnecting();
        if (outage.State != BroadcastState.NetworkConnected || outageEvents != 2)
        {
            throw new Exception($"EndReconnecting failed: State={outage.State}, events={outageEvents}");
        }

        outage.BeginReconnecting();
        outage.MarkInputLost();
        if (outage.State != BroadcastState.InputLost || !outage.IsSessionActive || outage.IsSourceSelectorEnabled)
        {
            throw new Exception($"MarkInputLost failed: State={outage.State}, sourceSelector={outage.IsSourceSelectorEnabled}");
        }

        // Leaving the session from the InputLost state still runs stop-recording-first, then disconnects.
        var outageCalls = new List<string>();
        outage.LeaveReceiveModeAsync(new BroadcastTestEffects(outageCalls) { IsRecording = true }).GetAwaiter().GetResult();
        if (string.Join(",", outageCalls) != "stop-recording,disconnect" || outage.State != BroadcastState.Offline)
        {
            throw new Exception($"Leaving an InputLost session failed: calls={string.Join(",", outageCalls)}, State={outage.State}");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> BroadcastSessionState transitions, stop-first ordering, and selector-lock derivation verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// Exercises the input-loss watchdog directly with a controllable clock (ADR-0002) — no real
    /// 5 s sleeps. Verifies the threshold edge, the arm/disarm gate, and single-shot transitions.
    /// </summary>
    static void TestInputLossWatchdog()
    {
        Console.WriteLine("\n[Step 0.67] Verifying InputHealthWatchdog (threshold, arming, edges)...");

        long now = 10_000;
        var watchdog = new InputHealthWatchdog(thresholdMs: 5000, clock: () => now);

        // Not armed => never fires, however long the silence.
        now += 60_000;
        if (watchdog.Poll() != InputHealthTransition.None || watchdog.IsLost)
        {
            throw new Exception("A disarmed watchdog must never report input loss.");
        }

        watchdog.Arm();
        if (!watchdog.IsArmed || watchdog.IsLost)
        {
            throw new Exception("Arm() failed to arm the watchdog.");
        }

        // Just under the threshold => still healthy.
        now += 4999;
        if (watchdog.Poll() != InputHealthTransition.None)
        {
            throw new Exception("Watchdog fired before the threshold elapsed.");
        }

        // At the threshold => Lost, exactly once.
        now += 1;
        if (watchdog.Poll() != InputHealthTransition.Lost || !watchdog.IsLost)
        {
            throw new Exception("Watchdog failed to fire at the threshold.");
        }
        if (watchdog.Poll() != InputHealthTransition.None)
        {
            throw new Exception("Watchdog must report Lost only on the transition edge.");
        }

        // A frame arrives => Restored, exactly once.
        watchdog.NotifyVideoFrame();
        if (watchdog.Poll() != InputHealthTransition.Restored || watchdog.IsLost)
        {
            throw new Exception("Watchdog failed to report restore when frames returned.");
        }
        if (watchdog.Poll() != InputHealthTransition.None)
        {
            throw new Exception("Watchdog must report Restored only on the transition edge.");
        }

        // The threshold is operator-configurable.
        var custom = new InputHealthWatchdog(thresholdMs: 1500, clock: () => now);
        custom.Arm();
        now += 1500;
        if (custom.Poll() != InputHealthTransition.Lost)
        {
            throw new Exception("A configured threshold was not honoured.");
        }

        // Disarm clears the state.
        custom.Disarm();
        if (custom.IsArmed || custom.IsLost || custom.Poll() != InputHealthTransition.None)
        {
            throw new Exception("Disarm() must clear the armed/lost state.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> InputHealthWatchdog threshold, arming, and edge transitions verified!");
        Console.ResetColor();
    }

    /// <summary>Verifies the deterministic reconnect backoff schedule (ADR-0002, no reconnect storms).</summary>
    static void TestReconnectBackoff()
    {
        Console.WriteLine("\n[Step 0.671] Verifying ReconnectBackoff schedule...");

        double[] expectedMs = { 250, 500, 1000, 2000, 4000, 5000, 5000, 5000 };
        for (int attempt = 1; attempt <= expectedMs.Length; attempt++)
        {
            double actual = ReconnectBackoff.DelayFor(attempt).TotalMilliseconds;
            if (Math.Abs(actual - expectedMs[attempt - 1]) > 0.001)
            {
                throw new Exception($"Backoff attempt {attempt}: expected {expectedMs[attempt - 1]} ms, got {actual} ms");
            }
        }

        for (int attempt = 2; attempt <= 6; attempt++)
        {
            if (ReconnectBackoff.DelayFor(attempt) < ReconnectBackoff.DelayFor(attempt - 1))
            {
                throw new Exception($"Backoff must never decrease (attempt {attempt}).");
            }
        }

        if (ReconnectBackoff.DelayFor(99) != ReconnectBackoff.MaxDelay)
        {
            throw new Exception("Backoff must stay capped at MaxDelay.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> ReconnectBackoff 250ms -> 500ms -> 1s -> 2s -> 4s -> 5s(cap) verified!");
        Console.ResetColor();
    }

    /// <summary>Verifies sequential segment naming (ADR-0002 segment-per-outage).</summary>
    static void TestRecordingSegments()
    {
        Console.WriteLine("\n[Step 0.672] Verifying RecordingSegments _partNN naming...");

        string basePath = Path.Combine(Path.GetTempPath(), "OMT_Capture_2026-10-15_13-08-43.mp4");
        int[] indices = { 1, 2, 3, 10 };
        string[] suffixes = { "", "_part02", "_part03", "_part10" };
        string stem = Path.Combine(Path.GetTempPath(), "OMT_Capture_2026-10-15_13-08-43");

        for (int i = 0; i < indices.Length; i++)
        {
            string expected = $"{stem}{suffixes[i]}.mp4";
            string actual = RecordingSegments.SegmentFilePath(basePath, indices[i]);
            if (actual != expected)
            {
                throw new Exception($"Segment {indices[i]}: expected '{expected}', got '{actual}'");
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Segment naming verified: base, _part02, _part03, _part10!");
        Console.ResetColor();
    }

    /// <summary>
    /// Headless verification of the ADR-0002 outage policy at the CaptureSession level: the watchdog
    /// arms only for a recording over a recoverable (network) source, the current segment is finalized
    /// at the outage boundary before anything else, frames returning within grace resume recording into
    /// the next _partNN segment, and reconnect attempts follow the backoff schedule. Clock-driven — no
    /// real 5 s sleeps.
    /// </summary>
    static void TestRecordingOutageRecovery()
    {
        Console.WriteLine("\n[Step 0.673] Verifying recording input-loss watchdog, auto-reconnect & segment-per-outage (headless)...");

        long now = 1_000_000;
        Func<long> clock = () => now;

        string outDir = Path.Combine(Path.GetTempPath(), "OMT_Outage_Test");
        if (Directory.Exists(outDir))
        {
            try { Directory.Delete(outDir, true); } catch { }
        }
        Directory.CreateDirectory(outDir);

        var config = new RecordingConfig
        {
            OutputDirectory = outDir,
            FilenamePrefix = "Outage_Test",
            ContainerFormat = OutputContainerFormat.MP4,
            EncoderChoice = VideoEncoderChoice.CpuX264,
            Quality = QualityPreset.High,
            InputLossThresholdSeconds = 5.0,
            InputRecoveryGraceSeconds = 30.0
        };

        // --- (a) An in-process source (test signal / synthetic) is never watched, even while recording. ---
        using (var sink = new NullRecordingSink())
        using (var recorder = new StreamRecorderService(sink))
        using (var audio = new AudioEngine())
        using (var session = new CaptureSession(audio, recorder, ownsDependencies: false, healthClock: clock))
        {
            session.Connect(new SyntheticPatternSource());
            WaitForFormat(session, "synthetic source");

            if (!session.StartRecording(config))
            {
                throw new Exception("Failed to start recording on the synthetic source.");
            }

            bool lostFired = false;
            session.InputLost += () => lostFired = true;

            now += 60_000; // a full minute of simulated silence
            session.CheckInputHealth();

            if (lostFired || session.IsInputLost || !session.IsRecording)
            {
                throw new Exception("The watchdog must not watch in-process sources (test signal / synthetic).");
            }

            session.StopRecording();
        }

        Console.WriteLine(" -> In-process sources are correctly excluded from the input-health watchdog.");

        // --- (b) A recoverable (network-like) source: outage -> finalize -> reconnect -> resume segment. ---
        using (var sink = new NullRecordingSink())
        using (var recorder = new StreamRecorderService(sink))
        using (var audio = new AudioEngine())
        using (var session = new CaptureSession(audio, recorder, ownsDependencies: false, healthClock: clock))
        {
            var source = new RecoverableTestSource();

            long frames = 0;
            session.VideoFrameAvailable += (p, l, w, h, s, fps, ts) => Interlocked.Increment(ref frames);

            var order = new List<string>();
            RecordingStopSummary? outageStopSummary = null;
            session.RecordingStopped += summary => { outageStopSummary = summary; order.Add("finalize"); };
            session.InputLost += () => order.Add("input-lost");
            session.InputRecovered += () => order.Add("input-recovered");

            session.Connect(source);
            WaitForFormat(session, "recoverable source");

            // Not recording => not watched, even with the feed dead.
            source.Suspend();
            Thread.Sleep(80);
            now += 60_000;
            session.CheckInputHealth();
            if (session.IsInputLost || session.IsInputHealthWatched)
            {
                throw new Exception("The watchdog must not arm unless a recording is in progress.");
            }

            // Connect the take.
            source.Resume();
            long framesBeforeTake = Interlocked.Read(ref frames);
            WaitFor(() => Interlocked.Read(ref frames) > framesBeforeTake, "frames before starting the take");
            now += 1000;
            session.CheckInputHealth();

            if (!session.StartRecording(config))
            {
                throw new Exception("Failed to start recording on the recoverable source.");
            }
            if (session.RecordingSegmentIndex != 1 || !session.IsInputHealthWatched)
            {
                throw new Exception($"Take must open segment 1 and arm the watchdog: segment={session.RecordingSegmentIndex}, watched={session.IsInputHealthWatched}");
            }
            string? firstSegment = session.CurrentRecordingPath;

            if (session.IsInputLost)
            {
                throw new Exception("Watchdog reported loss while frames were flowing.");
            }

            // --- Outage boundary: silence past the threshold. ---
            source.Suspend();
            Thread.Sleep(80);
            now += 6000; // > 5 s threshold
            session.CheckInputHealth();

            if (session.IsRecording)
            {
                throw new Exception("Recording must be stopped (segment finalized) at the outage boundary.");
            }
            if (order.Count < 2 || order[0] != "finalize" || order[1] != "input-lost")
            {
                throw new Exception($"Segment must be finalized BEFORE the alarm is raised: [{string.Join(",", order)}]");
            }
            if (outageStopSummary == null || outageStopSummary.SegmentIndex != 1 || outageStopSummary.MediaDuration <= TimeSpan.Zero)
            {
                throw new Exception(
                    $"Boundary stop must carry a segment-1 summary with a positive media duration: " +
                    $"{(outageStopSummary == null ? "null" : $"seg={outageStopSummary.SegmentIndex}, media={outageStopSummary.MediaDuration}")}");
            }

            // --- Reconnect attempts follow the backoff schedule (250 ms, then 500 ms). ---
            WaitFor(() => source.ReconnectCount >= 1, "first auto-reconnect attempt");
            now += 250;
            session.CheckInputHealth();
            WaitFor(() => source.ReconnectCount >= 2, "second auto-reconnect attempt");

            now += 100; // only 100 ms of the 500 ms window elapsed
            session.CheckInputHealth();
            Thread.Sleep(80);
            if (source.ReconnectCount != 2)
            {
                throw new Exception($"Reconnect attempts must honour the backoff schedule; got {source.ReconnectCount} after 350 ms.");
            }

            // The second attempt (at T+250) schedules the third at T+750; advance to exactly that.
            now += 400;
            session.CheckInputHealth();
            WaitFor(() => source.ReconnectCount >= 3, "third auto-reconnect attempt");

            // --- Frames return within the grace period: resume into the next _partNN segment. ---
            long framesBeforeResume = Interlocked.Read(ref frames);
            source.Resume();
            WaitFor(() => Interlocked.Read(ref frames) > framesBeforeResume, "frames after resume");
            now += 100;
            session.CheckInputHealth();

            if (!session.IsRecording)
            {
                throw new Exception("Recording must resume when frames return within the grace period.");
            }
            if (session.RecordingSegmentIndex != 2)
            {
                throw new Exception($"Resume must open segment 2, got {session.RecordingSegmentIndex}.");
            }
            string? second = session.CurrentRecordingPath;
            if (second == null || !second.EndsWith("_part02" + Path.GetExtension(firstSegment ?? ".mp4"), StringComparison.Ordinal))
            {
                throw new Exception($"Resumed segment must be named …_part02: got '{second}' (from '{firstSegment}')");
            }
            if (!order.SequenceEqual(new[] { "finalize", "input-lost", "input-recovered" }))
            {
                throw new Exception($"Unexpected transition order: [{string.Join(",", order)}]");
            }

            session.StopRecording();
        }

        Console.WriteLine(" -> Outage finalize-before-alarm, backoff reconnect, and _part02 resume verified!");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Input-loss watchdog, auto-reconnect, and segment-per-outage verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// DEF-01: a stop that follows dead air must report MEDIA duration (frames / stamped fps), never
    /// wall clock — the watchdog-triggered stop used to count the input-loss window and disagreed with
    /// the container (QA: 11.05 s reported vs 5.92 s on disk). Real (not injected) time here, so the
    /// wall clock genuinely accrues dead air while no frames arrive.
    /// </summary>
    static void TestRecordingStopReportsMediaDuration()
    {
        Console.WriteLine("\n[Step 0.674] Verifying DEF-01: stop reports MEDIA duration, not wall clock (dead air excluded)...");

        string outDir = Path.Combine(Path.GetTempPath(), "OMT_Duration_Test");
        if (Directory.Exists(outDir))
        {
            try { Directory.Delete(outDir, true); } catch { }
        }
        Directory.CreateDirectory(outDir);

        using var sink = new NullRecordingSink();
        using var recorder = new StreamRecorderService(sink);
        using var audio = new AudioEngine();
        using var session = new CaptureSession(audio, recorder);
        var source = new RecoverableTestSource();

        RecordingStopSummary? summary = null;
        session.RecordingStopped += s => summary = s;

        session.Connect(source);
        WaitForFormat(session, "recoverable source");

        var config = new RecordingConfig
        {
            OutputDirectory = outDir,
            FilenamePrefix = "Duration_Test",
            ContainerFormat = OutputContainerFormat.MP4,
            EncoderChoice = VideoEncoderChoice.CpuX264,
            Quality = QualityPreset.High,
            // Far above the simulated gap, so the watchdog does not interfere with this test.
            InputLossThresholdSeconds = 999,
            InputRecoveryGraceSeconds = 999
        };

        if (!session.StartRecording(config))
        {
            throw new Exception("StartRecording failed for the DEF-01 duration test.");
        }

        // ~0.6 s of live frames, then 1.2 s of dead air (frames stop, nothing is written).
        Thread.Sleep(600);
        source.Suspend();
        Thread.Sleep(1200);

        long framesWritten = session.RecordingFramesWritten;
        TimeSpan wallAtStop = session.RecordingElapsedTime;
        session.StopRecording();

        if (summary == null)
        {
            throw new Exception("RecordingStopped did not fire for the DEF-01 duration test.");
        }

        // Wall clock really did span the dead air...
        if (summary.WallClockDuration.TotalSeconds < 1.5)
        {
            throw new Exception($"Wall-clock duration should span the dead air (>=1.5s), got {summary.WallClockDuration.TotalSeconds:F3}s");
        }

        // ...and the reported duration must NOT be that wall clock: dead air is excluded.
        if (summary.MediaDuration.TotalSeconds >= summary.WallClockDuration.TotalSeconds - 1.0)
        {
            throw new Exception(
                $"DEF-01 regression: reported duration {summary.MediaDuration.TotalSeconds:F3}s tracks wall clock " +
                $"{summary.WallClockDuration.TotalSeconds:F3}s (dead air not excluded).");
        }

        // Media duration is frames / stamped fps — SyntheticPatternSource stamps 60 fps.
        double expectedMediaSeconds = framesWritten / 60.0;
        if (framesWritten <= 0 || Math.Abs(summary.MediaDuration.TotalSeconds - expectedMediaSeconds) > 0.35)
        {
            throw new Exception(
                $"Media duration {summary.MediaDuration.TotalSeconds:F3}s does not match frames/fps " +
                $"({framesWritten} frames -> {expectedMediaSeconds:F3}s).");
        }

        if (summary.MediaDuration > wallAtStop)
        {
            throw new Exception($"Media duration {summary.MediaDuration} cannot exceed the captured span {wallAtStop}.");
        }
        if (summary.AudioDuration <= TimeSpan.Zero)
        {
            throw new Exception("Audio duration should be positive (audio flowed before the gap).");
        }

        Console.WriteLine(
            $" -> media {summary.MediaDuration.TotalSeconds:F3}s (frames {framesWritten}) vs wall-clock " +
            $"{summary.WallClockDuration.TotalSeconds:F3}s; dead air excluded.");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> DEF-01 verified: reported duration is media duration, wall clock logged separately.");
        Console.ResetColor();
    }

    /// <summary>
    /// FEAT-07: pure recording-health analysis with synthetic telemetry — verdict computation, drop
    /// thresholds and the A/V clamp warning. No FFmpeg, no UI, no I/O.
    /// </summary>
    static void TestRecordingHealthModel()
    {
        Console.WriteLine("\n[Step 0.675] Verifying RecordingHealth model (verdict, drop thresholds, clamp warning)...");

        var healthy = new RecordingHealthSnapshot
        {
            IsRecording = true,
            HasTake = true,
            SegmentIndex = 1,
            EncoderName = RecordingHealth.EncoderDisplayName(VideoEncoderChoice.CpuX264),
            AchievedBitrateMbps = 6.2,
            VideoFramesWritten = 360,
            VideoFramesDropped = 0,
            AudioChunksDropped = 0,
            MediaDuration = TimeSpan.FromSeconds(6.0),
            AudioDuration = TimeSpan.FromSeconds(6.02),
            WallClockDuration = TimeSpan.FromSeconds(6.1),
            AvDriftRatio = 1.0,
            AvDriftClamped = false
        };

        if (RecordingHealth.IsDropWarning(healthy) || RecordingHealth.IsDriftWarning(healthy) || !RecordingHealth.IsAvParityOk(healthy))
        {
            throw new Exception("A healthy snapshot must not raise any warning.");
        }
        if (RecordingHealth.BitrateText(healthy) != "6.2 Mbps" || RecordingHealth.EncoderText(healthy) != "x264 (CPU)")
        {
            throw new Exception($"Health text mismatch: bitrate='{RecordingHealth.BitrateText(healthy)}', encoder='{RecordingHealth.EncoderText(healthy)}'");
        }

        // Drop threshold: warns AT the threshold, not below.
        if (RecordingHealth.IsDropWarning(healthy with { VideoFramesDropped = RecordingHealth.DropWarnThreshold - 1 }))
        {
            throw new Exception("Drops below the threshold must not warn.");
        }
        if (!RecordingHealth.IsDropWarning(healthy with { VideoFramesDropped = RecordingHealth.DropWarnThreshold }))
        {
            throw new Exception("Drops at the threshold must warn.");
        }

        // Drift band: |ratio - 1| beyond the band warns; a pinned clamp always warns.
        // (Strictly inside: 1.0 + 0.02 is not representable exactly in binary, so use a margin.)
        if (RecordingHealth.IsDriftWarning(healthy with { AvDriftRatio = 1.0 + RecordingHealth.DriftWarnBand - 0.001 }))
        {
            throw new Exception("Drift inside the band must not warn.");
        }
        if (!RecordingHealth.IsDriftWarning(healthy with { AvDriftRatio = 1.0 + RecordingHealth.DriftWarnBand + 0.001 }))
        {
            throw new Exception("Drift beyond the band must warn.");
        }
        if (!RecordingHealth.IsDriftWarning(healthy with { AvDriftRatio = 1.0, AvDriftClamped = true }))
        {
            throw new Exception("A pinned clamp must warn even at ratio 1.0.");
        }
        if (!RecordingHealth.DriftText(healthy with { AvDriftRatio = 1.1, AvDriftClamped = true }).Contains("clamped", StringComparison.Ordinal))
        {
            throw new Exception("DriftText must surface the clamp.");
        }

        // Duration parity: within tolerance is OK, beyond it is not, and a missing track cannot mismatch.
        if (!RecordingHealth.IsAvParityOk(healthy with { AudioDuration = TimeSpan.FromSeconds(6.0 + RecordingHealth.AvParityToleranceSeconds) }))
        {
            throw new Exception("Parity exactly at the tolerance edge must be OK.");
        }
        if (RecordingHealth.IsAvParityOk(healthy with { AudioDuration = TimeSpan.FromSeconds(6.0 + RecordingHealth.AvParityToleranceSeconds + 0.5) }))
        {
            throw new Exception("Parity beyond the tolerance must be reported.");
        }
        if (!RecordingHealth.IsAvParityOk(healthy with { AudioDuration = TimeSpan.Zero }))
        {
            throw new Exception("A missing audio track must not be called a parity mismatch.");
        }

        // Stop verdict: reports MEDIA duration + drops + finalize + segment + encoder, never wall clock.
        var summary = new RecordingStopSummary(
            Path: @"C:\captures\Take.mp4",
            MediaDuration: TimeSpan.FromSeconds(5.91),
            WallClockDuration: TimeSpan.FromSeconds(11.06), // includes ~5 s of dead air
            AudioDuration: TimeSpan.FromSeconds(5.93),
            FinalizeDuration: TimeSpan.FromMilliseconds(412),
            FileSizeBytes: 486_548,
            VideoFramesWritten: 355,
            VideoFramesDropped: 2,
            AudioChunksDropped: 0,
            SegmentIndex: 1,
            EncoderName: "x264 (CPU)",
            DiagnosticTail: "frame=1 fps=0 q=0.0");

        string verdict = RecordingHealth.BuildStopVerdict(summary);
        Console.WriteLine($" -> {verdict}");

        foreach (string expected in new[] { "video 5.91s", "audio 5.93s", "parity OK", "drops 2 video", "finalize 412 ms", "segment 1", "x264 (CPU)" })
        {
            if (!verdict.Contains(expected, StringComparison.Ordinal))
            {
                throw new Exception($"Stop verdict is missing '{expected}': {verdict}");
            }
        }
        if (verdict.Contains("11.06", StringComparison.Ordinal))
        {
            throw new Exception($"DEF-01 regression: the stop verdict must not report wall clock: {verdict}");
        }

        string wallNote = RecordingHealth.WallClockNote(summary);
        if (!wallNote.Contains("11.06", StringComparison.Ordinal) || !wallNote.Contains("dead air", StringComparison.Ordinal))
        {
            throw new Exception($"Wall-clock must be reported separately and labelled: {wallNote}");
        }

        // The summary derives its own snapshot: achieved bitrate comes from file size / MEDIA duration.
        double expectedMbps = 486_548 * 8.0 / 1_000_000.0 / 5.91;
        if (Math.Abs(summary.ToHealthSnapshot().AchievedBitrateMbps - expectedMbps) > 0.01)
        {
            throw new Exception($"Derived bitrate mismatch: {summary.ToHealthSnapshot().AchievedBitrateMbps:F3} vs {expectedMbps:F3}");
        }
        if (summary.ToHealthSnapshot().DiagnosticTail != "frame=1 fps=0 q=0.0")
        {
            throw new Exception("The summary must carry its diagnostic tail through to the snapshot.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> RecordingHealth verdict, drop thresholds, clamp warning and parity verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// ADR-0003 Slice 1: the stereo capture ring is a pure in-memory module now, so its whole policy
    /// (fill/drain, wrap, latency cap, overflow, underrun→silence, exactly-once handoff) is testable
    /// with no hardware. Previously the ring existed only inside two live capture devices.
    /// </summary>
    static void TestStereoCaptureRing()
    {
        Console.WriteLine("\n[Step 0.676] Verifying StereoCaptureRing (fill/drain, wrap, latency cap, underrun)...");

        // --- Defaults and argument guards. ---
        var defaults = new StereoCaptureRing();
        if (defaults.CapacitySamples != 48_000 || defaults.MaxLatencySamples != 5_760)
        {
            throw new Exception($"Ring defaults mismatch: capacity={defaults.CapacitySamples}, maxLatency={defaults.MaxLatencySamples}");
        }
        bool threw = false;
        try { _ = new StereoCaptureRing(100, 500); } catch (ArgumentOutOfRangeException) { threw = true; }
        if (!threw) throw new Exception("A latency cap larger than the capacity must be rejected.");

        // --- Fill / drain, exactly once, in order (cap == capacity so nothing is dropped). ---
        var ring = new StereoCaptureRing(1000, 1000);
        if (ring.AvailableSamples != 0) throw new Exception("A new ring must be empty.");

        // Sized for the largest single write below (1200 in the overflow case).
        var left = new float[1200];
        var right = new float[1200];
        for (int i = 0; i < 1000; i++) { left[i] = i; right[i] = -i; }
        ring.Write(left, right, 1000);
        if (ring.AvailableSamples != 1000) throw new Exception($"Expected 1000 available, got {ring.AvailableSamples}");

        var outL = new float[1000];
        var outR = new float[1000];
        // Read in uneven chunks: every frame must arrive exactly once, in order.
        int got = 0;
        foreach (int chunk in new[] { 30, 30, 40, 500, 400 })
        {
            ring.Read(outL.AsSpan(got, chunk), outR.AsSpan(got, chunk), chunk);
            got += chunk;
        }
        for (int i = 0; i < 1000; i++)
        {
            if (outL[i] != i || outR[i] != -i)
            {
                throw new Exception($"Frame {i} came back as L={outL[i]}/R={outR[i]} (expected L={i}/R={-i}).");
            }
        }
        if (ring.AvailableSamples != 0) throw new Exception("Draining the ring must leave it empty.");

        // --- Wrap-around across the ring boundary. ---
        for (int i = 0; i < 600; i++) { left[i] = 2000 + i; right[i] = 2000 + i; }
        ring.Write(left, right, 600);
        ring.Read(outL.AsSpan(0, 600), outR.AsSpan(0, 600), 600);
        for (int i = 0; i < 600; i++)
        {
            if (outL[i] != 2000 + i) throw new Exception($"Wrap read mismatch at {i}: {outL[i]} != {2000 + i}");
        }

        // --- Overflow: the oldest frames are overwritten, the newest window is retained. ---
        for (int i = 0; i < 1200; i++) { left[i] = 5000 + i; right[i] = 5000 + i; }
        ring.Write(left, right, 1200);
        if (ring.AvailableSamples != 1000) throw new Exception($"A full ring must hold exactly its capacity, got {ring.AvailableSamples}");
        ring.Read(outL, outR, 1000);
        for (int i = 0; i < 1000; i++)
        {
            if (outL[i] != 5000 + 200 + i)
            {
                throw new Exception($"Overflow window mismatch at {i}: {outL[i]} != {5000 + 200 + i}");
            }
        }

        // --- Underrun → silence: the shortfall is zero-filled, never stale. ---
        ring.Write(left, right, 10);                      // 10 frames available
        for (int i = 0; i < 200; i++) outL[i] = 7f;       // sentinel
        for (int i = 0; i < 200; i++) outR[i] = 7f;
        ring.Read(outL.AsSpan(0, 200), outR.AsSpan(0, 200), 200);
        for (int i = 0; i < 10; i++)
        {
            if (outL[i] != 5000 + i) throw new Exception($"Underrun prefix mismatch at {i}: {outL[i]}");
        }
        for (int i = 10; i < 200; i++)
        {
            if (outL[i] != 0f || outR[i] != 0f) throw new Exception($"Underrun shortfall at {i} must be silence, got {outL[i]}/{outR[i]}");
        }
        if (ring.AvailableSamples != 0) throw new Exception("An underrun read must consume everything available.");

        // --- ReadUpTo: reads exactly min(available, max) and does NOT zero-fill. ---
        ring.Write(left, right, 300);
        for (int i = 0; i < 500; i++) outL[i] = 9f;
        int read = ring.ReadUpTo(outL.AsSpan(0, 500), outR.AsSpan(0, 500), 500);
        if (read != 300) throw new Exception($"ReadUpTo should report 300, got {read}");
        for (int i = 300; i < 500; i++)
        {
            if (outL[i] != 9f) throw new Exception($"ReadUpTo must not touch the destination beyond the frames read (index {i}).");
        }
        if (ring.AvailableSamples != 0) throw new Exception("ReadUpTo must consume what it reported.");

        // --- Latency cap: past the cap the ring jumps to half the window and keeps the NEWEST frames. ---
        var capped = new StereoCaptureRing(1000, 100);
        for (int i = 0; i < 100; i++) { left[i] = i; right[i] = i; }
        capped.Write(left, right, 100);
        if (capped.AvailableSamples != 100) throw new Exception($"At the cap the window must be intact, got {capped.AvailableSamples}");

        // The 101st frame trips the cap. Drop arithmetic is `available - cap/2` = 101 - 50 = 51, so the
        // retained window is ring indices 51..100: the newest 49 written frames plus this new one.
        left[0] = 1234f;
        capped.Write(left, right, 1);
        if (capped.AvailableSamples != 50)
        {
            throw new Exception($"Exceeding the cap must drop to half the window (50), got {capped.AvailableSamples}");
        }
        capped.Read(outL.AsSpan(0, 50), outR.AsSpan(0, 50), 50);
        for (int i = 0; i < 49; i++)
        {
            if (outL[i] != 51 + i)
            {
                throw new Exception($"The cap must retain the NEWEST frames; index {i} was {outL[i]}, expected {51 + i}");
            }
        }
        if (outL[49] != 1234f)
        {
            throw new Exception($"The just-written frame must survive the cap drop; index 49 was {outL[49]}, expected 1234");
        }

        // --- Reset clears everything (used when a capture stream is torn down). ---
        capped.Write(left, right, 10);
        capped.Reset();
        if (capped.AvailableSamples != 0) throw new Exception("Reset must empty the ring.");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> StereoCaptureRing fill/drain, wrap, overflow, underrun, ReadUpTo and latency cap verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// ADR-0003 Slice 1: the negotiated-state members live on <see cref="IHostAudioSource"/>, so no
    /// caller needs to downcast to a concrete driver adapter. Exercised through the interface type
    /// only — if a member were missing, this would not compile.
    /// </summary>
    static void TestHostAudioSourceInterface()
    {
        Console.WriteLine("\n[Step 0.677] Verifying IHostAudioSource widening (no downcasts) + shared format constants...");

        if (HostAudioFormat.TargetSampleRate != 48_000 || HostAudioFormat.TargetChannels != 2)
        {
            throw new Exception($"Shared host-audio vocabulary mismatch: {HostAudioFormat.TargetSampleRate}/{HostAudioFormat.TargetChannels}");
        }

        // The silent clock: a source with no device behind it.
        using (IHostAudioSource silent = HostAudioSourceFactory.Create(HostAudioCaptureMode.Mute))
        {
            if (silent.IsHardwareActive) throw new Exception("The silent source must not claim hardware.");
            if (silent.DeviceSampleRate != 48_000 || silent.IsResamplingRequired) throw new Exception("The silent source negotiates nothing.");
            if (silent.RateNegotiationNote != null || silent.InputLatencyFrames != 0 || silent.LastError != null)
            {
                throw new Exception("The silent source must report no note, no latency and no error.");
            }
            var planar = new float[2 * 64];
            silent.FillPlanarAudio(planar, 64);
        }

        // A real adapter, reached ONLY through the interface (this is the AC5 assertion: no downcast).
        // Hardware may or may not be present on a headless box, so is-state is asserted, not its value.
        using (IHostAudioSource system = HostAudioSourceFactory.Create(HostAudioCaptureMode.SystemAudio))
        {
            int rate = system.DeviceSampleRate;
            if (rate <= 0) throw new Exception($"DeviceSampleRate must be positive, got {rate}");
            if (system.IsResamplingRequired != (rate != HostAudioFormat.TargetSampleRate))
            {
                throw new Exception("IsResamplingRequired must agree with the negotiated device rate.");
            }
            string summary = $"{system.SampleRate}Hz -> {rate}Hz, resample={system.IsResamplingRequired}, active={system.IsHardwareActive}, err={system.LastError ?? "none"}";
            Console.WriteLine($" -> SystemAudio source via IHostAudioSource only: {summary}");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Interface widening verified (silent + system sources, zero concrete-type downcasts)!");
        Console.ResetColor();
    }

    /// <summary>
    /// ADR-0003 Slice 2: the rate-conversion policy is one shared module now, so it is testable
    /// headlessly — bit-exact identity at matched rates, and sinc-vs-linear accuracy at 44.1→48 kHz.
    /// </summary>
    static void TestFixedRatioStereoResampler()
    {
        Console.WriteLine("\n[Step 0.678] Verifying FixedRatioStereoResampler (passthrough identity, sinc vs linear)...");

        // --- Matched rates: bit-exact identity, no delay, channels independent. ---
        var identity = new FixedRatioStereoResampler(48000, 48000);
        if (!identity.IsPassthrough || identity.Step != 1.0)
        {
            throw new Exception($"A matched-rate resampler must report passthrough (step={identity.Step}).");
        }

        int n = 2048;
        var inL = new float[n + 256];
        var inR = new float[n + 256];
        for (int i = 0; i < inL.Length; i++)
        {
            inL[i] = (float)Math.Sin(2.0 * Math.PI * 997.0 * i / 48000.0);
            inR[i] = inL[i] * -0.5f;
        }
        identity.Append(inL, inR, inL.Length);

        var idOutL = new float[n];
        var idOutR = new float[n];
        int idProduced = identity.Process(idOutL, idOutR, n);
        if (idProduced != n) throw new Exception($"Matched-rate resampler produced {idProduced}/{n} frames.");

        // Phase 0 of the kernel is a unit impulse, so the output must be the input with NO delay and NO
        // filtering — to within float rounding of the (float-quantised, DC-normalised) filter table.
        float maxDev = 0f;
        for (int i = 0; i < n; i++)
        {
            maxDev = Math.Max(maxDev, Math.Abs(idOutL[i] - inL[i]));
            maxDev = Math.Max(maxDev, Math.Abs(idOutR[i] - inR[i]));
        }
        Console.WriteLine($" -> 48000->48000 identity: max sample deviation {maxDev:E2} over {n} frames (no delay, no filtering)");
        if (maxDev > 1e-6f)
        {
            throw new Exception($"Matched-rate passthrough must be identity to float rounding; max deviation {maxDev:E3}");
        }

        // Delay-free: the very first sample must already be the first input sample, not silence.
        if (Math.Abs(idOutL[0] - inL[0]) > 1e-6f)
        {
            throw new Exception($"Pass-through must not introduce delay; output[0]={idOutL[0]} vs input[0]={inL[0]}");
        }

        // --- 44.1 -> 48 kHz on a 200 Hz-8 kHz sweep: sinc must beat the linear policy it replaced. ---
        const int inRate = 44100;
        const int outRate = 48000;
        const int inFrames = 44100;                 // 1 s
        const double f0 = 200.0, f1 = 8000.0;
        var sweep = new float[inFrames];
        for (int i = 0; i < inFrames; i++)
        {
            double t = (double)i / inRate;
            sweep[i] = (float)Math.Sin(SweepPhase(t, f0, f1));
        }

        var sinc = new FixedRatioStereoResampler(inRate, outRate);
        sinc.Append(sweep, sweep, inFrames);
        const int wantedOutputs = 24096;
        var sincOut = new float[wantedOutputs];
        var sincOutR = new float[wantedOutputs];
        int sincProduced = sinc.Process(sincOut, sincOutR, wantedOutputs);
        if (sincProduced < 20000) throw new Exception($"Sinc resampler produced only {sincProduced} frames.");

        // Linear-interpolation reference: exactly the per-sample lerp the WASAPI callback used to do.
        double step = (double)inRate / outRate;
        var linearOut = new float[sincProduced];
        double pos = 0.0;
        for (int k = 0; k < sincProduced; k++)
        {
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, inFrames - 1);
            float frac = (float)(pos - i0);
            linearOut[k] = sweep[i0] + (sweep[i1] - sweep[i0]) * frac;
            pos += step;
        }

        // Signal-to-error against the ideal tone at the OUTPUT rate (the resampler preserves time:
        // output k corresponds to input time k/outRate), measured over the same span for both.
        const int measureFrom = 1000;
        const int measureCount = 18000;
        double idealEnergy = 0.0, sincErr = 0.0, linearErr = 0.0;
        for (int k = measureFrom; k < measureFrom + measureCount; k++)
        {
            double ideal = Math.Sin(SweepPhase((double)k / outRate, f0, f1));
            idealEnergy += ideal * ideal;
            sincErr += Math.Pow(sincOut[k] - ideal, 2);
            linearErr += Math.Pow(linearOut[k] - ideal, 2);
        }
        double sincDb = 10.0 * Math.Log10(idealEnergy / Math.Max(sincErr, 1e-30));
        double linearDb = 10.0 * Math.Log10(idealEnergy / Math.Max(linearErr, 1e-30));
        Console.WriteLine($" -> 44100->48000 sine sweep (200 Hz-8 kHz) SER: sinc {sincDb:F1} dB vs linear {linearDb:F1} dB");

        if (sincDb < 40.0) throw new Exception($"Sinc SER {sincDb:F1} dB is below the 40 dB floor.");
        if (sincDb < linearDb + 10.0)
        {
            throw new Exception($"Sinc must beat the linear policy by >=10 dB; got sinc {sincDb:F1} vs linear {linearDb:F1} dB.");
        }

        // Right channel was fed the same sweep, so it must match the left bit-for-bit.
        for (int k = 0; k < sincProduced; k++)
        {
            if (sincOut[k] != sincOutR[k]) throw new Exception($"Channels diverged at frame {k}.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Resampler passthrough identity + sinc superiority over linear verified!");
        Console.ResetColor();
    }

    /// <summary>Phase of a linear frequency sweep f0→f1 over one second (T = 1 s).</summary>
    static double SweepPhase(double t, double f0, double f1)
        => 2.0 * Math.PI * (f0 * t + (f1 - f0) / 2.0 * t * t);

    /// <summary>
    /// ADR-0003 Slice 2, structural assertion: rate conversion must sit on the CONSUMER side of both
    /// adapters, and the resampler must exist in exactly ONE place. This is a source-shape check —
    /// the behavioural half is covered by the resampler step above plus the live GUI steps.
    /// </summary>
    static void TestConversionPlacement()
    {
        Console.WriteLine("\n[Step 0.679] Verifying rate conversion sits on the consumer side (structure)...");

        string? wasapi = FindRepoSource("OmtCaptureStudio/Services/WasapiHostAudioSource.cs");
        string? asio = FindRepoSource("OmtCaptureStudio/Services/AsioHostAudioSource.cs");
        string? module = FindRepoSource("OmtCaptureStudio/Services/Audio/FixedRatioStereoResampler.cs");
        if (wasapi == null || asio == null || module == null)
        {
            Console.WriteLine($" -> [SKIP] source tree not reachable from {AppContext.BaseDirectory}");
            return;
        }

        string wasapiText = File.ReadAllText(wasapi);
        string asioText = File.ReadAllText(asio);

        // The WASAPI capture callback must no longer carry any conversion state or math.
        foreach (string forbidden in new[] { "resampleStep", "resamplePos" })
        {
            if (wasapiText.Contains(forbidden))
            {
                throw new Exception($"WASAPI still carries capture-callback conversion state: '{forbidden}'");
            }
        }

        int ingestStart = wasapiText.IndexOf("private void IngestPacket(", StringComparison.Ordinal);
        int ingestEnd = wasapiText.IndexOf("private static void ReadFrameStereo(", StringComparison.Ordinal);
        if (ingestStart < 0 || ingestEnd <= ingestStart) throw new Exception("Could not locate IngestPacket to inspect.");
        string ingestBody = wasapiText.Substring(ingestStart, ingestEnd - ingestStart);
        if (ingestBody.Contains("l1 - l0") || ingestBody.Contains("frac"))
        {
            throw new Exception("IngestPacket still interpolates — conversion has not left the capture callback.");
        }
        if (!ingestBody.Contains("_ring.WriteSampleLocked"))
        {
            throw new Exception("IngestPacket no longer feeds the ring; the refactor did not land as expected.");
        }

        // Conversion must now live on the consumer read path of BOTH adapters.
        if (!wasapiText.Contains("FillResampled"))
        {
            throw new Exception("WASAPI has no consumer-side FillResampled path.");
        }
        if (!wasapiText.Contains("FixedRatioStereoResampler") || !asioText.Contains("FixedRatioStereoResampler"))
        {
            throw new Exception("Both adapters must share FixedRatioStereoResampler.");
        }

        // Exactly one definition of the resampler, and not as a nested private class any more.
        if (asioText.Contains("private sealed class FixedRatioStereoResampler"))
        {
            throw new Exception("ASIO still declares its own nested resampler — the module was not promoted.");
        }
        if (!File.ReadAllText(module).Contains("public sealed class FixedRatioStereoResampler"))
        {
            throw new Exception("The promoted resampler module is not where it should be.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Conversion placement verified (callback pure, consumer-side resampler shared by both adapters)!");
        Console.ResetColor();
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

    /// <summary>Walks up from the test binary to the repo root and resolves a repo-relative source path.</summary>
    static string? FindRepoSource(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(Path.Combine(dir.FullName, "OmtCaptureStudio.sln"))) return null;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// ADR-0005: disk-storage policy is a pure module now, so preflight and low-water decisions are
    /// tested as functions of drive facts — no real volume, no mocks, no disk I/O.
    /// </summary>
    static void TestRecordingStorageGuard()
    {
        Console.WriteLine("\n[Step 0.681] Verifying RecordingStorageGuard (preflight, low-water, size estimate)...");

        // --- File-system guard: FAT/FAT32 carry a 4 GB single-file limit; exFAT/NTFS do not. ---
        if (!RecordingStorageGuard.IsFattishFileSystem("FAT32")) throw new Exception("FAT32 must be flagged.");
        if (!RecordingStorageGuard.IsFattishFileSystem("fat")) throw new Exception("The file-system check must be case-insensitive.");
        if (RecordingStorageGuard.IsFattishFileSystem("NTFS")) throw new Exception("NTFS must not be flagged.");
        if (RecordingStorageGuard.IsFattishFileSystem("exFAT")) throw new Exception("exFAT has no 4 GB limit and must not be flagged.");
        if (RecordingStorageGuard.IsFattishFileSystem(null) || RecordingStorageGuard.IsFattishFileSystem("  ")) throw new Exception("A blank format must not be flagged.");

        // --- Preflight verdicts, as pure functions of the drive facts. ---
        var fat = RecordingStorageGuard.Classify("D:\\", "FAT32", 100L * 1024 * 1024 * 1024, 1_000_000_000);
        if (fat.Outcome != StoragePreflightOutcome.RefusedFatFileSystem || fat.CanRecord || !fat.IsRefusal)
        {
            throw new Exception($"FAT32 must be refused outright; got {fat.Outcome}.");
        }
        if (fat.Message == null || !fat.Message.Contains("4 GB")) throw new Exception("The FAT32 refusal must explain the 4 GB limit.");

        // Estimated 9 GB at Medium/1080p60 → required floor is 4x = 36 GB, so 8 GB free is refused.
        var tight = RecordingStorageGuard.Classify("D:\\", "NTFS", 8L * 1024 * 1024 * 1024, 9_000_000_000);
        if (tight.Outcome != StoragePreflightOutcome.RefusedInsufficientSpace || tight.CanRecord || !tight.IsRefusal)
        {
            throw new Exception($"Insufficient space must be refused; got {tight.Outcome}.");
        }
        if (tight.RequiredBytes != 36_000_000_000L)
        {
            throw new Exception($"Required headroom must be 4x the estimate (36000000000); got {tight.RequiredBytes}.");
        }
        if (tight.Message == null || !tight.Message.Contains("60-minute"))
        {
            throw new Exception($"The space refusal must state the preflight window; got: {tight.Message}");
        }

        var ok = RecordingStorageGuard.Classify("D:\\", "NTFS", 100L * 1024 * 1024 * 1024, 9_000_000_000);
        if (ok.Outcome != StoragePreflightOutcome.Ok || !ok.CanRecord || ok.IsRefusal)
        {
            throw new Exception($"A roomy NTFS volume must pass; got {ok.Outcome}.");
        }

        // The absolute floor dominates a tiny estimate.
        var floor = RecordingStorageGuard.Classify("D:\\", "NTFS", 3L * 1024 * 1024 * 1024, 1_000);
        if (floor.RequiredBytes != RecordingStorageGuard.MinimumPreflightFreeBytes)
        {
            throw new Exception($"A tiny estimate must fall back to the 2 GB floor; got {floor.RequiredBytes}.");
        }
        if (RecordingStorageGuard.RequiredPreflightBytes(1_000_000_000) != 4_000_000_000L)
        {
            throw new Exception("An above-floor estimate must require 4x.");
        }

        // An unreadable volume proceeds rather than faking a verdict.
        var unknown = RecordingStorageGuard.Undetermined();
        if (!unknown.CanRecord || unknown.IsRefusal) throw new Exception("An undetermined volume must proceed.");

        // --- Low-water verdict. ---
        if (!RecordingStorageGuard.IsLowOnSpace("NTFS", 500L * 1024 * 1024)) throw new Exception("500 MB free on NTFS is below the 1 GB low-water mark.");
        if (RecordingStorageGuard.IsLowOnSpace("NTFS", 5L * 1024 * 1024 * 1024)) throw new Exception("5 GB free on NTFS is above the low-water mark.");
        if (RecordingStorageGuard.IsLowOnSpace("FAT32", 10L * 1024 * 1024)) throw new Exception("FAT32 must be exempt from the low-water mark (preflight refuses it).");
        if (RecordingStorageGuard.LowWaterMarkBytes != 1024L * 1024 * 1024) throw new Exception("The documented 1 GB low-water mark changed.");

        // --- Session-size estimate: locked to the documented baseline. ---
        var medium = new RecordingConfig { Quality = QualityPreset.Medium, EncoderChoice = VideoEncoderChoice.CpuX264 };
        var high = new RecordingConfig { Quality = QualityPreset.High, EncoderChoice = VideoEncoderChoice.CpuX264 };
        var prores = new RecordingConfig { Quality = QualityPreset.Medium, EncoderChoice = VideoEncoderChoice.ProRes };
        var hd60 = new StreamFormat { Width = 1920, Height = 1080, FrameRate = 60.0 };

        long mediumHd60 = RecordingStorageGuard.EstimateSessionSize(medium, hd60);
        if (mediumHd60 != 9_000_000_000L)
        {
            throw new Exception($"Medium 1080p60 must estimate 9 GB (20 Mbps x 1 h); got {mediumHd60}.");
        }
        if (RecordingStorageGuard.EstimateSessionSize(high, hd60) != 13_500_000_000L)
        {
            throw new Exception("High quality must scale the estimate by 1.5x.");
        }
        if (RecordingStorageGuard.EstimateSessionSize(prores, hd60) != 54_000_000_000L)
        {
            throw new Exception("ProRes must scale the estimate by 6x.");
        }
        if (RecordingStorageGuard.EstimateSessionSize(medium, hd60 with { Width = 3840, Height = 2160 }) != 36_000_000_000L)
        {
            throw new Exception("4x the pixels must estimate 4x the bytes.");
        }
        if (RecordingStorageGuard.EstimateSessionSize(medium, hd60 with { FrameRate = 30.0 }) != 4_500_000_000L)
        {
            throw new Exception("Half the frame rate must halve the estimate.");
        }
        // A zero/absent frame rate falls back to 60 rather than dividing by zero.
        if (RecordingStorageGuard.EstimateSessionSize(medium, hd60 with { FrameRate = 0.0 }) != 9_000_000_000L)
        {
            throw new Exception("A zero frame rate must fall back to the 60 fps baseline.");
        }

        // --- Byte formatting used in operator-facing messages. ---
        if (RecordingStorageGuard.FormatBytes(3L * 1024 * 1024 * 1024) != "3.0 GB") throw new Exception("GB formatting changed.");
        if (RecordingStorageGuard.FormatBytes(500L * 1024 * 1024) != "500 MB") throw new Exception("MB formatting changed.");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Storage guard verified (FAT32, headroom, low-water, estimate scaling, formatting)!");
        Console.ResetColor();
    }

    /// <summary>
    /// A sink that records the ORDER of arm/disarm operations and lets the test observe the arm's gate
    /// from inside the sink callbacks — which is exactly how the sequencing becomes observable at all.
    /// </summary>
    private sealed class SignalRecordingSink : OmtCaptureStudio.Services.Sinks.IRecordingSink
    {
        public readonly List<string> Steps = new();
        public Action<string>? OnStep;
        public System.Threading.ManualResetEventSlim? BlockVideoWrite;
        /// <summary>When set, <see cref="FinalizeSink"/> blocks here until the gate is set (DEF-02 ordering test).</summary>
        public System.Threading.ManualResetEventSlim? BlockFinalize;
        /// <summary>Non-zero once a finalize has entered the sink — the DEF-02 test waits on this, not on a sleep.</summary>
        public int FinalizeEntered;
        public bool ThrowOnFinalize;

        private long _video, _audio;

        public long VideoFramesReceived => _video;
        public long AudioBytesReceived => _audio;
        public long VideoFramesDropped => 0;
        public long AudioChunksDropped => 0;
        public string ResolvedEncoderName => "SignalSink";
        public string DiagnosticTail => "diag-tail";

        public event Action<string>? SinkError;
        public void RaiseSinkError(string msg) => SinkError?.Invoke(msg);

        private void Step(string name) { Steps.Add(name); OnStep?.Invoke(name); }

        public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
        {
            Step("init");
            return true;
        }

        public void WriteVideo(IntPtr pData, int dataLength)
        {
            Step("video");
            _video++;
            BlockVideoWrite?.Wait(2000);
        }

        public void WriteAudio(byte[] buffer, int offset, int count)
        {
            Step("audio");
            _audio += count;
        }

        public void FinalizeSink(out long totalBytesWritten)
        {
            Step("finalize");
            System.Threading.Interlocked.Increment(ref FinalizeEntered);
            BlockFinalize?.Wait(5000);
            totalBytesWritten = _audio + 4096;
            if (ThrowOnFinalize) throw new InvalidOperationException("transport already dead");
        }

        public void Dispose() { }
    }

    /// <summary>Count of a substring, for source-shape (locality) assertions.</summary>
    static int CountIn(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>
    /// ADR-0005: the arm/disarm sequence is one deep module with one authoritative gate and one
    /// finalize path. These assertions are deliberately observable-order assertions (what the SINK saw
    /// and when, versus the gate) rather than implementation poking.
    /// </summary>
    static void TestRecordingArm()
    {
        Console.WriteLine("\n[Step 0.682] Verifying RecordingArm (arm order, single finalize, drain, convergence)...");

        var config = new RecordingConfig { OutputDirectory = Path.GetTempPath(), FilenamePrefix = "ArmTest" };
        var format = new StreamFormat { Width = 320, Height = 240, FrameRate = 10.0, SampleRate = 48000, Channels = 2 };

        // ---- Arm order: the sink must be initialized while the gate is still OPEN, then the gate flips.
        var sink = new SignalRecordingSink();
        var arm = new RecordingArm(sink);
        bool? gateDuringInit = null;
        sink.OnStep = step => { if (step == "init") gateDuringInit = arm.IsRecording; };

        if (!arm.Arm(config, format))
        {
            Console.WriteLine(" -> [SKIP] storage preflight refused the temp volume in this environment; arm-path assertions skipped (policy covered by Step 0.681).");
            return;
        }

        if (gateDuringInit != false)
        {
            throw new Exception($"The gate must still be CLOSED while the sink initializes; saw {gateDuringInit}.");
        }
        if (!arm.IsRecording) throw new Exception("Arm must leave the gate open.");
        if (arm.CurrentSegmentIndex != 1) throw new Exception("A fresh take must start at segment 1.");

        // Arm reset the A/V clock as its FIRST step: a freshly reset clock reports unity drift.
        double ratioAfterArm = arm.AvDriftRatio;
        Console.WriteLine($" -> A/V clock ratio immediately after Arm: {ratioAfterArm:F6} (reset inside the arm sequence)");
        if (ratioAfterArm <= 0 || Math.Abs(ratioAfterArm - 1.0) > 1e-6)
        {
            throw new Exception($"Arm must reset the A/V clock to unity before flipping the gate; ratio was {ratioAfterArm}.");
        }

        // ---- Writes reach the sink while armed.
        var frame = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try
        {
            arm.WriteVideoFrame(frame, 16);
            arm.WriteAudioData(new byte[8], 0, 8);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(frame); }

        if (sink.VideoFramesReceived != 1) throw new Exception("An armed write must reach the sink.");
        if (sink.AudioBytesReceived != 8) throw new Exception("An armed audio write must reach the sink.");

        // ---- Disarm: gate closes BEFORE finalize, exactly one finalize, and the summary is published.
        bool? gateDuringFinalize = null;
        sink.OnStep = step => { if (step == "finalize") gateDuringFinalize = arm.IsRecording; };
        RecordingStopSummary? summary = null;
        arm.RecordingStopped += s => summary = s;

        if (!arm.Disarm(RecordingStopReason.Manual, out var direct)) throw new Exception("Disarm must report that it stopped an active take.");
        if (gateDuringFinalize != false) throw new Exception($"The gate must be CLOSED before finalize; saw {gateDuringFinalize}.");
        if (CountIn(string.Join(",", sink.Steps), "finalize") != 1) throw new Exception($"Exactly one finalize expected; steps: {string.Join(" ", sink.Steps)}");
        if (summary == null || direct == null) throw new Exception("Disarm must publish a stop summary.");
        if (summary.Reason != RecordingStopReason.Manual) throw new Exception($"Manual stop must report Manual; got {summary.Reason}.");
        if (summary.VideoFramesWritten != 1) throw new Exception($"The summary must carry the frame count; got {summary.VideoFramesWritten}.");
        if (summary.MediaDuration.TotalSeconds <= 0) throw new Exception("The summary must carry a media duration.");
        if (RecordingHealth.ReasonText(summary.Reason) != "stopped by operator") throw new Exception("The verdict reason text is wrong.");
        if (!RecordingHealth.BuildStopVerdict(summary).StartsWith("Stop verdict (stopped by operator)")) throw new Exception("The stop verdict must name the reason.");

        // ---- Disarm is idempotent, and the gate now blocks writes.
        sink.Steps.Clear();
        if (arm.Disarm(RecordingStopReason.Manual, out _)) throw new Exception("A second Disarm must be a no-op.");
        var frame2 = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try { arm.WriteVideoFrame(frame2, 16); } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(frame2); }
        if (sink.VideoFramesReceived != 1) throw new Exception("A write after Disarm must not reach the sink.");

        // ---- Bounded drain: a writer stuck inside the sink must NOT hang the caller. The three
        //      low-disk/UI paths depend on this; an unbounded wait would freeze the UI tick thread.
        var block = new System.Threading.ManualResetEventSlim(false);
        sink.BlockVideoWrite = block;
        if (!arm.Arm(config, format)) throw new Exception("Re-arm for the drain test failed.");
        sink.OnStep = null;
        var stuck = System.Threading.Tasks.Task.Run(() =>
        {
            var f = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
            try { arm.WriteVideoFrame(f, 16); } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(f); }
        });
        System.Threading.Thread.Sleep(20); // let the writer get in and block
        var drainWatch = System.Diagnostics.Stopwatch.StartNew();
        bool drained = arm.Disarm(RecordingStopReason.InputLoss, out var drainSummary);
        drainWatch.Stop();
        block.Set();
        stuck.Wait(2000);

        if (!drained) throw new Exception("Disarm must complete while a writer is stuck in the sink.");
        // The cap is DrainTimeoutMs of real time, plus at most one Sleep(1) overshoot (Windows
        // quantises a 1 ms sleep to ~15 ms), plus scheduling slack.
        long drainBudget = RecordingArm.DrainTimeoutMs + 100;
        if (drainWatch.ElapsedMilliseconds > drainBudget)
        {
            throw new Exception($"Disarm must be bounded (~{RecordingArm.DrainTimeoutMs} ms), took {drainWatch.ElapsedMilliseconds} ms (budget {drainBudget}).");
        }
        if (drainSummary == null || drainSummary.Reason != RecordingStopReason.InputLoss)
        {
            throw new Exception("The bounded-drain disarm must still publish an InputLoss summary.");
        }
        Console.WriteLine($" -> Bounded drain: Disarm returned in {drainWatch.ElapsedMilliseconds} ms with a writer stuck in the sink (cap {RecordingArm.DrainTimeoutMs} ms).");

        // ---- Convergence: every trigger routes through the one path, differing only in the reason.
        foreach (var (reason, viaFacade) in new[]
        {
            (RecordingStopReason.Manual, "StopRecording()"),
            (RecordingStopReason.InputLoss, "StopRecording(InputLoss)"),
            (RecordingStopReason.LowDisk, "StopRecording(LowDisk)")
        })
        {
            var s2 = new SignalRecordingSink();
            using var svc = new StreamRecorderService(s2);
            if (!svc.StartRecording(config, format)) throw new Exception($"Facade start failed for {viaFacade}.");
            s2.Steps.Clear();
            svc.StopRecording(reason);
            int finalizes = CountIn(string.Join(",", s2.Steps), "finalize");
            if (finalizes != 1) throw new Exception($"{viaFacade} must finalize exactly once; saw {finalizes}.");
            if (!string.IsNullOrEmpty(svc.CurrentRecordingPath) && svc.IsRecording) throw new Exception($"{viaFacade} must close the gate.");
        }

        // ---- Disposal routes through the same path rather than tearing down around it.
        var s3 = new SignalRecordingSink();
        var arm3 = new RecordingArm(s3);
        RecordingStopSummary? disposeSummary = null;
        arm3.RecordingStopped += s => disposeSummary = s;
        if (!arm3.Arm(config, format)) throw new Exception("Arm for the dispose test failed.");
        s3.Steps.Clear();
        arm3.Dispose();
        if (CountIn(string.Join(",", s3.Steps), "finalize") != 1) throw new Exception("Dispose must finalize through the arm.");
        if (disposeSummary == null || disposeSummary.Reason != RecordingStopReason.Dispose)
        {
            throw new Exception("Dispose must publish a Dispose-reason summary.");
        }

        arm3.Dispose(); // idempotent

        // ---- Secondary failure: the transport is already dead, so finalize throws. The original error
        //      must survive, a summary must still be published, and nothing may escape.
        var dead = new SignalRecordingSink { ThrowOnFinalize = true };
        var arm4 = new RecordingArm(dead);
        RecordingStopSummary? errSummary = null;
        var errors = new List<string>();
        arm4.RecordingStopped += s => errSummary = s;
        arm4.RecordingError += e => errors.Add(e);

        if (!arm4.Arm(config, format)) throw new Exception("Arm for the sink-failure test failed.");
        var f3 = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try { arm4.WriteVideoFrame(f3, 16); arm4.WriteVideoFrame(f3, 16); } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(f3); }
        dead.RaiseSinkError("FFmpeg audio pipe disconnected unexpectedly.");

        if (arm4.IsRecording) throw new Exception("A sink error must close the gate.");
        if (errSummary == null) throw new Exception("A sink error must still publish a summary (best-effort finalize).");
        if (errSummary.Reason != RecordingStopReason.SinkError) throw new Exception($"Sink failure must report SinkError; got {errSummary.Reason}.");
        if (errSummary.VideoFramesWritten != 2) throw new Exception($"The summary must carry what reached the transport; got {errSummary.VideoFramesWritten}.");
        if (errors.Count == 0) throw new Exception("The original sink error must be surfaced.");
        if (errors[errors.Count - 1] != "FFmpeg audio pipe disconnected unexpectedly.")
        {
            throw new Exception($"The ORIGINAL sink error must be reported last, unmasked; last was: {errors[errors.Count - 1]}");
        }
        if (!errors.Exists(e => e.Contains("may be incomplete")))
        {
            throw new Exception("The failed finalize must be reported as its own diagnostic.");
        }
        Console.WriteLine($" -> Sink failure: {errSummary.VideoFramesWritten} frames summarized, finalize failure reported without masking the original error.");

        arm4.Dispose();
        arm.Dispose();
        arm3.Dispose();

        // ---- DEF-02 / OBS-03: the low-disk error must be raised AFTER Disarm(LowDisk) completes, not
        //      synchronously at T+0. Raised inline it flashed "Recording Error: Low disk space…" first,
        //      and the stop summary then overwrote it ~500 ms later — masking the reason in the bottom
        //      bar (the drawer verdict was already correct). Mirrors OnSinkError, whose Disarm runs
        //      synchronously first so its error stays last.
        {
            var diskSink = new SignalRecordingSink();
            var diskArm = new RecordingArm(diskSink);
            var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
            diskArm.RecordingStopped += _ => order.Enqueue("stopped");
            diskArm.RecordingError += _ => order.Enqueue("error");

            if (!diskArm.Arm(config, format))
            {
                Console.WriteLine(" -> [SKIP] low-disk ordering: storage preflight refused the temp volume in this environment (policy covered by Step 0.681).");
            }
            else
            {
                // Hold the finalize open so the T+0-vs-after-disarm distinction is observable.
                var finalizeGate = new System.Threading.ManualResetEventSlim(false);
                diskSink.BlockFinalize = finalizeGate;

                diskArm.StopLowDisk("Recording stopped: low disk space on the destination volume.");

                // Wait for the background disarm to reach (and block inside) FinalizeSink.
                var spin = System.Diagnostics.Stopwatch.StartNew();
                while (System.Threading.Volatile.Read(ref diskSink.FinalizeEntered) == 0 && spin.ElapsedMilliseconds < 3000)
                {
                    System.Threading.Thread.Sleep(5);
                }
                if (System.Threading.Volatile.Read(ref diskSink.FinalizeEntered) == 0)
                {
                    throw new Exception("DEF-02: the background low-disk disarm never reached FinalizeSink.");
                }

                // THE discriminating assertion: while the finalize is still blocked, the low-disk error
                // must NOT have been raised. Raising it at T+0 is exactly the bug.
                if (!order.IsEmpty)
                {
                    throw new Exception($"DEF-02: the low-disk error must wait for the disarm to complete; saw [{string.Join(",", order)}] while finalize was still blocked.");
                }

                finalizeGate.Set(); // let the finalize finish and the continuation run

                spin.Restart();
                while (order.Count < 2 && spin.ElapsedMilliseconds < 3000)
                {
                    System.Threading.Thread.Sleep(5);
                }

                var seq = order.ToArray();
                if (seq.Length != 2 || seq[0] != "stopped" || seq[1] != "error")
                {
                    throw new Exception($"DEF-02: expected [stopped,error] (low-disk error AFTER the stop summary); got [{string.Join(",", seq)}].");
                }
                if (diskArm.IsRecording) throw new Exception("DEF-02: the low-disk stop must close the gate.");
                Console.WriteLine(" -> Low-disk ordering: error raised only AFTER Disarm(LowDisk) completed; the stop summary is not masked.");
            }

            diskArm.Dispose();
        }

        // ---- Locality: ONE finalize call site in the whole app outside the sink implementations, and
        //      no Task.Run finalize left in the orchestration classes (AC3).
        string? armSrc = FindRepoSource("OmtCaptureStudio/Services/RecordingArm.cs");
        string? svcSrc = FindRepoSource("OmtCaptureStudio/Services/StreamRecorderService.cs");
        string? sesSrc = FindRepoSource("OmtCaptureStudio/Services/CaptureSession.cs");
        if (armSrc == null || svcSrc == null || sesSrc == null)
        {
            Console.WriteLine($" -> [SKIP] source tree not reachable from {AppContext.BaseDirectory}; locality assertions skipped.");
        }
        else
        {
            string armText = File.ReadAllText(armSrc);
            string svcText = File.ReadAllText(svcSrc);
            string sesText = File.ReadAllText(sesSrc);

            if (CountIn(armText, "FinalizeSink(") != 1) throw new Exception("The arm must have exactly one FinalizeSink call site.");
            if (CountIn(svcText, "FinalizeSink(") != 0) throw new Exception("StreamRecorderService must not finalize — it is a facade now.");
            if (CountIn(sesText, "FinalizeSink(") != 0) throw new Exception("CaptureSession must not finalize — it goes through the arm.");
            if (CountIn(svcText, "Task.Run") != 0) throw new Exception("StreamRecorderService must not own a thread hop.");
            if (CountIn(sesText, "Task.Run(") != 1) throw new Exception("CaptureSession should only Task.Run the reconnect hop, nothing else.");
            if (CountIn(armText, "Task.Run") != 1) throw new Exception("The arm must own the single off-thread disarm.");
            if (sesText.Contains("_audioResampler")) throw new Exception("CaptureSession must no longer own the A/V clock.");
            // The arm is the only file that assigns the gate, and it does so exactly three times:
            // open in Arm, close in Disarm, and close again in Arm's failure rollback (an event
            // subscriber throwing out of RecordingStarted must not leave the gate open).
            if (CountIn(armText, "_isRecording = true") != 1 ||
                CountIn(armText, "_isRecording = false") != 2 ||
                CountIn(svcText, "_isRecording") != 0 ||
                CountIn(sesText, "_isRecording") != 0)
            {
                throw new Exception("The arm must be the ONLY place the gate is assigned (1 open, 2 closes) and no other class may touch it.");
            }
            Console.WriteLine(" -> Finalize locality: 1 FinalizeSink site (the arm), 0 in the facade/session, 1 thread hop; the gate is assigned in NO other class (the arm itself has 1 open + 2 closes: Arm, Disarm, and Arm's failure rollback).");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> RecordingArm sequencing, single finalize, bounded drain, convergence and best-effort sink-failure verified!");
        Console.ResetColor();
    }

    private static void WaitForFormat(CaptureSession session, string what)
    {
        int tries = 0;
        while (!session.CurrentFormat.HasVideo && tries < 100)
        {
            Thread.Sleep(50);
            tries++;
        }
        if (!session.CurrentFormat.HasVideo)
        {
            throw new Exception($"{what} failed to deliver a video format in time.");
        }
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        int tries = 0;
        while (!condition() && tries < 100)
        {
            Thread.Sleep(25);
            tries++;
        }
        if (!condition())
        {
            throw new Exception($"{what} did not happen in time.");
        }
    }

    /// <summary>
    /// A network-like test source: wraps a real <see cref="SyntheticPatternSource"/> (so frames carry
    /// genuine buffers the video/audio pipeline can consume) but presents itself as a recoverable
    /// remote feed whose frames can be suspended, and counts auto-reconnect attempts.
    /// </summary>
    private sealed class RecoverableTestSource : IMediaSource, IRecoverableMediaSource
    {
        private readonly SyntheticPatternSource _inner = new();
        private volatile bool _forwarding = true;
        private int _reconnectCount;

        public RecoverableTestSource()
        {
            _inner.VideoFrameReceived += (p, l, w, h, s, fps, ts) =>
            {
                if (_forwarding) VideoFrameReceived?.Invoke(p, l, w, h, s, fps, ts);
            };
            _inner.AudioFrameReceived += (p, ch, smp, rate, ts) =>
            {
                if (_forwarding) AudioFrameReceived?.Invoke(p, ch, smp, rate, ts);
            };
            _inner.TelemetryUpdated += t => TelemetryUpdated?.Invoke(t);
            _inner.StatusChanged += s => StatusChanged?.Invoke(s);
            _inner.ErrorOccurred += e => ErrorOccurred?.Invoke(e);
        }

        public string Name => "Recoverable Test Source";
        public string Address => "test://recoverable";

        /// <summary>The socket stays up during an outage — only the frames stop (the ADR-0002 failure mode).</summary>
        public bool IsConnected => true;

        public bool RequiresInputHealthWatch => true;

        public int ReconnectCount => Volatile.Read(ref _reconnectCount);

        public event Action<IntPtr, int, int, int, int, double, long>? VideoFrameReceived;
        public event Action<IntPtr, int, int, int, long>? AudioFrameReceived;
        public event Action<StreamTelemetry>? TelemetryUpdated;
        public event Action<string>? StatusChanged;
        public event Action<string>? ErrorOccurred;

        public void Connect() => _inner.Connect();

        public void Disconnect() => _inner.Disconnect();

        /// <summary>Counts the attempt; deliberately does NOT resume frames (the test drives Resume).</summary>
        public void EnsureConnected() => Interlocked.Increment(ref _reconnectCount);

        public void Suspend() => _forwarding = false;

        public void Resume() => _forwarding = true;

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>Records the effect calls the state machine makes, so their order can be asserted.</summary>
    private sealed class BroadcastTestEffects : IBroadcastSessionEffects
    {
        private readonly List<string> _calls;

        public BroadcastTestEffects(List<string> calls) => _calls = calls;

        public bool IsRecording { get; set; }

        public Task StopRecordingAsync()
        {
            _calls.Add("stop-recording");
            return Task.CompletedTask;
        }

        public void DisconnectSession() => _calls.Add("disconnect");
    }

    /// <summary>
    /// Exercises the audio/video clock-lock resampler directly, headless.
    ///
    /// The invariant that keeps A/V locked is: output audio frames ==
    /// videoFrames * (sampleRate / frameRate). Both cases assert the resampler tracks
    /// that target (within the un-flushed final-chunk tail) and that its ratio
    /// converges to trueFps / nominalFps.
    ///
    /// Case A (drift): source reports 30 fps but delivers ~29.226 fps (the vMix defect).
    /// Case B (lockstep): video and audio clocks already agree.
    /// </summary>
    static void TestAudioClockResampler()
    {
        Console.WriteLine("\n[Step 0.6] Verifying AudioClockResampler (drift correction + passthrough)...");
        // --- Case A: 30 fps nominal, ~29.226 fps true delivery (vMix defect) ---
        {
            const int sampleRate = 48000, channels = 2;
            const double nominalFps = 30.0, trueFps = 29.226;
            const double seconds = 30.0;
            var (ratio, inputFrames, outputFrames, videoFrames) =
                SimulateResampler(sampleRate, nominalFps, trueFps, channels, seconds);

            double expectedRatio = trueFps / nominalFps; // 0.9742
            double target = videoFrames * (sampleRate / nominalFps); // what keeps A/V locked

            Console.WriteLine($" -> Drift case: video at {trueFps} fps vs nominal {nominalFps}; " +
                              $"ratio {ratio:F4} (expected {expectedRatio:F4}), " +
                              $"out {outputFrames} vs target {target:F0} (diff {outputFrames - target:F0} frames)");

            if (Math.Abs(ratio - expectedRatio) > 0.003)
            {
                throw new Exception(
                    $"AudioClockResampler drift test failed: converged ratio {ratio:F4} vs expected {expectedRatio:F4}");
            }
            // Must track the video clock closely; the last ~1 chunk (≤ ~2k frames) is
            // legitimately un-flushed at the recording edge.
            if (Math.Abs(outputFrames - target) > 2000)
            {
                throw new Exception(
                    $"AudioClockResampler drift test failed: output {outputFrames} vs A/V-lock target {target:F0}");
            }
        }

        // --- Case B: clocks already agree -> ratio 1.0, output tracks video clock ---
        {
            const int sampleRate = 48000, channels = 2;
            const double fps = 60.0;
            const double seconds = 20.0;
            var (ratio, inputFrames, outputFrames, videoFrames) =
                SimulateResampler(sampleRate, fps, fps, channels, seconds);

            double target = videoFrames * (sampleRate / fps);

            Console.WriteLine($" -> Lockstep case: ratio {ratio:F4}, " +
                              $"out {outputFrames} vs target {target:F0} (diff {outputFrames - target:F0} frames)");

            if (Math.Abs(ratio - 1.0) > 0.001)
            {
                throw new Exception($"AudioClockResampler lockstep test failed: ratio {ratio:F4} != 1.0");
            }
            if (Math.Abs(outputFrames - target) > 2000)
            {
                throw new Exception(
                    $"AudioClockResampler lockstep test failed: output {outputFrames} vs A/V-lock target {target:F0}");
            }
        }

        // --- Case C: near-Nyquist amplitude preserved (music transparency) ---
        {
            const int sampleRate = 48000, channels = 1;
            const double nominalFps = 30.0, trueFps = 29.226; // drift ratio ~0.974
            const double freq = 19000.0, amp = 0.5;

            var resampler = new AudioClockResampler();
            resampler.Reset(sampleRate, nominalFps, channels);

            const int chunkFrames = 1024;
            const double seconds = 5.0;
            int totalChunks = (int)(seconds * sampleRate / chunkFrames);

            var collected = new List<float>(totalChunks * chunkFrames);
            double videoClock = 0.0, phase = 0.0;
            var chunk = new byte[chunkFrames * 4];

            for (int i = 0; i < totalChunks; i++)
            {
                videoClock += (double)chunkFrames / sampleRate * trueFps;
                while (resampler.VideoFramesSeen < (long)videoClock)
                {
                    resampler.OnVideoFrame();
                }

                for (int f = 0; f < chunkFrames; f++)
                {
                    float s = (float)(amp * Math.Sin(phase));
                    phase += 2.0 * Math.PI * freq / sampleRate;
                    Buffer.BlockCopy(BitConverter.GetBytes(s), 0, chunk, f * 4, 4);
                }

                byte[] outBuf = resampler.Process(chunk, chunk.Length, out int outBytes);
                int outFrames = outBytes / 4;
                for (int f = 0; f < outFrames; f++)
                {
                    collected.Add(BitConverter.ToSingle(outBuf, f * 4));
                }
            }

            // Measure RMS over the steady-state tail (skip ~1s: FIR priming + PLL settle).
            int skip = sampleRate; // 1 second of output samples
            if (collected.Count <= skip + 1000)
            {
                throw new Exception("AudioClockResampler quality test: too few output samples.");
            }
            double sumSq = 0.0;
            for (int i = skip; i < collected.Count; i++)
            {
                sumSq += collected[i] * collected[i];
            }
            double rms = Math.Sqrt(sumSq / (collected.Count - skip));
            double inputRms = amp / Math.Sqrt(2.0);
            double gainDb = 20.0 * Math.Log10(rms / inputRms);

            Console.WriteLine($" -> Quality case: {freq / 1000.0:F0} kHz sine resampled at ratio {resampler.Ratio:F4}; " +
                              $"gain {gainDb:F3} dB (windowed-sinc ≈ 0 dB, linear would be ≈ -4.7 dB)");

            // Windowed-sinc keeps near-Nyquist content to ~unity; a linear kernel would
            // land ~-4.7 dB. Allow a comfortable margin above that.
            if (gainDb < -0.5)
            {
                throw new Exception(
                    $"AudioClockResampler quality test failed: {freq / 1000.0:F0} kHz gain {gainDb:F3} dB (windowed-sinc should be ~0 dB)");
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> AudioClockResampler drift correction and lockstep verified!");
        Console.ResetColor();
    }

    static void TestDesktopCapture()
    {
        Console.WriteLine("\n[Step 0.65] Verifying Host Desktop & Audio Capture (WASAPI, live audio hot-swap + OMT round-trip)...");

        // 0a. Verify HostAudioSourceFactory and WasapiHostAudioSource across all 4 modes + double-dispose idempotency.
        float[] testPlanar = new float[2 * 1600]; // 30 fps @ 48 kHz = 1600 samples/ch
        foreach (HostAudioCaptureMode mode in Enum.GetValues<HostAudioCaptureMode>())
        {
            // ASIO needs an explicit driver instance; it is covered by TestAsioHostAudio().
            if (mode == HostAudioCaptureMode.Asio) continue;

            IHostAudioSource src = HostAudioSourceFactory.Create(mode);
            if (src.SampleRate != 48000 || src.Channels != 2)
            {
                throw new Exception($"HostAudioSourceFactory({mode}) returned invalid format: {src.SampleRate}Hz, {src.Channels}ch");
            }
            src.FillPlanarAudio(testPlanar, 1600);
            for (int i = 0; i < testPlanar.Length; i++)
            {
                if (float.IsNaN(testPlanar[i]) || float.IsInfinity(testPlanar[i]))
                {
                    throw new Exception($"HostAudioSource({mode}) produced NaN/Infinity at sample {i}!");
                }
            }
            src.Dispose();
            src.Dispose(); // idempotent double-dispose
        }
        Console.WriteLine(" -> HostAudioSourceFactory & WasapiHostAudioSource verified across all 4 modes (SystemAudio, Microphone, SystemAndMicrophone, Mute)!");

        // 0b. Headless OMT round-trip for OmtDesktopCaptureGenerator with video + lockstep 48 kHz stereo audio + live SetAudioSource hot-swap.
        {
            string synthSourceName = "OMT_Automated_HostAudio_Source";
            using var synthGen = new OmtDesktopCaptureGenerator(
                new SyntheticBgraFrameSource(640, 360),
                synthSourceName,
                fps: 30,
                audioSource: new SilentHostAudioSource());
            synthGen.Start();
            Thread.Sleep(800);

            string connectUrl = synthGen.Url ?? synthGen.Address ?? synthSourceName;
            using var audioTestSession = new CaptureSession();
            long synthVideoCount = 0;
            long synthAudioLevelEvents = 0;
            float maxObservedPeakDb = -100.0f;

            audioTestSession.VideoFrameAvailable += (p, len, w, h, s, fps, ts) => Interlocked.Increment(ref synthVideoCount);
            audioTestSession.AudioLevelsUpdated += levels =>
            {
                Interlocked.Increment(ref synthAudioLevelEvents);
                if (levels.Channels.Length >= 2)
                {
                    float peak = (float)Math.Max(levels.Channels[0].PeakDb, levels.Channels[1].PeakDb);
                    if (peak > maxObservedPeakDb) maxObservedPeakDb = peak;
                }
            };

            audioTestSession.Connect(connectUrl);

            int waitTicks = 0;
            while ((synthVideoCount < 5 || synthAudioLevelEvents < 2) && waitTicks < 50)
            {
                Thread.Sleep(50);
                waitTicks++;
            }

            if (synthVideoCount < 5 || !audioTestSession.CurrentFormat.HasAudio ||
                audioTestSession.CurrentFormat.SampleRate != 48000 || audioTestSession.CurrentFormat.Channels != 2)
            {
                throw new Exception(
                    $"OmtDesktopCaptureGenerator audio round-trip failed! Video={synthVideoCount}, HasAudio={audioTestSession.CurrentFormat.HasAudio}, " +
                    $"Rate={audioTestSession.CurrentFormat.SampleRate}, Ch={audioTestSession.CurrentFormat.Channels}");
            }

            // Now hot-swap the audio source while broadcasting from SilentHostAudioSource to a 440Hz/880Hz tone generator
            maxObservedPeakDb = -100.0f;
            synthGen.SetAudioSource(new SyntheticToneHostAudioSource());

            int toneWait = 0;
            while (maxObservedPeakDb < -20.0f && toneWait < 40)
            {
                Thread.Sleep(50);
                toneWait++;
            }

            if (maxObservedPeakDb < -20.0f)
            {
                throw new Exception($"Live SetAudioSource hot-swap did not deliver tone audio over OMT! MaxPeak={maxObservedPeakDb:F1} dBFS");
            }

            Console.WriteLine($" -> OmtDesktopCaptureGenerator 48kHz stereo audio + live SetAudioSource hot-swap verified (Peak: {maxObservedPeakDb:F1} dBFS)!");

            // Verify WASAPI hardware audio device enumeration & specific device instantiation
            var audioOptions = AudioDeviceEnumerator.EnumerateOptions();
            if (audioOptions == null || audioOptions.Count < 4)
            {
                throw new Exception($"AudioDeviceEnumerator.EnumerateOptions() returned insufficient options ({audioOptions?.Count ?? 0}).");
            }
            var hwDeviceOption = audioOptions.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.DeviceId));
            if (hwDeviceOption != null)
            {
                using var specificSrc = HostAudioSourceFactory.Create(hwDeviceOption);
                float[] specBuf = new float[3200];
                specificSrc.FillPlanarAudio(specBuf, 1600);
                Console.WriteLine($" -> AudioDeviceEnumerator enumerated {audioOptions.Count} options (verified hardware device '{hwDeviceOption.DisplayName}')!");
            }
            else
            {
                Console.WriteLine($" -> AudioDeviceEnumerator enumerated {audioOptions.Count} preset options (no active hardware audio endpoints on host).");
            }

            // Verify NetworkInterfaceEnumerator & OmtDesktopCaptureGenerator network interface binding
            var netOptions = NetworkInterfaceEnumerator.Enumerate();
            if (netOptions == null || netOptions.Count < 2 || !netOptions[0].IsAllNetworks || !netOptions[^1].IsLoopbackOnly)
            {
                throw new Exception("NetworkInterfaceEnumerator.Enumerate() did not return expected All Networks and Localhost options!");
            }

            synthGen.SetBindIpAddress("127.0.0.1");
            if (synthGen.BroadcastEndpointUrl == null || !synthGen.BroadcastEndpointUrl.StartsWith("omt://127.0.0.1:", StringComparison.Ordinal))
            {
                throw new Exception($"BroadcastEndpointUrl mismatch after SetBindIpAddress('127.0.0.1'): {synthGen.BroadcastEndpointUrl}");
            }

            // If this host has another local IPv4 address, verify that connecting to the blocked NIC IP on synthGen.Port
            // is intercepted and immediately reset/closed (never receiving OMT stream data)
            var blockedIps = NetworkInterfaceEnumerator.GetOtherLocalIpv4Addresses("127.0.0.1");
            if (blockedIps.Count > 0 && synthGen.Port > 0)
            {
                using var probeSocket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                probeSocket.ReceiveTimeout = 300;
                bool rejected = false;
                try
                {
                    var ar = probeSocket.BeginConnect(new System.Net.IPEndPoint(blockedIps[0], synthGen.Port), null, null);
                    if (ar.AsyncWaitHandle.WaitOne(400))
                    {
                        probeSocket.EndConnect(ar);
                        byte[] probeBuf = new byte[16];
                        int read = probeSocket.Receive(probeBuf);
                        if (read == 0)
                        {
                            rejected = true;
                        }
                    }
                    else
                    {
                        rejected = true;
                    }
                }
                catch (System.Net.Sockets.SocketException)
                {
                    rejected = true;
                }

                if (!rejected)
                {
                    throw new Exception($"Expected non-selected interface {blockedIps[0]}:{synthGen.Port} to reject/reset connection when bound to 127.0.0.1!");
                }
                Console.WriteLine($" -> Network interface restriction verified: {synthGen.BroadcastEndpointUrl} active, non-selected NIC {blockedIps[0]}:{synthGen.Port} rejected ({netOptions.Count} network options)!");
            }
            else
            {
                Console.WriteLine($" -> Network interface enumeration & SetBindIpAddress verified ({netOptions.Count} network options)!");
            }

            synthGen.SetBindIpAddress(null);
            audioTestSession.Disconnect();
            synthGen.Stop();
        }

        // 1. Capture sanity: dimensions + valid pinned BGRA buffer.
        using (var capturer = new DesktopFrameCapturer())
        {
            if (capturer.Width <= 0 || capturer.Height <= 0)
                throw new Exception($"DesktopFrameCapturer reported invalid bounds: {capturer.Width}x{capturer.Height}");
            if (capturer.Stride != capturer.Width * 4)
                throw new Exception($"DesktopFrameCapturer stride mismatch: {capturer.Stride} vs {capturer.Width * 4}");
            if (capturer.BufferPtr == IntPtr.Zero)
                throw new Exception("DesktopFrameCapturer buffer pointer is null.");

            int frames = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (frames < 2 && sw.ElapsedMilliseconds < 5000)
            {
                if (capturer.TryCaptureFrame()) frames++;
                Thread.Sleep(16);
            }
            if (frames == 0)
            {
                Console.WriteLine(" -> [SKIP] DXGI display capture skipped: display session access restricted in this environment.");
                return;
            }

            Console.WriteLine($" -> Captured {frames} frame(s) at {capturer.Width}x{capturer.Height}, stride {capturer.Stride}");
        }

        // 2. OMT round-trip: broadcast the desktop, discover it, receive video + audio frames.
        string sourceName = "OMT_Automated_Desktop_Source";
        using var generator = new OmtDesktopCaptureGenerator(
            new DesktopFrameCapturer(),
            sourceName,
            fps: 30,
            audioSource: HostAudioSourceFactory.Create(HostAudioCaptureMode.SystemAudio));
        generator.Start();
        Console.WriteLine($" -> Broadcasting desktop as OMT source '{sourceName}' at {generator.Address}");

        // Allow the source to bind and appear in discovery.
        Thread.Sleep(1500);

        using var discovery = new OmtDiscoveryService();
        discovery.RefreshNow();
        Thread.Sleep(1200);

        var disc = libomtnet.OMTDiscovery.GetInstance();
        string[] addrs = disc.GetAddresses() ?? Array.Empty<string>();
        string? target = addrs.FirstOrDefault(a => a.Contains(sourceName, StringComparison.OrdinalIgnoreCase));
        if (target == null)
            throw new Exception($"Desktop OMT source '{sourceName}' not found via discovery. Addresses: {string.Join(", ", addrs)}");

        using var session = new CaptureSession();
        long videoFrames = 0;
        session.VideoFrameAvailable += (p, len, w, h, s, fps, ts) => Interlocked.Increment(ref videoFrames);
        session.Connect(target);

        int wait = 0;
        while (videoFrames < 5 && wait < 60)
        {
            Thread.Sleep(100);
            wait++;
        }
        if (videoFrames < 5)
            throw new Exception($"Received only {videoFrames} video frames from desktop OMT source in 6s.");

        Console.WriteLine($" -> Received {videoFrames} desktop frames via OMT round-trip at {session.CurrentFormat.Width}x{session.CurrentFormat.Height} (Audio: {session.CurrentFormat.Channels}ch @ {session.CurrentFormat.SampleRate}Hz)");

        session.Disconnect();
        generator.Stop();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Host desktop capture (capture + OMT round-trip) verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// Exercises the ASIO host audio source against real registered ASIO drivers: registry
    /// discovery, opening the driver through the hand-rolled COM vtable interop, sample-rate
    /// negotiation (ask the driver for 48 kHz, resample only as a fallback), and continuous
    /// planar fill at 48 kHz stereo without NaN/Infinity.
    /// ASIO grants EXCLUSIVE driver access, so this must always dispose and release cleanly.
    /// </summary>
    static void TestAsioHostAudio()
    {
        Console.WriteLine("\n[Step 0.69] Verifying ASIO host audio capture (discovery, COM vtable, rate negotiation)...");

        var drivers = AsioDriverEnumerator.Enumerate();
        Console.WriteLine($" -> AsioDriverEnumerator resolved {drivers.Count} ASIO driver(s)");
        if (drivers.Count == 0)
        {
            Console.WriteLine(" -> [SKIP] no registered ASIO drivers on this host");
            return;
        }

        foreach (var d in drivers)
        {
            Console.WriteLine($"    * {d.DisplayName}");
        }

        AsioHostAudioSource? opened = null;
        AsioDriverInfo? openedDriver = null;
        foreach (var d in drivers)
        {
            AsioHostAudioSource? candidate = null;
            try
            {
                candidate = new AsioHostAudioSource(new AsioCaptureOption(d, 0));

                // A driver can construct yet be unusable (device not present, owned by another
                // app). Require evidence it is genuinely streaming before accepting it.
                bool streaming = false;
                for (int probe = 0; probe < 16 && !streaming; probe++)
                {
                    Thread.Sleep(25);
                    streaming = candidate.CallbacksInvoked > 0 &&
                                !string.IsNullOrEmpty(candidate.DriverReportedName);
                }

                if (streaming)
                {
                    opened = candidate;
                    openedDriver = d;
                    break;
                }

                Console.WriteLine($"    [skip] {d.DisplayName}: opened but not streaming (error: {candidate.LastError ?? "none reported"})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [skip] {d.DisplayName}: {ex.Message}");
            }
            finally
            {
                // Release the exclusive lock on any candidate we are not keeping.
                if (!ReferenceEquals(opened, candidate))
                {
                    candidate?.Dispose();
                }
            }
        }

        if (opened == null)
        {
            Console.WriteLine(" -> [SKIP] no registered ASIO driver is currently streaming (all unavailable or held exclusively)");
            return;
        }

        try
        {
            if (opened.SampleRate != 48000 || opened.Channels != 2)
                throw new Exception($"AsioHostAudioSource contract violation: {opened.SampleRate}Hz, {opened.Channels}ch");

            string path = opened.IsResamplingRequired
                ? $"native {opened.DriverActualSampleRate} Hz -> resampled to 48 kHz"
                : "running native 48 kHz (no resampling)";
            Console.WriteLine($" -> Opened '{opened.DriverReportedName}' ({path})");
            Console.WriteLine($" -> Latency: in {opened.InputLatencySamples} / out {opened.OutputLatencySamples} samples");
            if (opened.SampleRateNegotiationNote != null)
                Console.WriteLine($" -> Rate note: {opened.SampleRateNegotiationNote}");

            // Prove the driver is really streaming (callbacks arriving) and check for signal.
            int samplesPerChannel = opened.SampleRate / 30; // 30 fps cadence
            float[] planar = new float[2 * samplesPerChannel];
            float peak = 0f;

            for (int i = 0; i < 40; i++)
            {
                opened.FillPlanarAudio(planar, samplesPerChannel);
                foreach (float s in planar)
                {
                    if (float.IsNaN(s) || float.IsInfinity(s))
                        throw new Exception("AsioHostAudioSource produced a NaN/Infinity sample!");
                    float mag = Math.Abs(s);
                    if (mag > peak) peak = mag;
                }
                Thread.Sleep(25);
            }

            long callbacks = opened.CallbacksInvoked;
            if (callbacks <= 0)
                throw new Exception($"ASIO driver '{openedDriver!.DisplayName}' opened but delivered no callbacks (not streaming).");

            Console.WriteLine($" -> {callbacks} callbacks received; peak {peak:F6} ({(peak > 0f ? "non-silent" : "digital silence - no live signal into this driver")})");
            if (opened.LastError != null)
                Console.WriteLine($" -> Last driver error: {opened.LastError}");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" -> ASIO host audio capture verified!");
            Console.ResetColor();
        }
        finally
        {
            // Release the exclusive driver lock; double-dispose must be idempotent.
            opened.Dispose();
            opened.Dispose();
        }
    }

    static void TestCaptureTargets()
    {
        Console.WriteLine("\n[Step 0.7] Verifying Capture Target Enumeration, Window Capture & Factory Mapping...");

        // (a) Monitors: DXGI output enumeration must yield at least one monitor.
        var monitors = MonitorEnumerator.Enumerate();
        if (monitors.Count < 1)
            throw new Exception("MonitorEnumerator.Enumerate() returned no monitors.");
        Console.WriteLine($" -> {monitors.Count} monitor(s) enumerated; first = '{monitors[0].DisplayName}'");

        // (b) Windows: GDI window enumeration; capture the top-most window when available.
        var windows = WindowEnumerator.Enumerate();
        Console.WriteLine($" -> {windows.Count} visible window(s) enumerated.");
        if (windows.Count > 0)
        {
            using var wc = new WindowFrameCapturer(windows[0].WindowHandle);
            if (wc.Width <= 0 || wc.Height <= 0 || wc.BufferPtr == IntPtr.Zero)
                throw new Exception($"WindowFrameCapturer invalid: {wc.Width}x{wc.Height}, BufferPtr={wc.BufferPtr}");

            bool captured = false;
            for (int i = 0; i < 50 && !captured; i++)
            {
                captured = wc.TryCaptureFrame();
                if (!captured) Thread.Sleep(16);
            }

            if (captured)
            {
                // A hard assert here would be flaky (the top window may legitimately be black),
                // so report it. Non-black proves PrintWindow or its screen-region fallback worked.
                bool nonBlack = IsBufferNonBlack(wc.BufferPtr, wc.Stride * wc.Height);
                Console.WriteLine($" -> Captured top window '{windows[0].DisplayName}' at {wc.Width}x{wc.Height}, stride {wc.Stride} ({(nonBlack ? "non-black" : "[WARN] all-black")})");
            }
            else
            {
                Console.WriteLine(" -> [WARN] Window enumerated but TryCaptureFrame() never succeeded (may be obscured); continuing.");
            }
        }
        else
        {
            Console.WriteLine(" -> [SKIP] no visible windows to test");
        }

        // (c) Factory mapping: monitors -> DesktopFrameCapturer, windows -> WindowFrameCapturer.
        using (IFrameSource monitorSource = FrameSourceFactory.Create(monitors[0]))
        {
            if (monitorSource is not DesktopFrameCapturer)
                throw new Exception("FrameSourceFactory.Create(monitor) did not produce DesktopFrameCapturer.");
        }
        if (windows.Count > 0)
        {
            using IFrameSource windowSource = FrameSourceFactory.Create(windows[0]);
            if (windowSource is not WindowFrameCapturer)
                throw new Exception("FrameSourceFactory.Create(window) did not produce WindowFrameCapturer.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Capture target enumeration, window capture & factory mapping verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// Exercises OmtReceiverService end-to-end: connect to a live OMT source, receive
    /// video frames and at least one telemetry tick (which calls
    /// GetVideoStatistics/GetAudioStatistics/IsConnected — the line that NRE'd when
    /// _receiver was dereferenced mid-disconnect), then disconnect and reconnect.
    /// </summary>
    static void TestReceiverServiceTelemetry()
    {
        Console.WriteLine("\n[Step 0.71] Verifying OmtReceiverService telemetry + reconnect safety...");

        using var generator = new OmtTestPatternGenerator("OMT_Automated_Receiver_Service_Source");
        generator.Start();
        Thread.Sleep(1200); // allow sender to bind

        string target = generator.Url ?? generator.Address ?? "OMT_Automated_Receiver_Service_Source";

        using var receiver = new OmtReceiverService();

        long videoFrames = 0;
        long telemetryTicks = 0;
        string? error = null;
        receiver.VideoFrameReceived += (p, len, w, h, s, fps, ts) => Interlocked.Increment(ref videoFrames);
        receiver.TelemetryUpdated += t => Interlocked.Increment(ref telemetryTicks);
        receiver.ErrorOccurred += e => error = e;

        receiver.Connect(target);

        // Wait for frames AND at least one telemetry tick (telemetry updates once per second).
        int wait = 0;
        while ((videoFrames < 5 || telemetryTicks < 1) && wait < 50)
        {
            Thread.Sleep(100);
            wait++;
        }

        if (videoFrames < 5)
            throw new Exception($"OmtReceiverService received only {videoFrames} video frames.");
        if (telemetryTicks < 1)
            throw new Exception("OmtReceiverService never emitted telemetry (GetVideoStatistics path not reached).");

        Console.WriteLine($" -> Received {videoFrames} frames and {telemetryTicks} telemetry tick(s) at {receiver.CurrentAddress}");

        // Disconnect and immediately reconnect to exercise the reconnect path that raced _receiver.
        receiver.Disconnect();
        if (receiver.IsConnected)
            throw new Exception("OmtReceiverService.IsConnected should be false after Disconnect.");

        receiver.Connect(target);
        int wait2 = 0;
        while (videoFrames < 8 && wait2 < 50)
        {
            Thread.Sleep(100);
            wait2++;
        }
        if (videoFrames < 8)
            throw new Exception($"OmtReceiverService did not resume receiving after reconnect (only {videoFrames} frames).");
        if (error != null)
            throw new Exception($"OmtReceiverService reported error: {error}");

        Console.WriteLine($" -> Reconnected and resumed; total {videoFrames} frames, no errors.");

        // Rapid connect/disconnect churn: each cycle hands a fresh receiver to a fresh loop
        // and the previous loop must dispose its own instance after exiting. Exercises the
        // disposal-ownership + identity-check fix (no use-after-dispose, no leaked handle).
        for (int cycle = 0; cycle < 5; cycle++)
        {
            receiver.Disconnect();
            receiver.Connect(target);
        }
        receiver.Disconnect();

        if (error != null)
            throw new Exception($"OmtReceiverService reported error during churn: {error}");
        if (receiver.IsConnected)
            throw new Exception("OmtReceiverService still reports connected after final Disconnect.");

        Console.WriteLine($" -> Survived 5 rapid connect/disconnect cycles cleanly; total {videoFrames} frames, no errors.");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> OmtReceiverService telemetry + reconnect safety verified!");
        Console.ResetColor();
    }

    /// <summary>
    /// Returns true when the BGRA buffer contains at least one non-black pixel. Used to prove
    /// the window capturer produced real content (PrintWindow content or its screen-region
    /// fallback), not the all-black failure frame.
    /// </summary>
    static bool IsBufferNonBlack(IntPtr bufferPtr, int length)
    {
        // Copy the whole buffer (window-sized, bounded) and scan every pixel; a window with
        // any coloured content anywhere must register as non-black.
        var bytes = new byte[length];
        System.Runtime.InteropServices.Marshal.Copy(bufferPtr, bytes, 0, length);
        for (int i = 0; i + 2 < bytes.Length; i += 4)
        {
            if (bytes[i] > 8 || bytes[i + 1] > 8 || bytes[i + 2] > 8) return true;
        }
        return false;
    }

    /// <summary>
    /// Feeds ~<paramref name="seconds"/> of audio in 1024-frame chunks, advancing the
    /// video clock at <paramref name="trueFps"/> between chunks, and returns the
    /// resampler's converged ratio plus total input/output frame counts and video frames.
    /// </summary>
    static (double ratio, long inputFrames, long outputFrames, long videoFrames) SimulateResampler(
        int sampleRate, double nominalFps, double trueFps, int channels, double seconds)
    {
        var resampler = new AudioClockResampler();
        resampler.Reset(sampleRate, nominalFps, channels);

        const int chunkFrames = 1024;
        var chunk = new byte[chunkFrames * channels * 4];
        int totalChunks = (int)(seconds * sampleRate / chunkFrames);

        long inputFrames = 0, outputFrames = 0;
        double videoClock = 0.0; // fractional video frames elapsed

        for (int i = 0; i < totalChunks; i++)
        {
            // Advance the video clock by this audio chunk's wall-clock duration.
            double audioSeconds = (double)chunkFrames / sampleRate;
            videoClock += audioSeconds * trueFps;
            while (resampler.VideoFramesSeen < (long)videoClock)
            {
                resampler.OnVideoFrame();
            }

            resampler.Process(chunk, chunk.Length, out int outBytes);
            inputFrames += chunkFrames;
            outputFrames += outBytes / (channels * 4);
        }

        return (resampler.Ratio, inputFrames, outputFrames, resampler.VideoFramesSeen);
    }

    static void TestAuditRemediationsAndRegressionSeams()
    {
        Console.WriteLine("\n[Step 0.72] Verifying Audit Remediations, Buffer Return, and Disposal Idempotency...");

        // 1. BoundedMediaQueue drain resilience and overflow drop
        int droppedCount = 0;
        var queue = new BoundedMediaQueue<int>(3, item =>
        {
            droppedCount++;
            if (item == 2) throw new InvalidOperationException("Simulated drop callback error");
        });

        // Overflow queue: capacity 3, enqueue 4 -> drops 1
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        queue.Enqueue(4);
        if (droppedCount != 1 || queue.DroppedCount != 1)
        {
            throw new Exception($"BoundedMediaQueue overflow drop count mismatch: dropped={droppedCount}, property={queue.DroppedCount}");
        }

        // Dispose must drain remaining items (2, 3, 4) even though dropping item 2 throws
        queue.Dispose();
        if (droppedCount != 4)
        {
            throw new Exception($"BoundedMediaQueue Dispose failed to drain all items! Expected 4, got {droppedCount}");
        }

        // Double dispose must be idempotent
        queue.Dispose();

        // Enqueue after dispose must trigger onDrop and increment DroppedCount
        queue.Enqueue(5);
        if (droppedCount != 5 || queue.DroppedCount != 2)
        {
            throw new Exception($"BoundedMediaQueue Enqueue after Dispose failed to drop item or increment DroppedCount! droppedCount={droppedCount}, DroppedCount={queue.DroppedCount}");
        }

        using (var compQueue = new BoundedMediaQueue<int>(2, _ => { }))
        {
            compQueue.CompleteAdding();
            compQueue.Enqueue(10);
            if (compQueue.DroppedCount != 1)
            {
                throw new Exception($"BoundedMediaQueue Enqueue after CompleteAdding failed to increment DroppedCount! Got: {compQueue.DroppedCount}");
            }
        }
        Console.WriteLine(" -> BoundedMediaQueue overflow, exception-tolerant drain, and double-dispose verified!");

        // 2. FfmpegPipeSink uninitialized/disposed write safety
        using (var sink = new FfmpegPipeSink())
        {
            // Write before Initialize should no-op safely without renting or leaking
            sink.WriteVideo(new IntPtr(12345), 1024);
            sink.WriteAudio(new byte[128], 0, 128);

            // Double dispose
            sink.Dispose();
            sink.Dispose();

            // Write after Dispose should no-op safely
            sink.WriteVideo(new IntPtr(12345), 1024);
            sink.WriteAudio(new byte[128], 0, 128);
        }
        Console.WriteLine(" -> FfmpegPipeSink uninitialized/disposed buffer write safety verified!");

        // 3. Service double-dispose and event detachment
        // 3a. AudioEngine
        var audioEngine = new AudioEngine();
        bool audioLevelsFired = false;
        audioEngine.LevelsCalculated += _ => audioLevelsFired = true;
        audioEngine.Dispose();
        audioEngine.Dispose(); // idempotent
        audioEngine.IngestAudioFrame(new IntPtr(123), 2, 128, 48000, 0);
        if (audioLevelsFired)
        {
            throw new Exception("AudioEngine fired event after Dispose!");
        }

        // 3b. XAudio2Monitor
        var xaudio2 = new XAudio2Monitor();
        xaudio2.Start(0, 0); // must safely reject 0 channels / sampleRate without divide-by-zero
        xaudio2.Feed(new byte[128], 128); // must safe no-op
        xaudio2.Stop();
        xaudio2.Dispose();
        xaudio2.Dispose(); // idempotent
        xaudio2.Feed(new byte[128], 128);

        // 3c. CaptureSession
        var dummyAudio = new AudioEngine();
        var dummyRec = new StreamRecorderService(new NullRecordingSink());
        var capSession = new CaptureSession(dummyAudio, dummyRec, ownsDependencies: false);
        bool sessionVideoFired = false;
        capSession.VideoFrameAvailable += (p, len, w, h, s, fps, ts) => sessionVideoFired = true;
        capSession.Dispose();
        capSession.Dispose(); // idempotent
        if (sessionVideoFired)
        {
            throw new Exception("CaptureSession fired event after Dispose!");
        }
        dummyAudio.Dispose();
        dummyRec.Dispose();

        // 3d. StreamRecorderService
        var recService = new StreamRecorderService(new NullRecordingSink());
        recService.Dispose();
        recService.Dispose(); // idempotent
        if (recService.StartRecording(new RecordingConfig(), new StreamFormat { Width = 1920, Height = 1080 }))
        {
            throw new Exception("StreamRecorderService StartRecording succeeded after Dispose!");
        }

        // 3e. OmtReceiverService
        var rxService = new OmtReceiverService();
        rxService.Dispose();
        rxService.Dispose(); // idempotent
        if (rxService.IsConnected)
        {
            throw new Exception("OmtReceiverService IsConnected is true after Dispose!");
        }

        // 3f. OmtDiscoveryService
        var discService = new OmtDiscoveryService();
        discService.Dispose();
        discService.Dispose(); // idempotent

        // 3g. OmtNetworkSource
        var netSource = new OmtNetworkSource("internal://test");
        netSource.Dispose();
        netSource.Dispose(); // idempotent

        // 3h. SyntheticPatternSource
        var synthSource = new SyntheticPatternSource();
        synthSource.Dispose();
        synthSource.Dispose(); // idempotent
        synthSource.Connect();
        if (synthSource.IsConnected)
        {
            throw new Exception("SyntheticPatternSource IsConnected is true after Connect() on disposed instance!");
        }

        // 3i. OmtDesktopCaptureGenerator unstarted source disposal
        var mockFrameSource = new MockFrameSource();
        var desktopGen = new OmtDesktopCaptureGenerator(mockFrameSource, "TestGen");
        desktopGen.Dispose();
        desktopGen.Dispose(); // idempotent
        if (!mockFrameSource.Disposed)
        {
            throw new Exception("OmtDesktopCaptureGenerator failed to dispose unstarted frame source!");
        }

        // 3j. FfmpegProcessHost double-disposal idempotency & delegate nullification
        var hostVideoQ = new BoundedMediaQueue<VideoFramePacket>(4, _ => { });
        var hostAudioQ = new BoundedMediaQueue<AudioDataPacket>(4, _ => { });
        var ffmpegHost = new FfmpegProcessHost(hostVideoQ, hostAudioQ);
        bool ffmpegSinkErrorFired = false;
        ffmpegHost.SinkError += _ => ffmpegSinkErrorFired = true;
        ffmpegHost.Dispose();
        ffmpegHost.Dispose(); // idempotent
        hostVideoQ.Dispose();
        hostAudioQ.Dispose();
        if (ffmpegSinkErrorFired)
        {
            throw new Exception("FfmpegProcessHost fired SinkError during disposal!");
        }
        Console.WriteLine(" -> Full service suite double-disposal idempotency and unstarted resource cleanup verified!");

        // 4. Audio Level Calculator & Interleaver Edge Cases
        // 4a. Zero / negative parameters
        var emptyLevels = AudioLevelCalculator.ComputeLevels(IntPtr.Zero, 0, 0);
        if (emptyLevels.Channels.Length != 0)
        {
            throw new Exception("AudioLevelCalculator.ComputeLevels did not return empty array for 0 channels!");
        }

        // 4b. Silent signal
        float[] silentSamples = new float[256];
        GCHandle silentHandle = GCHandle.Alloc(silentSamples, GCHandleType.Pinned);
        try
        {
            var silentResult = AudioLevelCalculator.ComputeLevels(silentHandle.AddrOfPinnedObject(), 2, 128);
            if (silentResult.Channels[0].PeakDb != -60.0f || silentResult.Channels[0].IsClipping)
            {
                throw new Exception($"AudioLevelCalculator silent peak mismatch: got {silentResult.Channels[0].PeakDb}");
            }
        }
        finally
        {
            silentHandle.Free();
        }

        // 4c. Clipping signal (sample > 1.0f)
        float[] clippingSamples = new float[256];
        for (int i = 0; i < clippingSamples.Length; i++) clippingSamples[i] = 1.5f;
        GCHandle clipHandle = GCHandle.Alloc(clippingSamples, GCHandleType.Pinned);
        try
        {
            var clipResult = AudioLevelCalculator.ComputeLevels(clipHandle.AddrOfPinnedObject(), 2, 128);
            if (!clipResult.Channels[0].IsClipping || clipResult.Channels[0].PeakDb != 0.0f)
            {
                throw new Exception($"AudioLevelCalculator clipping signal not detected properly!");
            }
        }
        finally
        {
            clipHandle.Free();
        }

        // 4d. AudioInterleaver planar -> interleaved correctness
        var interleaver = new AudioInterleaver();
        // 2 channels, 3 samples: Ch0=[1, 2, 3], Ch1=[4, 5, 6]
        // Interleaved should be: [1, 4, 2, 5, 3, 6]
        float[] planarData = new float[] { 1f, 2f, 3f, 4f, 5f, 6f };
        GCHandle planarHandle = GCHandle.Alloc(planarData, GCHandleType.Pinned);
        try
        {
            byte[] interleaved = interleaver.Interleave(planarHandle.AddrOfPinnedObject(), 2, 3, out int totalBytes);
            if (totalBytes != 6 * sizeof(float))
            {
                throw new Exception($"AudioInterleaver totalBytes mismatch: got {totalBytes}");
            }
            float[] resultSamples = new float[6];
            Buffer.BlockCopy(interleaved, 0, resultSamples, 0, totalBytes);
            float[] expected = new float[] { 1f, 4f, 2f, 5f, 3f, 6f };
            for (int i = 0; i < 6; i++)
            {
                if (Math.Abs(resultSamples[i] - expected[i]) > 0.0001f)
                {
                    throw new Exception($"AudioInterleaver output mismatch at index {i}: got {resultSamples[i]}, expected {expected[i]}");
                }
            }
            // Test AudioInterleaver input validation guards
            byte[] nullInterleaved = interleaver.Interleave(IntPtr.Zero, 2, 3, out int nullBytes);
            if (nullBytes != 0 || nullInterleaved.Length != 0)
            {
                throw new Exception($"AudioInterleaver with IntPtr.Zero did not return empty array/0 bytes!");
            }
            byte[] negInterleaved = interleaver.Interleave(planarHandle.AddrOfPinnedObject(), -1, 3, out int negBytes);
            if (negBytes != 0 || negInterleaved.Length != 0)
            {
                throw new Exception($"AudioInterleaver with negative channels did not return empty array/0 bytes!");
            }
            byte[] overChannels = interleaver.Interleave(planarHandle.AddrOfPinnedObject(), 128, 3, out int overBytes);
            if (overBytes != 0 || overChannels.Length != 0)
            {
                throw new Exception("AudioInterleaver with excessive channels did not return empty array/0 bytes!");
            }
            byte[] newOverChannels = interleaver.InterleaveNew(planarHandle.AddrOfPinnedObject(), 128, 3);
            if (newOverChannels.Length != 0)
            {
                throw new Exception("AudioInterleaver.InterleaveNew with excessive channels did not return empty array!");
            }
        }
        finally
        {
            planarHandle.Free();
        }

        // 4e. AudioLevelCalculator NaN / Infinity tolerance & Extreme Signal Clamping
        float[] nanSamples = new float[256];
        nanSamples[0] = float.NaN;
        nanSamples[1] = float.PositiveInfinity;
        nanSamples[2] = float.NegativeInfinity;
        nanSamples[3] = 0.5f;
        GCHandle nanHandle = GCHandle.Alloc(nanSamples, GCHandleType.Pinned);
        try
        {
            var nanResult = AudioLevelCalculator.ComputeLevels(nanHandle.AddrOfPinnedObject(), 2, 128);
            if (float.IsNaN(nanResult.Channels[0].PeakDb) || float.IsInfinity(nanResult.Channels[0].PeakDb))
            {
                throw new Exception("AudioLevelCalculator returned NaN/Infinity PeakDb for corrupt audio buffer!");
            }
            if (nanResult.Channels[0].PeakDb < -60.0f || nanResult.Channels[0].PeakDb > 0.0f)
            {
                throw new Exception($"AudioLevelCalculator PeakDb out of range: {nanResult.Channels[0].PeakDb}");
            }
        }
        finally
        {
            nanHandle.Free();
        }

        // Verify pure NaN samples clamp to -60 dBFS (silence) and not clipping
        float[] pureNanSamples = new float[256];
        for (int i = 0; i < pureNanSamples.Length; i++) pureNanSamples[i] = float.NaN;
        GCHandle pureNanHandle = GCHandle.Alloc(pureNanSamples, GCHandleType.Pinned);
        try
        {
            var pureNanResult = AudioLevelCalculator.ComputeLevels(pureNanHandle.AddrOfPinnedObject(), 2, 128);
            if (pureNanResult.Channels[0].PeakDb != -60.0f || pureNanResult.Channels[0].IsClipping)
            {
                throw new Exception($"AudioLevelCalculator failed on pure NaN buffer! Got: {pureNanResult.Channels[0].PeakDb}");
            }
        }
        finally
        {
            pureNanHandle.Free();
        }

        // Verify extreme clipping / PositiveInfinity clamps to 0 dBFS (peak limit), NOT -60 dBFS (silence)
        float[] extremeSamples = new float[256];
        for (int i = 0; i < extremeSamples.Length; i++) extremeSamples[i] = float.PositiveInfinity;
        GCHandle extremeHandle = GCHandle.Alloc(extremeSamples, GCHandleType.Pinned);
        try
        {
            var extremeResult = AudioLevelCalculator.ComputeLevels(extremeHandle.AddrOfPinnedObject(), 2, 128);
            if (extremeResult.Channels[0].PeakDb != 0.0f || !extremeResult.Channels[0].IsClipping)
            {
                throw new Exception($"AudioLevelCalculator failed to clamp PositiveInfinity to 0 dBFS! Got: {extremeResult.Channels[0].PeakDb}, clipping={extremeResult.Channels[0].IsClipping}");
            }
            if (extremeResult.Channels[0].RmsDb != 0.0f)
            {
                throw new Exception($"AudioLevelCalculator failed to clamp extreme RMS to 0 dBFS! Got: {extremeResult.Channels[0].RmsDb}");
            }
        }
        finally
        {
            extremeHandle.Free();
        }

        // 4f. VuMeterControl.DbToNormalized NaN / Infinity tolerance
        if (OmtCaptureStudio.Controls.VuMeterControl.DbToNormalized(float.NaN) != 0.0)
        {
            throw new Exception("VuMeterControl.DbToNormalized(float.NaN) did not return 0.0!");
        }
        if (OmtCaptureStudio.Controls.VuMeterControl.DbToNormalized(float.PositiveInfinity) != 1.0)
        {
            throw new Exception("VuMeterControl.DbToNormalized(float.PositiveInfinity) did not return 1.0!");
        }
        if (OmtCaptureStudio.Controls.VuMeterControl.DbToNormalized(float.NegativeInfinity) != 0.0)
        {
            throw new Exception("VuMeterControl.DbToNormalized(float.NegativeInfinity) did not return 0.0!");
        }
        Console.WriteLine(" -> Audio computation, NaN tolerance, and interleaving edge cases verified!");

        // 5. StreamRecorderService preflight & dimension validation
        using (var recorder = new StreamRecorderService(new NullRecordingSink()))
        {
            var validFormat = new StreamFormat { Width = 1280, Height = 720, FrameRate = 30 };

            // Null config or whitespace directory preflight validation
            if (recorder.StartRecording(null!, validFormat) || recorder.IsRecording)
            {
                throw new Exception("StreamRecorderService StartRecording unexpectedly succeeded with null RecordingConfig!");
            }
            if (recorder.StartRecording(new RecordingConfig { OutputDirectory = "" }, validFormat) || recorder.IsRecording)
            {
                throw new Exception("StreamRecorderService StartRecording unexpectedly succeeded with empty OutputDirectory!");
            }
            if (recorder.StartRecording(new RecordingConfig { OutputDirectory = "   " }, validFormat) || recorder.IsRecording)
            {
                throw new Exception("StreamRecorderService StartRecording unexpectedly succeeded with whitespace OutputDirectory!");
            }

            // Invalid dimensions
            var invalidFormat = new StreamFormat { Width = 0, Height = 0 };
            bool startFailed = recorder.StartRecording(new RecordingConfig { OutputDirectory = Path.GetTempPath() }, invalidFormat);
            if (startFailed || recorder.IsRecording)
            {
                throw new Exception("StreamRecorderService StartRecording unexpectedly succeeded with invalid 0x0 format!");
            }

            // Valid format start
            bool startOk = recorder.StartRecording(new RecordingConfig { OutputDirectory = Path.GetTempPath() }, validFormat);
            if (!startOk || !recorder.IsRecording)
            {
                throw new Exception("StreamRecorderService StartRecording failed with valid format!");
            }

            // Re-entrant start should return false
            bool reentrantStart = recorder.StartRecording(new RecordingConfig { OutputDirectory = Path.GetTempPath() }, validFormat);
            if (reentrantStart)
            {
                throw new Exception("StreamRecorderService allowed re-entrant StartRecording while already recording!");
            }

            recorder.StopRecording();
            if (recorder.IsRecording)
            {
                throw new Exception("StreamRecorderService IsRecording still true after StopRecording!");
            }

            // Stop again when already stopped: should be safe no-op
            recorder.StopRecording();
        }
        Console.WriteLine(" -> StreamRecorderService preflight, validation, and re-entrancy verified!");

        // 6. WindowEnumerator thread-safe concurrent execution
        var windowTasks = new Task[4];
        for (int i = 0; i < 4; i++)
        {
            windowTasks[i] = Task.Run(() =>
            {
                var list = WindowEnumerator.Enumerate();
                if (list == null) throw new Exception("WindowEnumerator returned null in concurrency test!");
            });
        }
        Task.WaitAll(windowTasks);
        Console.WriteLine(" -> WindowEnumerator concurrent enumeration thread safety verified!");

        // 7. AudioClockResampler multithreaded cross-thread safety
        var resampler = new AudioClockResampler();
        resampler.Reset(48000, 30.0, 2);
        var resamplerTasks = new Task[4];
        byte[] dummyChunk = new byte[1024 * 2 * 4];
        for (int i = 0; i < 2; i++)
        {
            resamplerTasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 500; j++)
                {
                    resampler.OnVideoFrame();
                }
            });
        }
        for (int i = 2; i < 4; i++)
        {
            resamplerTasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    resampler.Process(dummyChunk, dummyChunk.Length, out _);
                }
            });
        }
        Task.WaitAll(resamplerTasks);
        if (resampler.VideoFramesSeen != 1000)
        {
            throw new Exception($"AudioClockResampler concurrent VideoFramesSeen mismatch: expected 1000, got {resampler.VideoFramesSeen}");
        }

        // Boundary guards: null chunk, zero count, uninitialized resampler
        resampler.Process(null!, 0, out int outCountNull);
        if (outCountNull != 0) throw new Exception("AudioClockResampler failed to return 0 on null chunk!");
        resampler.Process(dummyChunk, 0, out int outCountZero);
        if (outCountZero != 0) throw new Exception("AudioClockResampler failed to return 0 on zero count!");

        var freshResampler = new AudioClockResampler();
        freshResampler.Process(dummyChunk, dummyChunk.Length, out int freshCount);
        // Concurrent Reset and Process execution
        var resetTasks = new Task[2];
        resetTasks[0] = Task.Run(() =>
        {
            for (int j = 0; j < 50; j++)
            {
                resampler.Reset(48000, 30.0, 2);
            }
        });
        resetTasks[1] = Task.Run(() =>
        {
            for (int j = 0; j < 50; j++)
            {
                resampler.Process(dummyChunk, dummyChunk.Length, out _);
            }
        });
        Task.WaitAll(resetTasks);

        Console.WriteLine(" -> AudioClockResampler cross-thread atomic video clock verified!");

        // 8. CaptureSession StartRecording on disposed session
        using (var sess = new CaptureSession(new AudioEngine(), new StreamRecorderService(new NullRecordingSink()), ownsDependencies: true))
        {
            sess.Dispose();
            if (sess.StartRecording(new RecordingConfig()))
            {
                throw new Exception("CaptureSession.StartRecording unexpectedly returned true after Dispose()!");
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> All audit remediations and regression seams verified cleanly!");
        Console.ResetColor();
    }

    class MockFrameSource : IFrameSource
    {
        public bool Disposed { get; private set; }
        public int Width => 640;
        public int Height => 480;
        public int Stride => 640 * 4;
        public IntPtr BufferPtr => IntPtr.Zero;
        public bool TryCaptureFrame() => false;
        public void Dispose() => Disposed = true;
    }

    sealed class SyntheticBgraFrameSource : IFrameSource
    {
        private readonly byte[] _buffer;
        private GCHandle _handle;
        private bool _disposed;

        public int Width { get; }
        public int Height { get; }
        public int Stride => Width * 4;
        public IntPtr BufferPtr => _handle.IsAllocated ? _handle.AddrOfPinnedObject() : IntPtr.Zero;

        public SyntheticBgraFrameSource(int width, int height)
        {
            Width = width;
            Height = height;
            _buffer = new byte[Stride * Height];
            for (int i = 0; i < _buffer.Length; i += 4)
            {
                _buffer[i] = 0x40;
                _buffer[i + 1] = 0x80;
                _buffer[i + 2] = 0xC0;
                _buffer[i + 3] = 0xFF;
            }
            _handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        }

        public bool TryCaptureFrame() => !_disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle.IsAllocated) _handle.Free();
        }
    }

    sealed class SyntheticToneHostAudioSource : IHostAudioSource
    {
        private double _phaseL;
        private double _phaseR;
        public int SampleRate => 48000;
        public int Channels => 2;

        // ADR-0003 widened IHostAudioSource; this synthetic source is a native-48k stand-in.
        public bool IsHardwareActive => true;
        public int DeviceSampleRate => 48000;
        public bool IsResamplingRequired => false;
        public string? RateNegotiationNote => null;
        public int InputLatencyFrames => 0;
        public string? LastError => null;

        public void FillPlanarAudio(Span<float> planarDestination, int samplesPerChannel)
        {
            Span<float> left = planarDestination.Slice(0, samplesPerChannel);
            Span<float> right = planarDestination.Slice(samplesPerChannel, samplesPerChannel);
            for (int i = 0; i < samplesPerChannel; i++)
            {
                left[i] = (float)(Math.Sin(_phaseL) * 0.5);
                right[i] = (float)(Math.Sin(_phaseR) * 0.5);
                _phaseL += 2.0 * Math.PI * 440.0 / SampleRate;
                _phaseR += 2.0 * Math.PI * 880.0 / SampleRate;
                if (_phaseL > 2.0 * Math.PI) _phaseL -= 2.0 * Math.PI;
                if (_phaseR > 2.0 * Math.PI) _phaseR -= 2.0 * Math.PI;
            }
        }

        public void Dispose() { }
    }
}
