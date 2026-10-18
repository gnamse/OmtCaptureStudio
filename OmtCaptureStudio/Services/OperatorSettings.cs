using System.Text.Json.Serialization;

namespace OmtCaptureStudio.Services;

/// <summary>
/// ADR-0006: the operator's persisted settings — the on-disk shape, deliberately SEPARATE from
/// <see cref="Models.RecordingConfig"/>.
///
/// <para>Why a distinct DTO rather than serializing <c>RecordingConfig</c> directly: the config is the
/// recording pipeline's live contract and grows when the pipeline grows (NET-008 added five fields,
/// NET-011 added one). Binding the file format to it would mean every pipeline field silently becomes
/// a persistence field, and every rename becomes a breaking file-format change. The DTO is the
/// operator-facing subset, it is allowed to lag the config, and it can carry a
/// <see cref="Version"/>.</para>
///
/// <para>Every member is nullable on purpose: a property missing from the file means "the operator has
/// never set this", which must fall back to that field's DEFAULT rather than to <c>default(T)</c>. That
/// is what makes a partial or hand-edited file safe.</para>
///
/// <para>Enums are stored as NAMES, not ordinals, so reordering an enum in code cannot silently
/// reinterpret an operator's saved choice.</para>
/// </summary>
public sealed class OperatorSettings
{
    /// <summary>Schema version, so a future format change can be migrated rather than guessed at.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The selected preset name, or <c>Custom</c>. Restored by name, never by index.</summary>
    public string? SelectedPreset { get; set; }

    public string? ContainerFormat { get; set; }
    public string? EncoderChoice { get; set; }
    public string? Quality { get; set; }

    public string? OutputDirectory { get; set; }
    public string? BackupOutputDirectory { get; set; }
    public string? FilenamePrefix { get; set; }

    /// <summary>ADR-0002 input-loss watchdog window, seconds.</summary>
    public double? InputLossThresholdSeconds { get; set; }

    /// <summary>ADR-0002 bounded reconnect grace period, seconds.</summary>
    public double? InputRecoveryGraceSeconds { get; set; }

    /// <summary>NET-008 segment rollover by duration (minutes); 0 = never.</summary>
    public double? SegmentRolloverMinutes { get; set; }

    /// <summary>NET-008 segment rollover by size (GB); 0 = never.</summary>
    public double? SegmentRolloverGigabytes { get; set; }

    /// <summary>NET-008 maximum take duration (minutes); 0 = unlimited.</summary>
    public double? MaxDurationMinutes { get; set; }

    /// <summary>NET-008 "start in N minutes".</summary>
    public double? ScheduledStartDelayMinutes { get; set; }

    public DateTime? ScheduledStartUtc { get; set; }
    public DateTime? ScheduledStopUtc { get; set; }

    /// <summary>
    /// NET-016 "stop after N minutes" - the operator's choice, which is what the picker restores. The
    /// absolute instant it resolves to is per-take and deliberately is NOT what the control reads back.
    /// </summary>
    public double? ScheduledStopAfterMinutes { get; set; }
}

/// <summary>
/// ADR-0006: source-generated <see cref="System.Text.Json"/> metadata.
///
/// <para>The app ships Native AOT (<c>PublishAot=true</c>), where reflection-based serialization is
/// trimmed away and a reflective <c>JsonSerializer.Serialize(settings)</c> would fail at runtime in the
/// field — exactly the class of bug this codebase does not accept. Source generation emits the
/// converters at compile time, so the build itself proves the format works (no reflection, no dynamic
/// code).</para>
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(OperatorSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
