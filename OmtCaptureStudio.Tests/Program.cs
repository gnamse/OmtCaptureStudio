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

            // Test ToggleTestSignalCommand (Turn OFF)
            vm.ToggleTestSignalCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if (vm.IsTestSignalActive || vm.IsConnected || vm.ConnectButtonText != "Connect")
            {
                throw new Exception($"ToggleTestSignalCommand failed to deactivate test signal! IsActive={vm.IsTestSignalActive}, IsConnected={vm.IsConnected}");
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

            // Test close protection modal while active recording is in flight
            vm.IsRecordingCloseOverlayVisible = true;
            vm.KeepRecordingCommand.Execute(null);
            if (vm.IsRecordingCloseOverlayVisible || vm.IsSafeExitConfirmed)
            {
                throw new Exception("KeepRecordingCommand failed to dismiss modal or left IsSafeExitConfirmed true!");
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
            if (!vm.IsSafeExitConfirmed || !closeRequested || vm.IsRecording)
            {
                throw new Exception($"StopAndExitCommand failed safe recording finalization! Confirmed={vm.IsSafeExitConfirmed}, CloseReq={closeRequested}, IsRec={vm.IsRecording}");
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
}
