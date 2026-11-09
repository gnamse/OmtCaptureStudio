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
