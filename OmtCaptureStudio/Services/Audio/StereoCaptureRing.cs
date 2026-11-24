using System;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.Services.Audio;

/// <summary>
/// The single stereo capture ring shared by every host-audio adapter (ADR-0003, Slice 1).
/// <para>
/// It owns the whole policy that used to be copy-pasted into <c>AsioHostAudioSource</c> and
/// <c>WasapiEndpointCapture</c>: capacity, the accumulated-latency cap, the drop-to-half-window
/// overflow rule, the underrun-to-silence rule, and the lock. The default cap is one second
/// (ADR-0014); the 120 ms cap discarded programme audio when the consumer was briefly late.
/// </para>
/// <para>
/// <b>Rate-agnostic by design.</b> The two adapters put DIFFERENT things in it — the ASIO path
/// stores device-rate samples and resamples on the consumer, the WASAPI path stores already-48 kHz
/// samples because it resamples on the capture thread — so the ring deliberately knows nothing about
/// sample rates. It is a plain dual-channel float frame queue with a nominal capacity.
/// </para>
/// <para>
/// <b>Hot path discipline.</b> <see cref="WriteSample"/>, <see cref="WriteLocked"/> and
/// <see cref="ReadLocked"/> allocate nothing; they only copy floats and move indices. The lock is a
/// plain <c>lock</c> on <see cref="SyncRoot"/>; the write side never grows a buffer.
/// </para>
/// </summary>
public sealed class StereoCaptureRing
{
    /// <summary>Default capacity: 2.0 s at 48 kHz, so a slow send loop can catch up.</summary>
    public const int DefaultCapacitySamples = 48_000 * 2;

    /// <summary>
    /// Default backlog cap: 1.0 s at 48 kHz. A 120 ms cap trimmed real programme audio whenever
    /// the send loop ran behind a frame or two (ADR-0014). Trim only past this, and log it.
    /// </summary>
    public const int DefaultMaxLatencySamples = 48_000;

    private readonly float[] _left;
    private readonly float[] _right;
    private int _readIndex;
    private int _writeIndex;
    private int _availableSamples;
    private long _trimmedSamples;
    private long _lastTrimLogMs;

