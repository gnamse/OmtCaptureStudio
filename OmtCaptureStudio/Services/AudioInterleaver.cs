using System;
using OmtCaptureStudio.Services.Audio;

namespace OmtCaptureStudio.Services;

public class AudioInterleaver
{
    private byte[]? _reusableInterleavedBuffer;
    private readonly object _lock = new();

    public unsafe byte[] Interleave(IntPtr planarData, int channels, int samplesPerChannel, out int totalBytes)
    {
        // SEC-OMT-01 R4: one policy for both consumers. These were local literals (64 / 192000) while
        // AudioLevelCalculator enforced nothing at all; they are now OmtAudioFrameGuard's constants, so the
        // two consumers of a single frame cannot disagree (measured pre-fix: 65 channels produced 65
        // channel results from the calculator and 0 bytes from here).
        if (planarData == IntPtr.Zero ||
            channels < 1 || channels > OmtAudioFrameGuard.MaxChannels ||
            samplesPerChannel < 1 || samplesPerChannel > OmtAudioFrameGuard.MaxSamplesPerChannel)
        {
            totalBytes = 0;
            return Array.Empty<byte>();
        }

        int totalSamples = channels * samplesPerChannel;
        totalBytes = totalSamples * sizeof(float);

        byte[] buffer;
        lock (_lock)
        {
            if (_reusableInterleavedBuffer == null || _reusableInterleavedBuffer.Length < totalBytes)
            {
                _reusableInterleavedBuffer = new byte[Math.Max(totalBytes, 65536)];
            }
            buffer = _reusableInterleavedBuffer;

            float* pSrc = (float*)planarData.ToPointer();
            fixed (byte* pDstBytes = buffer)
            {
                float* pDst = (float*)pDstBytes;
                for (int s = 0; s < samplesPerChannel; s++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        pDst[s * channels + ch] = (pSrc + (long)ch * samplesPerChannel)[s];
                    }
                }
            }
        }
        return buffer;
    }

    public unsafe byte[] InterleaveNew(IntPtr planarData, int channels, int samplesPerChannel)
    {
        // SEC-OMT-01 R4: one policy for both consumers. These were local literals (64 / 192000) while
        // AudioLevelCalculator enforced nothing at all; they are now OmtAudioFrameGuard's constants, so the
        // two consumers of a single frame cannot disagree (measured pre-fix: 65 channels produced 65
        // channel results from the calculator and 0 bytes from here).
        if (planarData == IntPtr.Zero ||
            channels < 1 || channels > OmtAudioFrameGuard.MaxChannels ||
            samplesPerChannel < 1 || samplesPerChannel > OmtAudioFrameGuard.MaxSamplesPerChannel)
        {
            return Array.Empty<byte>();
        }

        int totalSamples = channels * samplesPerChannel;
        int totalBytes = totalSamples * sizeof(float);
        byte[] buffer = new byte[totalBytes];

        float* pSrc = (float*)planarData.ToPointer();
        fixed (byte* pDstBytes = buffer)
        {
            float* pDst = (float*)pDstBytes;
            for (int s = 0; s < samplesPerChannel; s++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    pDst[s * channels + ch] = (pSrc + (long)ch * samplesPerChannel)[s];
                }
            }
        }
        return buffer;
    }
}
