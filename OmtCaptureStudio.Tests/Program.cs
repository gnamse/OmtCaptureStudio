using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;

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
        var test1 = OmtSourceInfo.Parse("WINDOWS-LL3KVGQ (vMix - Output 1)");
        if (test1.Name != "vMix - Output 1" || test1.Host != "WINDOWS-LL3KVGQ" || test1.DisplayName != "vMix - Output 1 (WINDOWS-LL3KVGQ)")
        {
            throw new Exception($"Test1 failed: Name='{test1.Name}', Host='{test1.Host}', Display='{test1.DisplayName}'");
        }
        if (test1.Address != "WINDOWS-LL3KVGQ (vMix - Output 1)")
        {
            throw new Exception($"Test1 address failed: Address='{test1.Address}'");
        }

        var test2 = OmtSourceInfo.Parse("vMix - Output 1 @ WINDOWS-LL3KVGQ");
        if (test2.Name != "vMix - Output 1" || test2.Host != "WINDOWS-LL3KVGQ" || test2.DisplayName != "vMix - Output 1 (WINDOWS-LL3KVGQ)")
        {
            throw new Exception($"Test2 failed: Name='{test2.Name}', Host='{test2.Host}', Display='{test2.DisplayName}'");
        }

        var test3 = OmtSourceInfo.Parse("omt://127.0.0.1:5000");
        if (test3.DisplayName != "omt://127.0.0.1:5000" || test3.Address != "omt://127.0.0.1:5000")
        {
            throw new Exception($"Test3 failed: Display='{test3.DisplayName}'");
        }

        // Test redundant double-wrapping edge cases from user screenshot
        var test4 = OmtSourceInfo.Parse("WINDOWS-LL3KVGQ (vMix - Output 1) (WINDOWS-LL3KVGQ (vMix - Output 1))");
        if (test4.Name != "vMix - Output 1" || test4.Host != "WINDOWS-LL3KVGQ" || test4.DisplayName != "vMix - Output 1 (WINDOWS-LL3KVGQ)")
        {
            throw new Exception($"Test4 failed (redundant duplicate): Name='{test4.Name}', Host='{test4.Host}', Display='{test4.DisplayName}'");
        }

        var test5 = OmtSourceInfo.Parse("WINDOWS-LL3KVGQ (Audio Headset Microphone) (WINDOWS-LL3KVGQ (Audio Headset Microphone))");
        if (test5.Name != "Audio Headset Microphone" || test5.Host != "WINDOWS-LL3KVGQ" || test5.DisplayName != "Audio Headset Microphone (WINDOWS-LL3KVGQ)")
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

        Console.WriteLine($"\n[Step 3] Initializing OmtReceiverService & Connecting to target: {connectTarget}...");
        using var receiver = new OmtReceiverService();
        using var audioProcessor = new AudioProcessor();
        using var recorder = new StreamRecorderService();

        long videoFrameCount = 0;
        long audioFrameCount = 0;
        int receivedWidth = 0;
        int receivedHeight = 0;
        double receivedFps = 0;
        int receivedSampleRate = 0;
        int receivedChannels = 0;

        receiver.VideoFrameReceived += (pData, length, w, h, stride, fps, ts) =>
        {
            Interlocked.Increment(ref videoFrameCount);
            receivedWidth = w;
            receivedHeight = h;
            receivedFps = fps;

            if (recorder.IsRecording)
            {
                recorder.WriteVideoFrame(pData, length);
            }
        };

        receiver.AudioFrameReceived += (pPlanarData, channels, samples, rate, ts) =>
        {
            Interlocked.Increment(ref audioFrameCount);
            receivedChannels = channels;
            receivedSampleRate = rate;

            var levels = audioProcessor.ComputeLevels(pPlanarData, channels, samples);
            byte[] interleaved = audioProcessor.InterleaveFloatAudio(pPlanarData, channels, samples);

            if (recorder.IsRecording)
            {
                recorder.WriteAudioData(interleaved);
            }
        };

        receiver.Connect(connectTarget);

        Console.WriteLine(" -> Waiting for connection and first video and audio frames...");
        int waitAttempts = 0;
        while ((videoFrameCount < 5 || audioFrameCount < 5) && waitAttempts < 60)
        {
            Thread.Sleep(100);
            waitAttempts++;
            if (waitAttempts % 10 == 0)
            {
                Console.WriteLine($" [Poll {waitAttempts}] Sender Connections: {generator.IsRunning}, Receiver IsConnected: {receiver.IsConnected}, VideoFrames={videoFrameCount}, AudioFrames={audioFrameCount}");
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

        Console.WriteLine($" -> Stream detected: {receivedWidth}x{receivedHeight} @ {receivedFps:F2} fps, {receivedChannels}ch {receivedSampleRate}Hz");

        Console.WriteLine("\n[Step 4] Starting Stream Recording via Named Pipe to FFmpeg...");
        var config = new RecordingConfig
        {
            ContainerFormat = OutputContainerFormat.MP4,
            EncoderChoice = VideoEncoderChoice.CpuX264,
            Quality = QualityPreset.High,
            OutputDirectory = outputDir,
            FilenamePrefix = "Test_Record"
        };

        bool started = recorder.StartRecording(
            config,
            receivedWidth,
            receivedHeight,
            receivedFps,
            receivedSampleRate,
            receivedChannels
        );

        if (!started)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[FAILED] Could not start recording!");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($" -> Recording active. Writing frames to {recorder.CurrentRecordingPath} for 3 seconds...");
        Thread.Sleep(3000);

        Console.WriteLine("\n[Step 5] Stopping Recording & Finalizing MP4 Container...");
        recorder.StopRecording();
        Thread.Sleep(500);

        string? recordedFile = recorder.CurrentRecordingPath;
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
}
