using System.Text.Json;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>How a load ended. Reported so the composition root can log the truth, not a guess.</summary>
public enum SettingsLoadOutcome
{
    /// <summary>A settings file was read and accepted.</summary>
    Loaded,

    /// <summary>No file yet — a first run. All defaults; nothing was quarantined.</summary>
    Missing,

    /// <summary>The file could not be parsed. It was quarantined (<c>.corrupt</c>) and defaults are in force.</summary>
    Corrupt,

    /// <summary>The file could not be READ (locked, permissions). Defaults are in force and the file was NOT touched.</summary>
    Unreadable
}

/// <summary>
/// ADR-0006: the operator-settings store — one deep module owning load, save, path validation and
/// fallback (the 3 a.m. recovery scenario: crash → reboot → relaunch, and nothing the operator set is
/// lost).
///
/// <para>Every public member is exception-tolerant by contract. A settings file is a convenience: it
/// must never be able to stop the app starting, and it must never break the UI when a write fails
/// (a full disk, a roaming profile, an antivirus lock). Failures are logged and swallowed.</para>
///
/// <para>The existence probe is injectable so the vanish policy is testable without a real
/// unplugged drive — the same seam shape NET-010's FFmpeg resolver uses.</para>
/// </summary>
public sealed class SettingsStore
{
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logError;

    /// <param name="path">The settings file. Use <see cref="DefaultPath"/> in production.</param>
    /// <param name="directoryExists">Existence probe; injected for tests.</param>
    /// <param name="fileExists">Existence probe for the quarantine path; injected for tests.</param>
    /// <param name="logInfo">Non-fatal notices (defaults used, path rejected).</param>
    /// <param name="logError">Failures (parse error, write failure).</param>
    public SettingsStore(
        string path,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? fileExists = null,
        Action<string>? logInfo = null,
        Action<string>? logError = null)
    {
        Path = path;
        _directoryExists = directoryExists ?? Directory.Exists;
        _fileExists = fileExists ?? File.Exists;
        _logInfo = logInfo ?? (_ => { });
        _logError = logError ?? (_ => { });
    }

    /// <summary>The settings file this store owns.</summary>
    public string Path { get; }

    /// <summary>The outcome of the most recent <see cref="Load"/>.</summary>
    public SettingsLoadOutcome LastOutcome { get; private set; } = SettingsLoadOutcome.Missing;

    /// <summary>
    /// <c>%LOCALAPPDATA%\OmtCaptureStudio</c> — the same non-portable home the forensics fallback uses
    /// (ADR-0006, NET-009 rider). The application directory is deliberately NOT used: under the installer
    /// it is not writable, and a portable ZIP should not carry the last operator's folder paths.
    /// </summary>
    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmtCaptureStudio");

