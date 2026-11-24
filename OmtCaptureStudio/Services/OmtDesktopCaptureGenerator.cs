using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using libomtnet;

using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Audio;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Broadcasts a host capture target (a monitor or a window — any <see cref="IFrameSource"/>)
/// and a host audio source (<see cref="IHostAudioSource"/>) over OMT by sending each captured
/// video frame and lockstep 48 kHz stereo <c>FPA1</c> audio frame through <see cref="OMTSend"/>.
/// </summary>
public sealed class OmtDesktopCaptureGenerator : IDisposable
{
    private const int StopJoinTimeoutMs = 1000;

    /// <summary>
    /// DIAG-BROADCAST: how long the send loop waits for the FIRST captured frame before reporting that
    /// the broadcast is carrying nothing. Deliberately generous - a session lock or a DXGI mode change can
    /// take seconds to settle, and a false "capture unavailable" alarm would be worse than a late one.
    /// </summary>
    public const int DefaultFirstFrameTimeoutMs = 10_000;

    /// <summary>
    /// How long a capture session may stay unhealthy AFTER frames have already arrived before the
    /// stall alarm fires. A static desktop (DXGI timeout on a live duplication) is not unhealthy and
    /// does not start this clock. Same generosity as the first-frame window: recreating a duplication
    /// after a mode change or UAC prompt can take a few seconds.
    /// </summary>
    public const int DefaultCaptureStallTimeoutMs = 10_000;

    private readonly string _sourceName;
    private readonly RationalFrameRate _frameRate;
    private readonly IFrameSource _frameSource;
    private readonly object _lock = new();
    private readonly List<Socket> _interfaceBlockerSockets = new();

    private IHostAudioSource? _audioSource;
    private string? _bindIpAddress;
    private OMTSend? _sender;
    private IFrameSource? _capturer;
    private Thread? _sendThread;
    private volatile bool _isRunning;
    private bool _started;
    private bool _disposed;
    private int _hasProducedFrames;

    /// <summary>The first-frame window; injectable so a headless test does not have to wait 10 s.</summary>
    private readonly int _firstFrameTimeoutMs;

    /// <summary>The post-frame unhealthy window; injectable so a headless test does not have to wait 10 s.</summary>
    private readonly int _captureStallTimeoutMs;

    public bool IsRunning => _isRunning;

    /// <summary>
    /// DIAG-BROADCAST: raised ONCE per run when no frame has been captured within the first-frame window.
    /// Carries the operator-facing message verbatim, so a presenter can put it on the status line without
    /// re-writing it. Raised on the SEND thread - a subscriber that touches the UI must marshal.
    /// </summary>
    public event Action<string>? CaptureStalled;

    /// <summary>True once at least one real frame has been captured (observability, and testable).</summary>
    public bool HasProducedFrames => Volatile.Read(ref _hasProducedFrames) == 1;

    public string SourceName => _sourceName;
    public string? Url => _sender?.URL;
    public string? Address => _sender?.Address;
    public int Port => _sender?.Port ?? 0;
    public string? BindIpAddress => _bindIpAddress;

    /// <summary>
    /// Direct OMT URL for the selected network interface (e.g. <c>omt://192.168.1.50:6400</c>),
    /// or the default <see cref="OMTSend.URL"/> when broadcasting on all interfaces.
    /// </summary>
    public string? BroadcastEndpointUrl
    {
        get
        {
            int port = Port;
            string? ip = _bindIpAddress;
            if (!string.IsNullOrWhiteSpace(ip) && port > 0)
            {
                return $"omt://{ip}:{port}";
            }
            return _sender?.URL;
        }
    }

    public OmtDesktopCaptureGenerator(
        IFrameSource frameSource,
        string sourceName = "OmtCaptureStudio_DesktopCapture",
        int fps = 30,
        IHostAudioSource? audioSource = null,
        string? bindIpAddress = null,
        int firstFrameTimeoutMs = DefaultFirstFrameTimeoutMs,
        int captureStallTimeoutMs = DefaultCaptureStallTimeoutMs,
        RationalFrameRate? frameRate = null)
    {
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        _sourceName = sourceName;
        // An explicit ratio wins. Otherwise the historical integer fps is N/1, so existing callers
        // (and a settings file that only stored an integer) keep the rate they asked for.
        _frameRate = frameRate is { IsValid: true } explicitRate
            ? explicitRate
            : RationalFrameRate.FromInteger(fps > 0 ? fps : 30);
        _audioSource = audioSource ?? new SilentHostAudioSource();
        _bindIpAddress = string.IsNullOrWhiteSpace(bindIpAddress) ? null : bindIpAddress;
        _firstFrameTimeoutMs = firstFrameTimeoutMs > 0 ? firstFrameTimeoutMs : DefaultFirstFrameTimeoutMs;
        _captureStallTimeoutMs = captureStallTimeoutMs > 0 ? captureStallTimeoutMs : DefaultCaptureStallTimeoutMs;
    }

