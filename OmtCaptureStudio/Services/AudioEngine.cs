using System;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

public class AudioEngine : IDisposable
{
    private readonly AudioInterleaver _interleaver;
    private readonly IAudioMonitor _monitor;

    private bool _isMonitoringEnabled;
    private int _currentSampleRate;
    private int _currentChannels;
    private readonly object _lock = new();

    public AudioEngine()
    {
        _interleaver = new AudioInterleaver();
        _monitor = new XAudio2Monitor();
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
                    _monitor.Stop();
                }
                else if (_currentSampleRate > 0 && _currentChannels > 0)
                {
                    _monitor.Start(_currentSampleRate, _currentChannels);
                }
            }
        }
    }

    public event Action<AudioLevelData>? LevelsCalculated;
    public event Action<byte[], int>? AudioInterleavedAvailable;

    public unsafe void IngestAudioFrame(IntPtr planarData, int channels, int samplesPerChannel, int sampleRate, long timestamp)
    {
        if (planarData == IntPtr.Zero || channels <= 0 || samplesPerChannel <= 0) return;

        // 1. Calculate VU meter levels
        var levelData = AudioLevelCalculator.ComputeLevels(planarData, channels, samplesPerChannel);
        LevelsCalculated?.Invoke(levelData);

        // 2. Interleave into reusable pooled memory
        int totalBytes;
        byte[] buffer = _interleaver.Interleave(planarData, channels, samplesPerChannel, out totalBytes);

        // 3. Feed local monitoring if active
        lock (_lock)
        {
            if (_isMonitoringEnabled)
            {
                if (_currentSampleRate != sampleRate || _currentChannels != channels)
                {
                    _currentSampleRate = sampleRate;
                    _currentChannels = channels;
                    _monitor.Start(sampleRate, channels);
                }
                _monitor.Feed(buffer, totalBytes);
            }
        }

        // 4. Dispatch to downstream recording taps outside lock (_lock)
        AudioInterleavedAvailable?.Invoke(buffer, totalBytes);
    }

    public unsafe AudioLevelData ComputeLevels(IntPtr planarData, int channels, int samplesPerChannel)
    {
        return AudioLevelCalculator.ComputeLevels(planarData, channels, samplesPerChannel);
    }

    public unsafe byte[] InterleaveFloatAudio(IntPtr planarData, int channels, int samplesPerChannel)
    {
        return _interleaver.InterleaveNew(planarData, channels, samplesPerChannel);
    }

    public void Dispose()
    {
        _monitor.Dispose();
        GC.SuppressFinalize(this);
    }
}