    /// <summary>The production settings file.</summary>
    public static string DefaultPath => System.IO.Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>
    /// Reads the settings file. NEVER throws.
    ///
    /// <para>Fallback ladder: a missing file is a first run (defaults, nothing quarantined); an
    /// unparseable file is quarantined to <c>settings.json.corrupt</c> and defaults are used; a file that
    /// cannot be READ (locked, permissions) falls back to defaults WITHOUT touching it — a transient lock
    /// must not cost the operator their settings, and a file we could not read is not evidence of
    /// corruption.</para>
    /// </summary>
    public OperatorSettings Load()
    {
        try
        {
            if (!_fileExists(Path))
            {
                LastOutcome = SettingsLoadOutcome.Missing;
                return new OperatorSettings();
            }

            string json = File.ReadAllText(Path);

            OperatorSettings? loaded;
            try
            {
                loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.OperatorSettings);
            }
            catch (JsonException ex)
            {
                return Quarantine(ex);
            }

            if (loaded is null)
            {
                // Literal "null" in the file — valid JSON, useless settings. Treat as corruption.
                return Quarantine(new JsonException("settings file deserialized to null"));
            }

            LastOutcome = SettingsLoadOutcome.Loaded;
            ValidatePaths(loaded);
            return loaded;
        }
        catch (Exception ex)
        {
            // I/O-level failure: defaults, but leave the file alone — see the ladder above.
            LastOutcome = SettingsLoadOutcome.Unreadable;
            _logError($"[settings] could not read {Path}: {ex.Message} — using defaults (file left in place)");
            return new OperatorSettings();
        }
    }

    /// <summary>
    /// Writes the whole file. NEVER throws. Returns whether it landed, so a caller MAY surface it — but
    /// no caller has to, and none does: a failed settings write must not interrupt the operator.
    /// </summary>
    public bool Save(OperatorSettings settings)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(
                Path,
                JsonSerializer.Serialize(settings, SettingsJsonContext.Default.OperatorSettings));
            return true;
        }
        catch (Exception ex)
        {
            _logError($"[settings] could not save to {Path}: {ex.Message} — the change applies to this session only");
            return false;
        }
    }

    /// <summary>
    /// True when a configured directory is usable.
    ///
    /// <para>An existing folder is usable. A folder that does not exist YET is usable as long as its
    /// DRIVE does — the recorder creates the folder, and falling back to MyVideos because the operator
    /// has not created a subfolder yet would be obnoxious. A VANISHED DRIVE is not usable: that is the
    /// 3 a.m. case, where continuing to point at <c>Z:\captures</c> would fail only when Record is
    /// pressed.</para>
    /// </summary>
    public static bool IsUsablePath(string? path, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (directoryExists(path)) return true;

        string? root = System.IO.Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && directoryExists(root);
    }

    /// <summary>
    /// Maps persisted settings onto a config, per-field, falling back to
    /// <paramref name="defaults"/> (or a fresh <see cref="RecordingConfig"/>) wherever the file was
    /// silent or a value was rejected. Pure — no I/O, no clock.
    /// </summary>
    public static RecordingConfig ToConfig(OperatorSettings settings, RecordingConfig? defaults = null)
    {
        var config = defaults?.Clone() ?? new RecordingConfig();

        if (Enum.TryParse<OutputContainerFormat>(settings.ContainerFormat, ignoreCase: true, out var container))
        {
            config.ContainerFormat = container;
        }

        if (Enum.TryParse<VideoEncoderChoice>(settings.EncoderChoice, ignoreCase: true, out var encoder))
        {
            config.EncoderChoice = encoder;
        }

        if (Enum.TryParse<QualityPreset>(settings.Quality, ignoreCase: true, out var quality))
        {
            config.Quality = quality;
        }

        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
        {
            config.OutputDirectory = settings.OutputDirectory!;
        }

        if (!string.IsNullOrWhiteSpace(settings.BackupOutputDirectory))
        {
            config.BackupOutputDirectory = settings.BackupOutputDirectory;
        }

        if (!string.IsNullOrWhiteSpace(settings.FilenamePrefix))
        {
            config.FilenamePrefix = settings.FilenamePrefix!;
        }

        if (settings.InputLossThresholdSeconds is { } loss && loss > 0)
        {
            config.InputLossThresholdSeconds = loss;
        }

        if (settings.InputRecoveryGraceSeconds is { } grace && grace > 0)
        {
            config.InputRecoveryGraceSeconds = grace;
        }

        if (settings.SegmentRolloverMinutes is { } rolloverMinutes and >= 0)
        {
            config.SegmentRolloverMinutes = rolloverMinutes;
        }

        if (settings.SegmentRolloverGigabytes is { } rolloverGb and >= 0)
        {
            config.SegmentRolloverGigabytes = rolloverGb;
        }

        if (settings.MaxDurationMinutes is { } maxMinutes and >= 0)
        {
            config.MaxDurationMinutes = maxMinutes;
        }

        if (settings.ScheduledStartDelayMinutes is { } delay and >= 0)
        {
            config.ScheduledStartDelayMinutes = delay;
        }

        if (settings.ScheduledStartUtc is { } startUtc)
        {
            config.ScheduledStartUtc = startUtc;
        }

        if (settings.ScheduledStopUtc is { } stopUtc)
        {
            config.ScheduledStopUtc = stopUtc;
        }

        if (settings.ScheduledStopAfterMinutes is { } stopAfter and >= 0)
        {
            config.ScheduledStopAfterMinutes = stopAfter;
        }

        return config;
    }

    /// <summary>
    /// The inverse of <see cref="ToConfig"/>, plus the selected preset name.
    /// <para>
    /// ADR-0011: <paramref name="lastAppMode"/> is the mode the operator last CHOSE — the value that
    /// pre-highlights a card on the picker. It is deliberately NOT "the mode the window is in": the
    /// picker always opens first (ADR-0011 decision 1), so returning to it must not erase the
    /// memory of which mode they used. A null therefore means "never chosen one yet" and the picker
    /// highlights nothing.
    /// </para>
    /// </summary>
    public static OperatorSettings FromConfig(RecordingConfig config, string? presetName, AppMode? lastAppMode = null)
    {
        return new OperatorSettings
        {
            Version = OperatorSettings.CurrentVersion,
            LastAppMode = lastAppMode,
            SelectedPreset = presetName,
            ContainerFormat = config.ContainerFormat.ToString(),
            EncoderChoice = config.EncoderChoice.ToString(),
            Quality = config.Quality.ToString(),
            OutputDirectory = config.OutputDirectory,
            BackupOutputDirectory = config.BackupOutputDirectory,
            FilenamePrefix = config.FilenamePrefix,
            InputLossThresholdSeconds = config.InputLossThresholdSeconds,
            InputRecoveryGraceSeconds = config.InputRecoveryGraceSeconds,
            SegmentRolloverMinutes = config.SegmentRolloverMinutes,
            SegmentRolloverGigabytes = config.SegmentRolloverGigabytes,
            MaxDurationMinutes = config.MaxDurationMinutes,
            ScheduledStartDelayMinutes = config.ScheduledStartDelayMinutes,
            ScheduledStartUtc = config.ScheduledStartUtc,
            ScheduledStopUtc = config.ScheduledStopUtc,
            ScheduledStopAfterMinutes = config.ScheduledStopAfterMinutes
        };
    }

    /// <summary>
    /// The quarantine name for a corrupt file — <c>settings.json.corrupt</c>, then
    /// <c>.corrupt.1</c>, <c>.corrupt.2</c>… so a second corruption cannot destroy the first file's
    /// evidence. Pure.
    /// </summary>
    public static string CorruptPathFor(string path, Func<string, bool> fileExists)
    {
        string candidate = path + ".corrupt";
        int suffix = 1;
        while (fileExists(candidate))
        {
            candidate = $"{path}.corrupt.{suffix}";
            suffix++;
        }

        return candidate;
    }

    /// <summary>
    /// Quarantines an unparseable file and returns all-defaults. Renaming keeps the evidence (a support
    /// case can ask for it) while unblocking startup — a failure to rename must not become a failure to
    /// start, so it is logged and the defaults stand regardless.
    /// </summary>
    private OperatorSettings Quarantine(Exception cause)
    {
        LastOutcome = SettingsLoadOutcome.Corrupt;

        string quarantined = CorruptPathFor(Path, _fileExists);
        try
        {
            File.Move(Path, quarantined, overwrite: false);
            _logError(
                $"[settings] {Path} could not be parsed ({cause.Message}) — moved to {quarantined} and using defaults");
        }
        catch (Exception ex)
        {
            _logError(
                $"[settings] {Path} could not be parsed ({cause.Message}) and could not be quarantined " +
                $"({ex.Message}) — using defaults and leaving the file in place");
        }

        return new OperatorSettings();
    }

    /// <summary>
    /// Per-field path fallback. A rejected path becomes <c>null</c>, which
    /// <see cref="ToConfig"/> reads as "use the default" — so a vanished drive costs the operator that
    /// ONE field, not the whole settings file.
    /// </summary>
    private void ValidatePaths(OperatorSettings settings)
    {
        if (settings.OutputDirectory is { Length: > 0 } primary && !IsUsablePath(primary, _directoryExists))
        {
            _logInfo(
                $"[settings] destination '{primary}' is unavailable (drive or folder missing) — " +
                "falling back to the default destination");
            settings.OutputDirectory = null;
        }

        if (settings.BackupOutputDirectory is { Length: > 0 } backup && !IsUsablePath(backup, _directoryExists))
        {
            _logInfo(
                $"[settings] backup destination '{backup}' is unavailable (drive or folder missing) — " +
                "disabling the backup destination for this session");
            settings.BackupOutputDirectory = null;
        }
    }
}
