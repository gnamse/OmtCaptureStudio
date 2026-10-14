using System;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Provides 48 kHz stereo planar 32-bit float audio frames (<c>OMTCodec.FPA1</c>)
/// to <see cref="OmtDesktopCaptureGenerator"/> at the video frame cadence.
/// Implementations must be allocation-free in <see cref="FillPlanarAudio"/> and
/// write silence (<c>0.0f</c>) whenever the underlying capture device is silent or unavailable.
/// </summary>
public interface IHostAudioSource : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>
    /// Fills <paramref name="planarDestination"/> with <c>Channels * samplesPerChannel</c>
    /// floats: all Left samples (<c>[0 .. samplesPerChannel - 1]</c>) followed by all Right
    /// samples (<c>[samplesPerChannel .. 2 * samplesPerChannel - 1]</c>).
    /// Never blocks; fills any underrun with <c>0.0f</c> silence.
    /// </summary>
    void FillPlanarAudio(Span<float> planarDestination, int samplesPerChannel);
}

/// <summary>
/// Emits a continuous 48 kHz stereo silent (<c>0.0f</c>) audio clock for <see cref="HostAudioCaptureMode.Mute"/>
/// or when no hardware capture endpoint is desired.
/// </summary>
public sealed class SilentHostAudioSource : IHostAudioSource
{
    public int SampleRate => 48000;
    public int Channels => 2;

    public void FillPlanarAudio(Span<float> planarDestination, int samplesPerChannel)
    {
        int totalSamples = Channels * samplesPerChannel;
        if (totalSamples > 0 && totalSamples <= planarDestination.Length)
        {
            planarDestination.Slice(0, totalSamples).Clear();
        }
        else
        {
            planarDestination.Clear();
        }
    }

    public void Dispose()
    {
        // Stateless; idempotent no-op.
    }
}

/// <summary>
/// Builds a concrete <see cref="IHostAudioSource"/> from a user-selected <see cref="HostAudioCaptureMode"/>
/// or <see cref="HostAudioCaptureOption"/> (including specific WASAPI hardware endpoint IDs).
/// </summary>
public static class HostAudioSourceFactory
{
    public static IHostAudioSource Create(HostAudioCaptureMode mode, string? deviceId = null)
    {
        return mode == HostAudioCaptureMode.Mute
            ? new SilentHostAudioSource()
            : new WasapiHostAudioSource(mode, deviceId);
    }

    public static IHostAudioSource Create(HostAudioCaptureOption? option)
    {
        if (option == null || option.Mode == HostAudioCaptureMode.Mute)
        {
            return new SilentHostAudioSource();
        }
        return new WasapiHostAudioSource(option.Mode, option.DeviceId);
    }
}
