using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using libomtnet;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Broadcasts a host capture target (a monitor or a window — any <see cref="IFrameSource"/>)
/// and a host audio source (<see cref="IHostAudioSource"/>) over OMT by sending each captured
/// video frame and lockstep 48 kHz stereo <c>FPA1</c> audio frame through <see cref="OMTSend"/>.
/// </summary>
public sealed class OmtDesktopCaptureGenerator : IDisposable
{
    private const int StopJoinTimeoutMs = 1000;

    private readonly string _sourceName;
    private readonly int _fps;
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

    public bool IsRunning => _isRunning;
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
        string? bindIpAddress = null)
    {
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        _sourceName = sourceName;
        _fps = fps > 0 ? fps : 30;
        _audioSource = audioSource ?? new SilentHostAudioSource();
        _bindIpAddress = string.IsNullOrWhiteSpace(bindIpAddress) ? null : bindIpAddress;
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
        const int sampleRate = WasapiHostAudioSource.TargetSampleRate;
        const int channels = WasapiHostAudioSource.TargetChannels;
        int samplesPerFrame = sampleRate / _fps;
        int audioSamplesTotal = channels * samplesPerFrame;

        float[] audioBuffer = new float[audioSamplesTotal];
        GCHandle audioHandle = GCHandle.Alloc(audioBuffer, GCHandleType.Pinned);
        IntPtr pAudio = audioHandle.AddrOfPinnedObject();

        Stopwatch sw = Stopwatch.StartNew();
        long frameIndex = 0;
        bool hasFrame = false;

        try
        {
            while (_isRunning && ReferenceEquals(_capturer, capturer) && ReferenceEquals(_sender, sender))
            {
                long targetMs = frameIndex * 1000 / _fps;
                long elapsedMs = sw.ElapsedMilliseconds;
                if (elapsedMs < targetMs)
                {
                    int delay = (int)(targetMs - elapsedMs);
                    if (delay > 0) Thread.Sleep(delay);
                }
                frameIndex++;

                // Desktop Duplication only yields a frame when the screen changes, so a static
                // desktop would otherwise starve receivers. Once one real frame is captured,
                // keep re-broadcasting the latest buffer to hold the stream at the paced rate.
                if (capturer.TryCaptureFrame())
                {
                    hasFrame = true;
                }
                else if (!hasFrame)
                {
                    continue; // no frame captured yet
                }

                // 1. Send Video Frame
                OMTMediaFrame vFrame = new()
                {
                    Type = OMTFrameType.Video,
                    Codec = (int)OMTCodec.BGRA,
                    Width = capturer.Width,
                    Height = capturer.Height,
                    Stride = capturer.Stride,
                    FrameRateN = _fps,
                    FrameRateD = 1,
                    Data = capturer.BufferPtr,
                    DataLength = capturer.Stride * capturer.Height,
                    Timestamp = -1 // automatic timestamp throttling
                };
                sender.Send(vFrame);

                // 2. Fill and Send Planar Audio Frame (FPA1, 48 kHz, 2ch) in lockstep with video
                IHostAudioSource? activeAudio = Volatile.Read(ref _audioSource);
                if (activeAudio != null)
                {
                    activeAudio.FillPlanarAudio(audioBuffer, samplesPerFrame);
                }
                else
                {
                    Array.Clear(audioBuffer, 0, audioBuffer.Length);
                }

                OMTMediaFrame aFrame = new()
                {
                    Type = OMTFrameType.Audio,
                    Codec = (int)OMTCodec.FPA1,
                    Channels = channels,
                    SampleRate = sampleRate,
                    SamplesPerChannel = samplesPerFrame,
                    Data = pAudio,
                    DataLength = audioSamplesTotal * sizeof(float),
                    Timestamp = -1
                };
                sender.Send(aFrame);
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
