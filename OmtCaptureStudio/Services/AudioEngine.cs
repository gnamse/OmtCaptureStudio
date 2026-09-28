using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Multimedia;
using Vortice.XAudio2;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Deep audio subsystem managing planar-to-interleaved conversion, buffer pooling,
/// real-time RMS/peak level metering, speaker monitoring via Vortice.XAudio2 (Native AOT safe),
/// and tap routing.
/// </summary>
public class AudioEngine : IDisposable
{
    private IXAudio2? _xaudio2;
    private IXAudio2MasteringVoice? _masterVoice;
    private IXAudio2SourceVoice? _sourceVoice;

    private bool _isMonitoringEnabled;
    private int _currentSampleRate;
    private int _currentChannels;
    private readonly object _lock = new();

    // Internal reusable buffer to prevent GC allocations
    private byte[]? _reusableInterleavedBuffer;

    // Ring buffer of native memory slots for low-latency XAudio2 streaming
    private const int RingSlotCount = 16;
    private const int RingSlotByteSize = 65536;
    private readonly IntPtr[] _ringBuffers = new IntPtr[RingSlotCount];
    private int _ringWriteIndex = 0;

    public AudioEngine()
    {
        for (int i = 0; i < RingSlotCount; i++)
        {
            _ringBuffers[i] = Marshal.AllocHGlobal(RingSlotByteSize);
        }
    }

    public bool IsMonitoringEnabled
    {
        get => _isMonitoringEnabled;
        set
        {
            lock (_lock)
            {
                _isMonitoringEnabled = value;
                if (!_isMonitoringEnabled)
                {
                    StopMonitoringInternal();
                }
                else if (_currentSampleRate > 0 && _currentChannels > 0)
                {
                    StartMonitoringInternal(_currentSampleRate, _currentChannels);
                }
            }
        }
    }

    /// <summary>
    /// Event fired when new VU audio levels are computed.
    /// </summary>
    public event Action<AudioLevelData>? LevelsCalculated;

    /// <summary>
    /// Event fired when interleaved PCM byte data is ready for downstream recording or processing.
    /// Passes the buffer and the valid byte count.
    /// </summary>
    public event Action<byte[], int>? AudioInterleavedAvailable;

    /// <summary>
    /// Ingests a raw planar 32-bit float audio frame from the media receiver.
    /// Computes levels, converts to interleaved PCM, feeds local monitoring, and dispatches to taps.
    /// </summary>
    public unsafe void IngestAudioFrame(IntPtr planarData, int channels, int samplesPerChannel, int sampleRate, long timestamp)
    {
        if (planarData == IntPtr.Zero || channels <= 0 || samplesPerChannel <= 0) return;

        // 1. Calculate VU meter levels
        var levelData = ComputeLevels(planarData, channels, samplesPerChannel);
        LevelsCalculated?.Invoke(levelData);

        // 2. Interleave into reusable pooled memory
        int totalSamples = channels * samplesPerChannel;
        int totalBytes = totalSamples * sizeof(float);

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

            // 3. Feed local monitoring if active
            if (_isMonitoringEnabled)
            {
                FeedMonitoringInternal(buffer, totalBytes, sampleRate, channels);
            }

            // 4. Dispatch to downstream recording taps
            AudioInterleavedAvailable?.Invoke(buffer, totalBytes);
        }
    }

    /// <summary>
    /// Computes channel audio levels (Peak dBFS, RMS dBFS, Clipping) from planar 32-bit float samples.
    /// </summary>
    public unsafe AudioLevelData ComputeLevels(IntPtr planarData, int channels, int samplesPerChannel)
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

    /// <summary>
    /// Interleaves planar 32-bit float audio into a newly allocated byte array (utility/compatibility).
    /// </summary>
    public unsafe byte[] InterleaveFloatAudio(IntPtr planarData, int channels, int samplesPerChannel)
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

    private void FeedMonitoringInternal(byte[] interleavedBytes, int length, int sampleRate, int channels)
    {
        try
        {
            if (_sourceVoice == null || _currentSampleRate != sampleRate || _currentChannels != channels)
            {
                StartMonitoringInternal(sampleRate, channels);
            }

            if (_sourceVoice != null)
            {
                // Drop buffer if too many are queued (> 6 chunks ~ 120ms) to prevent drift/delay
                if (_sourceVoice.State.BuffersQueued > 6)
                {
                    return;
                }

                int slot = _ringWriteIndex;
                _ringWriteIndex = (_ringWriteIndex + 1) % RingSlotCount;

                IntPtr destPtr = _ringBuffers[slot];
                if (destPtr == IntPtr.Zero) return;

                int copyBytes = Math.Min(length, RingSlotByteSize);
                Marshal.Copy(interleavedBytes, 0, destPtr, copyBytes);

                var audioBuffer = new AudioBuffer(destPtr, (uint)copyBytes);

                _sourceVoice.SubmitSourceBuffer(audioBuffer);
            }
        }
        catch
        {
            // If monitoring fails (e.g. device lost or buffer submit error), shut down cleanly
            StopMonitoringInternal();
            _isMonitoringEnabled = false;
        }
    }

    private void StartMonitoringInternal(int sampleRate, int channels)
    {
        StopMonitoringInternal();

        try
        {
            _currentSampleRate = sampleRate;
            _currentChannels = channels;

            _xaudio2 = XAudio2.XAudio2Create();
            _masterVoice = _xaudio2.CreateMasteringVoice();

            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _sourceVoice = _xaudio2.CreateSourceVoice(waveFormat);
            _sourceVoice.Start();
        }
        catch
        {
            StopMonitoringInternal();
            _isMonitoringEnabled = false;
        }
    }

    private void StopMonitoringInternal()
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
            StopMonitoringInternal();
            _reusableInterleavedBuffer = null;

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
