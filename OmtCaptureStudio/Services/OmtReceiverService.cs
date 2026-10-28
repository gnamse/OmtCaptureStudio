using System.Diagnostics;
using libomtnet;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Audio;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.Services;

public class OmtReceiverService : IDisposable
{
    private const int DisconnectJoinTimeoutMs = 1000;

    private OMTReceive? _receiver;
    private Thread? _receiveThread;
    private volatile bool _isRunning;
    private string? _currentAddress;
    private readonly object _lock = new();
    private bool _disposed;

    // DIAG-VMIX receiver hardening: frames refused before any consumer saw them. Non-zero means the
    // connected sender is putting audio on the wire this receiver will not interpret - the operator is
    // told once per distinct problem and the count is here for telemetry/tests.
    private long _rejectedAudioFrames;
    private string? _lastAudioRejection;

    // SEC-OMT-01 R10: `ReportRejectedAudioFrame`/`ReportRejectedVideoFrame` are PUBLIC (they are the
    // testable entry points for the reporting rule), so the "did the reason change?" decision must not
    // read and write unsynchronised state. Production touches this only on the receive thread, so the
    // lock is uncontended in practice and only taken on the exceptional path.
    private readonly object _rejectionLock = new();

    // SEC-OMT-01 R7: consecutive refusals with no accepted frame in between. Reset by ANY accepted frame,
    // so a sender that merely interleaves bad frames with good ones never trips this.
    private long _consecutiveRejections;
    private int _hostileSourceTornDown;

    /// <summary>
    /// SEC-OMT-01 R7: how many consecutive refused frames (no accepted frame in between) mean the peer is
    /// not a usable source. At vMix's measured 100 chunks/s that is ~6 s - long enough that a transient
    /// burst never trips it, short enough that the operator is not left staring at LIVE video with silently
    /// dead audio for a whole session.
    /// </summary>
    public const int MaxConsecutiveRejections = 600;

    /// <summary>SEC-OMT-01 R7: refusals since the last accepted frame. Non-zero means the peer is failing.</summary>
    public long ConsecutiveRejections => Interlocked.Read(ref _consecutiveRejections);

    public bool IsConnected => _receiver != null && _receiver.IsConnected();
    public string? CurrentAddress => _currentAddress;

    private long _rejectedVideoFrames;
    private string? _lastVideoRejection;

    /// <summary>Audio frames refused by <see cref="OmtAudioFrameGuard"/> since this receiver was created.</summary>
    public long RejectedAudioFrames => Interlocked.Read(ref _rejectedAudioFrames);

    /// <summary>Video frames refused by <see cref="OmtVideoFrameGuard"/> since this receiver was created.</summary>
    public long RejectedVideoFrames => Interlocked.Read(ref _rejectedVideoFrames);

    // Events
    public event Action<IntPtr, int, int, int, int, double, long>? VideoFrameReceived;
    public event Action<IntPtr, int, int, int, long>? AudioFrameReceived;
    public event Action<StreamTelemetry>? TelemetryUpdated;
    public event Action<string>? ConnectionStatusChanged;
    public event Action<string>? ErrorOccurred;

