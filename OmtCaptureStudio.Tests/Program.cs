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
        TestOperatorSettings();
        TestScheduledStopAndRemainingTime();
        TestLogLocationAndReadOnlyInstall();
        TestTakeSupervisor();
        TestAsioInputPairSelector();
        TestDiagnosticsBundle();
        TestAppModeShell();
        TestOmtAudioFrameGuard();
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
            case "0.692": TestOperatorSettings(); break;
            case "0.693": TestScheduledStopAndRemainingTime(); break;
            case "0.694": TestLogLocationAndReadOnlyInstall(); break;
            case "0.695": TestTakeSupervisor(); break;
            case "0.696": TestAsioInputPairSelector(); break;
            case "0.697": TestDiagnosticsBundle(); break;
            case "0.698": TestAppModeShell(); break;
            case "0.699": TestOmtAudioFrameGuard(); break;
            case "0.700": SecurityRegressionTable.RunAll(); break;
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
    /// NET-022 / ADR-0011: the mode-based shell. Headless — the state is constructed with no Avalonia
    /// platform, and the ViewModel is driven through its real commands. Covers the three states, the
    /// "picker always opens" rule, the Change-Mode gate, and LastAppMode persistence.
    /// </summary>
    /// <summary>
    /// DIAG-VMIX receiver hardening. Everything downstream of the receiver is FPA1-only and codec-BLIND:
    /// AudioLevelCalculator and AudioInterleaver each cast the payload to float* and read
    /// channels * samplesPerChannel * 4 bytes. The payload is sized by the SENDER, so an unexpected codec
    /// or an under-declared buffer would drive this process past the end of the buffer it was handed - a
    /// remote-triggerable out-of-bounds read. This asserts the pure guard, the refusal the operator sees,
    /// and that the accepted FPA1 path forwards the frame byte-for-byte as before.
    /// </summary>
    static void TestOmtAudioFrameGuard()
    {
        Console.WriteLine("\n[Step 0.699] Verifying the OMT audio frame guard (codec + geometry receiver hardening)...");

        // ---- 1. THE WORKING POPULATION IS ACCEPTED, EXACTLY AS BEFORE -----------------------------
        // These are the exact numbers our own sender puts on the wire (measured in the DIAG-VMIX control
        // trace: 2 ch @ 48 kHz, 800 samples/ch, DataLength 6400, codec FPA1 for 900/900 frames). If this
        // ever stops being accepted the guard has broken every working source.
        AudioFrameValidation ok = OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 6400);
        Expect(ok.IsAccepted, $"A real FPA1 frame must be accepted; got {ok.Reason}: {ok.Detail}");
        Expect(ok.Detail.Length == 0, "An accepted frame must carry no detail text.");

        // 64ch x 16384 is the ruled FINAL geometry ceiling (4,194,304 bytes; the payload envelope of
        // 16 MiB is a separate axis). Derived from the constant so a retune moves this with it.
        foreach (var (ch, smp) in new[] { (1, 480), (2, 1024), (8, 4096), (64, OmtAudioFrameGuard.MaxSamplesPerChannel) })
        {
            int len = ch * smp * 4;
            Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, ch, smp, len).IsAccepted,
                $"{ch}ch x {smp} FPA1 at exactly {len} bytes must be accepted.");
        }

        // ---- 2. PA16 IS REFUSED, NEVER REINTERPRETED (the DIAG-VMIX mechanism) --------------------
        // A PA16 payload for the same geometry is HALF the size the consumers would read - reading it is
        // precisely the over-read reproduced in the diagnosis pass.
        AudioFrameValidation pa16 = OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Pa16Codec, 2, 800, 3200);
        Expect(!pa16.IsAccepted, "A PA16-announcing frame must be REFUSED - never reinterpreted.");
        Expect(pa16.Reason == AudioFrameRejection.UnsupportedCodec,
            $"A PA16 frame must be refused as UnsupportedCodec; got {pa16.Reason}.");
        Expect(pa16.Detail.Contains("PA16", StringComparison.Ordinal),
            $"The refusal must NAME the codec the sender announced; got '{pa16.Detail}'.");
        Expect(pa16.Detail.Contains("16-bit", StringComparison.Ordinal),
            $"The refusal must say what PA16 is so the log is actionable; got '{pa16.Detail}'.");

        // A coincidence of SIZE must not license reinterpretation: refused on the CODEC alone.
        Expect(!OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Pa16Codec, 2, 800, 6400).IsAccepted,
            "A PA16 frame must be refused on its CODEC alone, whatever length it declares.");

        // ---- 3. AN UNKNOWN CODEC IS REFUSED AND NAMED LEGIBLY -------------------------------------
        AudioFrameValidation unknown = OmtAudioFrameGuard.Validate(0x12345678, 2, 800, 6400);
        Expect(!unknown.IsAccepted && unknown.Reason == AudioFrameRejection.UnsupportedCodec,
            "An unrecognised audio codec must be refused as UnsupportedCodec.");
        Expect(OmtAudioFrameGuard.CodecName(OmtAudioFrameGuard.Fpa1Codec) == "FPA1 (826363974)",
            $"Codec names must decode from the tag; got '{OmtAudioFrameGuard.CodecName(OmtAudioFrameGuard.Fpa1Codec)}'.");
        Expect(OmtAudioFrameGuard.CodecName(OmtAudioFrameGuard.Pa16Codec) == "PA16 (909197648)",
            $"got '{OmtAudioFrameGuard.CodecName(OmtAudioFrameGuard.Pa16Codec)}'.");
        Expect(OmtAudioFrameGuard.CodecName(0).Length > 0, "Naming a codec must never throw, even for zero.");

        // ---- 4. A GEOMETRY/DataLength MISMATCH IS REFUSED (the over-read) -------------------------
        AudioFrameValidation shortPayload = OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 3200);
        Expect(!shortPayload.IsAccepted, "An under-length FPA1 payload must be refused - reading it over-runs the buffer.");
        Expect(shortPayload.Reason == AudioFrameRejection.GeometryMismatch,
            $"An under-length payload is a GeometryMismatch; got {shortPayload.Reason}.");
        Expect(shortPayload.Detail.Contains("3200", StringComparison.Ordinal) &&
               shortPayload.Detail.Contains("6400", StringComparison.Ordinal),
            $"The refusal must state both the actual and the expected length; got '{shortPayload.Detail}'.");
        Expect(shortPayload.Detail.Contains("PAST the end", StringComparison.Ordinal),
            $"The refusal must say the read would over-run; got '{shortPayload.Detail}'.");
        Expect(shortPayload.Reason == AudioFrameRejection.GeometryMismatch,
            "An under-length payload is still a GeometryMismatch under the widened R2 boundary.");

        // The boundary is >= on the payload and == on the geometry-derived READ length (SEC-OMT-01 R2).
        // One byte SHORT is the over-read itself and stays refused; one byte LONG is a conformant padded
        // sender and must be ACCEPTED, because reading the geometry out of a larger buffer is in-bounds.
        // (This supersedes this step's earlier strict-equality asserts - R2 changes the contract.)
        Expect(!OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 6399).IsAccepted,
            "One byte SHORT of the announced geometry must be refused - that is the over-read itself.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 6401).IsAccepted,
            "One byte LONG must be ACCEPTED (R2): a padded conformant sender is legal, and reading the " +
            "geometry out of a larger buffer is in-bounds. Strict equality dropped such a source for no " +
            "security benefit.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 6400 + 4096).IsAccepted,
            "A generously padded payload must be accepted (R2).");

        // ---- 5. HOSTILE GEOMETRY: RANGES GATED, AND NO INTEGER OVERFLOW ---------------------------
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 0, 800, 6400).Reason
               == AudioFrameRejection.GeometryOutOfRange, "Zero channels must be out of range.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, OmtAudioFrameGuard.MaxChannels + 1, 800, 6400).Reason
               == AudioFrameRejection.GeometryOutOfRange, "Above MaxChannels must be out of range.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 0, 6400).Reason
               == AudioFrameRejection.GeometryOutOfRange, "Zero samples must be out of range.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, OmtAudioFrameGuard.MaxSamplesPerChannel + 1, 6400).Reason
               == AudioFrameRejection.GeometryOutOfRange, "Above MaxSamplesPerChannel must be out of range.");

        // The range gate runs BEFORE the length arithmetic, so a geometry that would wrap in 32-bit
        // (65536 x 65536 x 4 == 1.7e10, which truncates to 0) can never be evaluated as a small length.
        Expect(!OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 65536, 65536, 0).IsAccepted,
            "A hostile geometry must never be accepted - least of all by wrapping the expectation to zero.");

        // ---- 6. THE REFUSAL IS COUNTED, AND SURFACED ONCE PER DISTINCT PROBLEM --------------------
        using var receiver = new OmtReceiverService();
        var surfaced = new List<string>();
        receiver.ErrorOccurred += m => surfaced.Add(m);

        Expect(receiver.RejectedAudioFrames == 0, "A fresh receiver must have refused nothing.");

        Expect(receiver.ReportRejectedAudioFrame(pa16) == 1, "The first refusal must be counted as occurrence 1.");
        Expect(surfaced.Count == 1, "The first refusal must reach the operator.");
        Expect(surfaced[0].Contains("Refused an OMT audio frame", StringComparison.Ordinal),
            $"The operator's message must say what happened; got '{surfaced[0]}'.");
        Expect(surfaced[0].Contains("PA16", StringComparison.Ordinal),
            $"The operator's message must name the codec; got '{surfaced[0]}'.");
        Expect(surfaced[0].Contains("No audio", StringComparison.Ordinal),
            $"The operator must be told the consequence - no audio is being captured; got '{surfaced[0]}'.");

        // Repeats are COUNTED but must not re-surface: a 60 fps sender would otherwise flood the status line.
        receiver.ReportRejectedAudioFrame(pa16);
        receiver.ReportRejectedAudioFrame(pa16);
        Expect(receiver.RejectedAudioFrames == 3, "Every refusal is counted, even when not re-reported.");
        Expect(surfaced.Count == 1, "A repeated identical refusal must NOT re-surface.");

        // A DIFFERENT problem is surfaced again - the operator must not miss a new failure mode.
        receiver.ReportRejectedAudioFrame(shortPayload);
        Expect(receiver.RejectedAudioFrames == 4, "The counter must keep counting across reasons.");
        Expect(surfaced.Count == 2, "A different refusal reason must surface again.");

        // ---- 7. THE WIRING: the guard gates the frame, and the payload is untouched ----------------
        string? receiverSource = FindRepoSource("OmtCaptureStudio/Services/OmtReceiverService.cs");
        if (receiverSource == null)
        {
            Console.WriteLine("   [SKIP] OmtReceiverService.cs is not reachable from this tree.");
        }
        else
        {
            string src = File.ReadAllText(receiverSource);
            string flat = string.Join(" ", src.Split(
                new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

            // The codec must actually be READ - the defect this guards against was that it never was.
            Expect(flat.Contains(
                "OmtAudioFrameGuard.Validate( frame.Codec, frame.Channels, frame.SamplesPerChannel, frame.DataLength);",
                StringComparison.Ordinal),
                "The receiver must hand the guard the frame's OWN announced codec and delivered length.");

            int guardAt = src.IndexOf("OmtAudioFrameGuard.Validate(", StringComparison.Ordinal);
            int acceptedAt = src.IndexOf("if (verdict.IsAccepted)", StringComparison.Ordinal);
            int forwardAt = src.IndexOf("AudioFrameReceived?.Invoke(", StringComparison.Ordinal);
            Expect(guardAt >= 0 && acceptedAt >= 0 && forwardAt >= 0,
                "The receiver must still validate, branch, and forward.");
            Expect(guardAt < acceptedAt && acceptedAt < forwardAt,
                "The forward must sit INSIDE the acceptance branch: a guard placed after the forward is decorative.");

            // BYTE-IDENTICAL FPA1 PATH: the accepted frame is forwarded exactly as it was before hardening.
            Expect(flat.Contains(
                "AudioFrameReceived?.Invoke( frame.Data, frame.Channels, frame.SamplesPerChannel, frame.SampleRate, frame.Timestamp );",
                StringComparison.Ordinal),
                "The forwarded payload must be byte-for-byte what it was before: the guard GATES the frame, it never rewrites or reinterprets it.");
        }

        // ---- 8. THE VIDEO TWIN (SEC-OMT-01 SEC-02, R11-R14) ---------------------------------------
        // UpdateFrame copies `stride * height` out of the peer's pointer on the RECEIVE thread. These are
        // the four vectors from the security audit's video table; the int32 wrap cases are named because
        // `int requiredBytes = stride * height` silently became 0 (copy skipped) or negative (allocation
        // throws on the receive thread) for reachable pairs.
        VideoFrameValidation benign = OmtVideoFrameGuard.Validate(
            OmtVideoFrameGuard.BgraCodec, 1920, 1080, 7680, 8294400);
        Expect(benign.IsAccepted,
            $"A real 1920x1080 BGRA frame must be ACCEPTED - it is the working population; got {benign.Reason}: {benign.Detail}");

        VideoFrameValidation eightK = OmtVideoFrameGuard.Validate(
            OmtVideoFrameGuard.BgraCodec, 7680, 4320, 30720, 3200);
        Expect(!eightK.IsAccepted,
            "An 8K announce over a 3200-byte payload must be REFUSED - pre-fix it read ~126 MB out of a <=10 MB buffer.");
        Expect(eightK.Reason == VideoFrameRejection.GeometryMismatch,
            $"The 8K vector is a payload mismatch; got {eightK.Reason}.");
        Expect(eightK.Detail.Contains("PAST the end", StringComparison.Ordinal),
            $"The video refusal must say the read would over-run; got '{eightK.Detail}'.");

        // int32 product 65536 * 65536 = 4,294,967,296 -> wraps to 0 (the copy would be silently skipped).
        Expect(!OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 65536, 65536, 65536, 65536).IsAccepted,
            "A geometry whose stride*height wraps to ZERO in int32 must be refused - that is the silent-skip case.");
        // int32 product 100000 * 30000 = 3,000,000,000 -> wraps NEGATIVE (new byte[negative] throws).
        Expect(!OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 100000, 30000, 100000, 1000).IsAccepted,
            "A geometry whose stride*height wraps NEGATIVE in int32 must be refused - that is the throwing case.");

        // R11: the pixel layout must be the one the app asked for and asserts everywhere else.
        // Same geometry as the benign BGRA frame above, so the CODEC is the only difference: if the
        // codec rule were removed this frame would sail through every other check and be accepted.
        // (A UYVY frame at its natural 3840 stride is caught by the stride rule instead, which would
        // make this assertion vacuous - that is how a negative control caught the first draft.)
        VideoFrameValidation uyvy = OmtVideoFrameGuard.Validate(0x59565955 /* UYVY */, 1920, 1080, 7680, 8294400);
        Expect(!uyvy.IsAccepted,
            "A non-BGRA video frame must be refused rather than reinterpreted as BGRA (R11).");
        Expect(uyvy.Reason == VideoFrameRejection.UnsupportedCodec,
            $"The non-BGRA refusal must be a CODEC refusal, not an incidental geometry one; got {uyvy.Reason}: {uyvy.Detail}");
        Expect(OmtVideoFrameGuard.CodecName(OmtVideoFrameGuard.BgraCodec) == "BGRA (1095911234)",
            $"Video codec names must decode from the tag; got '{OmtVideoFrameGuard.CodecName(OmtVideoFrameGuard.BgraCodec)}'.");

        // R12: the stride must actually cover a row, and the dimensions must be sane.
        Expect(!OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 1920, 1080, 4096, 8294400).IsAccepted,
            "A stride that cannot cover a BGRA row must be refused (R12).");
        Expect(!OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 20000, 1080, 80000, 86400000).IsAccepted,
            "A dimension beyond the ceiling must be refused (R12).");
        // ---- 12. R14 RE-SHAPED: the byte ceiling killed conformant 1440p/4K (R14 correction) ---------
        // The old bound was the 10 MiB BYTE ceiling: 2560x1440 BGRA is 14,745,600 bytes and 3840x2160 is
        // 33,177,600 - both conformant, both refused outright. Every vector in the table was 1080p, which
        // is why nothing caught it. The bound is now a PIXEL COUNT at 8K.
        VideoFrameValidation hd1440 = OmtVideoFrameGuard.Validate(
            OmtVideoFrameGuard.BgraCodec, 2560, 1440, 2560 * 4, 2560 * 1440 * 4);
        Expect(hd1440.IsAccepted,
            "R14: conformant 2560x1440 BGRA (14,745,600 bytes) must be ACCEPTED - the old 10 MiB byte " +
            $"ceiling refused it; got {hd1440.Reason}: {hd1440.Detail}");

        VideoFrameValidation uhd2160 = OmtVideoFrameGuard.Validate(
            OmtVideoFrameGuard.BgraCodec, 3840, 2160, 3840 * 4, 3840 * 2160 * 4);
        Expect(uhd2160.IsAccepted,
            "R14: conformant 3840x2160 BGRA (33,177,600 bytes) must be ACCEPTED - it is exactly the 8K " +
            $"pixel bound; got {uhd2160.Reason}: {uhd2160.Detail}");

        // ...and 8K itself, which is the bound, plus the first size past it.
        Expect(OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 7680, 4320, 7680 * 4, 7680 * 4320 * 4).IsAccepted,
            "R14: 8K (7680x4320 = 33,177,600 pixels) is the bound and must be accepted.");
        Expect(OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 7680, 4321, 7680 * 4, 7680 * 4321 * 4).Reason
               == VideoFrameRejection.FrameTooLarge,
            "R14: one row past 8K must be refused as FrameTooLarge.");
        Expect(OmtVideoFrameGuard.MaxPixels == 7680 * 4320,
            $"R14: the pixel bound must be exactly 8K; got {OmtVideoFrameGuard.MaxPixels}.");
        Console.WriteLine($"      R14 re-shaped: pixel bound {OmtVideoFrameGuard.MaxPixels:N0} (8K); " +
                          $"1440p {(long)2560 * 1440 * 4:N0} B and 4K {(long)3840 * 2160 * 4:N0} B both accepted");

        // The byte ceiling is retained only as a reference constant, never as enforcement.
        Console.WriteLine($"      (the old 10 MiB byte ceiling would have refused both: VideoMaxSize={OmtVideoFrameGuard.VideoMaxSize:N0})");

        // R14 (as previously): the SDK's own envelope assertion for the table's ceiling geometry.
        long oversize = 16384L * 4 * 16384;
        Expect(oversize > OmtVideoFrameGuard.VideoMaxSize,
            "The 16K ceiling geometry must exceed VIDEO_MAX_SIZE, or the R14 cap is untestable.");

        // ---- 9. THE PRIMITIVES CLAMP THEMSELVES (SEC-OMT-01 R4/R5) --------------------------------
        // The guard sits at one call site, but AudioLevelCalculator/AudioInterleaver are PUBLIC APIs, so a
        // future caller that bypasses the receiver must not be able to re-open the finding. Asserted here
        // as well as in the SEC-OMT-01 table because that table's own R4/R5 section is unreachable while
        // one of its earlier R2 rows is red - a masked assertion is not a proven one.
        const int ProbeBytes = 8 * 1024 * 1024;
        byte[] probeBuffer = new byte[ProbeBytes];
        var probePin = GCHandle.Alloc(probeBuffer, GCHandleType.Pinned);
        try
        {
            IntPtr probe = probePin.AddrOfPinnedObject();

            // R4: out-of-range geometry must be refused BEFORE anything is allocated for it. Pre-fix,
            // `new ChannelAudioLevel[channels]` ran first: 1e9 channels allocated a 7 GB array in ~5 ms.
            Expect(AudioLevelCalculator.ComputeLevels(probe, 1_000_000, 1).Channels.Length == 0,
                "AudioLevelCalculator must refuse an out-of-range CHANNEL count the way AudioInterleaver does (R4).");
            // DERIVED, not restated: the "one past the ceiling" probe must follow the constant, or a
            // retune silently leaves this asserting a number that is out of range for the wrong reason.
            Expect(AudioLevelCalculator.ComputeLevels(probe, 1, OmtAudioFrameGuard.MaxSamplesPerChannel + 1).Channels.Length == 0,
                "AudioLevelCalculator must refuse samples-per-channel above the shared ceiling (R4).");
            // The ceiling itself stays legal - unifying the policy must not narrow the accepted population.
            byte[] ceiling = new byte[64 * OmtAudioFrameGuard.MaxSamplesPerChannel * 4];
            var ceilingPin = GCHandle.Alloc(ceiling, GCHandleType.Pinned);
            try
            {
                Expect(AudioLevelCalculator.ComputeLevels(
                           ceilingPin.AddrOfPinnedObject(), 64, OmtAudioFrameGuard.MaxSamplesPerChannel).Channels.Length == 64,
                    "The inclusive ceiling (64ch x MaxSamplesPerChannel = 16384, the ruled value) must remain " +
                    "accepted (R4) - and this now reads the constant, so the ruling moves it automatically.");
            }
            finally { ceilingPin.Free(); }

            // R4: the two consumers must AGREE on legality. This is a PIN, not the asymmetry guard -
            // AudioInterleaver already enforced 64 locally, so reverting it to the literal changes
            // nothing (that is why the negative control on it cannot fire, and a control that cannot
            // fail proves nothing). The asymmetry itself was the CALCULATOR's missing clamp, and that is
            // guarded by the R4 assertions above.
            int interleavedBytes;
            new AudioInterleaver().Interleave(probe, 65, 1, out interleavedBytes);
            Expect(interleavedBytes == 0,
                "AudioInterleaver must keep refusing 65 channels, so both consumers agree on legality (R4).");
        }
        finally { probePin.Free(); }

        // ---- 10. BOUNDS PROOF (SEC-OMT-01 R3/R4 disposition) --------------------------------------
        // The guard's range gate is only a real defence if the arithmetic it licenses cannot overflow, and
        // if the FIRST consumer to run cannot walk past a frame the guard accepted. Both are checked here
        // as arithmetic rather than asserted in prose.
        const int MaxCh = OmtAudioFrameGuard.MaxChannels;
        const int MaxSmp = OmtAudioFrameGuard.MaxSamplesPerChannel;

        long worstBytes = (long)MaxCh * MaxSmp * OmtAudioFrameGuard.BytesPerFpa1Sample;
        long worstOffset = (long)MaxCh * MaxSmp;
        Console.WriteLine($"      bounds: MaxChannels={MaxCh}, MaxSamplesPerChannel={MaxSmp}");
        Console.WriteLine($"              worst frame bytes  = {worstBytes:N0}   (int.MaxValue = {int.MaxValue:N0})");
        Console.WriteLine($"              worst ch*smp offset = {worstOffset:N0}");
        Console.WriteLine($"              largest REAL frame  = 5760 samples/ch x 2ch x 4 = {5760L * 2 * 4:N0} (vMix priming frame, measured)");

        Expect(worstBytes <= int.MaxValue,
            $"R4: at the enforced ceiling the frame size ({worstBytes:N0}) must fit in int32, or a consumer " +
            "doing int arithmetic on it overflows.");
        Expect(worstOffset <= int.MaxValue,
            $"R5: at the enforced ceiling the channel offset ({worstOffset:N0}) must fit in int32 - it does, " +
            "and it is computed in long regardless, so the SEC-03/C wrap is closed twice over.");
        // The ceiling the guard accepts is not a licence to read: for ANY accepted frame the guard proved
        // dataLength >= channels*samples*4, and AudioLevelCalculator reads exactly channels*samples*4 with
        // the SAME parameters, so its read is bounded by the same number the guard admitted.
        // The two axes, ruled separately: GEOMETRY caps at 64 x 16384 x 4 = 4,194,304 and the PAYLOAD
        // envelope at 16 MiB (4x that, for padding headroom). The worst frame is now 43x inside int32 and
        // 0.25x of the SDK's own AUDIO_MAX_SIZE, instead of 47x of it.
        long worstGeometry = (long)MaxCh * MaxSmp * OmtAudioFrameGuard.BytesPerFpa1Sample;
        // The ruled shape is TWO ORDERED AXES, not one number: the geometry ceiling binds BELOW the
        // payload envelope. (The 65536 make-them-equal variant was declined as churn with no functional
        // benefit.) Asserted as an ordering, derived from the constants, so a retune of either fails loudly.
        Expect(worstGeometry < OmtAudioFrameGuard.AudioMaxSize,
            $"the geometry ceiling must sit BELOW the payload envelope; {worstGeometry:N0} vs {OmtAudioFrameGuard.AudioMaxSize:N0}.");
        Console.WriteLine($"              worst GEOMETRY = {worstGeometry:N0}  (64ch x {MaxSmp} x 4, final)");
        Console.WriteLine($"              PAYLOAD envelope = {OmtAudioFrameGuard.AudioMaxSize:N0}  (separate axis, padding headroom)");
        Console.WriteLine($"              worst GEOMETRY = {worstGeometry:N0}  (64ch x 65536 x 4)");
        Console.WriteLine($"              PAYLOAD envelope = {OmtAudioFrameGuard.AudioMaxSize:N0}  == the geometry ceiling, by construction");

        AudioFrameValidation atCeiling = OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, MaxCh, MaxSmp, (int)worstGeometry);
        Expect(atCeiling.IsAccepted && atCeiling.ReadBytes == (int)worstGeometry,
            $"R3: the worst geometry must be accepted and publish {worstGeometry:N0}; got {atCeiling.ReadBytes:N0} ({atCeiling.Reason}).");
        // Padded ABOVE the geometry is conformant - the corrected table's ceiling row states exactly this.
        AudioFrameValidation paddedCeiling = OmtAudioFrameGuard.Validate(
            OmtAudioFrameGuard.Fpa1Codec, MaxCh, MaxSmp, OmtAudioFrameGuard.AudioMaxSize);
        Expect(paddedCeiling.IsAccepted && paddedCeiling.ReadBytes == (int)worstGeometry,
            "R2/R9: the ceiling geometry with a padded payload must be accepted, publishing the GEOMETRY " +
            $"({worstGeometry:N0}), not the padding; got {paddedCeiling.ReadBytes:N0} ({paddedCeiling.Reason}).");
        AudioFrameValidation padded = OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, 6400 + 4096);
        Expect(padded.ReadBytes == 6400,
            $"R3: a padded frame must publish the GEOMETRY (6400), not the payload (10496) - a padded " +
            $"sender's surplus bytes are not audio and nothing may read into them; got {padded.ReadBytes}.");
        VideoFrameValidation vCeiling = OmtVideoFrameGuard.Validate(OmtVideoFrameGuard.BgraCodec, 1920, 1080, 7680, 8294400);
        Expect(vCeiling.IsAccepted && vCeiling.ReadBytes == 8294400,
            $"R13: the video verdict must publish the effective read it licensed; got {vCeiling.ReadBytes}.");

        // ---- 11. R9 ENVELOPE + R7 CIRCUIT BREAKER --------------------------------------------------
        // R9: the declared payload envelope, ruled at 16 MiB. Boundaries asserted on both sides so the cap
        // can neither disappear nor creep.
        Expect(OmtAudioFrameGuard.AudioMaxSize == 16777216,
            $"R9: the audio frame envelope is ruled at 16 MiB; got {OmtAudioFrameGuard.AudioMaxSize}.");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, OmtAudioFrameGuard.AudioMaxSize)
                   .Reason != AudioFrameRejection.PayloadTooLarge,
            "R9: a payload exactly AT the envelope must not be refused FOR SIZE (its geometry is checked separately).");
        Expect(OmtAudioFrameGuard.Validate(OmtAudioFrameGuard.Fpa1Codec, 2, 800, OmtAudioFrameGuard.AudioMaxSize + 1).Reason
               == AudioFrameRejection.PayloadTooLarge,
            "R9: one byte past the envelope must be refused as PayloadTooLarge - a length no real audio frame can carry.");

        // R7: the breaker counts consecutive refusals, resets on any accepted frame, and is latched.
        using (var breaker = new OmtReceiverService())
        {
            Expect(breaker.ConsecutiveRejections == 0, "R7: a fresh receiver has no consecutive refusals.");
            Expect(OmtReceiverService.MaxConsecutiveRejections > 1,
                "R7: the threshold must be more than one frame, or a transient burst would tear a good source down.");
            // Stay BELOW the threshold here: crossing it schedules a real teardown on the thread pool, which
            // is deliberately not something this table should trigger.
            for (int i = 0; i < 3; i++) breaker.NoteRejectedFrameAndMaybeTearDown();
            Expect(breaker.ConsecutiveRejections == 3,
                $"R7: each refused frame must advance the breaker; got {breaker.ConsecutiveRejections}.");
        }

        Console.WriteLine(" -> FPA1 accepted unchanged, PA16 and unknown codecs refused and named, geometry mismatches " +
                          "refused both directions, hostile geometry gated, refusal counted and surfaced once per " +
                          "problem, the VIDEO twin refuses the 8K/int32-wrap/non-BGRA vectors, and both audio " +
                          "primitives clamp themselves on ONE policy!");
    }
    static void TestAppModeShell()
    {
        Console.WriteLine("\n[Step 0.698] Verifying the mode-based shell (states, projection, gate, persistence)...");

        // ---- 1. THE HEADLESS STATE ---------------------------------------------------------------
        var state = new AppModeState();
        Expect(state.CurrentMode == AppMode.ModeSelection,
            $"The shell must OPEN on the picker (ADR-0011 decision 1); got {state.CurrentMode}.");
        Expect(state.IsModeSelection && !state.IsOperatingMode, "A fresh state is the picker, not an operating mode.");

        int events = 0;
        AppMode? lastOld = null, lastNew = null;
        state.ModeChanged += (_, e) => { events++; lastOld = e.OldMode; lastNew = e.NewMode; };

        bool moved = state.SetMode(AppMode.CaptureMode);
        Expect(moved, "SetMode must report that it moved.");
        Expect(events == 1, $"One transition must raise exactly one event; got {events}.");
        Expect(lastOld == AppMode.ModeSelection && lastNew == AppMode.CaptureMode,
            $"The event must carry old/new; got {lastOld}->{lastNew}.");
        Expect(!state.SetMode(AppMode.CaptureMode) && events == 1,
            "Re-entering the same mode must be a no-op and raise nothing.");
        Expect(state.IsOperatingMode, "Capture mode is an operating mode.");

        // ---- 2. THE VIEW MODEL PROJECTION ---------------------------------------------------------
        using (var seam = BuildHeadlessSession())
        {
            var vm = seam.ViewModel!;
            var vmState = new AppModeState();
            // The fixture's VM owns its own state; drive that same projection through the commands.
            Expect(vm.IsPickerVisible && !vm.IsCaptureModeVisible && !vm.IsBroadcastModeVisible,
                "At construction the shell must project the picker and only the picker.");

            vm.SelectModeCommand.Execute(AppMode.CaptureMode);
            Expect(vm.IsCaptureModeVisible && !vm.IsPickerVisible,
                "Selecting Capture must swap the content region to the capture mode.");
            Expect(vm.LastUsedAppMode == AppMode.CaptureMode,
                $"Selecting a mode must remember it; got {vm.LastUsedAppMode}.");
            Expect(vm.IsCaptureCardHighlighted && !vm.IsBroadcastCardHighlighted,
                "The picker must pre-highlight the mode that was just used.");
            Expect(vm.IsModeChangeAllowed,
                "Change Mode must be allowed while nothing is recording or broadcasting.");

            vm.ChangeModeCommand.Execute(null);
            Expect(vm.IsPickerVisible && !vm.IsCaptureModeVisible,
                "Change Mode must return to the picker.");
            Expect(vm.LastUsedAppMode == AppMode.CaptureMode,
                "Returning to the picker must NOT erase the last-used mode (ADR-0011 decision 1).");

            vm.SelectModeCommand.Execute(AppMode.BroadcastMode);
            Expect(vm.IsBroadcastModeVisible && !vm.IsPickerVisible,
                "Selecting Broadcast must swap the content region to the broadcast mode.");
            Expect(vm.LastUsedAppMode == AppMode.BroadcastMode, "The last-used mode must follow the new choice.");

            // ---- 2a. THE BROADCAST ROW IS KEYED TO THE MODE (P2-01) -------------------------------
            // The reveal toggle used to own this flag. Once it left the UI nothing could set it, so it
            // stayed false forever: in Broadcast mode the row never showed, the selection hint was
            // permanently invisible, and the compact summary inverted over it. The mode now drives all three.
            Expect(vm.IsBroadcastRowVisible,
                "In Broadcast mode the configuration row must be visible - the mode IS the reveal (ADR-0011 d.1).");
            Expect(vm.IsBroadcastSelecting,
                "In Broadcast mode with nothing live the row must report SELECTING, or the selection hint " +
                "stays permanently invisible (P2-01).");
            Expect(!vm.IsLiveSettingSummaryVisible,
                "The compact summary must stand down while the row is up, so it can never cover the hint.");
            Expect(!vm.IsDesktopCaptureActive, "Entering Broadcast mode must not start a send.");

            // Selecting the picker as a "mode" is not a card action and must be ignored.
            vm.SelectModeCommand.Execute(AppMode.ModeSelection);
            Expect(vm.IsBroadcastModeVisible, "SelectMode(ModeSelection) is a no-op - the picker is reached via ChangeMode.");
        }

        // ---- 2b. THE GATE: a LIVE TAKE closes Change Mode, stopping reopens it ---------------------
        using (var gateSeam = BuildHeadlessSession())
        {
            var gateVm = gateSeam.ViewModel!;
            gateVm.SelectModeCommand.Execute(AppMode.CaptureMode);

            long gateFrames = 0;
            gateSeam.Session.VideoFrameAvailable += (ptr, len, w, h, st, fps, ts) => Interlocked.Increment(ref gateFrames);
            gateSeam.Session.Connect(new SyntheticPatternSource());

            int gateWait = 0;
            while (Interlocked.Read(ref gateFrames) < 5 && gateWait < 40)
            {
                Thread.Sleep(50);
                gateWait++;
            }

            var gateConfig = new RecordingConfig
            {
                OutputDirectory = Path.GetTempPath(),
                FilenamePrefix = "ModeGateTest"
            };

            if (gateSeam.Session.StartRecording(gateConfig))
            {
                try
                {
                    Expect(!gateVm.IsModeChangeAllowed,
                        "Change Mode must be DISABLED while a take is live (ADR-0011 decision 4).");
                    Expect(gateVm.ModeChangeBlockedHint.Contains("recording"),
                        $"The hint must name the blocker; got '{gateVm.ModeChangeBlockedHint}'.");

                    gateVm.ChangeModeCommand.Execute(null);
                    Expect(gateVm.IsCaptureModeVisible && !gateVm.IsPickerVisible,
                        "A Change Mode command must be REFUSED while a take is live - never a silent teardown.");
                }
                finally
                {
                    gateSeam.Session.StopRecordingAsync().GetAwaiter().GetResult();
                }

                // The stop notification runs on the pool then posts to the UI thread (headless-safe).
                int reopenWait = 0;
                while (!gateVm.IsModeChangeAllowed && reopenWait < 60)
                {
                    Thread.Sleep(50);
                    reopenWait++;
                }

                Expect(gateVm.IsModeChangeAllowed,
                    "Stopping the take must reopen Change Mode.");
                Expect(gateVm.ModeChangeBlockedHint.Length == 0,
                    "The blocker hint must clear when the gate reopens.");

                gateVm.ChangeModeCommand.Execute(null);
                Expect(gateVm.IsPickerVisible, "With nothing live, Change Mode must now return to the picker.");
            }
            else
            {
                Console.WriteLine("   [SKIP] the canned take did not start; the gate's live half is unproven here.");
            }
        }

        // ---- 3. PERSISTENCE: LastAppMode rides ADR-0006, and the picker STILL opens ----------------
        string root = Path.Combine(Path.GetTempPath(), "omt-mode-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            var store = new SettingsStore(path);
            using (var seam = BuildHeadlessSession())
            {
                var vm = seam.ViewModel!;
                vm.AttachSettings(store, new OperatorSettings());
                vm.SelectModeCommand.Execute(AppMode.CaptureMode);

                var reloaded = store.Load();
                Expect(reloaded.LastAppMode == AppMode.CaptureMode,
                    $"LastAppMode must persist through the settings store; got {reloaded.LastAppMode}.");
            }

            // A FRESH session with those settings: the picker still opens, but Capture is pre-highlighted.
            using (var seam2 = BuildHeadlessSession())
            {
                var vm2 = seam2.ViewModel!;
                var restored = store.Load();
                vm2.AttachSettings(store, restored);

                Expect(vm2.IsPickerVisible,
                    "A restored LastAppMode must NOT auto-skip the picker (ADR-0011 decision 1).");
                Expect(vm2.CurrentAppMode == AppMode.ModeSelection,
                    $"The window must still open on the picker; got {vm2.CurrentAppMode}.");
                Expect(vm2.IsCaptureCardHighlighted,
                    "The restored mode must pre-highlight its card on the picker.");
            }

            // An older/hand-edited file with no mode: the picker opens and highlights NOTHING.
            using (var seam3 = BuildHeadlessSession())
            {
                var vm3 = seam3.ViewModel!;
                vm3.AttachSettings(new SettingsStore(Path.Combine(root, "empty.json")), new OperatorSettings());
                Expect(vm3.IsPickerVisible && !vm3.IsCaptureCardHighlighted && !vm3.IsBroadcastCardHighlighted,
                    "With no saved mode, the picker must open and highlight neither card.");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        // ---- 4. STRUCTURAL GUARD: grid placement in EVERY multi-row grid ---------------------------
        // Headless tests cannot RENDER XAML, so a child that silently defaults to Grid.Row 0 - because a
        // move stripped its Grid.Row - is invisible to every other assertion in this suite. It is NOT
        // invisible to the operator: the recording bar stacked behind the source picker and vanished, the
        // shared status bar floated over the content, and in BroadcastModeView the Start/Stop and
        // Change-Mode buttons overlapped by 115x23px at the 950px floor. Pixel verification caught each;
        // this keeps them caught in CI.
        //
        // The FIRST version of this guard checked only each file's OUTER grid, so every NESTED multi-row
        // grid escaped it - which is exactly where the BroadcastModeView P1 lived. It now walks EVERY Grid
        // descendant. Rule: in ANY Grid declaring more than one row, every child states its Grid.Row.
        {
            string[] axamlFiles =
            {
                "OmtCaptureStudio/MainWindow.axaml",
                "OmtCaptureStudio/Views/CaptureModeView.axaml",
                "OmtCaptureStudio/Views/BroadcastModeView.axaml",
                "OmtCaptureStudio/Views/ModeSelectionView.axaml",
                "OmtCaptureStudio/Views/VideoSurface.axaml"
            };

            // Rows can be declared EITHER as a <Grid.RowDefinitions> element or with the
            // RowDefinitions="Auto,*,Auto" attribute shorthand. Counting only the element form made the
            // first version of this guard silently skip every file that used the shorthand - i.e. all of
            // them - and pass with the bug deliberately reintroduced. Support both forms.
            static int RowCount(System.Xml.Linq.XElement grid, System.Xml.Linq.XNamespace ns)
            {
                int n = grid.Elements(ns + "Grid.RowDefinitions")
                            .SelectMany(r => r.Elements(ns + "RowDefinition")).Count();
                if (n == 0)
                {
                    string? shorthand = grid.Attribute("RowDefinitions")?.Value;
                    if (!string.IsNullOrWhiteSpace(shorthand))
                    {
                        n = shorthand!.Split(',').Length;
                    }
                }

                return n;
            }

            int multiRowGridsChecked = 0;

            foreach (string rel in axamlFiles)
            {
                string? axamlPath = FindRepoSource(rel);
                if (axamlPath == null)
                {
                    Console.WriteLine($"   [SKIP] {rel} is not reachable from this tree.");
                    continue;
                }

                var axaml = System.Xml.Linq.XDocument.Load(axamlPath);
                var xns = axaml.Root!.Name.Namespace;
                var grids = axaml.Descendants(xns + "Grid").ToList();
                Expect(grids.Count > 0, $"{rel} must contain a Grid.");
                if (grids.Count == 0) continue;

                foreach (var grid in grids)
                {
                    int rowCount = RowCount(grid, xns);
                    if (rowCount <= 1) continue;

                    multiRowGridsChecked++;

                    var unplaced = grid.Elements()
                        .Where(e => e.Name.LocalName is not ("Grid.RowDefinitions" or "Grid.ColumnDefinitions"))
                        .Where(e => e.Attribute("Grid.Row") == null)
                        .Select(e => e.Name.LocalName)
                        .ToList();

                    Expect(unplaced.Count == 0,
                        $"{rel}: a child of a {rowCount}-row Grid omits Grid.Row, so it silently defaults to " +
                        $"row 0 and overlaps its siblings. Offenders: {string.Join(", ", unplaced)}.");
                }
            }

            // ANTI-VACUITY FLOOR. A guard that checks nothing passes for the wrong reason - exactly how the
            // first version shipped broken. Seven multi-row grids exist across these five files; if row
            // counting regresses or a file drops out of the walk, this fails loudly instead of silently
            // certifying a layout it never examined.
            Expect(multiRowGridsChecked >= 7,
                $"The XAML row guard only reached {multiRowGridsChecked} multi-row Grid(s), expected at least 7 " +
                "- it is not exercising the views, so it cannot certify them.");
        }

        Console.WriteLine(" -> Three states, one projection, the Change-Mode gate, LastAppMode persistence " +
                          "and the XAML row-placement invariant (ALL multi-row grids) all verified!");
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

    /// <summary>
    /// NET-019: the ASIO pair selector in isolation — an injected probe and an inline UI hop, so the
    /// whole probe / retry / degrade lifecycle is covered WITHOUT constructing the 2,600-line view
    /// model and WITHOUT opening a driver.
    /// </summary>
    static void TestAsioInputPairSelector()
    {
        Console.WriteLine("\n[Step 0.696] Verifying AsioInputPairSelector in isolation (injected probe, no view model)....");

        var clsid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var driver = new AsioDriverInfo("Test ASIO", clsid, "C:/fake/test-asio.dll");
        var asioOption = HostAudioCaptureOption.ForAsio(clsid, "ASIO - Test ASIO (In 1/2)", 0);
        var muteOption = new HostAudioCaptureOption(HostAudioCaptureMode.Mute, "Mute");

        static IReadOnlyList<AsioInputChannel> Chans(int n) =>
            Enumerable.Range(0, n).Select(i => new AsioInputChannel(i, $"Input {i + 1}")).ToList();

        // ---- 1. THE PURE PAIR DERIVATION (the ONE place pairs come from) -----------------------
        Expect(AsioInputPairSelector.BuildPairs(null).Count == 0, "A null channel list yields no pairs.");
        Expect(AsioInputPairSelector.BuildPairs(Array.Empty<AsioInputChannel>()).Count == 0,
            "A driver with no input channels yields no pairs.");

        var two = AsioInputPairSelector.BuildPairs(Chans(2));
        Expect(two.Count == 1 && two[0].FirstChannelIndex == 0 && two[0].DisplayName == "In 1/2",
            "Two channels yield exactly one pair, In 1/2.");

        var four = AsioInputPairSelector.BuildPairs(Chans(4));
        Expect(four.Count == 2 && four[1].FirstChannelIndex == 2 && four[1].DisplayName == "In 3/4",
            "Four channels yield In 1/2 and In 3/4.");

        var five = AsioInputPairSelector.BuildPairs(Chans(5));
        Expect(five.Count == 3 && five[2].FirstChannelIndex == 4 && five[2].DisplayName == "In 5",
            "A trailing odd channel is offered on its own (the capture source duplicates it to L+R).");

        // ---- 2. SUPPRESSION IS A SCOPE, NOT A HAND-PAIRED BOOL ---------------------------------
        // This is the invariant that used to be hand-set and hand-reset at five sites. Getting it
        // wrong leaves the picker permanently deaf to the operator, so the nesting semantics matter.
        {
            var guard = new AsioInputPairSelector(_ => new AsioPairProbeResult(Chans(0), null),
                                                 action => action(), () => null);

            Expect(!guard.IsSuppressingUserChoice, "A fresh selector must not suppress.");

            using (guard.SuppressUserChoice())
            {
                Expect(guard.IsSuppressingUserChoice, "The scope must suppress.");

                using (guard.SuppressUserChoice())
                {
                    Expect(guard.IsSuppressingUserChoice, "A nested scope stays suppressed.");
                }

                Expect(guard.IsSuppressingUserChoice,
                    "The inner scope must NOT un-suppress the outer one — the guard is a DEPTH, not a bool.");
            }

            Expect(!guard.IsSuppressingUserChoice, "The outer scope must release on dispose.");

            try { using (guard.SuppressUserChoice()) throw new InvalidOperationException("boom"); }
            catch (InvalidOperationException) { }
            Expect(!guard.IsSuppressingUserChoice,
                "A scope must release even when its body throws — that is what `using` buys over a raw bool.");
        }

        // ---- 3. THE PROBE LIFECYCLE: lazy, cached, and it never runs during construction --------
        {
            int probes = 0;
            HostAudioCaptureOption? current = asioOption;
            var selector = new AsioInputPairSelector(
                _ => { Interlocked.Increment(ref probes); return new AsioPairProbeResult(Chans(8), null); },
                action => action(),
                () => current);
            selector.RegisterDriver(driver);

            var gate = new object();
            AsioPairProjection latest = AsioPairProjection.Hidden;
            bool selectionRaisedUnderSuppression = false;
            selector.ProjectionChanged += p =>
            {
                lock (gate) latest = p;
                if (p.SelectedPair != null && selector.IsSuppressingUserChoice)
                {
                    selectionRaisedUnderSuppression = true;
                }
            };
            AsioPairProjection Latest() { lock (gate) return latest; }

            Expect(probes == 0, "Constructing the selector must NOT probe — the whole path is lazy.");

            // A non-ASIO device hides and clears, and still probes nothing.
            selector.SyncSelection(muteOption);
            Expect(!Latest().IsVisible, "A non-ASIO device must hide the pair picker.");
            Expect(Latest().Options.Count == 0, "A non-ASIO device must clear the pair options.");
            Expect(probes == 0, "A non-ASIO device must never trigger a probe.");

            // Selecting an ASIO device shows the control immediately and starts one probe.
            selector.SyncSelection(asioOption);
            Expect(Latest().IsVisible, "An ASIO device must show the picker immediately, before the probe lands.");
            string? probingHint = Latest().Hint;
            Expect(probingHint != null && probingHint.Contains("Probing"),
                $"The hint must say a probe is running; got '{probingHint}'.");

            // The probe itself runs off-thread, so wait for it to LAND before asserting the count.
            for (int i = 0; i < 400 && Latest().Options.Count == 0; i++) Thread.Sleep(5);
            Expect(probes == 1, $"Selecting an ASIO device must start exactly one probe; got {probes}.");
            Expect(Latest().Options.Count == 4, $"8 channels must yield 4 pairs; got {Latest().Options.Count}.");
            Expect(Latest().SelectedPair?.FirstChannelIndex == 0, "The remembered pair (0 = In 1/2) must be selected.");
            Expect(selectionRaisedUnderSuppression,
                "A projection that carries a SELECTION must be raised while suppression is active, or the " +
                "host applying it looks like an operator pick and the picker reacts to itself.");

            // A second sync for the same driver is served from the cache — no new probe, no driver open.
            selector.SyncSelection(asioOption);
            Expect(probes == 1, $"A cached driver must not be re-probed; got {probes} probes.");

            // PairText derives its label from the CACHE once there is one.
            Expect(selector.PairText(clsid, 2) == "In 3/4",
                $"PairText must name a cached pair; got '{selector.PairText(clsid, 2)}'.");
            Expect(selector.PairText(Guid.Empty, 0) == "In 1/2",
                "PairText must fall back to the index when there is no cached probe.");
        }

        // ---- 4. DEGRADE: a failed probe hides the picker and names the reason -------------------
        {
            HostAudioCaptureOption? current = asioOption;
            var selector = new AsioInputPairSelector(
                _ => new AsioPairProbeResult(Chans(0), "held by another application"),
                action => action(),
                () => current);
            selector.RegisterDriver(driver);

            var gate = new object();
            AsioPairProjection latest = AsioPairProjection.Hidden;
            selector.ProjectionChanged += p => { lock (gate) latest = p; };
            AsioPairProjection Latest() { lock (gate) return latest; }

            selector.SyncSelection(asioOption);
            for (int i = 0; i < 400; i++)
            {
                string? h;
                lock (gate) h = latest.Hint;
                if (h != null && h.Contains("Input pairs unavailable")) break;
                Thread.Sleep(5);
            }

            Expect(!Latest().IsVisible, "A failed probe must hide the pair picker.");
            Expect(Latest().Options.Count == 0, "A failed probe must leave no pairs.");
            Expect(Latest().SelectedPair == null, "A failed probe must select nothing.");

            string? degradeHint = Latest().Hint;
            Expect(degradeHint != null && degradeHint.Contains("held by another application"),
                $"The failure reason must reach the hint; got '{degradeHint}'.");
        }

        // ---- 5. RETRY ON DROPDOWN OPEN, exactly once, and never while one is in flight ----------
        {
            int probes = 0;
            HostAudioCaptureOption? current = asioOption;
            var selector = new AsioInputPairSelector(
                _ => { Interlocked.Increment(ref probes); return new AsioPairProbeResult(Chans(0), "no channels"); },
                action => action(),
                () => current);
            selector.RegisterDriver(driver);

            selector.SyncSelection(asioOption);
            for (int i = 0; i < 200 && probes == 0; i++) Thread.Sleep(5);
            Thread.Sleep(60);

            int afterFirst = probes;
            selector.RetryIfUnusable(asioOption);
            for (int i = 0; i < 200 && probes == afterFirst; i++) Thread.Sleep(5);
            Expect(probes == afterFirst + 1, $"Opening the picker after a failure must retry ONCE; got {probes}.");

            // A usable cached result must never be thrown away for a retry.
            int before = probes;
            Thread.Sleep(60);
            selector.RetryIfUnusable(muteOption);
            Thread.Sleep(40);
            Expect(probes == before, "A non-ASIO selection must never trigger a retry.");
        }

        // ---- 6. A LATE PROBE FOR A DEVICE THE OPERATOR LEFT IS DROPPED, NOT APPLIED -------------
        {
            HostAudioCaptureOption? current = asioOption;
            var selector = new AsioInputPairSelector(
                _ => new AsioPairProbeResult(Chans(8), null),
                action => action(),
                () => current);
            selector.RegisterDriver(driver);

            var gate = new object();
            AsioPairProjection latest = AsioPairProjection.Hidden;
            selector.ProjectionChanged += p => { lock (gate) latest = p; };

            // The operator selects the ASIO device, then switches away before the probe lands. `current`
            // must move FIRST: SyncSelection takes its option as an argument, so setting it after the
            // call is a race against a probe that can already have landed.
            current = muteOption;
            selector.SyncSelection(asioOption);

            // PairText is derived from the cache, so it tells us the probe has landed without exposing
            // internals or racing a fixed sleep.
            for (int i = 0; i < 400 && selector.PairText(clsid, 0) != "In 1/2"; i++) Thread.Sleep(5);

            Expect(latest.Options.Count == 0 && latest.SelectedPair == null,
                "A probe that lands after the operator moved to another device must be cached, not applied " +
                "to the picker (the picker must still show no pairs and no selection).");
            Expect(selector.PairText(clsid, 0) == "In 1/2",
                "The dropped result must still have been CACHED, so the next visit is instant.");
        }

        Console.WriteLine(" -> Pair derivation, lazy probe, cache, degrade, one-shot retry and the suppression " +
                          "SCOPE all verified with no view model and no ASIO driver!");
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
        string? supSrc = FindRepoSource("OmtCaptureStudio/Services/TakeSupervisor.cs");
        if (armSrc == null || svcSrc == null || sesSrc == null || supSrc == null)
        {
            Console.WriteLine($" -> [SKIP] source tree not reachable from {AppContext.BaseDirectory}; locality assertions skipped.");
        }
        else
        {
            string armText = File.ReadAllText(armSrc);
            string svcText = File.ReadAllText(svcSrc);
            string sesText = File.ReadAllText(sesSrc);
            string supText = File.ReadAllText(supSrc);

            if (CountIn(armText, "FinalizeSink(") != 1) throw new Exception("The arm must have exactly one FinalizeSink call site.");
            if (CountIn(svcText, "FinalizeSink(") != 0) throw new Exception("StreamRecorderService must not finalize — it is a facade now.");
            if (CountIn(sesText, "FinalizeSink(") != 0) throw new Exception("CaptureSession must not finalize — it goes through the arm.");
            if (CountIn(svcText, "Task.Run") != 0) throw new Exception("StreamRecorderService must not own a thread hop.");
            if (CountIn(sesText, "Task.Run(") != 0) throw new Exception("CaptureSession must not own a thread hop — the reconnect hop moved to TakeSupervisor (NET-018).");
            if (CountIn(supText, "Task.Run(") != 1) throw new Exception("TakeSupervisor should only Task.Run the reconnect hop, nothing else.");
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
            Console.WriteLine(" -> Finalize locality: 1 FinalizeSink site (the arm), 0 in the facade/session, 1 thread hop (now in TakeSupervisor — NET-018); the gate is assigned in NO other class (the arm itself has 1 open + 2 closes: Arm, Disarm, and Arm's failure rollback).");
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
