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

}
