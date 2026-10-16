namespace OmtCaptureStudio.Models;

/// <summary>
/// A named, selectable recording preset (NET-008): the container / encoder / quality / destination
/// unit the operator picks in ONE action, plus the segment-rollover policy that travels with it.
/// </summary>
/// <remarks>
/// A preset deliberately does NOT carry the input-loss policy, the schedule, or the filename prefix
/// policy for input-loss segments: those are per-take operator decisions, not part of "how do I want
/// this encoded". <see cref="OutputDirectory"/> and <see cref="FilenamePrefix"/> are nullable so a
/// preset can express "leave the destination alone" — a preset authored on one machine must never
/// silently redirect output on another.
/// </remarks>
public sealed record RecordingPreset(
    string Name,
    string Description,
    OutputContainerFormat ContainerFormat,
    VideoEncoderChoice EncoderChoice,
    QualityPreset Quality)
{
    /// <summary>Destination override. <c>null</c> = keep whatever the live config points at.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Filename-prefix override. <c>null</c> = keep the live config's prefix.</summary>
    public string? FilenamePrefix { get; init; }

    /// <summary>Segment rollover by minutes. <c>0</c> = never roll by time.</summary>
    public double SegmentRolloverMinutes { get; init; }

    /// <summary>Segment rollover by GB. <c>0</c> = never roll by size.</summary>
    public double SegmentRolloverGigabytes { get; init; }

    /// <summary>Max take duration in minutes, summed across segments. <c>0</c> = unlimited.</summary>
    public double MaxDurationMinutes { get; init; }
}

/// <summary>
/// The built-in preset catalog plus the pure capture/apply pair. Both directions are pure functions of
/// (preset, config) — no I/O, no clock — so the round-trip is asserted headlessly.
/// </summary>
public static class RecordingPresets
{
    /// <summary>Display name used once the operator edits any axis, so the picker cannot lie.</summary>
    public const string CustomName = "Custom";

    /// <summary>
    /// The shipped presets. Names are stable identifiers (the picker selects by name), so renaming one
    /// is a user-visible change.
    /// </summary>
    public static IReadOnlyList<RecordingPreset> BuiltIn { get; } = new[]
    {
        new RecordingPreset("Archive - ProRes MOV",
            "Editing master: ProRes in MOV, rolls every 10 min",
            OutputContainerFormat.MOV, VideoEncoderChoice.ProRes, QualityPreset.Ultra)
        {
            SegmentRolloverMinutes = 10
        },

        new RecordingPreset("Broadcast - NVENC MP4",
            "Deliverable H.264 with hardware encode, 15 min segments",
            OutputContainerFormat.MP4, VideoEncoderChoice.NvidiaNvenc, QualityPreset.High)
        {
            SegmentRolloverMinutes = 15
        },

        new RecordingPreset("Long event - MKV",
            "Crash-resilient MKV, 30 min segments, 8 h auto-stop",
            OutputContainerFormat.MKV, VideoEncoderChoice.AutoHardware, QualityPreset.Medium)
        {
            SegmentRolloverMinutes = 30,
            MaxDurationMinutes = 480
        },

        new RecordingPreset("Low disk - x264 MP4",
            "Software encode, small files, 5 min or 2 GB segments",
            OutputContainerFormat.MP4, VideoEncoderChoice.CpuX264, QualityPreset.Low)
        {
            SegmentRolloverMinutes = 5,
            SegmentRolloverGigabytes = 2
        },

        new RecordingPreset("Screen capture - QSV MKV",
            "Quick Sync capture, rolls at 2 GB",
            OutputContainerFormat.MKV, VideoEncoderChoice.IntelQuickSync, QualityPreset.High)
        {
            SegmentRolloverGigabytes = 2
        },
    };

    /// <summary>Finds a built-in preset by name (ordinal, case-insensitive), or null.</summary>
    public static RecordingPreset? FindByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        foreach (RecordingPreset preset in BuiltIn)
        {
            if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)) return preset;
        }

        return null;
    }

    /// <summary>
    /// Applies a preset's axis onto a live config, in place. The destination and prefix are only
    /// touched when the preset carries them; rollover and max-duration ARE always written, so a preset
    /// with no rollover actively clears a rollover set by a previous preset (otherwise preset A's
    /// segment policy would leak into preset B).
    /// </summary>
    public static void Apply(RecordingPreset preset, RecordingConfig config)
    {
        if (preset == null) throw new ArgumentNullException(nameof(preset));
        if (config == null) throw new ArgumentNullException(nameof(config));

        config.ContainerFormat = preset.ContainerFormat;
        config.EncoderChoice = preset.EncoderChoice;
        config.Quality = preset.Quality;

        if (!string.IsNullOrWhiteSpace(preset.OutputDirectory))
        {
            config.OutputDirectory = preset.OutputDirectory;
        }

        if (!string.IsNullOrWhiteSpace(preset.FilenamePrefix))
        {
            config.FilenamePrefix = preset.FilenamePrefix;
        }

        config.SegmentRolloverMinutes = preset.SegmentRolloverMinutes;
        config.SegmentRolloverGigabytes = preset.SegmentRolloverGigabytes;
        config.MaxDurationMinutes = preset.MaxDurationMinutes;
    }

    /// <summary>
    /// Reads the preset axis back off a config - the inverse of <see cref="Apply"/>. The destination
    /// and prefix come from the CONFIG (a preset may legitimately mean "wherever the config points"),
    /// so capturing a config applied from a destination-less preset yields the concrete path.
    /// </summary>
    public static RecordingPreset Capture(RecordingConfig config, string name, string description = "")
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        return new RecordingPreset(
            name, description, config.ContainerFormat, config.EncoderChoice, config.Quality)
        {
            OutputDirectory = config.OutputDirectory,
            FilenamePrefix = config.FilenamePrefix,
            SegmentRolloverMinutes = config.SegmentRolloverMinutes,
            SegmentRolloverGigabytes = config.SegmentRolloverGigabytes,
            MaxDurationMinutes = config.MaxDurationMinutes
        };
    }

    /// <summary>
    /// True when a config still matches every axis of a preset - drives the picker showing "Custom"
    /// after the operator edits any control, rather than a preset name that is no longer accurate.
    /// </summary>
    public static bool Matches(RecordingPreset preset, RecordingConfig config)
    {
        if (preset == null || config == null) return false;

        return preset.ContainerFormat == config.ContainerFormat
            && preset.EncoderChoice == config.EncoderChoice
            && preset.Quality == config.Quality
            && preset.SegmentRolloverMinutes.Equals(config.SegmentRolloverMinutes)
            && preset.SegmentRolloverGigabytes.Equals(config.SegmentRolloverGigabytes)
            && preset.MaxDurationMinutes.Equals(config.MaxDurationMinutes)
            && (string.IsNullOrWhiteSpace(preset.OutputDirectory)
                || string.Equals(preset.OutputDirectory, config.OutputDirectory, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(preset.FilenamePrefix)
                || string.Equals(preset.FilenamePrefix, config.FilenamePrefix, StringComparison.Ordinal));
    }
}
