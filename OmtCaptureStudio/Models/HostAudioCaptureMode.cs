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
    Mute
}

/// <summary>
/// User-selectable option for the Host Broadcast Audio dropdown.
/// When <see cref="DeviceId"/> is null, the system default endpoint for <see cref="Mode"/> is used.
/// When <see cref="DeviceId"/> is non-null, that specific Windows Core Audio endpoint ID is captured.
/// </summary>
public sealed record HostAudioCaptureOption(
    HostAudioCaptureMode Mode,
    string DisplayName,
    string? DeviceId = null);
