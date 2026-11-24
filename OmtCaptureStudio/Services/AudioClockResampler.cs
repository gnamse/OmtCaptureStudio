using System;
using System.Diagnostics;
using System.Threading;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Keeps recorded audio on the wall clock. Output length tracks elapsed time at the
/// stream sample rate; a slow or fast video frame count is not allowed to pitch-shift
/// the mix (ADR-0014). A small PLL still corrects a real clock skew between the audio
/// samples and the wall clock.
///
/// Design: a streaming polyphase windowed-sinc (Blackman-Harris, 96 taps, 64 phases)
/// resampler driven by that PLL. The measurement is <c>elapsedWall * sampleRate /
/// samplesReceived</c>, smoothed with an EMA. The windowed-sinc kernel is band-limited
/// with ~-92 dB stopband attenuation, so near-Nyquist content passes with negligible
/// rolloff. At ratio exactly 1.0 the phase-0 tap is a unit impulse, so in-sync streams
/// pass through bit-exact.
///
/// The resample ratio is clamped to [0.9, 1.1] as a hard safety band: a wild
/// measurement can never pitch audio beyond 10%. If the ratio stays on that clamp for
/// more than <see cref="ClampWarnSeconds"/>, the take logs it once and the stop
/// summary reports how long the clamp was held.
///
/// Threading: confined to the media/source thread (video and audio callbacks are
/// raised on the same thread). Headless-constructible and allocation-light — it
/// reuses its FIFO/output buffers, the filter table is built once, and the hot loop
/// performs no per-frame allocation. Native AOT safe (no reflection, no dynamic code).
/// </summary>
public sealed class AudioClockResampler
{
    private const double MinRatio = 0.9;
    private const double MaxRatio = 1.1;
    private const double RatioSmoothing = 0.1;     // EMA weight toward the cumulative estimate
    private const double MeasureStartSeconds = 1.0; // start PLL only after ~1s of audio and wall time

    /// <summary>A clamp that stays pinned this long is a warning, not a quiet pitch shift.</summary>
    public const double ClampWarnSeconds = 3.0;

    private const int HalfWidth = 48;   // filter half-width in samples
    private const int TapCount = HalfWidth * 2; // 96 taps
    private const int PhaseCount = 64;  // polyphase sub-sample resolution

    // Windowed-sinc filter table, [phase * TapCount + tap]. Built once, never mutated.
    private static float[]? s_filter;
    private static readonly object s_filterLock = new();

    private readonly object _processLock = new();
    private int _channels = 2;
    private int _sampleRate = 48000;

    // Rate-PLL state. The reference is the wall clock, not the video frame count.
    private long _videoFrames;
    private long _audioInFrames;
    private double _ratio = 1.0;
    private volatile bool _ratioClamped;
    private Func<long> _clock = static () => Stopwatch.GetTimestamp();
    private long _frequency = Stopwatch.Frequency;
    private long _clockOrigin;
    private long _clampPinStart;
    private long _lastClampSample;
    private long _clampPinnedTotalTicks;
    private int _clampWarned;

    // Streaming resampler state. `_fifo` holds interleaved input frames — the
    // HalfWidth-1 frames of FIR history plus the not-yet-consumed tail, oldest first.
    // `_readPos` is the fractional read position (in input frames) of the next output
    // sample, kept in [HalfWidth-1, HalfWidth) relative to _fifo[0]. `_outAcc` is a
    // fractional output-frame accumulator that makes the total output count exact over
    // arbitrarily long runs (integer floor/wrap, so it can never drift).
    private float[] _fifo = Array.Empty<float>();
    private int _fifoCount;
    private float[] _out = Array.Empty<float>();
    private byte[] _outBytes = Array.Empty<byte>();
    private double _readPos;
    private double _outAcc;

    /// <summary>Target audio frames per video frame (<c>sampleRate / frameRate</c>).</summary>
    public double SamplesPerFrame { get; private set; }

    /// <summary>Current resample ratio (output frames per input frame).</summary>
    public double Ratio => _ratio;

    /// <summary>
    /// True when the ratio is pinned against the [0.9, 1.1] safety clamp — i.e. the measured A/V
    /// clock drift has run past what the resampler is willing to correct, which is worth surfacing
    /// (FEAT-07). Read cross-thread, hence volatile.
    /// </summary>
    public bool IsRatioClamped => _ratioClamped;

    /// <summary>Seconds the safety clamp has been held during this take.</summary>
    public double ClampPinnedSeconds
    {
        get
        {
            long ticks = Interlocked.Read(ref _clampPinnedTotalTicks);
            long frequency = _frequency;
            return frequency > 0 ? ticks / (double)frequency : 0;
        }
    }

    /// <summary>True once the clamp has stayed pinned for <see cref="ClampWarnSeconds"/>.</summary>
    public bool ClampWarningRaised => Volatile.Read(ref _clampWarned) != 0;