    /// <summary>
    /// Hot-swaps the active <see cref="IHostAudioSource"/> while broadcasting (or before start)
    /// without interrupting the video stream, and disposes the previous audio source.
    /// </summary>
    public void SetAudioSource(IHostAudioSource newAudioSource)
    {
        ArgumentNullException.ThrowIfNull(newAudioSource);

        if (_disposed)
        {
            try { newAudioSource.Dispose(); } catch { }
            return;
        }

        IHostAudioSource? previous = Interlocked.Exchange(ref _audioSource, newAudioSource);
        if (previous != null && !ReferenceEquals(previous, newAudioSource))
        {
            try { previous.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Updates the network interface IPv4 address that OMT is allowed to accept connections on.
    /// Pass <c>null</c> to allow connections on all network interfaces.
    /// </summary>
    public void SetBindIpAddress(string? bindIpAddress)
    {
        if (_disposed) return;

        string? normalized = string.IsNullOrWhiteSpace(bindIpAddress) ? null : bindIpAddress.Trim();
        lock (_lock)
        {
            _bindIpAddress = normalized;
            if (_isRunning && _sender != null && _sender.Port > 0)
            {
                ApplyInterfaceBlockerSocketsLocked(_sender.Port, _bindIpAddress);
            }
        }
    }

    public void Start()
    {
        if (_isRunning || _disposed) return;
        _started = true;

        IFrameSource source = _frameSource;
        OMTSend? sender = null;
        bool threadStarted = false;
        try
        {
            sender = new OMTSend(_sourceName, OMTQuality.High);

            lock (_lock)
            {
                _capturer = source;
                _sender = sender;
                _isRunning = true;

                if (sender.Port > 0)
                {
                    ApplyInterfaceBlockerSocketsLocked(sender.Port, _bindIpAddress);
                }

                var thread = new Thread(() => SendLoop(source, sender))
                {
                    Name = "OMT_DesktopCaptureThread",
                    IsBackground = true
                };
                _sendThread = thread;
                thread.Start();
                threadStarted = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Desktop Capture Start Error] {ex.Message}");
            lock (_lock)
            {
                _isRunning = false;
                _capturer = null;
                _sender = null;
                _sendThread = null;
                ReleaseInterfaceBlockerSocketsLocked();
            }

            // The send loop owns disposal of the source + sender + audioSource it runs. If it never
            // started, dispose here so we don't leak the native handles.
            if (!threadStarted)
            {
                try { sender?.Dispose(); } catch { }
                try { source.Dispose(); } catch { }
                try { Interlocked.Exchange(ref _audioSource, null)?.Dispose(); } catch { }
            }

            throw;
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lock)
        {
            _isRunning = false;
            thread = _sendThread;
            _sendThread = null;
            _capturer = null; // identity mismatch -> the loop exits and disposes its own source/sender
            _sender = null;
            ReleaseInterfaceBlockerSocketsLocked();
        }

        // Bounded wait. We do NOT dispose here: if the join times out the loop is still
        // using the source/sender, and the loop's finally disposes them once it exits.
        if (thread != null && thread.IsAlive)
        {
            thread.Join(StopJoinTimeoutMs);
        }
    }

    /// <summary>
    /// DIAG-BROADCAST: the operator-facing text for a capture that never starts. Names the provider and
    /// states the CONSEQUENCE, because "no video" is only half of it - audio is skipped too.
    /// </summary>
    public string BuildNoFramesMessage(IFrameSource capturer)
    {
        string provider;
        try { provider = capturer.GetType().Name; }
        catch (Exception) { provider = "the capture provider"; }

        // The most diagnostic sentence available for this symptom class: not just that capture failed,
        // but whether it is running on the degraded GDI path. Read through the interface (the DIM keeps
        // this working for a provider with no such concept).
        string degradation = capturer.IsUsingFallbackCapture
            ? " (DXGI Desktop Duplication unavailable, using GDI fallback)"
            : string.Empty;

        return $"Broadcast is running but no frames are being captured (screen capture unavailable). " +
               $"'{provider}' has not produced a single frame in {_firstFrameTimeoutMs / 1000} s{degradation}. " +
               $"Receivers will see this source advertised as '{_sourceName}' but blank, and NO audio is being " +
               "sent either, because the send loop skips both streams until the first frame arrives.";
    }

    /// <summary>
    /// DIAG-BROADCAST: the operator-facing text once frames HAD been flowing and the session then
    /// went unhealthy (lost duplication that did not recover). Distinct from
    /// <see cref="BuildNoFramesMessage"/> because audio is still being sent and the last picture is
    /// still on the wire — it is frozen, not absent.
    /// </summary>
    public string BuildSessionStallMessage(IFrameSource capturer)
    {
        string provider;
        try { provider = capturer.GetType().Name; }
        catch (Exception) { provider = "the capture provider"; }

        string degradation = capturer.IsUsingFallbackCapture
            ? " (DXGI Desktop Duplication unavailable, using GDI fallback)"
            : string.Empty;

        return $"Broadcast capture stalled: no fresh frame for {_captureStallTimeoutMs / 1000} s after frames had been flowing. " +
               $"'{provider}' reports the capture session is unhealthy{degradation}. " +
               $"The last picture is still being repeated so receivers stay connected to '{_sourceName}', but it is frozen until capture recovers.";
    }

    private void ApplyInterfaceBlockerSocketsLocked(int port, string? allowedIp)
    {
        ReleaseInterfaceBlockerSocketsLocked();

        if (string.IsNullOrWhiteSpace(allowedIp) || port <= 0)
            return;

        IReadOnlyList<IPAddress> blockedIps = NetworkInterfaceEnumerator.GetOtherLocalIpv4Addresses(allowedIp);
        foreach (IPAddress ip in blockedIps)
        {
            Socket? blocker = null;
            try
            {
                // Binding and listening on a specific local IPv4 endpoint with ReuseAddress=true
                // takes precedence in Windows tcpip.sys over OMTSend's wildcard [::]:port listener,
                // intercepting incoming TCP connections on unselected NICs and immediately resetting
                // them (TCP RST) while keeping the selected NIC (and 127.0.0.1) open for OMTSend.
                blocker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                blocker.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                blocker.Bind(new IPEndPoint(ip, port));
                blocker.Listen(1);
                _interfaceBlockerSockets.Add(blocker);
                StartRejectLoop(blocker);
            }
            catch
            {
                try { blocker?.Dispose(); } catch { }
            }
        }
    }

    private static void StartRejectLoop(Socket blocker)
    {
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            var listener = (Socket)state!;
            while (true)
            {
                Socket? client = null;
                try
                {
                    client = listener.Accept();
                    client.LingerState = new LingerOption(enable: true, seconds: 0);
                    client.Close();
                }
                catch
                {
                    try { client?.Dispose(); } catch { }
                    break;
                }
            }
        }, blocker);
    }

    private void ReleaseInterfaceBlockerSocketsLocked()
    {
        foreach (Socket s in _interfaceBlockerSockets)
        {
            try { s.Close(); } catch { }
            try { s.Dispose(); } catch { }
        }
        _interfaceBlockerSockets.Clear();
    }

    private void SendLoop(IFrameSource capturer, OMTSend sender)
    {
        const int sampleRate = HostAudioFormat.TargetSampleRate;
        const int channels = HostAudioFormat.TargetChannels;
        // One second, rented once. A late loop sends every sample that came due, in chunks of
        // this size, instead of one video frame's worth.
        int chunkCapacity = Math.Max(1, sampleRate);
        float[] audioBuffer = new float[channels * chunkCapacity];
        GCHandle audioHandle = GCHandle.Alloc(audioBuffer, GCHandleType.Pinned);
        IntPtr pAudio = audioHandle.AddrOfPinnedObject();

        using var pacer = new FramePacer(_frameRate);
        long audioSamplesSent = 0;
        // The pacer owns the frame clock. This watch is only the first-frame diagnostic and must not
        // be the thing the send loop sleeps on.
        Stopwatch firstFrameWatch = Stopwatch.StartNew();
        bool stallReported = false;
        bool sessionStallReported = false;
        long unhealthySince = -1;
        bool hasFrame = false;

        try
        {
            while (_isRunning && ReferenceEquals(_capturer, capturer) && ReferenceEquals(_sender, sender))
            {
                long frameIndex = pacer.WaitNext();

                // Desktop Duplication only yields a frame when the screen changes, so a static
                // desktop would otherwise starve receivers. Once one real frame is captured,
                // keep re-broadcasting the latest buffer to hold the stream at the paced rate.
                bool fresh = capturer.TryCaptureFrame();
                bool sessionHealthy = capturer.IsCaptureSessionHealthy;
                if (fresh)
                {
                    hasFrame = true;
                    Volatile.Write(ref _hasProducedFrames, 1);
                    unhealthySince = -1;
                    sessionStallReported = false;
                }
                else if (!hasFrame)
                {
                    // DIAG-BROADCAST: this branch used to fail COMPLETELY SILENTLY. Sitting before BOTH
                    // sends, it means a provider that never yields a frame puts neither video NOR audio on
                    // the wire while mDNS still advertises the source - the operator sees a source that is
                    // listed, selectable and blank, with nothing in any log to explain it. Report once.
                    if (!stallReported && firstFrameWatch.ElapsedMilliseconds >= _firstFrameTimeoutMs)
                    {
                        stallReported = true;
                        string stalled = BuildNoFramesMessage(capturer);
                        AppLogger.LogError(stalled);
                        try { CaptureStalled?.Invoke(stalled); }
                        catch (Exception ex) { AppLogger.LogError("A CaptureStalled subscriber threw.", ex); }
                    }

                    continue; // no frame captured yet
                }
                else if (!sessionHealthy)
                {
                    // Frames already flowed, then the session died (DXGI access-lost that did not
                    // recover). A static desktop stays sessionHealthy and does not enter here, so a
                    // still slide does not raise a false stall. The last buffer is still sent below.
                    long now = Environment.TickCount64;
                    if (unhealthySince < 0) unhealthySince = now;
                    if (!sessionStallReported && now - unhealthySince >= _captureStallTimeoutMs)
                    {
                        sessionStallReported = true;
                        string stalled = BuildSessionStallMessage(capturer);
                        AppLogger.LogError(stalled);
                        try { CaptureStalled?.Invoke(stalled); }
                        catch (Exception ex) { AppLogger.LogError("A CaptureStalled subscriber threw.", ex); }
                    }
                }
                else
                {
                    unhealthySince = -1;
                    sessionStallReported = false;
                }

                // 1. Send Video Frame
                OMTMediaFrame vFrame = new()
                {
                    Type = OMTFrameType.Video,
                    Codec = (int)OMTCodec.BGRA,
                    Width = capturer.Width,
                    Height = capturer.Height,
                    Stride = capturer.Stride,
                    FrameRateN = _frameRate.Numerator,
                    FrameRateD = _frameRate.Denominator,
                    Data = capturer.BufferPtr,
                    DataLength = capturer.Stride * capturer.Height,
                    Timestamp = -1 // automatic timestamp throttling
                };
                sender.Send(vFrame);

                // 2. Send every sample the schedule owes since the previous pass, including
                //    pictures this loop skipped. One frame of audio per iteration is what made a
                //    slow desktop loop transmit less than real time (ADR-0014).
                int due = AudioSendSchedule.SamplesDue(_frameRate, sampleRate, frameIndex, audioSamplesSent);
                int remaining = due;
                while (remaining > 0)
                {
                    int samples = Math.Min(remaining, chunkCapacity);
                    IHostAudioSource? activeAudio = Volatile.Read(ref _audioSource);
                    if (activeAudio != null)
                    {
                        activeAudio.FillPlanarAudio(audioBuffer, samples);
                    }
                    else
                    {
                        Array.Clear(audioBuffer, 0, channels * samples);
                    }

                    OMTMediaFrame aFrame = new()
                    {
                        Type = OMTFrameType.Audio,
                        Codec = (int)OMTCodec.FPA1,
                        Channels = channels,
                        SampleRate = sampleRate,
                        SamplesPerChannel = samples,
                        Data = pAudio,
                        DataLength = channels * samples * sizeof(float),
                        Timestamp = -1
                    };
                    sender.Send(aFrame);
                    remaining -= samples;
                    audioSamplesSent += samples;
                }
            }
        }
        finally
        {
            if (audioHandle.IsAllocated) audioHandle.Free();
            // The loop owns the source, audioSource, and sender it was handed: dispose each once.
            try { Interlocked.Exchange(ref _audioSource, null)?.Dispose(); } catch { }
            try { capturer.Dispose(); } catch { }
            try { sender.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (!_started)
        {
            try { Interlocked.Exchange(ref _audioSource, null)?.Dispose(); } catch { }
            try { _frameSource.Dispose(); } catch { }
        }
        GC.SuppressFinalize(this);
    }
}
