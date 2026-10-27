using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Forensics;

/// <summary>
/// Everything a bundle should say about the run that produced it. Passed in rather than read from the
/// live app, so the whole bundle policy is assertable with no app, no session and no real crash
/// artifacts — the caller (the view model) is the only thing that knows where the live facts live.
/// </summary>
public sealed record DiagnosticsSnapshot
{
    /// <summary>The app version, as shown in the title bar (e.g. <c>v1.0.0.19</c>).</summary>
    public string AppVersion { get; init; } = "Unknown";

    /// <summary>The ffmpeg version the preflight resolved, or empty when it never ran.</summary>
    public string FfmpegVersion { get; init; } = string.Empty;

    /// <summary>True when the preflight could not prove ffmpeg runs.</summary>
    public bool IsRecordingUnavailable { get; init; }

    /// <summary>The operator-facing reason recording is unavailable, when it is.</summary>
    public string RecordingUnavailableText { get; init; } = string.Empty;

    /// <summary>The recording health readout at the moment the bundle was requested.</summary>
    public RecordingHealthSnapshot Health { get; init; }

    /// <summary>Destinations the composite sink detached, in detach order (NET-011).</summary>
    public IReadOnlyList<string> DetachedChildNames { get; init; } = Array.Empty<string>();

    /// <summary>The log directory actually in use.</summary>
    public string LogDirectory { get; init; } = string.Empty;

    /// <summary>The portable log directory, which a previous run may have used instead.</summary>
    public string PrimaryLogDirectory { get; init; } = string.Empty;

    /// <summary>The minidump directory actually in use.</summary>
    public string DumpDirectory { get; init; } = string.Empty;

    /// <summary>The portable minidump directory, which a previous run may have used instead.</summary>
    public string PrimaryDumpDirectory { get; init; } = string.Empty;

    /// <summary>Path of the newest crash report, when one exists.</summary>
    public string? CrashReportPath { get; init; }

    /// <summary>When the bundle was generated. Injected so the text is assertable.</summary>
    public DateTimeOffset GeneratedUtc { get; init; }

    /// <summary>OS description, e.g. <c>Microsoft Windows 11 Pro</c>.</summary>
    public string OsDescription { get; init; } = string.Empty;
}

/// <summary>One file selected for the bundle, with the name it will carry inside the archive.</summary>
public sealed record DiagnosticsArtifact(string FullPath, string EntryName, long SizeBytes);

/// <summary>Outcome of a bundle attempt. Never an exception — a bundle is a support action.</summary>
public sealed record DiagnosticsBundleResult
{
    public bool Success { get; init; }

    /// <summary>The written archive, when it succeeded.</summary>
    public string? Path { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Entry names inside the archive, <c>diagnostics.txt</c> first.</summary>
    public IReadOnlyList<string> Entries { get; init; } = Array.Empty<string>();

    /// <summary>Why it failed, when it did.</summary>
    public string? Error { get; init; }

    /// <summary>A line for the status bar, whether or not it worked.</summary>
    public string Summary => Success
        ? $"Diagnostics bundle saved ({Entries.Count} file(s), {SizeBytes / 1024} KB): {Path}"
        : $"Diagnostics bundle could not be created: {Error}";
}

/// <summary>
/// NET-020: collects the artifacts a support conversation actually needs into one archive — the recent
/// log lines, any crash reports, any minidumps, and a generated <c>diagnostics.txt</c> carrying the app
/// version, the ffmpeg result, the recording health readout and the fan-out state.
///
/// <para><b>It looks in BOTH forensics locations, not just the active one.</b> NET-017 made the
/// location hybrid: a run can resolve to the application directory while a previous one fell back to
/// <c>%LOCALAPPDATA%</c> (or the reverse, if the app was later moved somewhere writable). Collecting
/// only the active directory would omit the very artifacts the operator is being asked to send — the
/// same reasoning <see cref="CrashForensics.DumpDirectories"/> already encodes for the next-launch
/// notice.</para>
///
/// <para><b>Nothing here throws.</b> A bundle is requested when something has already gone wrong; a
/// support action that fails loudly would bury the original problem. An unwritable destination returns
/// a failed result with the reason, and a file that disappears mid-collection is skipped and noted
/// rather than aborting the archive.</para>
///
/// <para>Pure policy (<see cref="BuildDiagnosticsText"/>) is separated from I/O (<see cref="Create"/>),
/// so the wording and the keep-last-N rule are asserted with no filesystem at all.</para>
/// </summary>
public static class DiagnosticsBundle
{
    /// <summary>Newest daily logs included. Small on purpose — the recent lines are the useful ones.</summary>
    public const int KeepLastLogs = 5;

