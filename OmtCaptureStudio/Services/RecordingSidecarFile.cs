using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Facts written beside a finished take (ADR-0014). Source-generated JSON: the app is Native AOT,
/// and a reflective serialize would vanish in the field build.
/// </summary>
public sealed class RecordingSidecar
{
    public string? ArmTimeUtc { get; set; }
    public string? FirstVideoFrameTimeUtc { get; set; }
    public string? FirstAudioTimeUtc { get; set; }
    public int ConfiguredRateNumerator { get; set; }
    public int ConfiguredRateDenominator { get; set; }
    public double MeasuredInputFps { get; set; }
    public long DuplicateFrames { get; set; }
    public long DroppedFrames { get; set; }
    public double AudioClampSeconds { get; set; }
    public string? SourceName { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RecordingSidecar))]
internal sealed partial class RecordingSidecarJsonContext : JsonSerializerContext;

/// <summary>Writes <c>{mediaPath}.json</c> next to a finished recording. Never throws.</summary>
public static class RecordingSidecarFile
{
    public static string PathFor(string mediaPath) => mediaPath + ".json";

    public static string ToIso(long utcTicks)
    {
        if (utcTicks <= 0) return string.Empty;
        var stamp = new DateTime(utcTicks, DateTimeKind.Utc);
        return stamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    public static bool TryWrite(string mediaPath, RecordingSidecar sidecar)
    {
        try
        {
            string json = JsonSerializer.Serialize(sidecar, RecordingSidecarJsonContext.Default.RecordingSidecar);
            File.WriteAllText(PathFor(mediaPath), json);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not write the recording sidecar for {mediaPath}: {ex.Message}", ex);
            return false;
        }
    }
}
