using System;
using System.Runtime.InteropServices;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace OmtCaptureStudio.Services;

public class XAudio2Monitor : IAudioMonitor
{
    private IXAudio2? _xaudio2;
    private IXAudio2MasteringVoice? _masterVoice;
    private IXAudio2SourceVoice? _sourceVoice;

    private int _currentSampleRate;
    private int _currentChannels;
    private ulong _totalFramesSubmitted;

    // Ring buffer of native memory slots for low-latency XAudio2 streaming
    private const int RingSlotCount = 64;
    private const int RingSlotByteSize = 65536;
    private readonly IntPtr[] _ringBuffers = new IntPtr[RingSlotCount];
    private int _ringWriteIndex = 0;
    private readonly object _lock = new();

    public XAudio2Monitor()
    {
        for (int i = 0; i < RingSlotCount; i++)
        {
            _ringBuffers[i] = Marshal.AllocHGlobal(RingSlotByteSize);
        }
    }

    public void Start(int sampleRate, int channels)
    {
        lock (_lock)
        {
            StopInternal();
            try
            {
                _currentSampleRate = sampleRate;
                _currentChannels = channels;
                _totalFramesSubmitted = 0;

                _xaudio2 = XAudio2.XAudio2Create();
                _masterVoice = _xaudio2.CreateMasteringVoice();

                var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
                _sourceVoice = _xaudio2.CreateSourceVoice(waveFormat);
                _sourceVoice.Start();
            }
            catch
            {
                StopInternal();
            }
        }
    }

    public void Feed(byte[] interleavedBytes, int length)
    {
        lock (_lock)
        {
            try
            {
                if (_sourceVoice != null)
                {
                    int copyBytes = Math.Min(length, RingSlotByteSize);
                    if (copyBytes <= 0) return;

                    int frameSize = 4 * _currentChannels; // IEEE float is 4 bytes
                    uint framesInChunk = (uint)(copyBytes / frameSize);

                    ulong framesQueued = _totalFramesSubmitted - _sourceVoice.State.SamplesPlayed;
                    ulong maxFramesQueued = (ulong)(_currentSampleRate * 0.12); // ~120ms

                    // Drop buffer if too many frames are queued (~120ms) or max XAudio2 buffers hit
                    if (framesQueued > maxFramesQueued || _sourceVoice.State.BuffersQueued >= 60)
                    {
                        return;
                    }

                    int slot = _ringWriteIndex;
                    _ringWriteIndex = (_ringWriteIndex + 1) % RingSlotCount;

                    IntPtr destPtr = _ringBuffers[slot];
                    if (destPtr == IntPtr.Zero) return;

                    Marshal.Copy(interleavedBytes, 0, destPtr, copyBytes);

                    var audioBuffer = new AudioBuffer(destPtr, (uint)copyBytes);

                    _sourceVoice.SubmitSourceBuffer(audioBuffer);
                    
                    _totalFramesSubmitted += framesInChunk;
                }
            }
            catch
            {
                StopInternal();
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        try
        {
            if (_sourceVoice != null)
            {
                _sourceVoice.Stop();
                _sourceVoice.FlushSourceBuffers();
                _sourceVoice.DestroyVoice();
                _sourceVoice.Dispose();
                _sourceVoice = null;
            }

            if (_masterVoice != null)
            {
                _masterVoice.DestroyVoice();
                _masterVoice.Dispose();
                _masterVoice = null;
            }

            if (_xaudio2 != null)
            {
                _xaudio2.Dispose();
                _xaudio2 = null;
            }
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopInternal();

            for (int i = 0; i < RingSlotCount; i++)
            {
                if (_ringBuffers[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_ringBuffers[i]);
                    _ringBuffers[i] = IntPtr.Zero;
                }
            }
        }
        GC.SuppressFinalize(this);
    }
}