    /// <summary>Newest crash reports included.</summary>
    public const int KeepLastCrashLogs = 5;

    /// <summary>Newest minidumps included. Kept lowest: these are the biggest entries.</summary>
    public const int KeepLastDumps = 3;

    /// <summary>The generated readout; always the first entry in the archive.</summary>
    public const string DiagnosticsEntryName = "diagnostics.txt";

    /// <summary>Sub-folder for daily logs inside the archive.</summary>
    public const string LogsFolder = "logs";

    /// <summary>Sub-folder for crash reports inside the archive.</summary>
    public const string CrashLogsFolder = "crashlogs";

    /// <summary>Sub-folder for minidumps inside the archive.</summary>
    public const string DumpsFolder = "crashdumps";

    /// <summary>Folder holding exported bundles; a sibling of the log folder.</summary>
    public const string BundleFolder = "diagnostics";

    /// <summary>Default archive name — timestamped so repeated exports never collide or overwrite.</summary>
    public static string DefaultFileName(DateTimeOffset generatedUtc) =>
        $"omt-diagnostics-{generatedUtc:yyyyMMdd-HHmmss}.zip";

    /// <summary>
    /// Where a bundle goes when the operator does not choose: a <c>diagnostics</c> folder beside the
    /// logs it collects, so the export lives with the forensics. Falls back to the log directory itself
    /// if the path has no parent.
    /// </summary>
    public static string DefaultBundleDirectory(DiagnosticsSnapshot s)
    {
        try
        {
            string? parent = Directory.GetParent(s.LogDirectory)?.FullName;
            if (!string.IsNullOrEmpty(parent)) return Path.Combine(parent, BundleFolder);
        }
        catch { }

        return string.IsNullOrWhiteSpace(s.LogDirectory) ? BundleFolder : s.LogDirectory;
    }

    /// <summary>
    /// The generated readout. Pure: same snapshot in, same text out.
    /// </summary>
    public static string BuildDiagnosticsText(DiagnosticsSnapshot s) =>
        BuildDiagnosticsText(s, Array.Empty<DiagnosticsArtifact>());

