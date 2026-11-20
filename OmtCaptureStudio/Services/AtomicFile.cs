namespace OmtCaptureStudio.Services;

/// <summary>
/// Replaces a file by writing a temp file in the same directory, flushing it, then
/// <see cref="File.Replace"/> (or <see cref="File.Move"/> when the destination does not exist yet).
/// A crash mid-write leaves the previous file intact and a sibling <c>.tmp</c> that the next save ignores.
/// ADR-0006: a settings write must not truncate <c>settings.json</c> into an unreadable file.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // UTF-8 without a BOM, matching File.WriteAllText on modern .NET.
            var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // The original file was not touched. A leftover temp is ignored on the next save.
            }

            throw;
        }
    }
}
