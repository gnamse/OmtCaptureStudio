using System;
using System.IO;

namespace OmtCaptureStudio.Services.Forensics;

/// <summary>
/// NET-017: where log lines and crash artifacts live, and whether that is the fallback. Returned as a
/// value so a resolver can hand the answer to both consumers at once.
/// </summary>
public sealed record LogLocationResult(
    string LogDirectory,
    string DumpDirectory,
    bool UsingFallback,
    string PrimaryLogDirectory,
    string PrimaryDumpDirectory);

/// <summary>
/// NET-017: the ONE location ladder for the logging + forensics family.
///
/// <para><b>Why this module exists.</b> <see cref="AppLogger"/> pinned its directory at the application
/// directory with no writability fallback, while <see cref="CrashForensics"/> independently re-resolved
/// with a <c>%LOCALAPPDATA%</c> fallback. On a read-only install the two disagreed: the crash report
/// landed in the fallback while every log line that led up to it was written to a directory that could
/// not be written - and was therefore lost. That broke the crash-report-to-surrounding-lines link that
/// the forensics module itself exists to provide.</para>
///
/// <para><b>Policy.</b> The application directory is preferred: the app ships as a portable ZIP, so
/// logs and forensics should travel with the install. When it is NOT writable (a read-only share, a
/// locked-down Program Files install) everything - log lines AND crash artifacts - redirects together
/// to <c>%LOCALAPPDATA%\OmtCaptureStudio</c>, rather than one half silently producing nothing.</para>
///
/// <para><b>Cost.</b> The probe runs ONCE per resolution (at install/resolver time), never per write.
/// Both consumers cache the answer, so the live logging path allocates exactly what it did before.</para>
/// </summary>
public static class LogLocation
{
    /// <summary>The portable-first primary root: wherever the executable lives.</summary>
    public static string DefaultPrimaryRoot => AppDomain.CurrentDomain.BaseDirectory;

    /// <summary>The portable-first primary log directory (the application directory's <c>logs</c>).</summary>
    public static string PrimaryLogDirectory => Path.Combine(DefaultPrimaryRoot, "logs");

    /// <summary>The portable-first primary forensics directory (the application directory's <c>crashdumps</c>).</summary>
    public static string PrimaryDumpDirectory => Path.Combine(DefaultPrimaryRoot, "crashdumps");

    /// <summary>The shared non-portable fallback root - the same home <c>settings.json</c> uses (ADR-0006).</summary>
    public static string FallbackRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmtCaptureStudio");

    /// <summary>
    /// The ladder, as a pure function of (primary dirs, fallback root, probe). No clock, no ambient
    /// state - so the read-only-install path is fully asserted headlessly, with an injected probe.
    /// </summary>
    public static LogLocationResult Resolve(
        string primaryLogDir,
        string primaryDumpDir,
        string fallbackRoot,
        Func<string, bool> isWritable)
    {
        if (isWritable(primaryLogDir) && isWritable(primaryDumpDir))
        {
            return new LogLocationResult(primaryLogDir, primaryDumpDir, false, primaryLogDir, primaryDumpDir);
        }

        return new LogLocationResult(
            Path.Combine(fallbackRoot, "logs"),
            Path.Combine(fallbackRoot, "crashdumps"),
            true,
            primaryLogDir,
            primaryDumpDir);
    }

    /// <summary>The production ladder: primary = the application directory, exactly as before.</summary>
    public static LogLocationResult ResolveFromAppDirectory(Func<string, bool>? isWritable = null)
        => Resolve(
            PrimaryLogDirectory,
            PrimaryDumpDirectory,
            FallbackRoot,
            isWritable ?? IsDirectoryWritable);

    /// <summary>
    /// The production writability probe. Creating the directory is a deliberate side effect - the caller
    /// wants it to exist anyway - then one probe file is written and removed. Called ONCE per resolution.
    /// </summary>
    public static bool IsDirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Best-effort directory creation - a location failure must never become a startup failure.</summary>
    public static void TryCreateDirectory(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { }
    }
}
