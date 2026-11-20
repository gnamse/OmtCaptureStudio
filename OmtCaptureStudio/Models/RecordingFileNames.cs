namespace OmtCaptureStudio.Models;

/// <summary>
/// The recording filename stem. One function, used when a preset or a settings file is applied and
/// again when a take is armed, so a prefix cannot carry a path segment or a character Windows will
/// not store. The ffmpeg argument vector is built separately; this only decides the file name.
/// </summary>
public static class RecordingFileNames
{
    public const string DefaultPrefix = "OMT_Capture";

    /// <summary>Long enough for a show name, short enough to leave room for the timestamp and <c>_partNN</c>.</summary>
    public const int MaxPrefixLength = 80;

    /// <summary>
    /// Returns <paramref name="prefix"/> when it is a single safe file-name stem, otherwise
    /// <see cref="DefaultPrefix"/>. Rejects directory separators, <c>..</c>, rooted paths, trailing
    /// dots or spaces, and any character in <see cref="Path.GetInvalidFileNameChars"/>.
    /// </summary>
    public static string SanitizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return DefaultPrefix;

        string trimmed = prefix.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxPrefixLength) return DefaultPrefix;
        if (trimmed.EndsWith('.') || trimmed.EndsWith(' ')) return DefaultPrefix;
        if (trimmed is "." or "..") return DefaultPrefix;

        // The files land on Windows. Path.GetInvalidFileNameChars() is the OS the process is
        // running on, which omits quotes and the rest of the Windows set when this is evaluated
        // on Linux (the headless suite). Use the Windows set explicitly so a quote cannot become
        // a stem on either OS.
        if (HasWindowsInvalidFileNameChar(trimmed)) return DefaultPrefix;

        return trimmed;
    }

    /// <summary>Characters Windows rejects in a file name, plus ASCII controls. Independent of the host OS.</summary>
    private static bool HasWindowsInvalidFileNameChar(string value)
    {
        foreach (char c in value)
        {
            if (c < ' ') return true;
            switch (c)
            {
                case '"':
                case '<':
                case '>':
                case '|':
                case ':':
                case '*':
                case '?':
                case '\\':
                case '/':
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The segment-1 file name the arm writes: <c>{safePrefix}_{timestamp}{extension}</c>.
    /// Public so the suite can assert the stem without starting ffmpeg.
    /// </summary>
    public static string TakeFileName(string? prefix, DateTime timestamp, string extension)
    {
        string ext = string.IsNullOrEmpty(extension) ? ".mp4" : extension;
        return $"{SanitizePrefix(prefix)}_{timestamp:yyyy-MM-dd_HH-mm-ss}{ext}";
    }
}
