using System;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

public static class AudioLevelCalculator
{
    public static unsafe AudioLevelData ComputeLevels(IntPtr planarData, int channels, int samplesPerChannel)
    {
        if (planarData == IntPtr.Zero || channels <= 0 || samplesPerChannel <= 0)
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
            float* channelBuffer = pData + (ch * samplesPerChannel);
            float maxSample = 0f;
            double sumSquares = 0.0;

            for (int i = 0; i < samplesPerChannel; i++)
            {
                float val = MathF.Abs(channelBuffer[i]);
                if (val > maxSample) maxSample = val;
                sumSquares += channelBuffer[i] * channelBuffer[i];
            }

            double rms = Math.Sqrt(sumSquares / samplesPerChannel);

            // Convert to dBFS (-60 dB floor)
            float peakDb = maxSample > 0.00001f ? 20f * MathF.Log10(maxSample) : -60f;
            float rmsDb = rms > 0.00001 ? (float)(20.0 * Math.Log10(rms)) : -60f;

            if (peakDb < -60f) peakDb = -60f;
            if (peakDb > 0f) peakDb = 0f;
            if (rmsDb < -60f) rmsDb = -60f;
            if (rmsDb > 0f) rmsDb = 0f;

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