    /// <summary>The generated readout, naming the artifacts the archive actually carries.</summary>
    public static string BuildDiagnosticsText(DiagnosticsSnapshot s, IReadOnlyList<DiagnosticsArtifact> artifacts)
    {
        var text = new StringBuilder(2048);

        text.AppendLine("OMT Capture Studio - diagnostics bundle");
        text.AppendLine("======================================");
        text.AppendLine($"Generated   : {s.GeneratedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        text.AppendLine($"App version : {Value(s.AppVersion)}");
        text.AppendLine($"OS          : {Value(s.OsDescription)}");
        text.AppendLine($"Runtime     : {Value(RuntimeInformation.FrameworkDescription)}");

        text.AppendLine();
        text.AppendLine("--- FFmpeg ---");
        if (!s.IsRecordingUnavailable && !string.IsNullOrWhiteSpace(s.FfmpegVersion))
        {
            text.AppendLine($"status : available");
            text.AppendLine($"version: {s.FfmpegVersion}");
        }
        else
        {
            text.AppendLine($"status : UNAVAILABLE");
            text.AppendLine($"detail : {Value(s.RecordingUnavailableText)}");
        }

        text.AppendLine();
        text.AppendLine("--- Locations ---");
        text.AppendLine($"logs       (active) : {Value(s.LogDirectory)}");
        text.AppendLine($"logs       (primary): {Value(s.PrimaryLogDirectory)}");
        text.AppendLine($"crashdumps (active) : {Value(s.DumpDirectory)}");
        text.AppendLine($"crashdumps (primary): {Value(s.PrimaryDumpDirectory)}");
        text.AppendLine($"newest crash report : {Value(s.CrashReportPath)}");

        text.AppendLine();
        text.AppendLine("--- Recording health ---");
        RecordingHealthSnapshot h = s.Health;
        text.AppendLine($"recording     : {(h.IsRecording ? "yes" : "no")}");
        text.AppendLine($"take open     : {(h.HasTake ? "yes" : "no")}");
        text.AppendLine($"segment index : {h.SegmentIndex}");
        text.AppendLine($"encoder       : {Value(h.EncoderName)}");
        text.AppendLine($"bitrate       : {h.AchievedBitrateMbps:0.00} Mbps");
        text.AppendLine($"video frames  : {h.VideoFramesWritten} written, {h.VideoFramesDropped} dropped");
        text.AppendLine($"audio chunks  : {h.AudioChunksDropped} dropped");
        text.AppendLine($"media duration: {Format(h.MediaDuration)}");
        text.AppendLine($"audio duration: {Format(h.AudioDuration)}");
        text.AppendLine($"wall duration : {Format(h.WallClockDuration)} (includes input-loss dead air)");
        text.AppendLine($"A/V drift     : {h.AvDriftRatio:0.0000}");
        text.AppendLine();
        text.AppendLine("--- FFmpeg diagnostic tail ---");
        text.AppendLine(string.IsNullOrWhiteSpace(h.DiagnosticTail) ? "(none)" : h.DiagnosticTail);

        text.AppendLine();
        text.AppendLine("--- Fan-out (composite sink) ---");
        if (s.DetachedChildNames is { Count: > 0 })
        {
            foreach (string name in s.DetachedChildNames) text.AppendLine($"detached: {name}");
        }
        else
        {
            text.AppendLine("no destination was detached");
        }

        text.AppendLine();
        text.AppendLine("--- Files in this bundle ---");
        if (artifacts.Count == 0)
        {
            text.AppendLine($"{DiagnosticsEntryName} (this file - no logs or crash artifacts were found)");
        }
        else
        {
            int total = 0;
            foreach (DiagnosticsArtifact a in artifacts)
            {
                text.AppendLine($"{a.EntryName}  ({a.SizeBytes / 1024} KB)");
                total += (int)Math.Min(a.SizeBytes, int.MaxValue);
            }

            text.AppendLine($"-> {artifacts.Count} file(s), {total / 1024} KB of artifacts");
        }

        return text.ToString();
    }

    /// <summary>
    /// Selects the artifacts: the newest daily logs and crash reports from BOTH log locations, and the
    /// newest minidumps from BOTH dump locations. Newest first, deduplicated by entry name.
    /// </summary>
    public static IReadOnlyList<DiagnosticsArtifact> CollectArtifacts(DiagnosticsSnapshot s)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var artifacts = new List<DiagnosticsArtifact>();

        // The cap is GLOBAL across locations, not per directory: a bundle that grew a little every time
        // the forensics location changed would defeat the point of a cap. "crash-*.log" also matches
        // "*.log", so the two sets are separated explicitly rather than by the pattern alone.
        string[] logDirs = Distinct(s.LogDirectory, s.PrimaryLogDirectory).ToArray();
        string[] dumpDirs = Distinct(s.DumpDirectory, s.PrimaryDumpDirectory).ToArray();

        Add(Newest(Files(logDirs, "*.log", exclude: IsCrashLog), KeepLastLogs), LogsFolder, artifacts, seen);
        Add(Newest(Files(logDirs, "crash-*.log"), KeepLastCrashLogs), CrashLogsFolder, artifacts, seen);
        Add(Newest(Files(dumpDirs, "crash-*.dmp"), KeepLastDumps), DumpsFolder, artifacts, seen);

        return artifacts;
    }

