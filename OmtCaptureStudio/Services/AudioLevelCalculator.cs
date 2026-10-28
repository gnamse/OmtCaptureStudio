using System;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Audio;

namespace OmtCaptureStudio.Services;

public static class AudioLevelCalculator
{
    public static unsafe AudioLevelData ComputeLevels(IntPtr planarData, int channels, int samplesPerChannel)
    {
        // SEC-OMT-01 R4 (defence in depth): this is a public API reached both directly and through
        // AudioEngine.ComputeLevels, so a future caller that bypasses OmtAudioFrameGuard must not be able
        // to re-open the finding. The bounds are OmtAudioFrameGuard's OWN constants, so the two consumers
        // of one frame can never again disagree about what a legal frame is (they did: 65 vs 64 channels).
        //
        // The check runs BEFORE `new ChannelAudioLevel[channels]` below. That ordering is the whole
        // point: an unclamped channel count allocated a 7 GB reference array in ~5 ms before any other
        // validation could run - a remote allocation DoS, not merely an over-read.
        if (planarData == IntPtr.Zero ||
            channels < 1 || channels > OmtAudioFrameGuard.MaxChannels ||
            samplesPerChannel < 1 || samplesPerChannel > OmtAudioFrameGuard.MaxSamplesPerChannel)
        {
            return new AudioLevelData();
        }

        var result = new AudioLevelData
        {
            Channels = new ChannelAudioLevel[channels]
        };

        float* pData = (float*)planarData.ToPointer();

        for (int ch = 0; ch < channels; ch++)
        {
            // SEC-OMT-01 R5: `ch * samplesPerChannel` in int32 wrapped BACKWARDS for reachable pairs
            // (samples=800000 at ch=2999 gave -7 583 069 184), which walks the pointer BEFORE the buffer.
            float* channelBuffer = pData + (long)ch * samplesPerChannel;
            float maxSample = 0f;
            double sumSquares = 0.0;

            for (int i = 0; i < samplesPerChannel; i++)
            {
                float s = channelBuffer[i];
                if (float.IsNaN(s)) continue;
                if (float.IsInfinity(s))
                {
                    maxSample = float.PositiveInfinity;
                    sumSquares = double.PositiveInfinity;
                    continue;
                }
                float val = MathF.Abs(s);
                if (val > maxSample) maxSample = val;
                sumSquares += (double)s * s;
            }

            double rms = Math.Sqrt(sumSquares / samplesPerChannel);

            // Convert to dBFS (-60 dB floor)
            float peakDb = maxSample > 0.00001f ? 20f * MathF.Log10(maxSample) : -60f;
            float rmsDb = rms > 0.00001 ? (float)(20.0 * Math.Log10(rms)) : -60f;

            if (float.IsNaN(peakDb) || float.IsNegativeInfinity(peakDb) || peakDb < -60f) peakDb = -60f;
            else if (float.IsPositiveInfinity(peakDb) || peakDb > 0f) peakDb = 0f;

            if (float.IsNaN(rmsDb) || float.IsNegativeInfinity(rmsDb) || rmsDb < -60f) rmsDb = -60f;
            else if (float.IsPositiveInfinity(rmsDb) || rmsDb > 0f) rmsDb = 0f;

            result.Channels[ch] = new ChannelAudioLevel
            {
                ChannelIndex = ch,
                PeakDb = peakDb,
                RmsDb = rmsDb,
                IsClipping = maxSample >= 0.999f
            };
        }

        return result;
    }
}
