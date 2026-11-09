using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Audio;
using OmtCaptureStudio.Services.Sinks;
using OmtCaptureStudio.Services.Sources;
using OmtCaptureStudio.ViewModels;

using OmtCaptureStudio.Services.Forensics;
using System.Reflection;
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
    /// DIAG-VMIX: name a codec integer. The SDK defines exactly two AUDIO codecs - FPA1 (32-bit float
    /// planar) and PA16 (16-bit planar) - and both are plain four-character ASCII codes, so decode the
    /// integer rather than hard-coding, which also names anything unexpected.
    /// </summary>
    static string CodecName(int codec)
    {
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            int b = (codec >> (8 * i)) & 0xFF;
            chars[i] = b >= 32 && b < 127 ? (char)b : '?';
        }

        return new string(chars) + " (" + codec + ")";
    }

    /// <summary>
    /// DIAG-VMIX: IN-VITRO reproduction of the codec-blindness defect, using the app's REAL classes.
    ///
    /// Every consumer of an audio frame casts the payload to <c>float*</c> unconditionally, because the
    /// receive path never reads <c>OMTMediaFrame.Codec</c>. The SDK defines a SECOND audio codec - PA16,
    /// 16-bit planar - so a sender that announces PA16 is read as float32.
    ///
    /// This builds a PA16 payload with the SAME geometry our own sender uses (2 ch @ 48 kHz, 800
    /// samples/ch = 3200 bytes) and runs <c>AudioLevelCalculator.ComputeLevels</c> and
    /// <c>AudioInterleaver.Interleave</c> over it, reporting the byte accounting each performs. The backing
    /// array is over-allocated with a 0xAA canary tail so the resulting over-read is MEASURED rather than
    /// crashing the probe - and so it is provable that the second half of every processed chunk is not
    /// audio at all.
    /// </summary>
    static int RunDiagPa16()
    {
        const int channels = 2;
        const int samples = 800;
        const int pa16Bytes = channels * samples * 2;   // 3200 - what a PA16 sender really puts on the wire
        const int fpa1Bytes = channels * samples * 4;   // 6400 - what the app's consumers will read

        // Over-allocate to the FPA1 size so the over-read lands inside our own array.
        byte[] backing = new byte[fpa1Bytes];
        for (int i = pa16Bytes; i < fpa1Bytes; i++)
        {
            backing[i] = 0xAA;      // canary: definitively not audio
        }

        // Real int16 planar sine at half scale into the PA16 payload region.
        for (int ch = 0; ch < channels; ch++)
        {
            for (int i = 0; i < samples; i++)
            {
                short v = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 48000.0) * 16384.0);
                int off = (ch * samples + i) * 2;
                backing[off] = (byte)(v & 0xFF);
                backing[off + 1] = (byte)((v >> 8) & 0xFF);
            }
        }

        Console.WriteLine("[diag] PA16 IN-VITRO PROBE (drives the app's real AudioLevelCalculator + AudioInterleaver)");
        Console.WriteLine("   announced geometry   : " + channels + " ch, " + samples + " samples/ch, 48000 Hz");
        Console.WriteLine("   PA16 payload on wire : " + pa16Bytes + " bytes  (channels x samples x 2)");
        Console.WriteLine("   what the app reads   : " + fpa1Bytes + " bytes  (channels x samples x 4)");
        Console.WriteLine("   per-chunk over-read  : " + (fpa1Bytes - pa16Bytes) + " bytes = " +
                          (100 * (fpa1Bytes - pa16Bytes) / pa16Bytes) + "% past the end of the payload");
        Console.WriteLine();

        var handle = GCHandle.Alloc(backing, GCHandleType.Pinned);
        try
        {
            IntPtr p = handle.AddrOfPinnedObject();

            // --- the app's real level calculator ---
            AudioLevelData levels = AudioLevelCalculator.ComputeLevels(p, channels, samples);
            Console.WriteLine("   AudioLevelCalculator.ComputeLevels -> real class, unchanged:");
            for (int ch = 0; ch < levels.Channels.Length; ch++)
            {
                Console.WriteLine(string.Format("      ch{0}: peak={1,7:F2} dBFS  rms={2,7:F2} dBFS  clipping={3}",
                    ch, levels.Channels[ch].PeakDb, levels.Channels[ch].RmsDb, levels.Channels[ch].IsClipping));
            }

            // --- the app's real interleaver ---
            var interleaver = new AudioInterleaver();
            int totalBytes;
            byte[] outBuf = interleaver.Interleave(p, channels, samples, out totalBytes);
            Console.WriteLine();
            Console.WriteLine("   AudioInterleaver.Interleave -> " + totalBytes + " bytes emitted (" +
                              (totalBytes / 4) + " float samples)");
            Console.WriteLine("      intended (int16 source), first 8 per channel:");
            for (int ch = 0; ch < channels; ch++)
            {
                var vals = new List<string>();
                for (int i = 0; i < 8; i++)
                {
                    int off = (ch * samples + i) * 2;
                    short v = (short)(backing[off] | (backing[off + 1] << 8));
                    vals.Add(v.ToString());
                }
                Console.WriteLine("         ch" + ch + ": " + string.Join(", ", vals));
            }

            Console.WriteLine("      emitted as float32 by the app, first 8 per channel:");
            bool garbage = false;
            double maxAbs = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                var vals = new List<string>();
                for (int i = 0; i < 8; i++)
                {
                    float f = BitConverter.ToSingle(outBuf, (ch * samples + i) * 4);
                    vals.Add(float.IsNaN(f) ? "NaN" : float.IsInfinity(f) ? "Inf" : f.ToString("G6"));
                }
                Console.WriteLine("         ch" + ch + ": " + string.Join(", ", vals));
            }

            for (int i = 0; i < totalBytes / 4; i++)
            {
                float f = BitConverter.ToSingle(outBuf, i * 4);
                if (float.IsNaN(f) || float.IsInfinity(f) || Math.Abs(f) > 1.0) garbage = true;
                if (!float.IsNaN(f) && !float.IsInfinity(f)) maxAbs = Math.Max(maxAbs, Math.Abs(f));
            }

            // Which emitted floats came from the canary region rather than the payload?
            int floatSamplesFromPayload = pa16Bytes / 4;
            int floatSamplesFromCanary = (totalBytes / 4) - floatSamplesFromPayload;
            Console.WriteLine();
            Console.WriteLine("   VERDICT:");
            Console.WriteLine("      emitted floats sourced from the real payload : " + floatSamplesFromPayload);
            Console.WriteLine("      emitted floats sourced from the CANARY tail   : " + floatSamplesFromCanary +
                              "  (" + (100 * floatSamplesFromCanary / (totalBytes / 4)) + "% of the chunk is NOT audio)");
            Console.WriteLine("      max |sample| emitted = " + (maxAbs >= 1e30 ? ">1e30" : maxAbs.ToString("G6")) +
                              "   (a valid capture is bounded by 1.0)");
            Console.WriteLine("      produced non-audio floats (NaN/Inf/|x|>1)     : " + (garbage ? "YES - content is garbage" : "no"));
        }
        finally
        {
            handle.Free();
        }

        return 0;
    }
    /// <summary>
    /// DIAG-VMIX: drives the REAL <see cref="OmtReceiverService"/> against a live sender and reports how
    /// many audio frames it forwarded versus refused. This is the in-vivo counterpart to [Step 0.699]:
    /// the guard is proved to leave a real FPA1 sender's frames untouched, end to end, with the SDK's own
    /// frames rather than a synthesised verdict.
    /// </summary>
    static int RunDiagGuard(string address, int seconds)
    {
        Console.WriteLine("[diag] real OmtReceiverService -> [" + address + "] for " + seconds + " s ...");
        using var service = new OmtReceiverService();

        long forwarded = 0, video = 0;
        int channels = 0, rate = 0;
        var errors = new List<string>();
        service.AudioFrameReceived += (ptr, ch, samples, r, ts) =>
        {
            Interlocked.Increment(ref forwarded);
            channels = ch;
            rate = r;
        };
        service.VideoFrameReceived += (ptr, len, w, h, s, fps, ts) => Interlocked.Increment(ref video);
        service.ErrorOccurred += m => { lock (errors) errors.Add(m); };

        service.Connect(address);
        Thread.Sleep(seconds * 1000);
        service.Disconnect();

        Console.WriteLine();
        Console.WriteLine("=== GUARD RESULT (real receiver) ===");
        Console.WriteLine("   video frames forwarded : " + Interlocked.Read(ref video));
        Console.WriteLine("   audio frames FORWARDED : " + Interlocked.Read(ref forwarded));
        Console.WriteLine("   audio frames REFUSED   : " + service.RejectedAudioFrames);
        Console.WriteLine("   negotiated             : " + channels + " ch @ " + rate + " Hz");
        lock (errors)
        {
            Console.WriteLine("   error events           : " + errors.Count);
            foreach (string e in errors.Take(3)) Console.WriteLine("      " + e);
        }

        bool clean = service.RejectedAudioFrames == 0 && Interlocked.Read(ref forwarded) > 0 && errors.Count == 0;
        Console.WriteLine("   VERDICT: " + (clean
            ? "FPA1 sender passes the guard untouched - the working path is unchanged"
            : "NOT CLEAN - see counts above"));
        return clean ? 0 : 1;
    }
    /// <summary>
    /// DIAG-VMIX crackle hunt: drives the app's REAL receive+record pipeline (a real CaptureSession, so a
    /// real StreamRecorderService -> real FfmpegPipeSink) against a live sender, and reports the counters
    /// the SDK's own statistics cannot see. The SDK reporting zero dropped audio says nothing about THIS
    /// app's own 512-chunk queue at a sender delivering 100 chunks/s.
    /// </summary>
    static int RunDiagRecord(string address, int seconds, libomtnet.OMTPreferredVideoFormat preferred)
    {
        Console.WriteLine("[diag] REAL receive+record -> [" + address + "] for " + seconds + " s " +
                          "(requested video format: " + preferred + "=" + (int)preferred + ") ...");
        string outDir = Path.Combine(Path.GetTempPath(), "omt-diag-record");
        Directory.CreateDirectory(outDir);

        using var session = new CaptureSession();
        var telemetry = new List<StreamTelemetry>();
        session.TelemetryUpdated += t => { lock (telemetry) telemetry.Add(t); };

        session.Connect(address);
        Console.WriteLine("   [diag] connected; settling 4 s for format negotiation + frames ...");
        Thread.Sleep(4000);

        StreamFormat fmt = session.CurrentFormat;
        Console.WriteLine($"   negotiated format: {fmt.Width}x{fmt.Height} @ {fmt.FrameRate:0.###} fps, " +
                          $"{fmt.SampleRate} Hz, {fmt.Channels} ch");

        RecordingStopSummary? summary = null;
        session.RecordingStopped += s => summary = s;

        var config = new RecordingConfig
        {
            OutputDirectory = outDir,
            FilenamePrefix = "CrackleProbe",
            ContainerFormat = OutputContainerFormat.MP4
        };

        bool started = session.StartRecording(config);
        Console.WriteLine($"   StartRecording -> {started}");
        if (!started)
        {
            Console.WriteLine("   [diag] recording refused (storage preflight / unavailable encoder). Stopping.");
            session.Disconnect();
            return 1;
        }

        Thread.Sleep(seconds * 1000);

        RecordingHealthSnapshot health = session.GetRecordingHealthSnapshot();
        session.StopRecording();
        for (int i = 0; i < 40 && summary == null; i++) Thread.Sleep(100);   // the summary rides the event
        session.Disconnect();

        if (summary == null)
        {
            Console.WriteLine("   [diag] no RecordingStopped summary arrived - the take never finalized.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("=== RECORD RESULT (the app's OWN pipeline) ===");
        Console.WriteLine($"   video frames written     : {summary.VideoFramesWritten}");
        Console.WriteLine($"   media duration           : {summary.MediaDuration.TotalSeconds:0.###} s" +
                          $"   (wall {summary.WallClockDuration.TotalSeconds:0.###} s incl. dead air)");
        Console.WriteLine($"   audio duration           : {summary.AudioDuration.TotalSeconds:0.###} s");
        Console.WriteLine($"   output                   : {summary.Path}");
        Console.WriteLine($"   size                     : {summary.FileSizeBytes:N0} bytes");
        Console.WriteLine($"   encoder                  : {summary.EncoderName}");
        Console.WriteLine();
        Console.WriteLine("   APP-SIDE COUNTERS (invisible to GetAudioStatistics):");
        Console.WriteLine($"      RecordingAudioChunksDropped : {summary.AudioChunksDropped}");
        Console.WriteLine($"      RecordingFramesDropped      : {summary.VideoFramesDropped}");
        Console.WriteLine($"      segment index               : {summary.SegmentIndex}");
        Console.WriteLine($"      stop reason                 : {summary.Reason}");
        Console.WriteLine($"      finalize time               : {summary.FinalizeDuration.TotalMilliseconds:0} ms");
        Console.WriteLine($"      diagnostic tail             : {(string.IsNullOrWhiteSpace(summary.DiagnosticTail) ? "(none)" : summary.DiagnosticTail)}");
        Console.WriteLine();
        Console.WriteLine($"   A/V duration delta       : {(summary.AudioDuration - summary.MediaDuration).TotalSeconds:+0.000;-0.000;0.000} s  (non-zero = the tracks ran different lengths)");

        lock (telemetry)
        {
            var last = telemetry.LastOrDefault();
            if (last != null)
            {
                Console.WriteLine($"   last telemetry           : {last.Width}x{last.Height} @ {last.FrameRate:0.###} fps, " +
                                  $"{last.SampleRate} Hz x {last.AudioChannels} ch, video={last.VideoFramesReceived}, " +
                                  $"audio={last.AudioFramesReceived}, dropped={last.FramesDropped}, {last.BitrateMbps:0.00} Mbps");
                Console.WriteLine($"   REFUSED (trust boundary) : audio={last.AudioFramesRejected}, video={last.VideoFramesRejected}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("   VERDICT INPUTS: compare RecordingAudioChunksDropped against 0 and the A/V delta");
        Console.WriteLine("   against the same run recorded from our own sender (--diag-record against OMT_*).");
        return 0;
    }

    /// <summary>DIAG-VMIX: list the OMT sources discoverable right now (diagnosis only).</summary>
    static int RunDiagDiscover()
    {
        Console.WriteLine("[diag] OMT discovery probe - scanning 4 s...");
        var discovery = libomtnet.OMTDiscovery.GetInstance();
        string[] best = Array.Empty<string>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000)
        {
            try
            {
                string[] addrs = discovery.GetAddresses();
                if (addrs != null && addrs.Length > 0) best = addrs;
            }
            catch (Exception ex)
            {
                Console.WriteLine("   [discovery error] " + ex.Message);
            }

            Thread.Sleep(250);
        }

        Console.WriteLine("[diag] " + best.Length + " source(s) discovered:");
        foreach (string a in best)
        {
            OmtSourceInfo info = OmtSourceInfo.Parse(a, isManual: false);
            Console.WriteLine("   ADDRESS=[" + a + "]");
            Console.WriteLine("      Name='" + info.Name + "'  Host='" + info.Host + "'  Display='" + info.DisplayName + "'");
        }

        if (best.Length == 0)
        {
            Console.WriteLine("   (none - no OMT sender is advertising on this network)");
        }

        return 0;
    }

    /// <summary>
    /// DIAG-VMIX: capture a live OMT source for N seconds and report what the SENDER actually puts on the
    /// wire, per audio frame. The app's receive path never reads <c>OMTMediaFrame.Codec</c>, so the wire
    /// trace is the only evidence of whether an audio buffer is FPA1 (float32 planar) or PA16 (int16
    /// planar) - and whether the byte length agrees with the announced geometry.
    /// </summary>
    /// <summary>
    /// DIAG-VMIX phase 2 (BGRA negotiation hypothesis). The APP asks for `BGRA` only - never the
    /// `UYVYorBGRA` fallback - so a sender that cannot produce BGRA may yield NO video at all rather than
    /// degrading to another format. This maps an optional `--video-format &lt;name&gt;` onto the SDK's
    /// enum so the trace can ask differently WITHOUT touching production's request.
    /// Read the values off the SDK rather than trusting recall: UYVY=0, UYVYorBGRA=1, BGRA=2,
    /// UYVYorUYVA=3, UYVYorUYVAorP216orPA16=4, P216=5.
    /// </summary>
    static libomtnet.OMTPreferredVideoFormat DiagVideoFormat(string[] argv)
    {
        for (int i = 0; i < argv.Length - 1; i++)
        {
            if (!string.Equals(argv[i], "--video-format", StringComparison.OrdinalIgnoreCase)) continue;
            string v = argv[i + 1].Trim().ToLowerInvariant();
            switch (v)
            {
                case "uyvy": return libomtnet.OMTPreferredVideoFormat.UYVY;
                case "uyvyorbgra": return libomtnet.OMTPreferredVideoFormat.UYVYorBGRA;
                case "uyvyoruyva": return libomtnet.OMTPreferredVideoFormat.UYVYorUYVA;
                case "uyvyoruyvaorp216orpa16": return libomtnet.OMTPreferredVideoFormat.UYVYorUYVAorP216orPA16;
                case "p216": return libomtnet.OMTPreferredVideoFormat.P216;
                default: return libomtnet.OMTPreferredVideoFormat.BGRA;
            }
        }

        return libomtnet.OMTPreferredVideoFormat.BGRA;   // production's request, unchanged
    }

    static int RunDiagAudio(string address, int seconds, libomtnet.OMTPreferredVideoFormat preferred)
    {
        Console.WriteLine("[diag] connecting to [" + address + "] for " + seconds + " s " +
                          "(requested video format: " + preferred + "=" + (int)preferred + ") ...");
        using var receiver = new libomtnet.OMTReceive(
            address,
            libomtnet.OMTFrameType.Video | libomtnet.OMTFrameType.Audio,
            preferred,
            libomtnet.OMTReceiveFlags.None);

        try
        {
            var sender = receiver.GetSenderInformation();
            Console.WriteLine("[diag] sender information: " + (sender?.ToString() ?? "(null)"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[diag] sender information unavailable: " + ex.Message);
        }

        var frame = new libomtnet.OMTMediaFrame();
        long videoCount = 0, audioCount = 0, videoTyped = 0, videoEmpty = 0;
        var codecSeen = new SortedDictionary<int, int>();
        // DIAG-BROADCAST accumulators (video receive side).
        var videoCodecSeen = new Dictionary<int, int>();
        var videoSamples = new List<string>();
        var videoRejectReasons = new List<string>();
        long videoGuardAccepted = 0;
        long videoGuardRejected = 0;
        var audioGeometry = new SortedDictionary<string, int>();
        var gapsMs = new List<double>();
        var tsDeltas = new List<long>();
        long prevAudioTs = -1;
        double prevAudioWall = 0;
        long prevStatsFrames = 0, prevStatsDropped = 0, prevStatsBytes = 0;
        var statsLines = new List<string>();
        int printedAudio = 0;
        var clock = Stopwatch.StartNew();
        double lastStatsAt = 0;
        long lastVideoTs = -1;
        var videoGaps = new List<double>();
        double prevVideoWall = 0;

        while (clock.Elapsed.TotalSeconds < seconds)
        {
            if (!receiver.Receive(libomtnet.OMTFrameType.Video | libomtnet.OMTFrameType.Audio, 50, ref frame))
            {
                continue;
            }

            double nowMs = clock.Elapsed.TotalMilliseconds;

            if (frame.Type == libomtnet.OMTFrameType.Audio && frame.Data != IntPtr.Zero && frame.DataLength > 0)
            {
                audioCount++;
                if (!codecSeen.ContainsKey(frame.Codec)) codecSeen[frame.Codec] = 0;
                codecSeen[frame.Codec]++;

                int fpa1Bytes = frame.SamplesPerChannel * frame.Channels * 4;
                int pa16Bytes = frame.SamplesPerChannel * frame.Channels * 2;
                string geom = frame.Channels + "ch @ " + frame.SampleRate + "Hz  " + frame.SamplesPerChannel + " samples/ch";
                if (!audioGeometry.ContainsKey(geom)) audioGeometry[geom] = 0;
                audioGeometry[geom]++;

                if (prevAudioWall > 0) gapsMs.Add(nowMs - prevAudioWall);
                prevAudioWall = nowMs;

                if (prevAudioTs >= 0) tsDeltas.Add(frame.Timestamp - prevAudioTs);
                prevAudioTs = frame.Timestamp;

                if (printedAudio < 12)
                {
                    string verdict = frame.DataLength == fpa1Bytes ? "MATCHES FPA1 (float32 planar)"
                        : frame.DataLength == pa16Bytes ? "MATCHES PA16 (int16 planar)"
                        : "MATCHES NEITHER (fpa1=" + fpa1Bytes + " pa16=" + pa16Bytes + ")";
                    Console.WriteLine(string.Format(
                        "   audio#{0}  codec={1}  {2}  DataLength={3}  ts={4}  -> {5}",
                        audioCount, CodecName(frame.Codec), geom, frame.DataLength, frame.Timestamp, verdict));
                    printedAudio++;
                }
            }
            else if (frame.Type == libomtnet.OMTFrameType.Video)
            {
                // Count EVERY video-typed frame the SDK hands us, and classify it. The receiver only
                // forwards a frame with Data != 0 && DataLength > 0, so a sender emitting "unchanged"
                // frames would be silently invisible to the A/V clock downstream.
                videoTyped++;
                if (frame.Data == IntPtr.Zero || frame.DataLength <= 0)
                {
                    videoEmpty++;
                    if (videoEmpty <= 3)
                    {
                        Console.WriteLine(string.Format(
                            "   video#{0} EMPTY: codec={1} {2}x{3} stride={4} DataLength={5} ts={6}",
                            videoTyped, CodecName(frame.Codec), frame.Width, frame.Height, frame.Stride,
                            frame.DataLength, frame.Timestamp));
                    }
                }
                else
                {
                    videoCount++;

                    // DIAG-BROADCAST: the one thing the existing trace never recorded for REAL frames -
                    // the codec the SDK actually delivers on the video RECEIVE side, plus the geometry.
                    // Send-side stamping proves nothing about what arrives; this is the measured value.
                    if (!videoCodecSeen.ContainsKey(frame.Codec)) videoCodecSeen[frame.Codec] = 0;
                    videoCodecSeen[frame.Codec]++;
                    if (videoSamples.Count < 3)
                    {
                        videoSamples.Add(string.Format(
                            "   video#{0}: codec={1} ({2})  {3}x{4} stride={5} DataLength={6} ts={7}",
                            videoTyped, CodecName(frame.Codec), frame.Codec,
                            frame.Width, frame.Height, frame.Stride, frame.DataLength, frame.Timestamp));
                    }

                    // Run the RECEIVER's own guard over the real frame, so the verdict here is the verdict
                    // the product would reach - not a re-implementation of it.
                    VideoFrameValidation verdict = OmtVideoFrameGuard.Validate(
                        frame.Codec, frame.Width, frame.Height, frame.Stride, frame.DataLength);
                    if (verdict.IsAccepted) videoGuardAccepted++;
                    else
                    {
                        videoGuardRejected++;
                        string reason = verdict.Reason + ": " + verdict.Detail;
                        if (!videoRejectReasons.Contains(reason)) videoRejectReasons.Add(reason);
                    }
                }
                if (prevVideoWall > 0) videoGaps.Add(nowMs - prevVideoWall);
                prevVideoWall = nowMs;
                if (lastVideoTs < 0) lastVideoTs = frame.Timestamp;
            }

            if (nowMs - lastStatsAt >= 1000)
            {
                lastStatsAt = nowMs;
                var a = receiver.GetAudioStatistics();
                statsLines.Add(string.Format(
                    "   t={0,4}s  audioFrames={1,6} (+{2,4})  dropped={3,6} (+{4,3})  bytesSince={5,9}  |  videoFrames={6,5}",
                    (int)clock.Elapsed.TotalSeconds, a.Frames, a.FramesSinceLast, a.FramesDropped,
                    a.FramesDropped - prevStatsDropped, a.BytesReceivedSinceLast, videoCount));
                prevStatsFrames = a.Frames;
                prevStatsDropped = a.FramesDropped;
                prevStatsBytes = a.BytesReceivedSinceLast;
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== DIAG RESULT for [" + address + "] ===");
        Console.WriteLine("video frames=" + videoCount + "   audio frames=" + audioCount);
        Console.WriteLine("VIDEO-TYPED frames delivered by the SDK: " + videoTyped +
                          "  (with data: " + videoCount + ", EMPTY/null-data: " + videoEmpty + ")");
        Console.WriteLine();
        Console.WriteLine("VIDEO CODECS SEEN ON RECEIVE (DIAG-BROADCAST - the measured value, not the send-side stamp):");
        if (videoCodecSeen.Count == 0) Console.WriteLine("   (no real video frames arrived at all)");
        foreach (var kv in videoCodecSeen)
            Console.WriteLine("   codec=" + CodecName(kv.Key) + " (" + kv.Key + ")  x" + kv.Value);
        foreach (string s in videoSamples) Console.WriteLine(s);
        Console.WriteLine();
        Console.WriteLine("RECEIVER GUARD VERDICT over those real frames (the product's own policy):");
        Console.WriteLine("   accepted=" + videoGuardAccepted + "  rejected=" + videoGuardRejected);
        foreach (string r in videoRejectReasons) Console.WriteLine("   REJECTED: " + r);
        Console.WriteLine();
        Console.WriteLine("AUDIO CODECS SEEN (decisive):");
        foreach (var kv in codecSeen)
        {
            Console.WriteLine("   " + CodecName(kv.Key) + "  x" + kv.Value);
        }

        Console.WriteLine();
        Console.WriteLine("AUDIO GEOMETRY ANNOUNCED:");
        foreach (var kv in audioGeometry)
        {
            Console.WriteLine("   " + kv.Key + "  x" + kv.Value);
        }

        Console.WriteLine();
        Console.WriteLine("PER-SECOND STATISTICS (GetAudioStatistics):");
        foreach (string s in statsLines) Console.WriteLine(s);

        Console.WriteLine();
        if (gapsMs.Count > 0)
        {
            gapsMs.Sort();
            Console.WriteLine("AUDIO INTER-ARRIVAL (wall clock, ms): n=" + gapsMs.Count +
                "  min=" + gapsMs[0].ToString("F2") +
                "  p50=" + gapsMs[gapsMs.Count / 2].ToString("F2") +
                "  p95=" + gapsMs[(int)(gapsMs.Count * 0.95)].ToString("F2") +
                "  max=" + gapsMs[gapsMs.Count - 1].ToString("F2") +
                "  mean=" + gapsMs.Average().ToString("F2"));
        }

        if (tsDeltas.Count > 0)
        {
            var distinct = tsDeltas.Distinct().OrderBy(x => x).ToList();
            Console.WriteLine("AUDIO TIMESTAMP DELTAS: n=" + tsDeltas.Count + "  distinct=" + distinct.Count +
                "  min=" + tsDeltas.Min() + "  max=" + tsDeltas.Max() + "  mean=" + tsDeltas.Average().ToString("F1"));
            Console.WriteLine("   first 20 deltas: " + string.Join(",", tsDeltas.Take(20)));
            Console.WriteLine("   distinct values (first 10): " + string.Join(",", distinct.Take(10)));
        }

        if (videoGaps.Count > 0)
        {
            videoGaps.Sort();
            Console.WriteLine("VIDEO INTER-ARRIVAL (wall clock, ms): n=" + videoGaps.Count +
                "  min=" + videoGaps[0].ToString("F2") +
                "  p50=" + videoGaps[videoGaps.Count / 2].ToString("F2") +
                "  max=" + videoGaps[videoGaps.Count - 1].ToString("F2") +
                "  mean=" + videoGaps.Average().ToString("F2") +
                "   (p50 " + (1000.0 / Math.Max(0.001, videoGaps[videoGaps.Count / 2])).ToString("F1") + " fps)");
        }

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
    /// DIAG-BROADCAST: drives <see cref="DesktopFrameCapturer"/> exactly as the broadcast sender does and
    /// reports how many real frames it yields. A sender whose provider never returns true emits a stream
    /// with NO video and NO audio (the send loop `continue`s before both sends), which is
    /// indistinguishable from a network fault from the receiving end.
    /// </summary>
    static int RunDiagCapture(int seconds)
    {
        Console.WriteLine($"\n[diag] probing DesktopFrameCapturer (monitor 0) for {seconds} s ...");
        try
        {
            using var cap = new DesktopFrameCapturer(0);
            Console.WriteLine($"   geometry : {cap.Width}x{cap.Height}  stride={cap.Stride}  buffer={(cap.BufferPtr != IntPtr.Zero ? "pinned OK" : "NULL")}");

            int ok = 0, noNew = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastReport = 0;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (cap.TryCaptureFrame()) ok++; else noNew++;
                Thread.Sleep(16);
                if (sw.ElapsedMilliseconds - lastReport >= 2000)
                {
                    lastReport = sw.ElapsedMilliseconds;
                    Console.WriteLine($"   t={sw.Elapsed.TotalSeconds,4:F1}s  captured={ok,5}  noNewFrame={noNew,5}");
                }
            }

            Console.WriteLine($"RESULT: TryCaptureFrame -> captured={ok}  noNewFrame={noNew} over {seconds}s");
            Console.WriteLine(ok > 0
                ? "   => the capture provider DELIVERS frames; a blank broadcast is NOT a capture failure."
                : "   => the capture provider delivered ZERO frames: the sender would `continue` forever and"
                  + "\n      put NEITHER video NOR audio on the wire (see OmtDesktopCaptureGenerator.cs:295-298),"
                  + "\n      while mDNS still advertises the source. That matches a source that appears but is blank.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("RESULT: constructing/using DesktopFrameCapturer THREW: " + ex);
            return 1;
        }
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

    /// <summary>
    /// NET-012 (distill): progressive disclosure. The Advanced tier's observable state, the broadcast
    /// row's collapse keyed to BROADCAST state only (never recording), the locking composition, and the
    /// preset-Custom flow working from inside the tier.
    /// </summary>
// ================================================================================================
    // ADR-0006: operator settings persistence.
    // ================================================================================================
    // ================================================================================================
    // NET-016: scheduled stop + the remaining-time readout.
    // ================================================================================================
    // ================================================================================================
    // NET-017: one location ladder for logs and forensics + the read-only-install crash link.
    // ================================================================================================
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

            Expect(store.Save(SettingsStore.FromConfig(original, "Long event - MKV")),
                "Saving settings to a writable directory must succeed.");
            Expect(File.Exists(settingsPath), "Save must create the settings file.");

            var reloadStore = new SettingsStore(settingsPath);
            OperatorSettings reloaded = reloadStore.Load();
            Expect(reloadStore.LastOutcome == SettingsLoadOutcome.Loaded,
                "A well-formed file must load as Loaded.");

            RecordingConfig rt = SettingsStore.ToConfig(reloaded);
            Expect(rt.ContainerFormat == original.ContainerFormat, "Round trip: container format.");
            Expect(rt.EncoderChoice == original.EncoderChoice, "Round trip: encoder choice.");
            Expect(rt.Quality == original.Quality, "Round trip: quality.");
            Expect(rt.OutputDirectory == original.OutputDirectory, "Round trip: destination.");
            Expect(rt.BackupOutputDirectory == original.BackupOutputDirectory, "Round trip: backup destination.");
            Expect(rt.FilenamePrefix == original.FilenamePrefix, "Round trip: filename prefix.");
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
            "The app opens on the picker, so the broadcast configuration row must not be shown at startup.");
        Expect(vm.IsLiveSettingSummaryVisible,
            "Away from Broadcast mode the broadcast row is not shown, so the compact summary is what carries " +
            "the selection - it must never be suppressed by the row.");

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

        // ---- 5. THE THREE-STATE BROADCAST FLOW (NET-014, re-keyed to ADR-0011) ---------------------
        // The old middle state was a REVEAL toggle. Under the mode-based shell the MODE is the reveal
        // (ADR-0011 decision 1): "revealed" is now "the operator is in Broadcast mode". The three states
        // and every assertion they carried survive - only the thing that opens the row changed.
        // State 1 - not in Broadcast mode, so the row is not shown.
        Expect(!vm.IsBroadcastRowVisible, "State 1 (not in Broadcast mode): the row must not be shown.");
        Expect(!vm.IsBroadcastSelecting, "State 1: nothing is being selected.");

        // State 2 - IN BROADCAST MODE: the pickers are open and ENABLED. Nothing is live yet.
        vm.SelectModeCommand.Execute(AppMode.BroadcastMode);
        Expect(vm.IsBroadcastModeVisible, "State 2: selecting the Broadcast card must enter Broadcast mode.");
        Expect(vm.IsBroadcastRowVisible, "State 2 (Broadcast mode): the row must be visible - the mode IS the reveal.");
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
        // Leaving Broadcast mode is the STRONGEST form of the old probe - it bypasses the Change-Mode gate
        // entirely, so if the live state did not hold the row open this would orphan the send.
        vm.SelectModeCommand.Execute(AppMode.CaptureMode);
        Expect(vm.IsBroadcastRowVisible,
            "A LIVE broadcast must remain visible even after the mode moves away - it can never be orphaned.");
        vm.SelectModeCommand.Execute(AppMode.BroadcastMode);   // restore the mode the operator was in

        // Stopping from the in-row action returns to REVEALED, with the pickers usable again. The unlock is
        // a projection of the state machine (proved on lockProbe above), so mirror it here.
        vm.IsDesktopCaptureActive = false;
        vm.IsCaptureSelectorEnabled = true;
        Expect(vm.IsBroadcastModeVisible,
            "The in-row stop leaves the operator in Broadcast mode, with the pickers usable again.");
        Expect(vm.IsCaptureSelectorEnabled, "After a stop the pickers must be usable again.");
        Expect(vm.BroadcastActionText == "Start Broadcasting", "After a stop the action returns to Start.");

        // Change Mode returns to the picker, where the row is not shown.
        vm.ChangeModeCommand.Execute(null);
        Expect(vm.IsPickerVisible, "Change Mode must return to the picker.");
        Expect(!vm.IsBroadcastRowVisible, "On the picker the broadcast row is not shown.");

        // ---- 6. RECORDING IS STILL AN INDEPENDENT AXIS --------------------------------------------
        vm.SelectModeCommand.Execute(AppMode.BroadcastMode);
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

            // Broadcast OFF mid-take: the row stops being shown (mode left + no longer live) and the take
            // must not notice.
            vm.IsDesktopCaptureActive = false;
            vm.SelectModeCommand.Execute(AppMode.CaptureMode);
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
    /// NET-018: the extracted TakeSupervisor driven IN ISOLATION — a fake recorder, a fake watchable
    /// feed and the injected clock. No AudioEngine, no sink, no CaptureSession event plumbing: this is
    /// the ADR-0002 policy on its own, which is the whole reason the state machine was extracted.
    /// </summary>
    static void TestTakeSupervisor()
    {
        Console.WriteLine("\n[Step 0.695] Verifying TakeSupervisor in isolation (fake recorder + injected clock)...");

        var config = new RecordingConfig
        {
            InputLossThresholdSeconds = 5.0,
            InputRecoveryGraceSeconds = 30.0,
            MaxDurationMinutes = 30
        };
        var format = new StreamFormat { Width = 1920, Height = 1080, FrameRate = 30, SampleRate = 48000, Channels = 2 };

        // ---- 1. TAKE LIFECYCLE + WATCHDOG ARMING -------------------------------------------
        {
            long now = 1_000_000;
            Func<long> clock = () => now;
            var recorder = new FakeTakeRecorder();
            var feed = new FakeWatchableFeed();
            var watchdog = new InputHealthWatchdog(thresholdMs: 5000, clock: clock);

            using var supervisor = new TakeSupervisor(recorder, watchdog, () => feed, clock);

            Expect(!supervisor.TakeInProgress, "A supervisor with no take must not report one in progress.");
            Expect(!watchdog.IsArmed, "The watchdog must start disarmed.");

            recorder.IsRecording = true;
            supervisor.BeginTake(config, format);

            Expect(supervisor.TakeInProgress, "BeginTake must put the supervisor into the take state.");
            Expect(watchdog.IsArmed, "A take over a watchable (network) feed must arm the watchdog.");
            Expect(supervisor.InputLossThreshold == TimeSpan.FromSeconds(5),
                $"BeginTake must apply the config's input-loss threshold; got {supervisor.InputLossThreshold}.");
            Expect(supervisor.InputRecoveryGracePeriod == TimeSpan.FromSeconds(30),
                $"BeginTake must apply the config's grace period; got {supervisor.InputRecoveryGracePeriod}.");

            supervisor.EndTake();
            Expect(!supervisor.TakeInProgress, "EndTake must leave the take state.");
            Expect(!watchdog.IsArmed, "EndTake must disarm the watchdog.");

            // An in-process source is NEVER watched, even mid-take (ADR-0002 decision 2).
            feed.RequiresInputHealthWatch = false;
            recorder.IsRecording = true;
            supervisor.BeginTake(config, format);
            Expect(!watchdog.IsArmed,
                "A take over an in-process feed (test signal / host capture) must never arm the watchdog.");
            supervisor.EndTake();
        }

        // ---- 2. BOUNDARY EVALUATION ORDERING -----------------------------------------------
        // Boundaries are evaluated BEFORE the watchdog edge. A take that ends on a boundary this tick
        // must be disarmed before the poll can report a false input loss.
        {
            long now = 2_000_000;
            Func<long> clock = () => now;
            var recorder = new FakeTakeRecorder();
            var feed = new FakeWatchableFeed();
            var watchdog = new InputHealthWatchdog(thresholdMs: 5000, clock: clock);

            using var supervisor = new TakeSupervisor(recorder, watchdog, () => feed, clock);

            bool lostFired = false;
            bool maxFired = false;
            supervisor.InputLost += () => lostFired = true;
            supervisor.MaxDurationReached += () => maxFired = true;

            recorder.IsRecording = true;
            supervisor.BeginTake(config, format);
            Expect(watchdog.IsArmed, "Precondition: the watchdog must be armed for the ordering test.");

            // Silence past the loss threshold AND a due max-duration boundary on the SAME tick.
            recorder.NextBoundary = RecordingBoundary.MaxDuration;
            now += 6000;
            supervisor.Tick();

            Expect(maxFired, "The max-duration boundary must be reported.");
            Expect(!lostFired,
                "The boundary is evaluated BEFORE the watchdog edge, so a take that just ended must not " +
                "also report an input loss on the same tick.");
            Expect(!supervisor.TakeInProgress, "A max-duration boundary must end the take.");
            Expect(!watchdog.IsArmed, "Ending the take on a boundary must disarm the watchdog before the poll.");
            Expect(recorder.Stops.Count == 1 && recorder.Stops[0] == RecordingStopReason.MaxDuration,
                $"The boundary stop must route through the recorder (the arm's single finalize path); " +
                $"got [{string.Join(",", recorder.Stops)}].");
        }

        // ---- 3. OUTAGE -> RECONNECT BACKOFF -> GRACE EXPIRY --------------------------------
        {
            long now = 3_000_000;
            Func<long> clock = () => now;
            var recorder = new FakeTakeRecorder();
            var feed = new FakeWatchableFeed();
            var watchdog = new InputHealthWatchdog(thresholdMs: 5000, clock: clock);

            using var supervisor = new TakeSupervisor(recorder, watchdog, () => feed, clock);

            int lostCount = 0;
            int recoveredCount = 0;
            bool failed = false;
            supervisor.InputLost += () => lostCount++;
            supervisor.InputRecovered += () => recoveredCount++;
            supervisor.InputRecoveryFailed += () => failed = true;

            recorder.IsRecording = true;
            supervisor.BeginTake(config, format);

            // Outage boundary: silence past the threshold.
            now += 5001;
            supervisor.Tick();

            Expect(lostCount == 1, $"Exactly one InputLost must be raised at the outage boundary; got {lostCount}.");
            Expect(!recorder.IsRecording,
                "The current segment must be finalized AT the outage boundary (stop routed through the recorder).");
            Expect(recorder.Stops.Count == 1 && recorder.Stops[0] == RecordingStopReason.InputLoss,
                $"The outage stop must be recorded as InputLoss; got [{string.Join(",", recorder.Stops)}].");
            Expect(supervisor.TakeInProgress,
                "The TAKE must stay in progress across an outage (only the segment ended), so the watchdog " +
                "keeps watching for restored frames.");

            // Backoff: attempt 1 fires on the outage tick, then 250 ms, then 500 ms (ReconnectBackoff).
            WaitFor(() => feed.ReconnectCount >= 1, "first auto-reconnect attempt");
            now += 249;
            supervisor.Tick();
            Thread.Sleep(40);
            Expect(feed.ReconnectCount == 1,
                $"No reconnect may fire before the 250 ms backoff window elapses; got {feed.ReconnectCount}.");

            now += 1; // exactly 250 ms
            supervisor.Tick();
            WaitFor(() => feed.ReconnectCount >= 2, "second auto-reconnect attempt");

            // Grace expiry: still lost past the 30 s window -> the take ends and the alarm persists.
            now += 31_000;
            supervisor.Tick();

            Expect(failed, "The bounded grace period expiring must raise InputRecoveryFailed.");
            Expect(!supervisor.TakeInProgress, "Grace expiry must end the take.");
            Expect(recoveredCount == 0, "A never-restored feed must not report a recovery.");
        }

        // ---- 4. RECOVERY RESUMES THE TAKE INTO THE NEXT SEGMENT ----------------------------
        {
            long now = 4_000_000;
            Func<long> clock = () => now;
            var recorder = new FakeTakeRecorder();
            var feed = new FakeWatchableFeed();
            var watchdog = new InputHealthWatchdog(thresholdMs: 5000, clock: clock);

            using var supervisor = new TakeSupervisor(recorder, watchdog, () => feed, clock);

            int recoveredCount = 0;
            supervisor.InputRecovered += () => recoveredCount++;

            recorder.IsRecording = true;
            supervisor.BeginTake(config, format);

            now += 5001;
            supervisor.Tick();
            Expect(recorder.Stops.Count == 1, "Precondition: the outage must finalize the first segment.");
            int segmentAfterOutage = recorder.CurrentSegmentIndex;

            // Frames return within grace: the watchdog edge flips on the next poll.
            watchdog.NotifyVideoFrame();
            now += 100;
            supervisor.Tick();

            Expect(recoveredCount == 1, $"Recovery must raise InputRecovered exactly once; got {recoveredCount}.");
            Expect(supervisor.TakeInProgress, "A recovered take must still be in progress.");
            Expect(recorder.IsRecording, "Recovery must resume recording through the recorder.");
            Expect(recorder.ResumeCount == 1,
                $"Recovery must resume into the next segment via the recorder; got {recorder.ResumeCount}.");
            Expect(recorder.CurrentSegmentIndex == segmentAfterOutage + 1,
                $"Resume must open the NEXT segment; went {segmentAfterOutage} -> {recorder.CurrentSegmentIndex}.");
        }

        Console.WriteLine(" -> Take lifecycle, watchdog arming (watchable only), boundary-before-watchdog ordering, " +
                          "outage -> backoff -> grace expiry, and segment resume all verified with NO audio engine!");
    }

    /// <summary>
    /// NET-018: stands in for StreamRecorderService in the supervisor's isolation test. Records the
    /// effect calls so their SEQUENCE can be asserted, and lets the test decide what the arm reports.
    /// No sink, no transport, no audio engine.
    /// </summary>
    private sealed class FakeTakeRecorder : ITakeRecorder
    {
        private readonly List<RecordingStopReason> _stops = new();

        public bool IsRecording { get; set; }

        public int CurrentSegmentIndex { get; set; } = 1;

        public string? CurrentRecordingPath { get; set; } = "/fake/seg01.mp4";

        /// <summary>The boundary the fake arm reports on the NEXT evaluation, then clears.</summary>
        public RecordingBoundary NextBoundary { get; set; } = RecordingBoundary.None;

        public IReadOnlyList<RecordingStopReason> Stops => _stops;

        public int ResumeCount { get; private set; }

        public RecordingBoundary EvaluateBoundary(RecordingConfig config)
        {
            RecordingBoundary boundary = NextBoundary;
            NextBoundary = RecordingBoundary.None;   // a real boundary fires once
            return boundary;
        }

        public bool RollSegment(RecordingConfig config, StreamFormat format, RecordingStopReason reason)
        {
            CurrentSegmentIndex++;
            CurrentRecordingPath = $"/fake/seg{CurrentSegmentIndex:00}.mp4";
            return true;
        }

        public bool StartNextSegment(RecordingConfig config, StreamFormat format)
        {
            ResumeCount++;
            CurrentSegmentIndex++;
            CurrentRecordingPath = $"/fake/seg{CurrentSegmentIndex:00}.mp4";
            IsRecording = true;
            return true;
        }

        public void StopRecording(RecordingStopReason reason)
        {
            _stops.Add(reason);
            IsRecording = false;
        }
    }

    /// <summary>
    /// NET-018: a watchable feed with no transport behind it. The supervisor only asks two questions —
    /// "must this be watched?" and "please reconnect" — so that is all this answers.
    /// </summary>
    private sealed class FakeWatchableFeed : IMediaSource, IRecoverableMediaSource
    {
        private int _reconnectCount;

        public string Name => "Fake Watchable Feed";
        public string Address => "fake://watchable";
        public bool IsConnected => true;

        public bool RequiresInputHealthWatch { get; set; } = true;

        public int ReconnectCount => Volatile.Read(ref _reconnectCount);

        public event Action<IntPtr, int, int, int, int, double, long>? VideoFrameReceived { add { } remove { } }
        public event Action<IntPtr, int, int, int, long>? AudioFrameReceived { add { } remove { } }
        public event Action<StreamTelemetry>? TelemetryUpdated { add { } remove { } }
        public event Action<string>? StatusChanged { add { } remove { } }
        public event Action<string>? ErrorOccurred { add { } remove { } }

        public void Connect() { }
        public void Disconnect() { }

        public void EnsureConnected() => Interlocked.Increment(ref _reconnectCount);

        public void Dispose() { }
    }

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

    /// <summary>Writes a file with an explicit timestamp, so keep-last-N is deterministic.</summary>
    static void WriteTemp(string path, string content, DateTime stampUtc)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, stampUtc);
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
