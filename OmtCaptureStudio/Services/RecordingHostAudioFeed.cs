using System.Diagnostics;
using System.Runtime.InteropServices;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Pulls a local WASAPI/ASIO input into the recorder by elapsed time (ADR-0014). The production
/// loop is a background thread owned here, not by <c>CaptureSession</c>. Tests call
/// <see cref="PullDue"/> with an injected clock and never start the thread.
/// </summary>
public sealed class RecordingHostAudioFeed : IDisposable
{
    private readonly IHostAudioSource _source;
    private readonly Action<byte[], int> _onInterleaved;
    private readonly Func<long> _clock;
    private readonly long _frequency;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly float[] _planar;
    private readonly byte[] _interleaved;
    private long _origin;
    private long _sent;
    private int _originSet;
    private Thread? _thread;
    private volatile bool _running;
    private int _disposed;

    public RecordingHostAudioFeed(
        IHostAudioSource source,
        Action<byte[], int> onInterleaved,
        Func<long>? clock = null,
        long frequency = 0)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _onInterleaved = onInterleaved ?? throw new ArgumentNullException(nameof(onInterleaved));
        _clock = clock ?? (static () => Stopwatch.GetTimestamp());
        _frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
        _sampleRate = source.SampleRate > 0 ? source.SampleRate : 48000;
        _channels = source.Channels > 0 ? source.Channels : 2;
        _planar = new float[_sampleRate * _channels];
        _interleaved = new byte[_sampleRate * _channels * sizeof(float)];
    }

    public int SampleRate => _sampleRate;

    /// <summary>Samples that should exist at <paramref name="nowTick"/> since <paramref name="originTick"/>.</summary>
    public static long SamplesAt(long originTick, long nowTick, long frequency, int sampleRate)
    {
        if (nowTick <= originTick || frequency <= 0 || sampleRate <= 0) return 0;
        Int128 samples = (Int128)(nowTick - originTick) * sampleRate / frequency;
        if (samples <= 0) return 0;
        if (samples > long.MaxValue) return long.MaxValue;
        return (long)samples;
    }

    /// <summary>
    /// Pulls whatever the clock says is due and hands interleaved float32 to the callback.
    /// The first call anchors the origin and pulls nothing. Returns the samples pulled.
    /// </summary>
    public int PullDue(long nowTick)
    {
        if (Interlocked.CompareExchange(ref _originSet, 1, 0) == 0)
        {
            _origin = nowTick;
            return 0;
        }

        long dueTotal = SamplesAt(_origin, nowTick, _frequency, _sampleRate);
        long owed = dueTotal - _sent;
        if (owed <= 0) return 0;
        int chunk = (int)Math.Min(_sampleRate, owed);
        _source.FillPlanarAudio(_planar, chunk);
        int bytes = Interleave(_planar, _channels, chunk, _interleaved);
        if (bytes > 0)
        {
            _onInterleaved(_interleaved, bytes);
            _sent += chunk;
        }

        return chunk;
    }

    public void Start()
    {
        if (_running || _disposed != 0) return;
        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "RecordingHostAudio"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        Thread? thread = _thread;
        if (thread != null && thread != Thread.CurrentThread)
        {
            thread.Join(500);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        try { _source.Dispose(); } catch (Exception ex) { AppLogger.LogError($"Host recording audio dispose failed: {ex.Message}", ex); }
    }

    /// <summary>Planar channel-major float to interleaved float32 bytes. Stereo is Left then Right.</summary>
    public static int Interleave(ReadOnlySpan<float> planar, int channels, int samples, Span<byte> destination)
    {
        if (channels <= 0 || samples <= 0) return 0;
        int bytes = samples * channels * sizeof(float);
        if (bytes > destination.Length || samples * channels > planar.Length) return 0;

        for (int i = 0; i < samples; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                float sample = planar[(c * samples) + i];
                MemoryMarshal.Write(destination.Slice(((i * channels) + c) * sizeof(float), sizeof(float)), in sample);
            }
        }

        return bytes;
    }

    private void Loop()
    {
        try
        {
            while (_running)
            {
                int pulled = PullDue(_clock());
                if (pulled <= 0 && _running)
                {
                    Thread.Sleep(5);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Recording host audio stopped: {ex.Message}", ex);
        }
    }
}
