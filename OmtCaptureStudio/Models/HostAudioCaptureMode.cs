namespace OmtCaptureStudio.Models;

/// <summary>
/// Selects which host audio source(s) are captured and broadcast alongside
/// screen or window video in <see cref="Services.OmtDesktopCaptureGenerator"/>.
/// </summary>
public enum HostAudioCaptureMode
{
    /// <summary>Desktop system loopback audio (default excludes OmtCaptureStudio's own playback to prevent echo, or captures a specific output endpoint).</summary>
    SystemAudio,

    /// <summary>Microphone / audio input endpoint (default or a specific input device).</summary>
    Microphone,

    /// <summary>Simultaneous mix of Desktop System Audio + Default Microphone.</summary>
    SystemAndMicrophone,

    /// <summary>Silent audio stream (sends continuous 0.0f 48 kHz stereo clock so receivers maintain A/V sync).</summary>
    Mute,

    /// <summary>
    /// Pro-audio input captured through an ASIO driver (see <see cref="AsioDriverInfo"/>).
    /// ASIO bypasses the Windows audio engine, so it offers far lower latency and access to
    /// multichannel interfaces that WASAPI never exposes — at the cost of <b>exclusive</b>
    /// access: only one process may hold a given ASIO driver open at a time, so a DAW already
    /// holding the driver will block capture (and vice versa). Selecting this mode requires
    /// <see cref="HostAudioCaptureOption.AsioDriverClsid"/> to name a registered driver; the
    /// driver then supplies stereo from the input channel pair starting at
    /// <see cref="HostAudioCaptureOption.AsioFirstInputChannelIndex"/>.
    /// </summary>
    Asio
}

/// <summary>
/// User-selectable option for the Host Broadcast Audio dropdown.
/// When <see cref="DeviceId"/> is null, the system default endpoint for <see cref="Mode"/> is used.
/// When <see cref="DeviceId"/> is non-null, that specific Windows Core Audio endpoint ID is captured
/// (or, for <see cref="HostAudioCaptureMode.Asio"/>, the selected driver's COM class id as a string —
/// the authoritative driver identity always lives in <see cref="AsioDriverClsid"/>).
/// </summary>
/// <param name="Mode">Which capture backend / routing this option selects.</param>
/// <param name="DisplayName">Dropdown label, e.g. <c>ASIO · Realtek ASIO (In 1/2)</c>.</param>
/// <param name="DeviceId">WASAPI endpoint id, or the ASIO driver CLSID string; null means "system default".</param>
/// <param name="AsioDriverClsid">
/// Registered ASIO driver to open when <paramref name="Mode"/> is <see cref="HostAudioCaptureMode.Asio"/>.
/// It is resolved through <see cref="Services.AsioDriverEnumerator"/> at construction time, so merely
/// offering a driver in the dropdown never opens or locks it.
/// </param>
/// <param name="AsioFirstInputChannelIndex">
/// Zero-based index of the first ASIO input channel to broadcast. This option always broadcasts a
/// stereo <em>pair</em>: this channel becomes Left and the next one Right. 0 (the default) is input
/// pair 1/2, 2 is 3/4, and so on.
/// </param>
public sealed record HostAudioCaptureOption(
    HostAudioCaptureMode Mode,
    string DisplayName,
    string? DeviceId = null,
    Guid? AsioDriverClsid = null,
    int AsioFirstInputChannelIndex = 0)
{
    /// <summary>True when this option fully identifies an ASIO driver (as opposed to a WASAPI endpoint or preset).</summary>
    public bool IsAsio => Mode == HostAudioCaptureMode.Asio && AsioDriverClsid.HasValue;

    /// <summary>
    /// Builds a dropdown entry for one registered ASIO driver and input channel pair.
    /// <paramref name="displayName"/> should already carry its human-readable prefix,
    /// e.g. <c>"ASIO · Realtek ASIO (In 1/2)"</c>.
    /// </summary>
    public static HostAudioCaptureOption ForAsio(Guid driverClsid, string displayName, int firstInputChannelIndex = 0) =>
        new(HostAudioCaptureMode.Asio, displayName, driverClsid.ToString("B"), driverClsid, firstInputChannelIndex);
}
