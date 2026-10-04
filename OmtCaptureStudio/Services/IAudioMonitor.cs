using System;

namespace OmtCaptureStudio.Services;

public interface IAudioMonitor : IDisposable
{
    void Start(int sampleRate, int channels);
    void Stop();
    void Feed(byte[] interleavedBytes, int length);
}
