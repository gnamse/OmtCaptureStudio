using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.AccessControl;
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
    /// <summary>
    /// SEC-04 (R18/R20/R23). The bootstrapper used to trust an extracted file whose BYTE LENGTH matched the
    /// embedded one, so a same-size planted DLL was never re-extracted and was then loaded. These cases pin
    /// the replacement rule by consequence: the exact shape the old check accepted must no longer verify.
    /// </summary>
    static void TestNativePayloadVerification()
    {
        Console.WriteLine("\n[Step 0.704] Verifying native payloads by CONTENT, not length (SEC-04, R18/R20/R23)...");

        Assembly product = typeof(NativePayloadBootstrapper).Assembly;
        string root = Path.Combine(Path.GetTempPath(), "omt-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        const string lib = "libvmx.dll";
        string expectedHash = NativePayloadBootstrapper.HashOfResource(product, lib);
        Expect(expectedHash.Length == 64,
            $"R18: the payload identity must be a SHA-256 over CONTENT; got '{expectedHash}'.");

        long payloadLength;
        using (Stream? res = product.GetManifestResourceStream("NativePayload." + lib))
        {
            Expect(res is not null, "The native payload must be embedded in the product assembly.");
            payloadLength = res!.Length;
        }

        // ---- THE HEADLINE CASE: same LENGTH, wrong CONTENT ------------------------------------------
        // This is precisely the file the OLD check accepted without looking: equal byte count, different
        // bytes. It must not verify, and extraction must overwrite it.
        string planted = Path.Combine(root, lib);
        File.WriteAllBytes(planted, new byte[payloadLength]);   // all zeros: identical size, wrong content
        Expect(new FileInfo(planted).Length == payloadLength,
            "The planted file must be the same SIZE as the real payload for this case to mean anything.");
        Expect(!NativePayloadBootstrapper.FileMatches(planted, expectedHash),
            "R18/R23: a same-size, wrong-content file must NOT be treated as already extracted.");

        NativePayloadBootstrapper.ExtractVerified(root, product);
        Expect(NativePayloadBootstrapper.FileMatches(planted, expectedHash),
            "R18/R23: verified extraction must REPLACE a same-size planted file with the real content.");
        Expect(NativePayloadBootstrapper.AllLibrariesPresentAndValid(root, product),
            "R23: after verified extraction the whole directory must pass the content check.");

        // ---- R23: ONE wrong-content library makes the WHOLE directory unusable ----------------------
        // This is what stops an unverified directory being used as the fast path.
        string victim = Path.Combine(root, "libSkiaSharp.dll");
        byte[] correct = File.ReadAllBytes(victim);
        File.WriteAllBytes(victim, new byte[correct.Length]);   // same size again, wrong content
        Expect(!NativePayloadBootstrapper.AllLibrariesPresentAndValid(root, product),
            "R23: a directory with ONE wrong-content library must be rejected, so the verified extraction runs instead.");

        // ---- and the check is not a blanket refusal -------------------------------------------------
        File.WriteAllBytes(victim, correct);
        Expect(NativePayloadBootstrapper.AllLibrariesPresentAndValid(root, product),
            "R23: restoring the correct content must pass - the check discriminates, it does not just always say no.");

        // ---- an empty directory is not usable (no library is trusted into existence) -----------------
        string emptyDir = Path.Combine(Path.GetTempPath(), "omt-native-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        Expect(!NativePayloadBootstrapper.AllLibrariesPresentAndValid(emptyDir, product),
            "R23: a directory with no libraries must not pass the fast-path check.");

        // ---- R20: a failed extraction is a HARD failure, never a silent skip ------------------------
        // The blanket catch used to swallow this, after which the planted file was loaded anyway. A target
        // path that cannot be created as a directory must THROW rather than continue.
        string notADir = Path.Combine(Path.GetTempPath(), "omt-native-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(notADir, "I am a file, not a directory");
        bool threw = false;
        try { NativePayloadBootstrapper.ExtractVerified(notADir, product); }
        catch (Exception) { threw = true; }
        Expect(threw, "R20: an extraction that cannot proceed must THROW, not be silently skipped.");
        File.Delete(notADir);

        // ---- R23: the directory name is derived from CONTENT, and is stable -------------------------
        string nameA = NativePayloadBootstrapper.ComputeContentHash(product);
        string nameB = NativePayloadBootstrapper.ComputeContentHash(product);
        Expect(nameA.Length == 16, $"R23: the content-addressed name is 16 hex chars; got '{nameA}'.");
        Expect(nameA.All(char.IsAsciiHexDigitLower), $"R23: the name must be lowercase hex; got '{nameA}'.");
        Expect(nameA == nameB,
            "R23: identical content must produce an IDENTICAL name - that is what makes the directory content-addressed.");

        // and the naming rule must be the one actually in force on this machine
        string liveDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmtCaptureStudio", "native", nameA);
        if (Directory.Exists(liveDir))
        {
            Expect(NativePayloadBootstrapper.AllLibrariesPresentAndValid(liveDir, product),
                "R23: the live extraction directory (named by the content hash) must hold the verified payload.");
            Console.WriteLine($"   [note] the live extraction dir IS named by the content hash: {nameA}");
        }
        else
        {
            Console.WriteLine("   [note] no live extraction dir present in this session - naming rule asserted above.");
        }

        // ---- R22: the ACL bound is ASSERTED, not assumed -------------------------------------------
        string aclDir = Path.Combine(Path.GetTempPath(), "omt-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(aclDir);
        Expect(NativePayloadBootstrapper.ApplyRestrictiveAcl(aclDir),
            "R22: applying the restrictive ACL to a directory we own must SUCCEED - and report that it did.");
        Expect(NativePayloadBootstrapper.AllLibrariesPresentAndValid(aclDir, product) == false,
            "R22: an ACL'd but empty directory still must not pass the payload check.");
        Directory.Delete(aclDir, recursive: true);

        // ---- R22: explicit Everyone (CI TEMP shape) must not survive ApplyRestrictiveAcl ------------
        // GitHub Actions often freezes TEMP inheritance so CreateDirectory leaves Everyone as an
        // *explicit* ACE. The old GetAccessControl()+disable-inheritance path kept that ACE and
        // DirectoryIsTrusted then refused ExtractVerified (step 0.704 on runners).
        string explicitEveryoneDir = Path.Combine(Path.GetTempPath(), "omt-acl-explicit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(explicitEveryoneDir);
        {
            var info = new DirectoryInfo(explicitEveryoneDir);
            DirectorySecurity inherited = info.GetAccessControl();
            inherited.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            info.SetAccessControl(inherited);
        }
        Expect(NativePayloadBootstrapper.ApplyRestrictiveAcl(explicitEveryoneDir),
            "R22: ApplyRestrictiveAcl must succeed even when Everyone is already an explicit ACE.");
        {
            var info = new DirectoryInfo(explicitEveryoneDir);
            DirectorySecurity after = info.GetAccessControl();
            bool everyoneWrite = false;
            foreach (FileSystemAccessRule rule in after.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (rule.IdentityReference.Value != "S-1-1-0") continue;
                if ((rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.AppendData
                                              | FileSystemRights.Delete | FileSystemRights.TakeOwnership)) != 0)
                    everyoneWrite = true;
            }
            Expect(!everyoneWrite,
                "R22: after ApplyRestrictiveAcl, Everyone must not retain write/delete (clean ACL descriptor).");
        }
        // And ExtractVerified on a fresh sibling under this parent must still arm (CI failure site).
        string extractUnderExplicit = Path.Combine(explicitEveryoneDir, "extract-" + Guid.NewGuid().ToString("N"));
        NativePayloadBootstrapper.ExtractVerified(extractUnderExplicit, product);
        Expect(NativePayloadBootstrapper.AllLibrariesPresentAndValid(extractUnderExplicit, product),
            "R22: ExtractVerified must protect+populate a new directory even when the parent once had explicit Everyone.");
        Directory.Delete(explicitEveryoneDir, recursive: true);

        // ---- R19/R21: the LOAD path, driven through an injectable loader -----------------------------
        // (3) a content-mismatched file must be refused BEFORE the loader is ever called
        int loaderCalls = 0;
        Func<string, IntPtr> countingLoader = _ => { loaderCalls++; return new IntPtr(1); };

        string badDir2 = Path.Combine(Path.GetTempPath(), "omt-load-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(badDir2);
        File.WriteAllBytes(Path.Combine(badDir2, lib), new byte[payloadLength]);   // same length, wrong content
        bool refusedBeforeLoad = false;
        try { NativePayloadBootstrapper.LoadAll(badDir2, countingLoader); }
        catch (Exception) { refusedBeforeLoad = true; }
        Expect(refusedBeforeLoad, "R18/R19: a content-mismatched file must be refused on the LOAD path.");
        Expect(loaderCalls == 0,
            $"R19: the loader must NEVER be called for an unverified file; it was called {loaderCalls} time(s).");
        Directory.Delete(badDir2, recursive: true);

        // (5) a verified file whose LOAD fails must report the library name and the loader's failure
        string badDir3 = Path.Combine(Path.GetTempPath(), "omt-load-fail-" + Guid.NewGuid().ToString("N"));
        NativePayloadBootstrapper.ExtractVerified(badDir3, product);   // real, verified content
        string loadFailMessage = "";
        try { NativePayloadBootstrapper.LoadAll(badDir3, _ => IntPtr.Zero); }   // the loader fails
        catch (Exception ex) { loadFailMessage = ex.Message; }
        Expect(loadFailMessage.Length > 0, "R21: a LoadLibrary failure must be RAISED, not discarded.");
        Expect(loadFailMessage.Contains(lib),
            $"R21: the failure must NAME the library; got '{loadFailMessage}'.");
        Directory.Delete(badDir3, recursive: true);

        // and the positive direction: verified content + a working loader returns what it loaded
        string goodDir = Path.Combine(Path.GetTempPath(), "omt-load-ok-" + Guid.NewGuid().ToString("N"));
        NativePayloadBootstrapper.ExtractVerified(goodDir, product);
        IReadOnlyList<string> loadedPaths = NativePayloadBootstrapper.LoadAll(goodDir, _ => new IntPtr(1));
        Expect(loadedPaths.Count > 0, "A verified directory with a working loader must load its libraries.");
        Expect(loadedPaths.All(x => x.StartsWith(goodDir, StringComparison.OrdinalIgnoreCase)),
            "R24: every library must be loaded by ABSOLUTE PATH from the verified directory.");

        // R19's mechanism, demonstrated rather than described: LoadAll RETAINS a handle per verified file
        // for the process lifetime, so the bytes that were verified are the bytes that stay on disk. The
        // observable consequence is that the verified directory cannot be swapped out from under us -
        // deletion fails while the handles are held. That is the TOCTOU protection working.
        bool couldBeDeleted = true;
        try { Directory.Delete(goodDir, recursive: true); } catch (Exception) { couldBeDeleted = false; }
        Expect(!couldBeDeleted,
            "R19: the verified files must be HELD OPEN, so the directory cannot be replaced while loaded. " +
            "If this deletes, the retention guarantee is gone.");
        // deliberately NOT cleaned up: the handles are held for the process lifetime by design. The
        // directory lives in the temp root and goes with it.

        // ---- R22/R23: an UNTRUSTED pre-existing directory is walked away from ------------------------
        // The content-hash directory exists but is not one we can vouch for, so a FRESH nonce sibling is
        // used instead - never the directory we cannot stand behind.
        string distrustRoot = Path.Combine(Path.GetTempPath(), "omt-distrust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(distrustRoot);
        string wouldBeTarget = Path.Combine(distrustRoot, NativePayloadBootstrapper.ComputeContentHash(product));
        Directory.CreateDirectory(wouldBeTarget);
        File.WriteAllBytes(Path.Combine(wouldBeTarget, lib), new byte[payloadLength]);   // hostile content

        string chosen = NativePayloadBootstrapper.EnsureExtractedDirectory(
            product, rootOverride: distrustRoot, isTrusted: _ => false);   // force distrust
        Expect(chosen != wouldBeTarget,
            "R22/R23: an untrusted pre-existing directory must NOT be used.");
        Expect(Path.GetDirectoryName(chosen) == distrustRoot,
            "R22: the replacement must be a SIBLING in the same root, not somewhere else.");
        Expect(NativePayloadBootstrapper.AllLibrariesPresentAndValid(chosen, product),
            "R23: the replacement directory must hold the VERIFIED payload.");
        Directory.Delete(distrustRoot, recursive: true);

        // ---- R22: a CREATED directory whose ACL application FAILS is never silently used ----------------
        // The failure mode this closes: ApplyRestrictiveAcl swallowed its own error, so a directory that was
        // never actually protected was then used anyway - the bound existed only in a comment.
        string aclFailRoot = Path.Combine(Path.GetTempPath(), "omt-aclfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(aclFailRoot);
        string expectedFirst = Path.Combine(aclFailRoot, NativePayloadBootstrapper.ComputeContentHash(product));

        int aclCalls = 0;
        string aclChosen = NativePayloadBootstrapper.EnsureExtractedDirectory(
            product, rootOverride: aclFailRoot,
            // The FIRST attempt reports failure; the retry performs the REAL ACL application. A fake that
            // simply returns true would not be believed - the real DirectoryIsTrusted still runs, and an
            // unprotected directory is correctly rejected. That is the bound working, so the retry must
            // genuinely protect.
            applyAcl: path =>
            {
                aclCalls++;
                return aclCalls > 1 && NativePayloadBootstrapper.ApplyRestrictiveAcl(path);
            });

        Expect(aclCalls >= 2,
            $"R22: a failed ACL application must trigger a retry in a fresh directory; ACL calls were {aclCalls}.");
        Expect(aclChosen != expectedFirst,
            "R22: a directory whose protection FAILED must not be used - a fresh sibling must be chosen.");
        Expect(Path.GetDirectoryName(aclChosen) == aclFailRoot,
            "R22: the fallback must be a sibling in the same root.");
        Expect(NativePayloadBootstrapper.AllLibrariesPresentAndValid(aclChosen, product),
            "R23: the fallback directory must still hold the VERIFIED payload.");
        Directory.Delete(aclFailRoot, recursive: true);

        // and when NO directory can be protected, it must STOP rather than proceed unprotected
        string aclAlways = Path.Combine(Path.GetTempPath(), "omt-aclnever-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(aclAlways);
        bool aclRefused = false;
        try
        {
            NativePayloadBootstrapper.EnsureExtractedDirectory(
                product, rootOverride: aclAlways, applyAcl: _ => false);
        }
        catch (Exception) { aclRefused = true; }
        Expect(aclRefused,
            "R22: if no directory can be protected, extraction must REFUSE rather than load from an unprotected path.");
        Directory.Delete(aclAlways, recursive: true);

        Directory.Delete(root, recursive: true);
        Directory.Delete(emptyDir, recursive: true);

        Console.WriteLine(" -> payload identity is a content SHA-256; a same-SIZE wrong-content file is no longer trusted,");
        Console.WriteLine("    extraction replaces it, one bad file invalidates the directory, and the check discriminates!");
    }
}

