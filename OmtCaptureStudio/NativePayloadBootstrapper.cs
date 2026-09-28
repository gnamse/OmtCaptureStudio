using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OmtCaptureStudio;

/// <summary>
/// Ensures all native C++ runtimes and codecs are available locally.
/// If running as a standalone single-file binary, extracts embedded native DLLs to LocalAppData on first run
/// and sets the DLL search directory before any native P/Invoke or Avalonia rendering occurs.
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
            // Standalone single executable mode: extract to LocalAppData
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            nativeDir = Path.Combine(localAppData, "OmtCaptureStudio", "runtimes", "v1.0.0.4");
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
                // In case another instance is running and has locked the file
            }
        }
    }
}
