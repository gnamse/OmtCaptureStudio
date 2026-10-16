using System;
using System.IO;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>Verdict of a start-of-recording storage preflight.</summary>
public enum StoragePreflightOutcome
{
    /// <summary>Enough room on a file system without a single-file size limit.</summary>
    Ok,

    /// <summary>The volume's status could not be determined (mapped/UNC path offline). Recording proceeds.</summary>
    Undetermined,

    /// <summary>FAT/FAT32: a 4 GB single-file cap makes a long recording silently unplayable. Refused.</summary>
    RefusedFatFileSystem,

    /// <summary>Free space below the session-size headroom requirement. Refused.</summary>
    RefusedInsufficientSpace
}

/// <summary>Outcome of a preflight, with the drive facts that produced it so callers can explain it.</summary>
public readonly record struct StoragePreflightResult(
    StoragePreflightOutcome Outcome,
    string DriveName,
    string DriveFormat,
    long FreeBytes,
    long RequiredBytes,
    string? Message)
{
    /// <summary>True when recording may start: <see cref="StoragePreflightOutcome.Ok"/> or <see cref="StoragePreflightOutcome.Undetermined"/>.</summary>
    public bool CanRecord => Outcome is StoragePreflightOutcome.Ok or StoragePreflightOutcome.Undetermined;

    /// <summary>True when the refusal was a deliberate policy decision (not an unknown volume).</summary>
    public bool IsRefusal => Outcome is StoragePreflightOutcome.RefusedFatFileSystem or StoragePreflightOutcome.RefusedInsufficientSpace;
}

/// <summary>
/// Disk-storage policy for recording: start-of-take preflight (file-system sanity, session-size
/// headroom) and the ongoing hard low-water mark. Pure and headless-testable — the only impure part
/// is the <see cref="DriveInfo"/> read in <see cref="TryGetFreeSpace"/>, and every decision that
/// follows from it is a pure function of the facts it returns (<see cref="Classify"/>,
/// <see cref="IsLowOnSpace"/>), so the policy is tested without a real volume.
///
/// <para>Extracted from <see cref="StreamRecorderService"/>, which previously mixed this policy in
/// beside the recording orchestration (ADR-0005).</para>
/// </summary>
public static class RecordingStorageGuard
{
    /// <summary>Planning horizon for the preflight size estimate: a default recording window.</summary>
    public static readonly TimeSpan PreflightWindow = TimeSpan.FromMinutes(60);

    /// <summary>Absolute free-space floor required to start, regardless of the estimate.</summary>
    public const long MinimumPreflightFreeBytes = 2L * 1024L * 1024L * 1024L; // 2 GB

    /// <summary>Hard low-water mark: below this while recording, stop and finalize to keep the file playable.</summary>
    public const long LowWaterMarkBytes = 1024L * 1024L * 1024L; // 1 GB

    /// <summary>Minimum interval between periodic low-disk checks (one DriveInfo query each).</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>File systems with a 4 GB single-file limit, which long recordings cannot survive.</summary>
    private static readonly string[] FattishFileSystems = { "FAT32", "FAT" };

