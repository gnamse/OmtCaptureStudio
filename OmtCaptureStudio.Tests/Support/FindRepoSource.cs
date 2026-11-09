using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Xml.Linq;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Audio;
using OmtCaptureStudio.Services.Sinks;
using OmtCaptureStudio.Services.Sources;
using OmtCaptureStudio.Services.Forensics;
using OmtCaptureStudio.ViewModels;

namespace OmtCaptureStudio.Tests;

partial class Program
{
    /// <summary>Walks up from the test binary to the repo root and resolves a repo-relative source path.</summary>
    static string? FindRepoSource(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(Path.Combine(dir.FullName, "OmtCaptureStudio.sln"))) return null;
            dir = dir.Parent;
        }
        return null;
    }
}

