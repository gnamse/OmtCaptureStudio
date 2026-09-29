using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OmtCaptureStudio;

/// <summary>
/// Ensures all native C++ runtimes and codecs are available locally.
/// Follows the official .NET single-file extraction model:
/// If running as a standalone single-file binary, extracts embedded native DLLs to %TEMP%\.net\OmtCaptureStudio\{hash}\
/// and configures the DLL search directory before any native P/Invoke or Avalonia rendering occurs.
/// </summary>
public static class NativePayloadBootstrapper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpLibFileName);

    private static readonly string[] NativeLibraries = new[]
    {
        "libvmx.dll",
        "libomt.dll",
        "libomtnet.dll",
        "libHarfBuzzSharp.dll",
        "libSkiaSharp.dll",
        "av_libglesv2.dll"
    };

    public static void Initialize()
    {
        string baseDir = AppContext.BaseDirectory;
        
        // If all libraries already exist in the base directory (e.g. dev/loose directory), use them directly
        bool allExistInBase = true;
        foreach (var lib in NativeLibraries)
        {
            if (!File.Exists(Path.Combine(baseDir, lib)))
            {
                allExistInBase = false;
                break;
            }
        }

        string nativeDir = baseDir;

        if (!allExistInBase)
        {
            // Official .NET Single-File convention: %TEMP%\.net\OmtCaptureStudio\{hash}\
            string payloadHash = ComputePayloadHash();
            nativeDir = Path.Combine(Path.GetTempPath(), ".net", "OmtCaptureStudio", payloadHash);
            EnsureExtracted(nativeDir);
        }

        SetDllDirectory(nativeDir);

        // Pre-load libraries in dependency order to ensure immediate resolution
        foreach (var lib in NativeLibraries)
        {
            string fullPath = Path.Combine(nativeDir, lib);
            if (File.Exists(fullPath))
            {
                LoadLibrary(fullPath);
            }
        }
    }

    /// <summary>
    /// Computes a deterministic SHA-256 hash from the assembly version and embedded resource sizes.
    /// Changes automatically whenever code or embedded native binaries change, with zero hardcoded version strings.
    /// </summary>
    private static string ComputePayloadHash()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Include assembly version
        string versionStr = assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        writer.Write(versionStr);

        // Include metadata for each embedded native library
        foreach (var lib in NativeLibraries)
        {
            string resourceName = $"NativePayload.{lib}";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            writer.Write(lib);
            writer.Write(stream?.Length ?? 0L);
        }

        writer.Flush();
        ms.Position = 0;

        byte[] hashBytes = sha.ComputeHash(ms);
        return Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
    }

    private static void EnsureExtracted(string targetDir)
    {
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var lib in NativeLibraries)
        {
            string targetPath = Path.Combine(targetDir, lib);
            string resourceName = $"NativePayload.{lib}";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            // Only extract if file doesn't exist or size differs
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length == stream.Length)
            {
                continue;
            }

            try
            {
                string tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.CopyTo(fs);
                }
                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch
            {
                // In case another instance is running concurrently and has locked the file
            }
        }
    }
}
