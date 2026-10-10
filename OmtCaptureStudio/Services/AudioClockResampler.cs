using System;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Locks the recording's audio stream to the video frame clock by resampling the
/// interleaved 32-bit float PCM so that, on average, exactly
/// <c>sampleRate / frameRate</c> audio frames are written per video frame.
///
/// Desktop/network capture sources deliver a variable true frame rate (vMix
/// "Maximum Frame Rate 30" typically yields ~29.2 fps) while their audio runs on
/// an independent clock (e.g. a virtual audio cable). FFmpeg is fed raw video and
/// raw audio over named pipes and stamps both at the *nominal* rates we declare
/// (<c>-r</c> / <c>-ar</c>), so any gap between the true and nominal rates makes
/// the two tracks drift apart over a long take — the audible "skipping" a player
/// produces when it periodically re-synchronizes.
///
/// Design: a streaming polyphase windowed-sinc (Blackman-Harris, 96 taps, 64 phases)
/// resampler driven by a rate-PLL. The PLL measures the true clock ratio from
/// *cumulative* video-frame and audio-frame counts and smooths it with an EMA; the
/// cumulative ratio converges exactly to <c>trueFps / nominalFps</c> with no
/// window-boundary quantization bias (a per-window count is biased down by up to one
/// video frame per window, and an instantaneous cumulative-gap P-controller settles on
/// a constant ~1.7% ratio offset — both rejected). The windowed-sinc kernel is
/// band-limited with ~-92 dB stopband attenuation, so near-Nyquist content (cymbals,
/// sibilance) passes with negligible rolloff — transparent for music, unlike linear
/// interpolation which attenuates 19 kHz by ~4.7 dB. At ratio exactly 1.0 the phase-0
/// tap is a unit impulse, so in-sync streams pass through bit-exact.
///
/// The resample ratio is clamped to [0.9, 1.1] as a hard safety band: the true drift
/// is ~2.6% (well inside) and a wild measurement can never pitch audio beyond 10%.
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
    private const double MeasureStartSeconds = 1.0; // start PLL only after ~1s of audio

    private const int HalfWidth = 48;   // filter half-width in samples
    private const int TapCount = HalfWidth * 2; // 96 taps
    private const int PhaseCount = 64;  // polyphase sub-sample resolution

    // Windowed-sinc filter table, [phase * TapCount + tap]. Built once, never mutated.
    private static float[]? s_filter;
    private static readonly object s_filterLock = new();

    private int _channels;
    private int _sampleRate;

    // Rate-PLL state (cumulative video frames vs cumulative audio input frames).
    private long _videoFrames;
    private long _audioInFrames;
    private double _ratio = 1.0;

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

    public long VideoFramesSeen => _videoFrames;

    /// <summary>
    /// Re-arms the resampler for a new recording session, using the (measured) true
    /// frame rate that FFmpeg will stamp video with, and the stream's sample rate/channels.
    /// </summary>
    public void Reset(int sampleRate, double frameRate, int channels)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _channels = channels > 0 ? channels : 2;
        SamplesPerFrame = _sampleRate / (frameRate > 0 ? frameRate : 30.0);
        _videoFrames = 0;
        _audioInFrames = 0;
        _ratio = 1.0;
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

    /// <summary>Report one video frame received (keeps the running clock in step).</summary>
    public void OnVideoFrame()
    {
        _videoFrames++;
    }

    /// <summary>
    /// Resamples one interleaved float32 PCM chunk and returns the reusable output
    /// buffer (valid only until the next call). <paramref name="outCount"/> is the
    /// number of bytes written.
    /// </summary>
    public byte[] Process(byte[] interleaved, int count, out int outCount)
    {
        int ch = _channels;
        int inFrames = count / (ch * 4);
        if (inFrames <= 0)
        {
            outCount = 0;
            return _outBytes;
        }

        // 1. Re-estimate the true clock ratio from cumulative counts and smooth it.
        //    Converges exactly to trueFps/nominalFps once enough audio has flowed.
        _audioInFrames += inFrames;
        if (_audioInFrames >= (long)(_sampleRate * MeasureStartSeconds) && _videoFrames > 0)
        {
            double measured = (_videoFrames * SamplesPerFrame) / _audioInFrames;
            _ratio += (measured - _ratio) * RatioSmoothing;
            if (_ratio < MinRatio) _ratio = MinRatio;
            else if (_ratio > MaxRatio) _ratio = MaxRatio;
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
