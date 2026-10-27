using System;
using System.Diagnostics;
using System.IO;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Outcome of the startup FFmpeg probe (NET-010).
/// </summary>
/// <param name="IsAvailable">True when an executable was found AND answered <c>-version</c>.</param>
/// <param name="ExecutablePath">The resolved binary, or null when none was usable.</param>
/// <param name="VersionLine">The first line of <c>ffmpeg -version</c>, trimmed.</param>
/// <param name="Version">The version token parsed out of the line, when it could be read.</param>
/// <param name="Detail">Human-readable detail - the locations checked when unavailable.</param>
public sealed record FfmpegPreflightResult(
    bool IsAvailable,
    string? ExecutablePath,
    string? VersionLine,
    string? Version,
    string Detail)
{
    public static FfmpegPreflightResult Available(string path, string versionLine) =>
        new(true, path, versionLine, FfmpegPreflight.ParseVersion(versionLine), string.Empty);

    public static FfmpegPreflightResult Missing(string detail) =>
        new(false, null, null, null, detail);
}

/// <summary>
/// Startup preflight for ffmpeg.exe (NET-010). FFmpeg is the recording feature's ONLY external
/// dependency, and before this nothing validated it until the operator pressed Record - where a
/// failure surfaced as a transport error at the worst possible moment. This resolves the binary and
/// proves it RUNS, once, at startup.
///
/// <para><b>Resolution order matches the release build:</b> the application directory first (where
/// <c>scripts/build-release.ps1</c> stages <c>ffmpeg.exe</c>, and where the installer puts it), then
/// <c>PATH</c>.</para>
///
/// <para><b>Fully injectable.</b> File existence, the PATH value and the version probe are all
/// parameters, so the whole policy is asserted headlessly with no filesystem dependency and without
/// spawning anything. AOT-safe: plain <see cref="Process"/>, no reflection.</para>
/// </summary>
public static class FfmpegPreflight
{
    /// <summary>The bundled binary's name. Staged into the app directory by the release script.</summary>
    public const string ExecutableName = "ffmpeg.exe";

    /// <summary>How long <c>ffmpeg -version</c> may take before it is treated as unusable.</summary>
    public static int VersionTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Resolves ffmpeg and proves it runs. <paramref name="appDirectory"/> first, then each
    /// <paramref name="pathVariable"/> entry. Every filesystem and process touch is injectable.
    /// </summary>
    public static FfmpegPreflightResult Run(
        string appDirectory,
        Func<string, bool>? fileExists = null,
        Func<string, string?>? versionProbe = null,
        string? pathVariable = null)
    {
        Func<string, bool> exists = fileExists ?? (static path => File.Exists(path));
        Func<string, string?> probe = versionProbe ?? RunVersionCommand;
        string? path = pathVariable ?? Environment.GetEnvironmentVariable("PATH");

        var checkedLocations = new System.Collections.Generic.List<string>();

        // 1. The application directory - where the release stages and the installer places it.
        string local = Path.Combine(appDirectory ?? string.Empty, ExecutableName);
        checkedLocations.Add(local);

        if (exists(local))
        {
            string? line = TryReadVersion(local, probe);
            if (line != null) return FfmpegPreflightResult.Available(local, line);
        }

        // 2. PATH, in order. A binary that exists but cannot run is skipped rather than accepted.
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string entry in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                string candidate;
                try { candidate = Path.Combine(entry.Trim(), ExecutableName); }
                catch { continue; }

                if (!checkedLocations.Contains(candidate)) checkedLocations.Add(candidate);
                if (!exists(candidate)) continue;

                string? line = TryReadVersion(candidate, probe);
                if (line != null) return FfmpegPreflightResult.Available(candidate, line);
            }
        }

        return FfmpegPreflightResult.Missing(
            $"ffmpeg.exe not found or not runnable. Checked: {string.Join(", ", checkedLocations)}");
    }

    /// <summary>
    /// Pulls the version token out of an <c>ffmpeg -version</c> line, e.g.
    /// <c>ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) ...</c> yields
    /// <c>7.1-full_build-www.gyan.dev</c>. Returns null when the line does not carry a version.
    /// Pure, so the parsing is asserted directly.
    /// </summary>
    public static string? ParseVersion(string? versionLine)
    {
        if (string.IsNullOrWhiteSpace(versionLine)) return null;

        const string marker = "version";
        int at = versionLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        int start = at + marker.Length;
        while (start < versionLine.Length && char.IsWhiteSpace(versionLine[start])) start++;
        if (start >= versionLine.Length) return null;

        int end = start;
        while (end < versionLine.Length && !char.IsWhiteSpace(versionLine[end])) end++;

        string token = versionLine[start..end].Trim();
        return token.Length == 0 ? null : token;
    }

    private static string? TryReadVersion(string executable, Func<string, string?> probe)
    {
        try
        {
            string? line = probe(executable);
            if (string.IsNullOrWhiteSpace(line)) return null;
            return line.Trim();
        }
        catch
        {
            // A binary that exists but will not launch is "not runnable", not "found".
            return null;
        }
    }

    /// <summary>
    /// The production probe: runs <c>&lt;path&gt; -version</c> and returns its FIRST output line, which
    /// is the provenance line the release script records too. Bounded, because a wedged binary must not
    /// hang startup.
    /// </summary>
    public static string? RunVersionCommand(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            string? firstLine = process.StandardOutput.ReadLine();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(VersionTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
        }
        catch
        {
            return null;
        }
    }
}
