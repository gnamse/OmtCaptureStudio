namespace OmtCaptureStudio.Services.Audio;

/// <summary>
/// The shared vocabulary of the host-audio capture layer (ADR-0003, Slice 1).
/// <para>
/// These constants used to live on <c>WasapiHostAudioSource</c> and were reached into by
/// <c>AsioHostAudioSource</c> and <c>OmtDesktopCaptureGenerator</c> — one module importing a peer's
/// public constants to describe a contract that applies to all of them. They belong to the audio
/// layer, not to one driver adapter.
/// </para>
/// </summary>
public static class HostAudioFormat
{
    /// <summary>OMT host audio is always captured and broadcast as 48 kHz stereo.</summary>
    public const int TargetSampleRate = 48_000;

    /// <summary>Stereo (two channels).</summary>
    public const int TargetChannels = 2;
}