    /// <summary>
    /// Writes the archive. Flushes the logger FIRST so the lines immediately before the request are on
    /// disk — the most valuable lines in the bundle are the ones the background writer has not reached.
    /// Never throws.
    /// </summary>
    public static DiagnosticsBundleResult Create(DiagnosticsSnapshot snapshot, string outputPath)
    {
        if (snapshot is null) return Failure("No snapshot was supplied.");
        if (string.IsNullOrWhiteSpace(outputPath)) return Failure("No output path was supplied.");

        var entries = new List<string>();
        bool created = false;

        try
        {
            AppLogger.Flush();

            IReadOnlyList<DiagnosticsArtifact> artifacts = CollectArtifacts(snapshot);

            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (FileStream file = File.Create(outputPath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                created = true;

                // The readout goes in FIRST and even when nothing else was found, so an empty bundle is
                // still a valid bundle rather than a zero-byte file.
                WriteText(archive, DiagnosticsEntryName, BuildDiagnosticsText(snapshot, artifacts));
                entries.Add(DiagnosticsEntryName);

                foreach (DiagnosticsArtifact artifact in artifacts)
                {
                    try
                    {
                        archive.CreateEntryFromFile(artifact.FullPath, artifact.EntryName, CompressionLevel.Optimal);
                        entries.Add(artifact.EntryName);
                    }
                    catch (Exception ex)
                    {
                        // The file was locked or removed between listing and reading. One missing
                        // artifact must not cost the operator the whole bundle.
                        AppLogger.LogInfo($"[DIAG] skipped '{artifact.FullPath}' in the bundle: {ex.Message}");
                    }
                }
            }

            long size = 0;
            try { size = new FileInfo(outputPath).Length; } catch { }

            AppLogger.LogInfo($"[DIAG] diagnostics bundle written: {outputPath} ({size} bytes, {entries.Count} entr(ies))");
            return new DiagnosticsBundleResult { Success = true, Path = outputPath, SizeBytes = size, Entries = entries };
        }
        catch (Exception ex)
        {
            // A half-written archive is worse than none: it looks like a valid bundle and would be
            // attached in place of the real artifacts.
            if (created) TryDelete(outputPath);

            AppLogger.LogInfo($"[DIAG] diagnostics bundle FAILED: {ex.Message}");
            return Failure(ex.Message);
        }
    }

    private static DiagnosticsBundleResult Failure(string error) =>
        new() { Success = false, Error = error };

    private static void WriteText(ZipArchive archive, string entryName, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void Add(
        IEnumerable<string> files, string folder, List<DiagnosticsArtifact> artifacts, HashSet<string> seen)
    {
        foreach (string path in files)
        {
            string entryName = UniqueEntryName(folder, Path.GetFileName(path), seen);
            if (entryName.Length == 0) continue;

            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }

            artifacts.Add(new DiagnosticsArtifact(path, entryName, size));
        }
    }

    /// <summary>
    /// Builds a folder-qualified entry name, disambiguating collisions. Two locations can hold a log
    /// with the same date, and a zip with duplicate entry names is not a valid archive.
    /// </summary>
    private static string UniqueEntryName(string folder, string fileName, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;

        string candidate = $"{folder}/{fileName}";
        if (seen.Add(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            candidate = $"{folder}/{stem}-{i}{extension}";
            if (seen.Add(candidate)) return candidate;
        }

        return string.Empty;
    }

    /// <summary>Every file matching the pattern across the given directories. An unreadable one contributes nothing.</summary>
    private static IEnumerable<string> Files(IEnumerable<string> directories, string pattern, Func<string, bool>? exclude = null)
    {
        var all = new List<string>();
        foreach (string directory in directories)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
                all.AddRange(Directory.GetFiles(directory, pattern)
                    .Where(p => exclude == null || !exclude(p)));
            }
            catch
            {
                // A locked or unreadable directory contributes nothing; the rest of the bundle still works.
            }
        }

        return all;
    }

    /// <summary>The newest <paramref name="keep"/> of the given files. Ties break on path so the choice is deterministic.</summary>
    private static IEnumerable<string> Newest(IEnumerable<string> files, int keep)
    {
        if (keep <= 0) return Array.Empty<string>();

        return files
            .Select(p => new { Path = p, Stamp = LastWriteUtc(p) })
            .OrderByDescending(x => x.Stamp)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Take(keep)
            .Select(x => x.Path)
            .ToList();
    }

    private static DateTime LastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static bool IsCrashLog(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Distinct(params string?[] directories)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? dir in directories)
        {
            if (!string.IsNullOrWhiteSpace(dir) && seen.Add(dir)) yield return dir;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Value(string? text) => string.IsNullOrWhiteSpace(text) ? "(unknown)" : text!;

    private static string Format(TimeSpan span) =>
        $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
}
