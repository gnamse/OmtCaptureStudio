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

    // ---- Negotiated state (ADR-0003) -----------------------------------------------------------
    // Exposed here so callers never have to downcast to a concrete driver adapter to report what the
    // device actually negotiated.

    /// <summary>True once the underlying capture device is running.</summary>
    bool IsHardwareActive { get; }

    /// <summary>Device mix rate actually in use; equals <see cref="SampleRate"/> when no conversion is needed.</summary>
    int DeviceSampleRate { get; }

    /// <summary>True when the device rate differs from the target rate and this source converts.</summary>
    bool IsResamplingRequired { get; }

    /// <summary>Operator-facing note about rate negotiation, or <c>null</c> when there is nothing to say.</summary>
    string? RateNegotiationNote { get; }

    /// <summary>Driver-reported input latency in frames, or 0 when the device does not report one.</summary>
    int InputLatencyFrames { get; }

    /// <summary>Last setup/runtime failure, or <c>null</c> when everything succeeded.</summary>
    string? LastError { get; }

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

    // Silent clock: no device, nothing to negotiate, nothing to report.
    public bool IsHardwareActive => false;
    public int DeviceSampleRate => 48000;
    public bool IsResamplingRequired => false;
    public string? RateNegotiationNote => null;
    public int InputLatencyFrames => 0;
    public string? LastError => null;

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
/// or <see cref="HostAudioCaptureOption"/> (including specific WASAPI hardware endpoint IDs and
/// ASIO drivers).
/// </summary>
public static class HostAudioSourceFactory
{
    /// <summary>
    /// Creates a source for <paramref name="mode"/>. For every mode except <see cref="HostAudioCaptureMode.Asio"/>
    /// <paramref name="deviceId"/> is an optional WASAPI endpoint id; for ASIO it is the driver's CLSID string
    /// (see <see cref="HostAudioCaptureOption.AsioDriverClsid"/>).
    /// </summary>
    public static IHostAudioSource Create(HostAudioCaptureMode mode, string? deviceId = null)
    {
        if (mode == HostAudioCaptureMode.Mute)
        {
            return new SilentHostAudioSource();
        }

        if (mode == HostAudioCaptureMode.Asio)
        {
            Guid? clsid = TryParseClsid(deviceId);
            AsioDriverInfo driver = ResolveAsioDriver(clsid, null);
            return new AsioHostAudioSource(new AsioCaptureOption(driver));
        }

        return new WasapiHostAudioSource(mode, deviceId);
    }

    public static IHostAudioSource Create(HostAudioCaptureOption? option)
    {
        if (option == null || option.Mode == HostAudioCaptureMode.Mute)
        {
            return new SilentHostAudioSource();
        }

        if (option.Mode == HostAudioCaptureMode.Asio)
        {
            Guid? clsid = option.AsioDriverClsid ?? TryParseClsid(option.DeviceId);
            AsioDriverInfo driver = ResolveAsioDriver(clsid, option.DisplayName);
            return new AsioHostAudioSource(new AsioCaptureOption(driver, option.AsioFirstInputChannelIndex));
        }

        return new WasapiHostAudioSource(option.Mode, option.DeviceId);
    }

    /// <summary>
    /// Maps a stored ASIO CLSID back onto a currently registered <see cref="AsioDriverInfo"/>.
    /// Drivers can disappear (uninstalled, or simply not registered on this machine/user), so the
    /// id is re-resolved through <see cref="AsioDriverEnumerator"/> rather than trusted blindly —
    /// the dropdown only ever stores identity, never a live handle, because ASIO drivers grant
    /// exclusive access. Throws <see cref="InvalidOperationException"/> when nothing resolves.
    /// </summary>
    private static AsioDriverInfo ResolveAsioDriver(Guid? clsid, string? displayName)
    {
        IReadOnlyList<AsioDriverInfo> drivers;
        try
        {
            drivers = AsioDriverEnumerator.Enumerate();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "ASIO drivers could not be enumerated on this machine, so ASIO capture is unavailable.", ex);
        }

        if (clsid.HasValue)
        {
            foreach (AsioDriverInfo candidate in drivers)
            {
                if (candidate.Clsid == clsid.Value)
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                $"The selected ASIO driver '{displayName ?? clsid.Value.ToString("B")}' ({clsid.Value:B}) is not " +
                "registered on this machine anymore. Pick another audio device or reinstall the driver.");
        }

        // No explicit driver (mode-only construction): only unambiguous when exactly one exists.
        if (drivers.Count == 1)
        {
            return drivers[0];
        }

        throw new InvalidOperationException(
            drivers.Count == 0
                ? "No ASIO drivers are registered on this machine, so ASIO capture is unavailable."
                : $"{drivers.Count} ASIO drivers are registered ({string.Join(", ", drivers.Select(d => d.DisplayName))}); " +
                  "select one explicitly through a HostAudioCaptureOption.");
    }

    private static Guid? TryParseClsid(string? value) =>
        value != null && Guid.TryParse(value, out Guid parsed) ? parsed : (Guid?)null;
}
