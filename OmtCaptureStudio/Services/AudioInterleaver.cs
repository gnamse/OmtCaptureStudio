using System;

namespace OmtCaptureStudio.Services;

public class AudioInterleaver
{
    private byte[]? _reusableInterleavedBuffer;
    private readonly object _lock = new();

    public unsafe byte[] Interleave(IntPtr planarData, int channels, int samplesPerChannel, out int totalBytes)
    {
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
                        pDst[s * channels + ch] = pSrc[ch * samplesPerChannel + s];
                    }
                }
            }
        }
        return buffer;
    }

    public unsafe byte[] InterleaveNew(IntPtr planarData, int channels, int samplesPerChannel)
    {
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
                    pDst[s * channels + ch] = pSrc[ch * samplesPerChannel + s];
                }
            }
        }
        return buffer;
    }
}