    public StereoCaptureRing(
        int capacitySamples = DefaultCapacitySamples,
        int maxLatencySamples = DefaultMaxLatencySamples)
    {
        if (capacitySamples < 1) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        if (maxLatencySamples < 1 || maxLatencySamples > capacitySamples)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLatencySamples));
        }

        CapacitySamples = capacitySamples;
        MaxLatencySamples = maxLatencySamples;
        _left = new float[capacitySamples];
        _right = new float[capacitySamples];
    }

    /// <summary>Frames the ring can hold before it starts overwriting the oldest.</summary>
    public int CapacitySamples { get; }

    /// <summary>
    /// Accumulated frames beyond which the ring discards down to half this window, so audio can
    /// never drift minutes behind video over a multi-hour session.
    /// </summary>
    public int MaxLatencySamples { get; }

    /// <summary>
    /// The lock guarding the ring. Callers that must compose a ring operation with their own state
    /// (the ASIO resampler feed, the WASAPI packet ingest) take this rather than a private lock, so
    /// there is exactly one lock per ring.
    /// </summary>
    public object SyncRoot { get; } = new();

    /// <summary>Frames currently readable. Safe to call without holding <see cref="SyncRoot"/>.</summary>
    public int AvailableSamples
    {
        get { lock (SyncRoot) { return _availableSamples; } }
    }

    /// <summary>Frames currently readable. Caller must already hold <see cref="SyncRoot"/>.</summary>
    public int AvailableSamplesLocked => _availableSamples;

    /// <summary>Drops everything buffered (used when a capture stream is torn down/restarted).</summary>
    public void Reset()
    {
        lock (SyncRoot)
        {
            ResetLocked();
        }
    }

    /// <summary>Drops everything buffered. Caller must already hold <see cref="SyncRoot"/>.</summary>
    public void ResetLocked()
    {
        _readIndex = 0;
        _writeIndex = 0;
        _availableSamples = 0;
    }

    /// <summary>Appends one stereo frame. Allocation-free; takes the lock.</summary>
    public void WriteSample(float left, float right)
    {
        lock (SyncRoot)
        {
            WriteSampleLocked(left, right);
        }
    }

    /// <summary>Appends <paramref name="count"/> frames from the two channel spans. Takes the lock.</summary>
    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int count)
    {
        lock (SyncRoot)
        {
            WriteLocked(left, right, count);
        }
    }

    /// <summary>Appends <paramref name="count"/> frames. Caller must already hold <see cref="SyncRoot"/>.</summary>
    public void WriteLocked(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int count)
    {
        for (int i = 0; i < count; i++)
        {
            WriteSampleLocked(left[i], right[i]);
        }
    }

    /// <summary>Appends one stereo frame. Caller must already hold <see cref="SyncRoot"/>.</summary>
    public void WriteSampleLocked(float left, float right)
    {
        _left[_writeIndex] = left;
        _right[_writeIndex] = right;
        _writeIndex = (_writeIndex + 1) % CapacitySamples;

        if (_availableSamples < CapacitySamples)
        {
            _availableSamples++;
        }
        else
        {
            // Full: the oldest frame is overwritten, so the read head advances with it.
            _readIndex = (_readIndex + 1) % CapacitySamples;
        }

        // Only a genuine backlog is trimmed. A send loop that is a frame or two late must be
        // able to drain the samples it already captured.
        if (_availableSamples > MaxLatencySamples)
        {
            int drop = _availableSamples - (MaxLatencySamples / 2);
            _readIndex = (_readIndex + drop) % CapacitySamples;
            _availableSamples -= drop;
            _trimmedSamples += drop;
            long now = Environment.TickCount64;
            if (now - _lastTrimLogMs >= 1000)
            {
                _lastTrimLogMs = now;
                AppLogger.LogError(
                    $"Host audio ring trimmed {_trimmedSamples} samples; the backlog exceeded {MaxLatencySamples} frames.");
            }
        }
    }

    /// <summary>
    /// Reads up to <paramref name="wanted"/> frames, zero-filling any shortfall (underrun → silence).
    /// Takes the lock.
    /// </summary>
    public void Read(Span<float> leftDestination, Span<float> rightDestination, int wanted)
    {
        lock (SyncRoot)
        {
            ReadLocked(leftDestination, rightDestination, wanted);
        }
    }

    /// <summary>
    /// Reads up to <paramref name="wanted"/> frames, zero-filling any shortfall.
    /// Caller must already hold <see cref="SyncRoot"/>.
    /// </summary>
    public void ReadLocked(Span<float> leftDestination, Span<float> rightDestination, int wanted)
    {
        int copied = 0;
        int toCopy = Math.Min(wanted, _availableSamples);
        while (copied < toCopy)
        {
            int contiguous = Math.Min(toCopy - copied, CapacitySamples - _readIndex);
            _left.AsSpan(_readIndex, contiguous).CopyTo(leftDestination.Slice(copied, contiguous));
            _right.AsSpan(_readIndex, contiguous).CopyTo(rightDestination.Slice(copied, contiguous));
            _readIndex = (_readIndex + contiguous) % CapacitySamples;
            copied += contiguous;
        }

        _availableSamples -= toCopy;

        if (copied < wanted)
        {
            leftDestination.Slice(copied).Clear();
            rightDestination.Slice(copied).Clear();
        }
    }

    /// <summary>
    /// Reads exactly what is available, up to <paramref name="maxWanted"/> frames, and reports how
    /// many were copied. Unlike <see cref="ReadLocked"/> it does not zero-fill, so a consumer that
    /// feeds a resampler can size its pull from the return value without a second, racy query.
    /// Takes the lock.
    /// </summary>
    public int ReadUpTo(Span<float> leftDestination, Span<float> rightDestination, int maxWanted)
    {
        lock (SyncRoot)
        {
            return ReadUpToLocked(leftDestination, rightDestination, maxWanted);
        }
    }

    /// <summary>Reads exactly what is available, up to <paramref name="maxWanted"/> frames.</summary>
    public int ReadUpToLocked(Span<float> leftDestination, Span<float> rightDestination, int maxWanted)
    {
        int toCopy = Math.Min(maxWanted, _availableSamples);
        int copied = 0;
        while (copied < toCopy)
        {
            int contiguous = Math.Min(toCopy - copied, CapacitySamples - _readIndex);
            _left.AsSpan(_readIndex, contiguous).CopyTo(leftDestination.Slice(copied, contiguous));
            _right.AsSpan(_readIndex, contiguous).CopyTo(rightDestination.Slice(copied, contiguous));
            _readIndex = (_readIndex + contiguous) % CapacitySamples;
            copied += contiguous;
        }

        _availableSamples -= toCopy;
        return copied;
    }
}