    public long VideoFramesSeen => Interlocked.Read(ref _videoFrames);

    /// <summary>
    /// Re-arms the resampler for a new recording session. <paramref name="frameRate"/> is the
    /// announced output rate (kept for callers that still read <see cref="SamplesPerFrame"/>);
    /// it does not drive the resample ratio.
    /// </summary>
    public void Reset(int sampleRate, double frameRate, int channels)
        => Reset(sampleRate, frameRate, channels, null, 0);

    /// <summary>
    /// Re-arms the resampler. <paramref name="mediaClock"/> and <paramref name="mediaFrequency"/>
    /// are the same clock the video conformer uses, so a test can advance both without sleeping.
    /// A null clock reads <see cref="Stopwatch"/>.
    /// </summary>
    public void Reset(int sampleRate, double frameRate, int channels, Func<long>? mediaClock, long mediaFrequency)
    {
        lock (_processLock)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            _channels = channels > 0 ? channels : 2;
            SamplesPerFrame = _sampleRate / (frameRate > 0 ? frameRate : 30.0);
            _clock = mediaClock ?? (static () => Stopwatch.GetTimestamp());
            _frequency = mediaFrequency > 0 ? mediaFrequency : Stopwatch.Frequency;
            _clockOrigin = _clock();
            _clampPinStart = 0;
            _lastClampSample = 0;
            Interlocked.Exchange(ref _clampPinnedTotalTicks, 0);
            Volatile.Write(ref _clampWarned, 0);
            Interlocked.Exchange(ref _videoFrames, 0);
            _audioInFrames = 0;
            _ratio = 1.0;
            _ratioClamped = false;
            _outAcc = 0.0;

            // Prime the FIFO with HalfWidth frames of silence so the FIR has its left-edge
            // history for the first real sample, and start reading at the first real frame.
            _fifoCount = HalfWidth;
            if (_fifo.Length < HalfWidth * _channels)
            {
                Array.Resize(ref _fifo, HalfWidth * _channels);
            }
            Array.Clear(_fifo, 0, HalfWidth * _channels);
            _readPos = HalfWidth;
        }
    }

    /// <summary>Counts a written video picture. Telemetry only; it does not move the resample ratio.</summary>
    public void OnVideoFrame()
    {
        Interlocked.Increment(ref _videoFrames);
    }

    /// <summary>
    /// Resamples one interleaved float32 PCM chunk and returns the reusable output
    /// buffer (valid only until the next call). <paramref name="outCount"/> is the
    /// number of bytes written.
    /// </summary>
    public byte[] Process(byte[] interleaved, int count, out int outCount)
    {
        lock (_processLock)
        {
            int ch = _channels;
            if (ch <= 0 || interleaved == null || count <= 0 || count > interleaved.Length)
            {
                outCount = 0;
                return _outBytes;
            }

            int inFrames = count / (ch * 4);
            if (inFrames <= 0)
            {
                outCount = 0;
                return _outBytes;
            }

            // 1. Re-estimate the ratio from wall time versus samples received. A video frame
            //    count is intentionally not in this formula: slaving audio to it is what pinned
            //    a field take at the 1.1 clamp and slowed the mix.
            _audioInFrames += inFrames;
            long now = _clock();
            double elapsed = _frequency > 0 ? (now - _clockOrigin) / (double)_frequency : 0;
            if (_audioInFrames >= (long)(_sampleRate * MeasureStartSeconds) && elapsed >= MeasureStartSeconds)
            {
                double measured = (elapsed * _sampleRate) / _audioInFrames;
                _ratio += (measured - _ratio) * RatioSmoothing;
                bool pinned;
                if (_ratio < MinRatio)
                {
                    _ratio = MinRatio;
                    pinned = true;
                }
                else if (_ratio > MaxRatio)
                {
                    _ratio = MaxRatio;
                    pinned = true;
                }
                else
                {
                    pinned = false;
                }

                _ratioClamped = pinned;
                NoteClamp(now, pinned);
            }

            // 2. Append the incoming chunk to the FIFO.
            int needFrames = _fifoCount + inFrames;
            if (_fifo.Length < needFrames * ch)
            {
                Array.Resize(ref _fifo, Math.Max(needFrames * ch, Math.Max(4096, _fifo.Length * 2)));
            }
            Buffer.BlockCopy(interleaved, 0, _fifo, _fifoCount * ch * 4, inFrames * ch * 4);
            _fifoCount += inFrames;

            // 3. Exact output count from a fractional accumulator (no float drift).
            _outAcc += inFrames * _ratio;
            int want = (int)_outAcc;
            _outAcc -= want;

            // 4. Produce output frames by polyphase windowed-sinc convolution.
            if (_out.Length < want * ch)
            {
                Array.Resize(ref _out, want * ch);
                Array.Resize(ref _outBytes, want * ch * 4);
            }

            float[] filter = GetFilter();
            double step = 1.0 / _ratio; // input frames consumed per output frame
            int produced = 0;
            while (produced < want)
            {
                int i = (int)_readPos;
                // Need HalfWidth-1 frames of history before i and HalfWidth of lookahead after.
                if (i - HalfWidth + 1 < 0 || i + HalfWidth >= _fifoCount) break;

                int phase = (int)((_readPos - i) * PhaseCount + 0.5);
                if (phase >= PhaseCount) phase = PhaseCount - 1;

                int baseIdx = (i - HalfWidth + 1) * ch;
                int p = phase * TapCount;
                for (int c = 0; c < ch; c++)
                {
                    double acc = 0.0;
                    for (int t = 0; t < TapCount; t++)
                    {
                        acc += filter[p + t] * _fifo[baseIdx + t * ch + c];
                    }
                    _out[produced * ch + c] = (float)acc;
                }
                produced++;
                _readPos += step;
            }

            // 5. Carry any deferred output so the long-run rate stays exact.
            if (produced < want)
            {
                _outAcc += want - produced;
            }

            // 6. Drop frames fully consumed by the FIR, retaining HalfWidth-1 frames of
            //    history before the read position for the next convolution.
            int dropCount = (int)_readPos - (HalfWidth - 1);
            int maxDrop = _fifoCount - (HalfWidth + 1); // always retain HalfWidth+1 frames
            if (dropCount < 0) dropCount = 0;
            if (dropCount > maxDrop) dropCount = maxDrop;
            if (dropCount > 0)
            {
                int remain = _fifoCount - dropCount;
                Array.Copy(_fifo, dropCount * ch, _fifo, 0, remain * ch);
                _fifoCount = remain;
                _readPos -= dropCount;
            }

            // 7. Copy produced floats out to the reusable byte buffer.
            outCount = produced * ch * 4;
            if (outCount > 0)
            {
                Buffer.BlockCopy(_out, 0, _outBytes, 0, outCount);
            }
            return _outBytes;
        }
    }

    private void NoteClamp(long now, bool pinned)
    {
        if (!pinned)
        {
            _clampPinStart = 0;
            _lastClampSample = 0;
            return;
        }

        if (_clampPinStart == 0) _clampPinStart = now;
        if (_lastClampSample != 0 && now > _lastClampSample)
        {
            Interlocked.Add(ref _clampPinnedTotalTicks, now - _lastClampSample);
        }

        _lastClampSample = now;
        long continuous = now - _clampPinStart;
        if (Volatile.Read(ref _clampWarned) == 0
            && _frequency > 0
            && continuous >= (long)(_frequency * ClampWarnSeconds))
        {
            Volatile.Write(ref _clampWarned, 1);
            AppLogger.LogError(
                $"Recording audio resample ratio is pinned at {_ratio:F3} for {ClampWarnSeconds:F0}s. " +
                "The safety clamp is holding the pitch; recorded audio length will not match wall time.");
        }
    }

    private static float[] GetFilter()
    {
        if (s_filter == null)
        {
            lock (s_filterLock)
            {
                s_filter ??= BuildFilter();
            }
        }
        return s_filter;
    }

    /// <summary>
    /// Builds the normalized Blackman-Harris windowed-sinc polyphase filter table.
    /// Each phase is DC-normalized (taps sum to 1) so a constant signal passes at
    /// unity gain; phase 0 is a unit impulse, giving bit-exact passthrough at ratio 1.0.
    /// </summary>
    private static float[] BuildFilter()
    {
        var table = new float[PhaseCount * TapCount];
        for (int q = 0; q < PhaseCount; q++)
        {
            double frac = (double)q / PhaseCount;
            double sum = 0.0;
            for (int j = 0; j < TapCount; j++)
            {
                double m = j - HalfWidth + 1;      // tap offset from the read index
                double t = m - frac;               // signed distance from the ideal position
                double v = Sinc(t) * BlackmanHarris(t);
                table[q * TapCount + j] = (float)v;
                sum += v;
            }
            if (sum > 1e-9)
            {
                for (int j = 0; j < TapCount; j++)
                {
                    table[q * TapCount + j] = (float)(table[q * TapCount + j] / sum);
                }
            }
        }
        return table;
    }

    private static double Sinc(double t)
    {
        if (Math.Abs(t) < 1e-12) return 1.0;
        double x = Math.PI * t;
        return Math.Sin(x) / x;
    }

    /// <summary>Symmetrical 4-term Blackman-Harris window, normalized so w(0) == 1.</summary>
    private static double BlackmanHarris(double t)
    {
        double arg = Math.PI * t / HalfWidth;
        return 0.35875
             + 0.48829 * Math.Cos(arg)
             + 0.14128 * Math.Cos(2.0 * arg)
             + 0.01168 * Math.Cos(3.0 * arg);
    }
}