    /// <summary>True for a file system whose single-file size limit breaks long recordings.</summary>
    public static bool IsFattishFileSystem(string? driveFormat)
    {
        if (string.IsNullOrWhiteSpace(driveFormat)) return false;
        foreach (string fattish in FattishFileSystems)
        {
            if (driveFormat.Equals(fattish, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Estimated bytes for one capture at the configured quality/encoder, scaled by pixel count and
    /// frame rate. The final duration is unknown up front, so this sizes
    /// <see cref="PreflightWindow"/>; the periodic low-water guard protects whatever actually happens.
    /// </summary>
    public static long EstimateSessionSize(RecordingConfig config, StreamFormat format)
    {
        // Baseline ≈ 20 Mbps for 1080p60 H.264 of a normal-quality broadcast feed.
        double baselineMbps = 20.0;

        double qualityScale = config.Quality switch
        {
            QualityPreset.Ultra => 2.0,
            QualityPreset.High => 1.5,
            QualityPreset.Medium => 1.0,
            QualityPreset.Low => 0.7,
            _ => 1.5
        };

        // ProRes is uncompressed-ish and very heavy.
        if (config.EncoderChoice == VideoEncoderChoice.ProRes)
        {
            qualityScale *= 6.0;
        }

        double pixels = Math.Max(1, (double)format.Width * format.Height);
        double fps = format.FrameRate > 0 ? format.FrameRate : 60.0;
        double pixelScale = pixels / (1920.0 * 1080.0) * (fps / 60.0);

        double estimatedMbps = baselineMbps * qualityScale * pixelScale;
        const double bitsPerByte = 8.0;
        double estimatedBytesPerSecond = estimatedMbps * 1_000_000.0 / bitsPerByte;
        return (long)(estimatedBytesPerSecond * PreflightWindow.TotalSeconds);
    }

    /// <summary>Bytes required to start: four times the estimate, or the absolute floor, whichever is larger.</summary>
    public static long RequiredPreflightBytes(long estimatedSessionBytes)
        => Math.Max(estimatedSessionBytes * 4L, MinimumPreflightFreeBytes);

    /// <summary>
    /// Pure preflight verdict from already-read drive facts. Kept separate from
    /// <see cref="Preflight"/> so the policy is testable without a real volume.
    /// </summary>
    public static StoragePreflightResult Classify(
        string driveName,
        string driveFormat,
        long freeBytes,
        long estimatedSessionBytes)
    {
        long required = RequiredPreflightBytes(estimatedSessionBytes);

        if (IsFattishFileSystem(driveFormat))
        {
            return new StoragePreflightResult(
                StoragePreflightOutcome.RefusedFatFileSystem,
                driveName,
                driveFormat,
                freeBytes,
                required,
                $"Cannot record: {driveName} is formatted as {driveFormat}, which has a 4 GB single-file limit. " +
                "Choose an output folder on an NTFS (or exFAT) volume for long recordings.");
        }

        if (freeBytes < required)
        {
            return new StoragePreflightResult(
                StoragePreflightOutcome.RefusedInsufficientSpace,
                driveName,
                driveFormat,
                freeBytes,
                required,
                $"Cannot record: only {FormatBytes(freeBytes)} free on {driveName}, but a " +
                $"{PreflightWindow.TotalMinutes:0}-minute capture at the selected quality needs about " +
                $"{FormatBytes(required)} free. Free up disk space or choose a different output folder.");
        }

        return new StoragePreflightResult(
            StoragePreflightOutcome.Ok, driveName, driveFormat, freeBytes, required, null);
    }

    /// <summary>The result used when the volume cannot be read: proceed, and let the periodic guard protect the session.</summary>
    public static StoragePreflightResult Undetermined()
        => new(StoragePreflightOutcome.Undetermined, string.Empty, string.Empty, 0, 0, null);

    /// <summary>Reads the volume holding <paramref name="filePath"/> and classifies it.</summary>
    public static StoragePreflightResult Preflight(string filePath, RecordingConfig config, StreamFormat format)
    {
        if (!TryGetFreeSpace(filePath, out string driveName, out string driveFormat, out long freeBytes))
        {
            return Undetermined();
        }

        return Classify(driveName, driveFormat, freeBytes, EstimateSessionSize(config, format));
    }

    /// <summary>
    /// Pure low-water verdict for the periodic guard. Already-low file systems (FAT32/exFAT) are exempt:
    /// they can legitimately show little free space, and refusing to start on them is the preflight's job.
    /// </summary>
    public static bool IsLowOnSpace(string? driveFormat, long freeBytes)
    {
        if (IsFattishFileSystem(driveFormat)) return false;
        return freeBytes < LowWaterMarkBytes;
    }

    /// <summary>Operator-facing explanation for a low-disk stop.</summary>
    public static string LowDiskMessage(long freeBytes, string path)
        => $"Low disk space: only {FormatBytes(freeBytes)} free. Stopping and finalizing recording to {path} to keep it playable.";

    /// <summary>
    /// Resolves the volume (and its free space / format) that will hold the given file. Uses
    /// <see cref="DriveInfo"/> directly; no reflection. Returns false when the volume can't be
    /// determined (UNC or dismounted target), so callers can degrade gracefully.
    /// </summary>
    public static bool TryGetFreeSpace(string filePath, out string driveName, out string driveFormat, out long freeBytes)
    {
        driveName = string.Empty;
        driveFormat = string.Empty;
        freeBytes = 0;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(filePath));
            if (string.IsNullOrEmpty(root)) return false;

            var info = new DriveInfo(root);
            if (!info.IsReady) return false;

            driveName = info.Name;
            driveFormat = info.DriveFormat ?? string.Empty;
            freeBytes = info.AvailableFreeSpace;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Compact operator-facing byte count.</summary>
    public static string FormatBytes(long bytes)
    {
        const double gb = 1024.0 * 1024.0 * 1024.0;
        const double mb = 1024.0 * 1024.0;
        return bytes >= gb ? $"{bytes / gb:F1} GB" : $"{bytes / mb:F0} MB";
    }
}
