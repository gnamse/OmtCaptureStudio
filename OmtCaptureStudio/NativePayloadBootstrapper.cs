using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio;

/// <summary>
/// Ensures all native C++ runtimes and codecs are available locally.
///
/// <para><b>SEC-04 (R18-R24).</b> This used to trust an extracted file whose <i>byte length</i> matched
/// the embedded one, and it extracted into a user-writable <c>%TEMP%</c> directory that it then handed to
/// a process-wide <c>SetDllDirectory</c>. Both halves were exploitable: a same-size planted file was
/// never re-extracted, and a planted file of any size survived a failed write because the extraction
/// failure was swallowed. Every input to the old directory name (version string, library names, library
/// lengths) is public, so the path was computable offline.</para>
///
/// <para>The rules now: content decides, not length (R18); the directory name is derived from the
/// payload CONTENT (R23); extraction goes to a per-user protected directory, never a shared temp path
/// (R22); a file in a pre-existing directory is validated by content before it is used (R23); an
/// extraction failure is a hard, reported failure rather than a silent skip (R20); the load is verified
/// at load time with the file held open across the check and the load, closing the verify-then-load race
/// (R19); <c>LoadLibrary</c>'s result is checked and reported (R21); and the process-wide DLL search path
/// is never pointed at a writable directory (R24) - every library is loaded by ABSOLUTE PATH.</para>
/// </summary>
public static class NativePayloadBootstrapper
{
    // R24: no SetDllDirectory at all. Loading by absolute path makes the process search path irrelevant
    // for these libraries, so widening it - especially to a writable location - buys nothing and is the
    // documented CWE-427 sink this pass exists to remove.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    private static readonly string[] NativeLibraries = new[]
    {
        "libvmx.dll",
        "libomt.dll",
        "libomtnet.dll",
        "libHarfBuzzSharp.dll",
        "libSkiaSharp.dll",
        "av_libglesv2.dll"
    };

    /// <summary>Handles held open for the process lifetime so a verified file cannot be swapped (R19).</summary>
    private static readonly List<FileStream> VerifiedHandles = new();

    private static readonly object Gate = new();

