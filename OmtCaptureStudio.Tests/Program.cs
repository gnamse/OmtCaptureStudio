using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Sinks;
using OmtCaptureStudio.Services.Sources;
using OmtCaptureStudio.ViewModels;

namespace OmtCaptureStudio.Tests;

class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    static void Main(string[] args)
    {
        Console.WriteLine("====================================================");
        Console.WriteLine("  OMT Capture Studio - End-to-End Automated Test");
        Console.WriteLine("====================================================");

        SetDllDirectory(AppDomain.CurrentDomain.BaseDirectory);

        Console.WriteLine("\n[Step 0] Verifying OMT Source Info Parsing & Display Formatting...");
        var test1 = OmtSourceInfo.Parse("PC (vMix - Output 1)");
        if (test1.Name != "vMix - Output 1" || test1.Host != "PC" || test1.DisplayName != "vMix - Output 1 (PC)")
        {
            throw new Exception($"Test1 failed: Name='{test1.Name}', Host='{test1.Host}', Display='{test1.DisplayName}'");
        }
        if (test1.Address != "PC (vMix - Output 1)")
        {
            throw new Exception($"Test1 address failed: Address='{test1.Address}'");
        }

        var test2 = OmtSourceInfo.Parse("vMix - Output 1 @ PC");
        if (test2.Name != "vMix - Output 1" || test2.Host != "PC" || test2.DisplayName != "vMix - Output 1 (PC)")
        {
            throw new Exception($"Test2 failed: Name='{test2.Name}', Host='{test2.Host}', Display='{test2.DisplayName}'");
        }

        var test3 = OmtSourceInfo.Parse("omt://127.0.0.1:5000");
        if (test3.DisplayName != "omt://127.0.0.1:5000" || test3.Address != "omt://127.0.0.1:5000")
        {
            throw new Exception($"Test3 failed: Display='{test3.DisplayName}'");
        }

        // Test redundant double-wrapping edge cases from user screenshot
        var test4 = OmtSourceInfo.Parse("PC (vMix - Output 1) (PC (vMix - Output 1))");
        if (test4.Name != "vMix - Output 1" || test4.Host != "PC" || test4.DisplayName != "vMix - Output 1 (PC)")
        {
            throw new Exception($"Test4 failed (redundant duplicate): Name='{test4.Name}', Host='{test4.Host}', Display='{test4.DisplayName}'");
        }

        var test5 = OmtSourceInfo.Parse("PC (Audio Headset Microphone) (PC (Audio Headset Microphone))");
        if (test5.Name != "Audio Headset Microphone" || test5.Host != "PC" || test5.DisplayName != "Audio Headset Microphone (PC)")
        {
            throw new Exception($"Test5 failed: Name='{test5.Name}', Host='{test5.Host}', Display='{test5.DisplayName}'");
        }

        var test6 = OmtSourceInfo.Parse("STUDIO-PC (Camera 1 (Main))");
        if (test6.Name != "Camera 1 (Main)" || test6.Host != "STUDIO-PC" || test6.DisplayName != "Camera 1 (Main) (STUDIO-PC)")
        {
            throw new Exception($"Test6 failed: Name='{test6.Name}', Host='{test6.Host}', Display='{test6.DisplayName}'");
        }

        var test7 = OmtSourceInfo.Parse("");
        if (test7.DisplayName != "" || test7.Name != "" || test7.Host != "")
        {
            throw new Exception($"Test7 failed on empty string");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> All source parsing & display formatting tests passed cleanly!");
        Console.ResetColor();

        Console.WriteLine("\n[Step 0.5] Verifying In-Memory Seams (SyntheticPatternSource + NullRecordingSink)...");
        var nullSink = new NullRecordingSink();
        using (var testRecorder = new StreamRecorderService(nullSink))
        using (var testAudio = new AudioEngine())
        using (var testSession = new CaptureSession(testAudio, testRecorder))
        {
            long syntheticVideoCount = 0;
            testSession.VideoFrameAvailable += (p, len, w, h, s, fps, ts) => Interlocked.Increment(ref syntheticVideoCount);

            testSession.Connect(new SyntheticPatternSource());

            int seamWait = 0;
            while (syntheticVideoCount < 5 && seamWait < 20)
            {
                Thread.Sleep(50);
                seamWait++;
            }

            if (syntheticVideoCount == 0)
            {
                throw new Exception("SyntheticPatternSource failed to deliver frames in-memory!");
            }

            Console.WriteLine($" -> SyntheticPatternSource delivered {syntheticVideoCount} frames in-memory with zero network sockets!");

            var testConfig = new RecordingConfig
            {
                OutputDirectory = Path.GetTempPath(),
                FilenamePrefix = "Seam_Test"
            };

            bool seamRecordStarted = testSession.StartRecording(testConfig);
            if (!seamRecordStarted)
            {
                throw new Exception("Failed to start recording via NullRecordingSink!");
            }

            Thread.Sleep(300);
            testSession.StopRecording();

            Console.WriteLine($" -> NullRecordingSink captured {nullSink.VideoFramesReceived} video frames, {nullSink.AudioBytesReceived} audio bytes in memory (0 disk I/O, 0 FFmpeg processes)!");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" -> Architectural seams successfully verified!");
            Console.ResetColor();
        }

        Console.WriteLine("\n[Step 0.6] Verifying AudioClockResampler (drift correction + passthrough)...");
        TestAudioClockResampler();
        TestDesktopCapture();
        TestCaptureTargets();
        TestReceiverServiceTelemetry();
        TestAsioHostAudio();
        TestAuditRemediationsAndRegressionSeams();

        Console.WriteLine("\n[Step 0.75] Verifying MainWindowViewModel (MVVM State, Commands & Seams)...");
        using (var vmSession = new CaptureSession(new AudioEngine(), new StreamRecorderService(new NullRecordingSink())))
        using (var vmDiscovery = new OmtDiscoveryService())
        using (var vm = new MainWindowViewModel(vmSession, vmDiscovery))
        {
            // Verify default UI state
            if (vm.SelectedContainerFormat != OutputContainerFormat.MP4 ||
                vm.SelectedEncoderChoice != VideoEncoderChoice.AutoHardware ||
                vm.SelectedQualityPreset != QualityPreset.High)
            {
                throw new Exception("ViewModel recording config defaults mismatch!");
            }

            if (!vm.IsNoSignalOverlayVisible || vm.LiveStatusText != "OFFLINE")
            {
                throw new Exception("ViewModel live status defaults mismatch!");
            }

            if (vm.CaptureAudioOptions.Count < 4 || vm.SelectedCaptureAudioMode != HostAudioCaptureMode.SystemAudio)
            {
                throw new Exception($"ViewModel host audio capture defaults mismatch: count={vm.CaptureAudioOptions.Count}, mode={vm.SelectedCaptureAudioMode}");
            }
            if (vm.BroadcastNetworks.Count < 2 || !vm.SelectedBroadcastNetwork.IsAllNetworks)
            {
                throw new Exception($"ViewModel broadcast network defaults mismatch: count={vm.BroadcastNetworks.Count}, selected={vm.SelectedBroadcastNetwork?.DisplayName}");
            }
            vm.SelectedCaptureAudioMode = HostAudioCaptureMode.SystemAndMicrophone;
            if (vm.SelectedCaptureAudioOption.Mode != HostAudioCaptureMode.SystemAndMicrophone)
            {
                throw new Exception($"SelectedCaptureAudioOption did not sync with SelectedCaptureAudioMode: {vm.SelectedCaptureAudioOption.Mode}");
            }
            vm.SelectedCaptureAudioMode = HostAudioCaptureMode.SystemAudio;

            // Test MOV auto-switches encoder to ProRes
            vm.SelectedContainerFormat = OutputContainerFormat.MOV;
            if (vm.SelectedEncoderChoice != VideoEncoderChoice.ProRes)
            {
                throw new Exception($"Expected ProRes for MOV, got: {vm.SelectedEncoderChoice}");
            }

            // Test switching away from MOV restores AutoHardware
            vm.SelectedContainerFormat = OutputContainerFormat.MP4;
            if (vm.SelectedEncoderChoice != VideoEncoderChoice.AutoHardware)
            {
                throw new Exception($"Expected AutoHardware for MP4, got: {vm.SelectedEncoderChoice}");
            }

            // Test Source ToolTip generation
            var dummySource = OmtSourceInfo.Parse("PC (Camera 1)");
            vm.SelectedSource = dummySource;
            if (string.IsNullOrWhiteSpace(vm.SelectedSourceToolTip) || !vm.SelectedSourceToolTip.Contains("Camera 1"))
            {
                throw new Exception($"ViewModel tooltip not updated correctly: '{vm.SelectedSourceToolTip}'");
            }
            vm.SelectedSource = null;
            if (vm.SelectedSourceToolTip != null)
            {
                throw new Exception("ViewModel tooltip should be null when SelectedSource is null!");
            }

            // Test Commands
            vm.RefreshSourcesCommand.Execute(null);
            if (!vm.StatusText.Contains("Scanning network"))
            {
                throw new Exception($"RefreshSourcesCommand failed to update status: '{vm.StatusText}'");
            }

            vm.IsRecordingCloseOverlayVisible = true;
            vm.KeepRecordingCommand.Execute(null);
            if (vm.IsRecordingCloseOverlayVisible || !vm.IsKeepRecordingEnabled)
            {
                throw new Exception("KeepRecordingCommand failed to dismiss overlay or restore button state!");
            }

            // Connect validation without source
            vm.ToggleConnectCommand.Execute(null);
            if (!vm.StatusText.Contains("Please select an OMT source"))
            {
                throw new Exception($"ToggleConnectCommand without source failed validation: '{vm.StatusText}'");
            }

            // Test StudioPalette token identity in default state
            if (vm.ConnectButtonForeground != StudioPalette.ButtonDarkForeground)
            {
                throw new Exception("ViewModel ConnectButtonForeground default state is not StudioPalette.ButtonDarkForeground!");
            }
            if (vm.TestSignalForeground != StudioPalette.ButtonLightForeground)
            {
                throw new Exception("ViewModel TestSignalForeground default state is not StudioPalette.ButtonLightForeground!");
            }
            if (vm.DesktopCaptureForeground != StudioPalette.ButtonLightForeground)
            {
                throw new Exception("ViewModel DesktopCaptureForeground default state is not StudioPalette.ButtonLightForeground!");
            }
            if (!vm.IsRecordingConfigEnabled || !vm.IsMainContentEnabled)
            {
                throw new Exception("ViewModel IsRecordingConfigEnabled and IsMainContentEnabled should default to true!");
            }

            // Test ToggleTestSignalCommand (Turn ON)
            vm.ToggleTestSignalCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if (!vm.IsTestSignalActive || !vm.IsConnected || vm.ConnectButtonText != "Disconnect")
            {
                throw new Exception($"ToggleTestSignalCommand failed to activate test signal! IsActive={vm.IsTestSignalActive}, IsConnected={vm.IsConnected}");
            }
            if (!vm.StatusText.Contains("Internal Test Pattern Generator", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"Unexpected status after starting test signal: '{vm.StatusText}'");
            }
            if (vm.TestSignalForeground != StudioPalette.ButtonWhiteForeground)
            {
                throw new Exception("ViewModel TestSignalForeground active state is not StudioPalette.ButtonWhiteForeground!");
            }
            if (vm.DesktopCaptureForeground != StudioPalette.ButtonLightForeground)
            {
                throw new Exception("ViewModel DesktopCaptureForeground must remain ButtonLightForeground when only TestSignal is active!");
            }
            if (vm.ConnectButtonForeground != StudioPalette.ButtonWhiteForeground)
            {
                throw new Exception("ViewModel ConnectButtonForeground connected state is not StudioPalette.ButtonWhiteForeground!");
            }

            // Test ToggleTestSignalCommand (Turn OFF)
            vm.ToggleTestSignalCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if (vm.IsTestSignalActive || vm.IsConnected || vm.ConnectButtonText != "Connect")
            {
                throw new Exception($"ToggleTestSignalCommand failed to deactivate test signal! IsActive={vm.IsTestSignalActive}, IsConnected={vm.IsConnected}");
            }
            if (vm.TestSignalForeground != StudioPalette.ButtonLightForeground)
            {
                throw new Exception("ViewModel TestSignalForeground inactive state is not StudioPalette.ButtonLightForeground!");
            }
            if (vm.ConnectButtonForeground != StudioPalette.ButtonDarkForeground)
            {
                throw new Exception("ViewModel ConnectButtonForeground disconnected state is not StudioPalette.ButtonDarkForeground!");
            }

            // Test Folder Picker seam & exception resilience
            vm.OutputDirectory = "";
            vm.OpenOutputDirectoryCommand.Execute(null);
            if (!vm.StatusText.Contains("not set", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"OpenOutputDirectoryCommand with empty path should report not set, got: '{vm.StatusText}'");
            }

            vm.PickFolderAsync = _ => throw new InvalidOperationException("Simulated picker error");
            vm.BrowseOutputDirectoryCommand.Execute(null);
            if (!vm.StatusText.Contains("cancelled or failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"BrowseOutputDirectoryCommand failed to catch error gracefully: '{vm.StatusText}'");
            }

            string mockFolder = Path.Combine(Path.GetTempPath(), "OMT_Mock_Capture_Dir");
            vm.PickFolderAsync = _ => Task.FromResult<string?>(mockFolder);
            vm.BrowseOutputDirectoryCommand.Execute(null);
            if (vm.OutputDirectory != mockFolder)
            {
                throw new Exception($"BrowseOutputDirectoryCommand failed to update OutputDirectory! Got: '{vm.OutputDirectory}'");
            }

            // Test audio monitoring toggle
            vm.SetAudioMonitoring(true);
            if (!vmSession.IsAudioMonitoringEnabled)
            {
                throw new Exception("SetAudioMonitoring(true) failed to update session audio monitoring state!");
            }
            vm.SetAudioMonitoring(false);
            if (vmSession.IsAudioMonitoringEnabled)
            {
                throw new Exception("SetAudioMonitoring(false) failed to disable session audio monitoring state!");
            }

            // Test ToggleRecordCommand validation when disconnected
            vm.ToggleRecordCommand.Execute(null);
            if (!vm.StatusText.Contains("Please connect to an active OMT stream"))
            {
                throw new Exception($"ToggleRecordCommand without active stream failed validation: '{vm.StatusText}'");
            }

            // Connect live synthetic pattern generator to verify HUD overlay and live recording
            vm.SetTestSignalActiveAsync(true).GetAwaiter().GetResult();
            if (!vm.IsTestSignalActive || !vm.IsConnected || vm.ConnectButtonText != "Disconnect")
            {
                throw new Exception("Failed to activate test signal generator for recording test!");
            }

            // Wait briefly for synthetic frames to negotiate stream format
            int frameWait = 0;
            while (!vmSession.CurrentFormat.HasVideo && frameWait < 30)
            {
                Thread.Sleep(50);
                frameWait++;
            }
            if (!vmSession.CurrentFormat.HasVideo)
            {
                throw new Exception("Synthetic pattern generator failed to deliver video stream format!");
            }
            if (vm.IsNoSignalOverlayVisible || vm.LiveStatusText != "LIVE")
            {
                throw new Exception($"Video frame HUD overlay failed to transition to LIVE! OverlayVisible={vm.IsNoSignalOverlayVisible}, LiveStatus='{vm.LiveStatusText}'");
            }
            if (!vm.VideoSpecsText.Contains("1920x1080"))
            {
                throw new Exception($"VideoSpecsText does not reflect synthetic 1080p stream! Got: '{vm.VideoSpecsText}'");
            }

            // Test empty output directory pre-flight validation while connected
            vm.OutputDirectory = "   ";
            vm.ToggleRecordCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if (!vm.StatusText.Contains("valid destination folder", StringComparison.OrdinalIgnoreCase) || vm.IsRecording)
            {
                throw new Exception($"ToggleRecordCommand with empty directory failed pre-flight validation: '{vm.StatusText}'");
            }

            // Test active recording lifecycle through ViewModel
            vm.OutputDirectory = Path.GetTempPath();
            vm.ToggleRecordCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if (!vm.IsRecording || !vm.RecordButtonText.Contains("STOP RECORDING"))
            {
                throw new Exception($"ToggleRecordCommand failed to start recording! IsRecording={vm.IsRecording}, BtnText='{vm.RecordButtonText}', Status='{vm.StatusText}'");
            }
            if (vm.IsRecordingConfigEnabled)
            {
                throw new Exception("IsRecordingConfigEnabled must be false while recording is active!");
            }

            // Test close protection modal while active recording is in flight
            vm.IsRecordingCloseOverlayVisible = true;
            if (vm.IsMainContentEnabled)
            {
                throw new Exception("IsMainContentEnabled must be false while RecordingCloseOverlay is visible!");
            }
            vm.KeepRecordingCommand.Execute(null);
            if (vm.IsRecordingCloseOverlayVisible || vm.IsSafeExitConfirmed || !vm.IsMainContentEnabled)
            {
                throw new Exception("KeepRecordingCommand failed to dismiss modal, restore IsMainContentEnabled, or left IsSafeExitConfirmed true!");
            }

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
            if (!vm.IsSafeExitConfirmed || !closeRequested || vm.IsRecording || !vm.IsRecordingConfigEnabled)
            {
                throw new Exception($"StopAndExitCommand failed safe recording finalization! Confirmed={vm.IsSafeExitConfirmed}, CloseReq={closeRequested}, IsRec={vm.IsRecording}, ConfigEnabled={vm.IsRecordingConfigEnabled}");
            }

            // Deactivate test signal generator and verify offline state
            vm.SetTestSignalActiveAsync(false).GetAwaiter().GetResult();
            if (vm.IsTestSignalActive || vm.IsConnected || vm.ConnectButtonText != "Connect" || !vm.IsNoSignalOverlayVisible || vm.LiveStatusText != "OFFLINE")
            {
                throw new Exception($"Deactivating test signal failed to restore offline state! IsActive={vm.IsTestSignalActive}, Live={vm.LiveStatusText}");
            }
            if (vm.AudioTelemetryText != "Audio: --" || vm.BitrateText != "Bitrate: 0.0 Mbps" || vm.DroppedFramesText != "Dropped: 0" || vm.SourceNameText != "")
            {
                throw new Exception($"Deactivating test signal failed to reset telemetry! Audio='{vm.AudioTelemetryText}', Bitrate='{vm.BitrateText}', Dropped='{vm.DroppedFramesText}', Source='{vm.SourceNameText}'");
            }

            // Test OmtSourceInfo equality
            var srcA = OmtSourceInfo.Parse("PC (Camera 1)");
            var srcB = OmtSourceInfo.Parse("PC (Camera 1)");
            var srcC = OmtSourceInfo.Parse("PC (Camera 2)");
            if (!srcA.Equals(srcB) || srcA != srcB || srcA == srcC || srcA.GetHashCode() != srcB.GetHashCode())
            {
                throw new Exception("OmtSourceInfo equality implementation failed!");
            }

            // Test Dispose idempotency & seam cleanup
            vm.PickFolderAsync = _ => Task.FromResult<string?>("mock");
            vm.Dispose();
            if (vm.PickFolderAsync != null)
            {
                throw new Exception("PickFolderAsync hook was not cleared on Dispose!");
            }
            vm.Dispose();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" -> MainWindowViewModel MVVM patterns, bindings, commands, and seams verified!");
            Console.ResetColor();
        }

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