    public void Connect(string address)
    {
        Disconnect();

        OMTReceive? receiver = null;
        bool threadStarted = false;
        try
        {
            _currentAddress = address;
            _lastAudioRejection = null;   // a new sender gets its own first refusal reported
            _lastVideoRejection = null;
            Interlocked.Exchange(ref _consecutiveRejections, 0);
            Interlocked.Exchange(ref _hostileSourceTornDown, 0);   // a fresh connection gets a fresh chance
            ConnectionStatusChanged?.Invoke($"Connecting to {address}...");

            // Prefer BGRA for direct WPF WriteableBitmap rendering and FFmpeg rawvideo input
            receiver = new OMTReceive(
                address,
                OMTFrameType.Video | OMTFrameType.Audio,
                OMTPreferredVideoFormat.BGRA,
                OMTReceiveFlags.None
            );

            lock (_lock)
            {
                _receiver = receiver;
                _isRunning = true;

                var thread = new Thread(() => ReceiveLoop(receiver))
                {
                    Name = "OMT_ReceiveThread",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _receiveThread = thread;
                thread.Start();
                threadStarted = true;
            }

            ConnectionStatusChanged?.Invoke($"Connected to {address}");
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _isRunning = false;
                _receiver = null;
                _receiveThread = null;
            }
            _currentAddress = null;

            // The receive loop owns disposal of the receiver it runs. If it never started,
            // dispose here so we don't leak the native handle.
            if (!threadStarted)
            {
                try { receiver?.Dispose(); } catch { }
            }

            ConnectionStatusChanged?.Invoke($"Connection Failed: {ex.Message}");
            ErrorOccurred?.Invoke($"Failed to connect to {address}: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        Thread? thread;
        lock (_lock)
        {
            _isRunning = false;
            thread = _receiveThread;
            _receiveThread = null;
            _receiver = null; // signals the running loop to exit; the loop disposes its own instance
            _currentAddress = null;
        }

        // Bounded wait. We deliberately do NOT dispose the receiver here: if the join times
        // out the loop is still using it, and the loop's finally disposes it once it exits.
        if (thread != null && thread.IsAlive)
        {
            thread.Join(DisconnectJoinTimeoutMs);
        }

        ConnectionStatusChanged?.Invoke("Disconnected");
    }

    /// <summary>
    /// Re-establishes the feed after an input-loss gap (ADR-0002 auto-reconnect). Reuses
    /// <see cref="Connect"/>, which bounds-joins the previous receive loop and starts a fresh receiver:
    /// the loop owns the receiver it runs and disposes it exactly once behind the identity check, so a
    /// join that times out never disposes a receiver a live loop still holds.
    /// </summary>
    public void EnsureConnected(string address)
    {
        if (_disposed || string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        Connect(address);
    }

    private void ReceiveLoop(OMTReceive receiver)
    {
        OMTMediaFrame frame = new();
        DateTime lastStatsTime = DateTime.UtcNow;
        long videoCount = 0;
        long audioCount = 0;
        int lastWidth = 0;
        int lastHeight = 0;
        double lastFps = 0;
        int lastChannels = 0;
        int lastSampleRate = 0;

        try
        {
            // Exit when asked to stop OR when this thread's receiver is no longer the current
            // one (a reconnect swapped it out). The identity check guarantees two loops never
            // share a receiver, which the old shared-flag design allowed.
            while (_isRunning && ReferenceEquals(_receiver, receiver))
            {
                try
                {
                    // Receive Video or Audio with a 50ms timeout
                    if (receiver.Receive(OMTFrameType.Video | OMTFrameType.Audio, 50, ref frame))
                    {
                        if (frame.Type == OMTFrameType.Video && frame.Data != IntPtr.Zero && frame.DataLength > 0)
                        {
                            // SEC-OMT-01 SEC-02 (R11-R15): the VIDEO twin of the audio guard, and the
                            // reason it must land with it. VideoViewportControl.UpdateFrame copies
                            // `stride * height` bytes straight out of this pointer, and it is invoked by
                            // a DIRECT delegate on this same receive thread - so an unvalidated size is a
                            // remote over-read that also stalls frame reception. Measured pre-fix: an 8K
                            // announce over a small payload read ~126 MB out of a <=10 MB buffer, and the
                            // int32 product wrapped to 0 or negative for two reachable pairs.
                            VideoFrameValidation videoVerdict = OmtVideoFrameGuard.Validate(
                                frame.Codec,
                                frame.Width,
                                frame.Height,
                                frame.Stride,
                                frame.DataLength);

                            if (videoVerdict.IsAccepted)
                            {
                                videoCount++;
                                Interlocked.Exchange(ref _consecutiveRejections, 0);   // R7: any accepted frame resets it
                                lastWidth = frame.Width;
                                lastHeight = frame.Height;
                                lastFps = frame.FrameRateD > 0 ? (double)frame.FrameRateN / frame.FrameRateD : 60.0;

                                VideoFrameReceived?.Invoke(
                                    frame.Data,
                                    frame.DataLength,
                                    frame.Width,
                                    frame.Height,
                                    frame.Stride,
                                    lastFps,
                                    frame.Timestamp
                                );
                            }
                            else
                            {
                                ReportRejectedVideoFrame(videoVerdict);
                                NoteRejectedFrameAndMaybeTearDown();
                            }
                        }
                        else if (frame.Type == OMTFrameType.Audio && frame.Data != IntPtr.Zero && frame.DataLength > 0)
                        {
                            // DIAG-VMIX receiver hardening. Everything downstream of this point is
                            // FPA1-only and codec-BLIND: AudioLevelCalculator and AudioInterleaver each
                            // cast the payload to float* and read channels * samplesPerChannel * 4 bytes.
                            // The payload is sized by the SENDER, so an unexpected codec or an
                            // under-declared buffer would make this process read past the end of the
                            // buffer it was handed. The frame is therefore forwarded ONLY when it is FPA1
                            // and its length is exactly the size the consumers will read.
                            AudioFrameValidation verdict = OmtAudioFrameGuard.Validate(
                                frame.Codec,
                                frame.Channels,
                                frame.SamplesPerChannel,
                                frame.DataLength);

                            if (verdict.IsAccepted)
                            {
                                audioCount++;
                                Interlocked.Exchange(ref _consecutiveRejections, 0);   // R7: any accepted frame resets it
                                lastChannels = frame.Channels;
                                lastSampleRate = frame.SampleRate;

                                AudioFrameReceived?.Invoke(
                                    frame.Data,
                                    frame.Channels,
                                    frame.SamplesPerChannel,
                                    frame.SampleRate,
                                    frame.Timestamp
                                );
                            }
                            else
                            {
                                ReportRejectedAudioFrame(verdict);
                                NoteRejectedFrameAndMaybeTearDown();
                            }
                        }
                    }

                    // Update Telemetry once per second
                    if ((DateTime.UtcNow - lastStatsTime).TotalMilliseconds >= 1000)
                    {
                        lastStatsTime = DateTime.UtcNow;

                        var vStats = receiver.GetVideoStatistics();
                        var aStats = receiver.GetAudioStatistics();

                        double mbps = (vStats.BytesReceivedSinceLast + aStats.BytesReceivedSinceLast) * 8.0 / 1_000_000.0;

                        var telemetry = new StreamTelemetry
                        {
                            IsConnected = receiver.IsConnected(),
                            Width = lastWidth,
                            Height = lastHeight,
                            FrameRate = lastFps,
                            VideoFormat = "BGRA 8-bit",
                            AudioChannels = lastChannels,
                            SampleRate = lastSampleRate,
                            VideoFramesReceived = videoCount,
                            AudioFramesReceived = audioCount,
                            AudioFramesRejected = RejectedAudioFrames,
                            VideoFramesRejected = RejectedVideoFrames,
                            FramesDropped = vStats.FramesDropped,
                            BitrateMbps = mbps
                        };

                        TelemetryUpdated?.Invoke(telemetry);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OMT Receive Loop Error] {ex.Message}");
                }
            }
        }
        finally
        {
            // The loop owns the receiver it was handed: dispose exactly once, after last use.
            try { receiver.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// DIAG-VMIX: reports a refused audio frame. Deliberately RATE-LIMITED - a mismatched sender offers a
    /// frame every ~16 ms, so an unthrottled line per frame would bury the very log it exists to be found
    /// in. The first refusal always reports; afterwards only a CHANGE of reason or every 600th frame
    /// (~10 s at 60 fps) is written.
    ///
    /// <para>The operator is told through <see cref="ErrorOccurred"/> as well as the log, because a source
    /// silently contributing no audio is precisely the failure that is hardest to notice.</para>
    ///
    /// <para>Public rather than private deliberately, and for the same reason
    /// <c>RecordingArm.StopLowDisk</c> is: the reporting rule (count every refusal, surface each distinct
    /// one exactly once) is behaviour worth asserting, and a headless test needs an entry point that does
    /// not require a live OMT sender. It is safe to call directly.</para>
    /// </summary>
    /// <returns>How many frames have been refused since this receiver was created.</returns>
    public long ReportRejectedAudioFrame(AudioFrameValidation verdict)
    {
        long occurrence = Interlocked.Increment(ref _rejectedAudioFrames);

        bool changed;
        lock (_rejectionLock)
        {
            changed = !string.Equals(verdict.Detail, _lastAudioRejection, StringComparison.Ordinal);
            if (changed)
            {
                _lastAudioRejection = verdict.Detail;
            }
        }

        bool heartbeat = occurrence % 600 == 0;
        if (!changed && !heartbeat)
        {
            return occurrence;
        }

        string message = $"Refused an OMT audio frame from {_currentAddress ?? "(unknown address)"} " +
                         $"({verdict.Reason}, {occurrence} refused so far): {verdict.Detail} " +
                         "No audio from this source is being captured.";

        AppLogger.LogError(message);
        Debug.WriteLine($"[OMT Audio Guard] {message}");

        if (changed)
        {
            ErrorOccurred?.Invoke(message);
        }

        return occurrence;
    }

    /// <summary>
    /// SEC-OMT-01 R11-R14: reports a refused video frame. Rate-limited for the same reason as the audio
    /// one - a hostile peer offers a frame every ~16 ms - and surfaced to the operator because a source
    /// stuck on this path shows a frozen viewport with no explanation.
    /// </summary>
    public long ReportRejectedVideoFrame(VideoFrameValidation verdict)
    {
        long occurrence = Interlocked.Increment(ref _rejectedVideoFrames);

        bool changed;
        lock (_rejectionLock)
        {
            changed = !string.Equals(verdict.Detail, _lastVideoRejection, StringComparison.Ordinal);
            if (changed)
            {
                _lastVideoRejection = verdict.Detail;
            }
        }

        bool heartbeat = occurrence % 600 == 0;
        if (!changed && !heartbeat)
        {
            return occurrence;
        }

        string message = $"Refused an OMT video frame from {_currentAddress ?? "(unknown address)"} " +
                         $"({verdict.Reason}, {occurrence} refused so far): {verdict.Detail} " +
                         "No video from this source is being captured.";

        AppLogger.LogError(message);
        Debug.WriteLine($"[OMT Video Guard] {message}");

        if (changed)
        {
            ErrorOccurred?.Invoke(message);
        }

        return occurrence;
    }

    /// <summary>
    /// SEC-OMT-01 R7: circuit-breaks a peer whose every frame is refused, so the operator is not left with
    /// live video and silently dead audio for a whole session.
    ///
    /// <para><b>The teardown MUST NOT run on this thread.</b> <see cref="Disconnect"/> calls
    /// <c>Join(DisconnectJoinTimeoutMs)</c> on the receive thread, and this runs ON that thread - calling it
    /// directly is a self-join deadlock. It is therefore scheduled on the thread pool, and the guard is
    /// latched (<see cref="_hostileSourceTornDown"/>) so exactly one teardown is ever scheduled.</para>
    /// </summary>
    public void NoteRejectedFrameAndMaybeTearDown()
    {
        long consecutive = Interlocked.Increment(ref _consecutiveRejections);
        if (consecutive != MaxConsecutiveRejections)
        {
            return;
        }

        if (Interlocked.Exchange(ref _hostileSourceTornDown, 1) != 0)
        {
            return;
        }

        string message =
            $"Disconnecting from {_currentAddress ?? "(unknown address)"}: {MaxConsecutiveRejections} " +
            "consecutive frames were refused and not one was usable. This peer does not send audio/video " +
            "this receiver can interpret, so it is being dropped rather than left connected and silent.";

        AppLogger.LogError(message);
        Debug.WriteLine($"[OMT Trust Boundary] {message}");
        ErrorOccurred?.Invoke(message);

        // Off the receive thread - see the summary above. A self-join here would wedge frame reception.
        _ = Task.Run(() =>
        {
            try
            {
                Disconnect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OMT Trust Boundary] teardown failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Disconnect();

        VideoFrameReceived = null;
        AudioFrameReceived = null;
        TelemetryUpdated = null;
        ConnectionStatusChanged = null;
        ErrorOccurred = null;

        GC.SuppressFinalize(this);
    }
}
