using System;

namespace OmtCaptureStudio.Services.Audio;

/// <summary>
/// Stereo fixed-ratio polyphase windowed-sinc (Blackman-Harris, 96 taps / 64 phases) resampler —
/// ADR-0003's single rate-conversion policy for BOTH host-audio adapters. Same kernel ideals as
/// <c>AudioClockResampler</c> (~92 dB stopband, DC-normalised phases, unit impulse at phase 0) but
/// driven by a plain constant input/output rate pair rather than a video-coupled PLL.
///
/// <para><b>Threading:</b> confined to whichever thread calls it — always the CONSUMER
/// (<see cref="IHostAudioSource.FillPlanarAudio"/>), never a real-time capture callback. That is the
/// whole point of the module: running ~96 MACs/sample/channel on a driver callback is the hazard the
/// ADR names, and the consumer already carries exactly this load for ASIO.</para>
///
/// <para><b>At a matching rate the caller should not construct this at all</b> (pass a null and read
/// the ring straight through); when constructed at ratio 1.0 it is nevertheless bit-exact, because
/// phase 0 of the kernel is a unit impulse.</para>
/// </summary>
public sealed class FixedRatioStereoResampler
{
    private const int HalfWidth = 48;
    private const int TapCount = HalfWidth * 2; // 96 taps
    private const int PhaseCount = 64;
    private const int MaxFifoFrames = 65536;

    private static readonly float[] s_filter = BuildFilter();

    private readonly double _step;       // input frames consumed per output frame
    private float[] _fifoLeft;
    private float[] _fifoRight;
    private int _fifoCount;
    private double _readPos;

    /// <summary>True when the source and target rates match, so this resampler is a bit-exact identity.</summary>
    public bool IsPassthrough => _step == 1.0;

    /// <summary>Input frames consumed per output frame (sourceRate / targetRate).</summary>
    public double Step => _step;

    /// <summary>
    /// Lookahead, in input frames, the FIR needs before it can emit its first sample. This is the
    /// documented added latency of the unified policy: ~1 ms at 48 kHz.
    /// </summary>
    public static int LookaheadFrames => HalfWidth;

    public FixedRatioStereoResampler(double sourceRate, double targetRate)
    {
        if (sourceRate <= 0) sourceRate = HostAudioFormat.TargetSampleRate;
        if (targetRate <= 0) targetRate = HostAudioFormat.TargetSampleRate;
        _step = sourceRate / targetRate;

        // Prime with HalfWidth frames of silence so the FIR has left-edge history for sample 0.
        _fifoLeft = new float[HalfWidth * 2 + 4096];
        _fifoRight = new float[HalfWidth * 2 + 4096];
        _fifoCount = HalfWidth;
        _readPos = HalfWidth;
    }

    public int MaxFeedFrames(int wanted) => (int)(wanted * 8) + 4096;

    public void Append(float[] left, float[] right, int frames)
    {
        if (frames <= 0) return;

        EnsureCapacity(_fifoCount + frames);
        Array.Copy(left, 0, _fifoLeft, _fifoCount, frames);
        Array.Copy(right, 0, _fifoRight, _fifoCount, frames);
        _fifoCount += frames;

        // Safety valve: if no output is ever requested, never grow without bound.
        if (_fifoCount > HalfWidth + MaxFifoFrames) Compact();
    }

    /// <summary>Produces up to <paramref name="wanted"/> frames per channel; returns how many were produced.</summary>
    public int Process(Span<float> leftDest, Span<float> rightDest, int wanted)
    {
        if (wanted <= 0) return 0;
        int limit = Math.Min(wanted, Math.Min(leftDest.Length, rightDest.Length));
        float[] filter = s_filter;
        int produced = 0;

        while (produced < limit)
        {
            int i = (int)_readPos;
            // Need HalfWidth-1 frames of history before i and HalfWidth frames of lookahead after.
            if (i - HalfWidth + 1 < 0 || i + HalfWidth > _fifoCount) break;

            int phase = (int)((_readPos - i) * PhaseCount + 0.5);
            if (phase >= PhaseCount) phase = PhaseCount - 1;
            int p = phase * TapCount;
            int baseIdx = i - HalfWidth + 1;

            double accL = 0.0;
            double accR = 0.0;
            for (int t = 0; t < TapCount; t++)
            {
                float k = filter[p + t];
                accL += k * _fifoLeft[baseIdx + t];
                accR += k * _fifoRight[baseIdx + t];
            }
            leftDest[produced] = (float)accL;
            rightDest[produced] = (float)accR;
            produced++;
            _readPos += _step;
        }

        Compact();
        return produced;
    }

    private void Compact()
    {
        int dropCount = (int)_readPos - (HalfWidth - 1);
        int maxDrop = _fifoCount - (HalfWidth + 1); // always retain HalfWidth+1 frames
        if (dropCount < 0) dropCount = 0;
        if (dropCount > maxDrop) dropCount = maxDrop;
        if (dropCount > 0)
        {
            int remain = _fifoCount - dropCount;
            Array.Copy(_fifoLeft, dropCount, _fifoLeft, 0, remain);
            Array.Copy(_fifoRight, dropCount, _fifoRight, 0, remain);
            _fifoCount = remain;
            _readPos -= dropCount;
        }
    }

    private void EnsureCapacity(int frames)
    {
        if (_fifoLeft.Length < frames)
        {
            int size = frames + 4096;
            Array.Resize(ref _fifoLeft, size);
            Array.Resize(ref _fifoRight, size);
        }
    }

    private static float[] BuildFilter()
    {
        var table = new float[PhaseCount * TapCount];
        for (int q = 0; q < PhaseCount; q++)
        {
            double frac = (double)q / PhaseCount;
            double sum = 0.0;
            for (int j = 0; j < TapCount; j++)
            {
                double m = j - HalfWidth + 1;   // tap offset from the read index
                double t = m - frac;            // signed distance from the ideal sample position
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

    /// <summary>Symmetrical 4-term Blackman-Harris window, normalised so w(0) == 1.</summary>
    private static double BlackmanHarris(double t)
    {
        double arg = Math.PI * t / HalfWidth;
        return 0.35875
             + 0.48829 * Math.Cos(arg)
             + 0.14128 * Math.Cos(2.0 * arg)
             + 0.01168 * Math.Cos(3.0 * arg);
    }
}
