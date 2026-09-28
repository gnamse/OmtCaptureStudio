using NAudio.Wave;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

public class AudioProcessor : IDisposable
{
    private BufferedWaveProvider? _waveProvider;
    private WasapiOut? _audioOut;
    private bool _isMonitoringEnabled;
    private int _currentSampleRate;
    private int _currentChannels;
    private readonly object _lock = new();

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
    /// Interleaves planar 32-bit float audio into an interleaved byte array for FFmpeg input pipe or playback.
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
                    // In planar format, channel 'ch' starts at pSrc + (ch * samplesPerChannel)
                    pDst[s * channels + ch] = pSrc[ch * samplesPerChannel + s];
                }
            }
        }

        return buffer;
    }

    /// <summary>
    /// Feeds interleaved float audio buffer to local Windows audio output if monitoring is enabled.
    /// </summary>
    public void FeedMonitoringAudio(byte[] interleavedFloatBytes, int sampleRate, int channels)
    {
        if (!_isMonitoringEnabled) return;

        lock (_lock)
        {
            if (_waveProvider == null || _currentSampleRate != sampleRate || _currentChannels != channels)
            {
                StartMonitoringInternal(sampleRate, channels);
            }

            if (_waveProvider != null)
            {
                // Prevent buffer overrun if UI slows down
                if (_waveProvider.BufferedBytes + interleavedFloatBytes.Length <= _waveProvider.BufferLength)
                {
                    _waveProvider.AddSamples(interleavedFloatBytes, 0, interleavedFloatBytes.Length);
                }
            }
        }
    }

    private void StartMonitoringInternal(int sampleRate, int channels)
    {
        StopMonitoringInternal();

        try
        {
            _currentSampleRate = sampleRate;
            _currentChannels = channels;

            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _waveProvider = new BufferedWaveProvider(waveFormat)
            {
                BufferLength = waveFormat.AverageBytesPerSecond * 2,
                DiscardOnBufferOverflow = true
            };

            _audioOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
            _audioOut.Init(_waveProvider);
            _audioOut.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio Monitoring Error] {ex.Message}");
            StopMonitoringInternal();
        }
    }

    private void StopMonitoringInternal()
    {
        try
        {
            if (_audioOut != null)
            {
                _audioOut.Stop();
                _audioOut.Dispose();
                _audioOut = null;
            }
            _waveProvider = null;
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopMonitoringInternal();
        }
    }
}