    private static bool _initialized;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
        }

        string baseDir = AppContext.BaseDirectory;
        Assembly assembly = Assembly.GetExecutingAssembly();

        // Fast path unchanged in spirit: a loose directory that already carries the payload is used in
        // place. It is no longer TRUSTED on existence alone - each file is content-verified (R23) and the
        // whole set must verify, or we fall through to the verified extraction path.
        if (AllLibrariesPresentAndValid(baseDir, assembly))
        {
            LoadAll(baseDir);
            return;
        }

        string nativeDir = EnsureExtractedDirectory(assembly);
        LoadAll(nativeDir);
    }

    // ------------------------------------------------------------------ verification primitives

    /// <summary>
    /// R18/R23: the identity of the payload is its CONTENT. Streamed so an embedded 40 MB library is not
    /// materialised as a byte array just to be hashed.
    /// </summary>
    /// <summary>SEC-04 (R18): the content identity of one embedded payload. Public so the suite can build a
    /// deliberately same-length-but-wrong file - the exact shape the old length check accepted.</summary>
    public static string HashOfResource(Assembly assembly, string lib)
    {
        using Stream? stream = assembly.GetManifestResourceStream($"NativePayload.{lib}");
        if (stream is null)
            throw new InvalidOperationException($"Native payload '{lib}' is not embedded in this build.");

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// R19: hashes an OPEN handle, not a path. The caller keeps the handle for the process lifetime, so
    /// the bytes that were verified are the bytes that stay on disk - a verify-then-load pair with no gap.
    /// </summary>
    private static string HashOfHandle(FileStream handle)
    {
        long position = handle.Position;
        handle.Position = 0;
        using var sha = SHA256.Create();
        string hash = Convert.ToHexString(sha.ComputeHash(handle)).ToLowerInvariant();
        handle.Position = position;
        return hash;
    }

    /// <summary>
    /// R23: the DIRECTORY name is content-addressed, so a directory can only be reused by a build whose
    /// payload is byte-identical. The old name mixed the version string and the libraries' LENGTHS -
    /// every one of which is public, so the location was predictable without touching the machine.
    /// </summary>
    private static string ComputeContentHash(Assembly assembly)
    {
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            foreach (string lib in NativeLibraries)
            {
                writer.Write(lib);
                writer.Write(HashOfResource(assembly, lib));   // content, never length
            }
        }

        ms.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(ms))[..16].ToLowerInvariant();
    }

    // ------------------------------------------------------------------ target directory (R22/R23)

    /// <summary>
    /// R22: a per-user protected directory, never a shared temp path. Returns a directory whose files are
    /// all content-verified; throws rather than returning one that is not (R20).
    /// </summary>
    private static string EnsureExtractedDirectory(Assembly assembly)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmtCaptureStudio",
            "native");

        Directory.CreateDirectory(root);

        string contentHash = ComputeContentHash(assembly);
        string target = Path.Combine(root, contentHash);

        if (Directory.Exists(target) && !DirectoryIsTrusted(target))
        {
            // R22's "do not use it". Rather than load from a directory whose owner or ACL we cannot stand
            // behind - and rather than brick the app by refusing outright, which would hand an attacker a
            // trivial denial of service - fall back to a fresh unpredictable sibling directory that the
            // attacker cannot pre-create. The verified extraction below still has to pass.
            target = Path.Combine(root, contentHash + "-" + Guid.NewGuid().ToString("N")[..12]);
        }

        ExtractVerified(target, assembly);
        return target;
    }

    /// <summary>
    /// R22: is this directory ours and closed? A directory owned by someone else, or granting write access
    /// to a broad principal, is not a place to load executable code from.
    /// </summary>
    private static bool DirectoryIsTrusted(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            DirectorySecurity security = info.GetAccessControl();

            string currentUser = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
            string owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty;
            if (owner.Length > 0 && owner != currentUser) return false;

            // Any allow-rule that grants write/append to a principal which is not the current user, SYSTEM
            // or Administrators is a reason to walk away.
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.AppendData
                                              | FileSystemRights.Delete | FileSystemRights.TakeOwnership)) == 0)
                    continue;

                string id = rule.IdentityReference.Value;
                if (id == currentUser) continue;
                if (id == "S-1-5-18") continue;   // SYSTEM
                if (id == "S-1-5-32-544") continue;   // Administrators
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            // If we cannot read the ACL we cannot vouch for the directory - do not use it.
            return false;
        }
    }

    /// <summary>R22: current user, SYSTEM and Administrators only - no inherited broad grants.</summary>
    private static void ApplyRestrictiveAcl(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            DirectorySecurity security = info.GetAccessControl();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Native payload: could not apply the restrictive ACL to '{path}'.", ex);
        }
    }

    // ------------------------------------------------------------------ extraction (R18/R20/R23)

    /// <summary>
    /// SEC-04 (R18/R20/R23): extract every payload into <paramref name="targetDir"/> and prove each one by
    /// content. Public so the headless suite can drive this against a TEMP directory with a deliberately
    /// planted file - the only way to prove the length-match defence without touching %LOCALAPPDATA%.
    /// </summary>
    public static void ExtractVerified(string targetDir, Assembly assembly)
    {
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            ApplyRestrictiveAcl(targetDir);
        }

        foreach (string lib in NativeLibraries)
        {
            string target = Path.Combine(targetDir, lib);
            string expected = HashOfResource(assembly, lib);

            // R23: a pre-existing file is judged by CONTENT. If it matches, it is already correct; if it
            // does not - or cannot be read - it is replaced by the verified copy.
            if (File.Exists(target) && FileMatches(target, expected))
                continue;

            ExtractOne(assembly, lib, target, expected);
        }
    }

    /// <summary>SEC-04 (R23): does the file at this path hash to the expected value? Public because the
    /// suite asserts both directions of it directly.</summary>
    public static bool FileMatches(string path, string expectedHash)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return fs.Length > 0 && HashOfHandle(fs) == expectedHash;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// R20: an extraction failure for a library is a HARD, reported failure. The previous version's
    /// blanket catch meant any planted file survived a failed write and was then loaded anyway.
    /// </summary>
    private static void ExtractOne(Assembly assembly, string lib, string target, string expectedHash)
    {
        string temp = target + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            using (Stream? source = assembly.GetManifestResourceStream($"NativePayload.{lib}"))
            {
                if (source is null)
                    throw new InvalidOperationException($"Native payload '{lib}' is not embedded in this build.");

                using var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                source.CopyTo(fs);
            }

            // Verify the TEMP COPY before it is promoted, so a truncated write never becomes the file we
            // are about to trust. (Belt and braces: the promote below is followed by a re-verify too.)
            if (!FileMatches(temp, expectedHash))
                throw new InvalidOperationException($"Native payload '{lib}' did not verify after extraction.");

            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            AppLogger.LogError(
                $"Native payload '{lib}' could not be extracted to '{targetDirFor(target)}'. " +
                "The application cannot load its native components and will not continue on an unverified file.", ex);
            throw new InvalidOperationException(
                $"Native payload '{lib}' could not be extracted and verified. Refusing to load an unverified native library.", ex);
        }

        // R23 in the strict form: what is now on disk is what was embedded, or we stop.
        if (!FileMatches(target, expectedHash))
        {
            AppLogger.LogError($"Native payload '{lib}' failed verification after being written to disk.");
            throw new InvalidOperationException($"Native payload '{lib}' failed post-extraction verification.");
        }

        static string targetDirFor(string t)
        {
            try { return Path.GetDirectoryName(t) ?? t; } catch (Exception) { return t; }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    // ------------------------------------------------------------------ loading (R19/R21/R24)

    /// <summary>
    /// R24: every library is loaded by ABSOLUTE PATH, so the process search path plays no part. R19: the
    /// file is opened, its content verified through that same handle, and the handle is RETAINED for the
    /// process lifetime - it cannot be replaced between the check and the load, and cannot be replaced
    /// afterwards either. R21: the load's return value is checked and the failure is reported with the
    /// library name and the Win32 error.
    /// </summary>
    private static void LoadAll(string directory)
    {
        var loaded = new List<FileStream>();

        foreach (string lib in NativeLibraries)
        {
            string path = Path.Combine(directory, lib);
            if (!File.Exists(path)) continue;

            FileStream? handle = null;
            try
            {
                handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

                Assembly assembly = Assembly.GetExecutingAssembly();
                string expected = HashOfResource(assembly, lib);
                if (HashOfHandle(handle) != expected)
                {
                    handle.Dispose();
                    throw new InvalidOperationException(
                        $"Native payload '{lib}' at '{path}' does not match this build's embedded content. Refusing to load it.");
                }

                IntPtr module = LoadLibraryW(path);
                if (module == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    AppLogger.LogError(
                        $"Native payload '{lib}' failed to load from '{path}' (Win32 error {error}).");
                    throw new InvalidOperationException(
                        $"Native payload '{lib}' failed to load (Win32 error {error}).");
                }

                loaded.Add(handle);
            }
            catch (Exception)
            {
                handle?.Dispose();
                throw;
            }
        }

        lock (Gate)
        {
            VerifiedHandles.AddRange(loaded);
        }
    }

    // ------------------------------------------------------------------ base-directory validation

    /// <summary>
    /// R23: the loose-directory fast path is no longer taken merely because files exist - every file must
    /// match the embedded content. A directory that fails is not used; the verified extraction runs instead.
    /// </summary>
    /// <summary>
    /// SEC-04 (R23): the loose-directory fast path, judged by CONTENT. Public for the same reason as
    /// <see cref="ExtractVerified"/>: the suite must be able to show that a directory holding a WRONG-CONTENT
    /// file is rejected, because that is what stops the unverified directory being used.
    /// </summary>
    public static bool AllLibrariesPresentAndValid(string baseDir, Assembly assembly)
    {
        foreach (string lib in NativeLibraries)
        {
            string candidate = Path.Combine(baseDir, lib);
            if (!File.Exists(candidate)) return false;
            if (!FileMatches(candidate, HashOfResource(assembly, lib))) return false;
        }

        return true;
    }
}
