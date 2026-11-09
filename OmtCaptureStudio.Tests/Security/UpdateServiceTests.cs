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
    /// <summary>
    /// <summary>
    /// NET-005: one deep assertion per fact. Replaces the four-line `if (!cond) { throw new Exception(...); }`
    /// blocks that dominated the presentation step — the message is the FACT, and it may interpolate the
    /// actual value when the value is the interesting part of the failure.
    /// </summary>
    /// <summary>NET-023: a release feed with no network. Counts calls so the once-per-launch cache is provable.</summary>
    sealed class FakeUpdateTransport : IUpdateTransport
    {
        public readonly Dictionary<string, string> Strings = new(StringComparer.Ordinal);
        public readonly Dictionary<string, byte[]> Bytes = new(StringComparer.Ordinal);
        public int StringCalls;
        public int ByteCalls;
        public bool Offline;

        public Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            StringCalls++;
            if (Offline) return Task.FromResult<string?>(null);
            return Task.FromResult(Strings.TryGetValue(url, out string? v) ? v : null);
        }

        public Task<byte[]?> GetBytesAsync(string url, CancellationToken cancellationToken)
        {
            ByteCalls++;
            if (Offline) return Task.FromResult<byte[]?>(null);
            return Task.FromResult(Bytes.TryGetValue(url, out byte[]? v) ? v : null);
        }
    }

    static string FakeReleaseJson(string tag, string setupName, long size, string shaName)
    {
        // Plain concatenation on purpose: a raw interpolated string here would need brace escaping, and
        // a transport layer that eats braces is exactly how a generated test silently stops testing JSON.
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"tag_name\":\"").Append(tag).Append("\",");
        sb.Append("\"html_url\":\"https://github.com/gnamse/OmtCaptureStudio/releases/tag/").Append(tag).Append("\",");
        sb.Append("\"assets\":[");
        sb.Append("{\"name\":\"").Append(setupName).Append("\",\"browser_download_url\":\"https://dl.example/").Append(setupName).Append("\",\"size\":").Append(size).Append("},");
        sb.Append("{\"name\":\"").Append(shaName).Append("\",\"browser_download_url\":\"https://dl.example/").Append(shaName).Append("\",\"size\":102}");
        sb.Append("]}");
        return sb.ToString();
    }

    static void TestUpdateService()
    {
        Console.WriteLine("\n[Step 0.701] Verifying the auto-update service (version policy, feed parsing, verification, channel)...");

        // ---- 1. VERSION PARSING (pure policy, no I/O) ----------------------------------------------
        Expect(UpdateService.TryParseVersion("v1.0.0.22", out Version? vTag) && vTag!.ToString() == "1.0.0.22",
            "A leading v on a release tag must be stripped.");
        Expect(UpdateService.TryParseVersion("1.0.0.22+10c78ce", out Version? vSha) && vSha!.ToString() == "1.0.0.22",
            "A +<sha> build suffix must be ignored, not parsed as a segment.");
        Expect(!UpdateService.TryParseVersion("1.0.0-beta", out _), "A non-numeric tag must not be coerced into a version.");
        Expect(!UpdateService.TryParseVersion("", out _) && !UpdateService.TryParseVersion(null, out _),
            "An empty or null version must be rejected.");
        Expect(!UpdateService.TryParseVersion("1..0", out _), "A malformed dotted version must be rejected.");

        // ---- 2. THE COMPARISON PREDICATE, EACH SEGMENT ---------------------------------------------
        Expect(!UpdateService.IsNewerVersion(new Version(1, 0, 0, 22), new Version(1, 0, 0, 22)), "An equal version is not an update.");
        Expect(UpdateService.IsNewerVersion(new Version(1, 0, 0, 23), new Version(1, 0, 0, 22)), "A newer build segment is an update.");
        Expect(UpdateService.IsNewerVersion(new Version(1, 1, 0, 0), new Version(1, 0, 0, 22)), "A newer minor is an update.");
        Expect(UpdateService.IsNewerVersion(new Version(2, 0, 0, 0), new Version(1, 0, 0, 22)), "A newer major is an update.");
        Expect(!UpdateService.IsNewerVersion(new Version(1, 0, 0, 21), new Version(1, 0, 0, 22)), "An older build segment is not an update.");
        // THE ONE THAT WOULD HAVE SHIPPED A PERMANENT NAG: a three-part ProductVersion (what the entry
        // assembly actually reports today) is ALWAYS 'older' than a four-part tag, so using it as the
        // version source would offer an update on every single launch forever.
        Expect(UpdateService.IsNewerVersion(new Version(1, 0, 0, 22), new Version(1, 0, 0)),
            "This is why the version source must be four-part: 1.0.0 is ordered below 1.0.0.22.");

        // ---- 3. THE VERSION SOURCE: version.txt wins, the assembly value is the fallback -------------
        string vdir = Path.Combine(Path.GetTempPath(), "omt-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vdir);
        string vfile = Path.Combine(vdir, UpdateService.VersionFileName);
        File.WriteAllText(vfile, "1.0.0.22");
        string stamped = UpdateService.ResolveInstalledVersion(vdir, () => "1.0.0+deadbeef");
        Expect(stamped == "1.0.0.22", $"The stamped version file must win over the assembly value; got {stamped}.");
        File.Delete(vfile);
        string fellBack = UpdateService.ResolveInstalledVersion(vdir, () => "1.0.0+deadbeef");
        Expect(fellBack == "1.0.0+deadbeef", $"With no version file the assembly value is the fallback; got {fellBack}.");
        Directory.Delete(vdir, recursive: true);

        // ---- 4. CHANNEL DETECTION --------------------------------------------------------------------
        var env = new Func<string, string?>(k => k switch
        {
            "LOCALAPPDATA" => @"C:\Users\op\AppData\Local",
            "ProgramFiles" => @"C:\Program Files",
            "ProgramFiles(x86)" => @"C:\Program Files (x86)",
            _ => null
        });
        Expect(UpdateService.DetectChannel(@"C:\Users\op\AppData\Local\Programs\OMT Capture Studio\OmtCaptureStudio.exe", env) == InstallChannel.Installer,
            "A per-user installer placement is the Installer channel.");
        // DEVIATION FROM THE BRIEF, asserted deliberately: the SAME .iss installs machine-wide into
        // Program Files when the operator picks the admin option (PrivilegesRequiredOverridesAllowed=dialog,
        // DefaultDirName={autopf}). Treating that as portable would deny those users updates forever.
        Expect(UpdateService.DetectChannel(@"C:\Program Files\OMT Capture Studio\OmtCaptureStudio.exe", env) == InstallChannel.Installer,
            "A machine-wide installer placement must ALSO be the Installer channel, not portable.");
        Expect(UpdateService.DetectChannel(@"C:\Apps\OMT\OmtCaptureStudio.exe", env) == InstallChannel.Portable,
            "A copy outside both install roots is portable.");
        // the INSTANCE property must use the same seam - asserting only the static would leave a wiring
        // bug invisible (this is exactly how the machine-dependent channel slipped in once already)
        var probe = new UpdateService(new FakeUpdateTransport(),
            executablePath: () => @"C:\Program Files\OMT Capture Studio\OmtCaptureStudio.exe",
            environment: env);
        Expect(probe.Channel == InstallChannel.Installer, "The instance Channel property must honour the injected environment.");
        Expect(UpdateService.DetectChannel(null, env) == InstallChannel.Portable, "An unknown executable path is portable.");

        // ---- 5. THE FEED: newer, equal, offline ------------------------------------------------------
        const string shaAsset = "OmtCaptureStudio-v1.0.0.23-Setup.exe.sha256";
        string feed = FakeReleaseJson("v1.0.0.23", "OmtCaptureStudio-v1.0.0.23-Setup.exe", 73017804, shaAsset);

        var transport = new FakeUpdateTransport();
        transport.Strings[UpdateService.ReleasesApiUrl] = feed;
        var svc = new UpdateService(transport, currentVersion: () => "1.0.0.22",
            executablePath: () => @"C:\Users\op\AppData\Local\Programs\OMT Capture Studio\OmtCaptureStudio.exe",
            launchProcess: _ => throw new InvalidOperationException("A check must never launch anything."),
            tempDirectory: () => Path.GetTempPath());

        UpdateCheckResult newer = svc.CheckForUpdateAsync().GetAwaiter().GetResult();
        Expect(newer.Outcome == UpdateCheckOutcome.UpdateAvailable, $"A newer tag must be an available update; got {newer.Outcome}.");
        Expect(newer.Release is { Version: "1.0.0.23" }, $"The release version must be normalised without the v; got {newer.Release?.Version}.");
        Expect(newer.Release!.SetupAssetName == "OmtCaptureStudio-v1.0.0.23-Setup.exe", "The Setup asset must be matched by name.");
        Expect(newer.Release.SetupSha256Url is not null, "The matching .sha256 sidecar must be located alongside the Setup asset.");

        // once per launch: a second check is refused WITHOUT touching the network
        int callsAfterFirst = transport.StringCalls;
        UpdateCheckResult second = svc.CheckForUpdateAsync().GetAwaiter().GetResult();
        Expect(second.Outcome == UpdateCheckOutcome.AlreadyCheckedThisLaunch, $"A second check in one launch must be refused; got {second.Outcome}.");
        Expect(transport.StringCalls == callsAfterFirst, $"A refused second check must make NO network call; saw {transport.StringCalls} total.");

        var equalTransport = new FakeUpdateTransport();
        equalTransport.Strings[UpdateService.ReleasesApiUrl] = FakeReleaseJson("v1.0.0.22", "OmtCaptureStudio-v1.0.0.22-Setup.exe", 1, "x.sha256");
        var equalSvc = new UpdateService(equalTransport, currentVersion: () => "1.0.0.22");
        Expect(equalSvc.CheckForUpdateAsync().GetAwaiter().GetResult().Outcome == UpdateCheckOutcome.UpToDate,
            "An equal tag must report up to date, not an update.");

        var offlineTransport = new FakeUpdateTransport { Offline = true };
        var offlineSvc = new UpdateService(offlineTransport, currentVersion: () => "1.0.0.22");
        UpdateCheckResult offline = offlineSvc.CheckForUpdateAsync().GetAwaiter().GetResult();
        Expect(offline.Outcome == UpdateCheckOutcome.Unreachable, $"An unreachable API must be an ordinary skip; got {offline.Outcome}.");

        var junkTransport = new FakeUpdateTransport();
        junkTransport.Strings[UpdateService.ReleasesApiUrl] = "{\"not\":\"a release\"}";
        var junkSvc = new UpdateService(junkTransport, currentVersion: () => "1.0.0.22");
        Expect(junkSvc.CheckForUpdateAsync().GetAwaiter().GetResult().Outcome == UpdateCheckOutcome.Unreachable,
            "A Github shape change must degrade to no-update, never to a crash.");

        // ---- 6. DOWNLOAD + SHA-256 VERIFICATION ------------------------------------------------------
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("pretend-installer-payload");
        string goodHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();

        var dl = new FakeUpdateTransport();
        dl.Strings[UpdateService.ReleasesApiUrl] = feed;
        dl.Strings["https://dl.example/" + shaAsset] = goodHash + "  OmtCaptureStudio-v1.0.0.23-Setup.exe";
        dl.Bytes["https://dl.example/OmtCaptureStudio-v1.0.0.23-Setup.exe"] = payload;
        string dlDir = Path.Combine(Path.GetTempPath(), "omt-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dlDir);
        var dlSvc = new UpdateService(dl, currentVersion: () => "1.0.0.22", tempDirectory: () => dlDir);
        UpdateRelease rel = dlSvc.CheckForUpdateAsync().GetAwaiter().GetResult().Release!;
        string? verified = dlSvc.DownloadAndVerifyAsync(rel).GetAwaiter().GetResult();
        Expect(verified is not null && File.Exists(verified), $"A matching hash must yield a verified installer path; got {verified}.");
        Expect(File.ReadAllBytes(verified!).SequenceEqual(payload), "The verified file must be byte-identical to the downloaded payload.");

        // the sidecar must be a real sha256sum line, not a bare word or a short hex
        Expect(UpdateService.ParseSha256Sidecar(goodHash + "  name.exe") == goodHash, "A sha256sum sidecar must parse.");
        Expect(UpdateService.ParseSha256Sidecar("deadbeef") is null, "A short hex string must be refused as a sidecar.");
        Expect(UpdateService.ParseSha256Sidecar("zz".PadRight(64, 'z')) is null, "A non-hex 64-char string must be refused as a sidecar.");

        // MISMATCH: refused, and nothing is left on disk for a later accident to run
        var bad = new FakeUpdateTransport();
        bad.Strings[UpdateService.ReleasesApiUrl] = feed;
        bad.Strings["https://dl.example/" + shaAsset] = new string('a', 64);
        bad.Bytes["https://dl.example/OmtCaptureStudio-v1.0.0.23-Setup.exe"] = payload;
        // Its OWN directory: the success case above legitimately wrote the same file name into dlDir, so
        // asserting "nothing is on disk" there would test the previous step, not this one.
        string badDir = Path.Combine(Path.GetTempPath(), "omt-dl-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(badDir);
        var badSvc = new UpdateService(bad, currentVersion: () => "1.0.0.22", tempDirectory: () => badDir);
        string? refused = badSvc.DownloadAndVerifyAsync(badSvc.CheckForUpdateAsync().GetAwaiter().GetResult().Release!).GetAwaiter().GetResult();
        Expect(refused is null, "A hash mismatch must refuse the download.");
        Expect(!File.Exists(Path.Combine(badDir, "OmtCaptureStudio-v1.0.0.23-Setup.exe")),
            "A failed verification must leave NO installer on disk - verification happens before the write.");
        Expect(!Directory.EnumerateFiles(badDir).Any(), "A refused verification must leave its temp directory empty.");

        // a release with no sidecar is refused outright rather than run unverified
        var noSha = new FakeUpdateTransport();
        noSha.Strings[UpdateService.ReleasesApiUrl] = FakeReleaseJson("v1.0.0.23", "OmtCaptureStudio-v1.0.0.23-Setup.exe", 1, "unused.sha256");
        var noShaSvc = new UpdateService(noSha, currentVersion: () => "1.0.0.22", tempDirectory: () => dlDir);
        Expect(noShaSvc.DownloadAndVerifyAsync(noShaSvc.CheckForUpdateAsync().GetAwaiter().GetResult().Release!).GetAwaiter().GetResult() is null,
            "A release that publishes no .sha256 must be refused, never installed unverified.");

        // ---- 7. INSTALL: THE ARGUMENT SHAPE IS ASSERTED, NOTHING IS EVER LAUNCHED ---------------------
        var psi = UpdateService.BuildInstallStartInfo(@"C:\Temp\OmtCaptureStudio-v1.0.0.23-Setup.exe");
        Expect(psi.Arguments == "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            $"The silent-install switch set must be exact; got '{psi.Arguments}'.");
        // Brief amendment 2: CreateProcess, NOT the shell. UseShellExecute=true honours Mark-of-the-Web,
        // so it would turn our own deliberate MotW mark into a SmartScreen prompt on every update.
        Expect(!psi.UseShellExecute,
            "The installer must be launched via CreateProcess - UseShellExecute=true would honour the MotW mark we apply.");
        Expect(psi.WorkingDirectory == Path.GetDirectoryName(@"C:\Temp\OmtCaptureStudio-v1.0.0.23-Setup.exe"),
            $"The working directory must be the staged file's folder; got '{psi.WorkingDirectory}'.");
        Expect(psi.Arguments == "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            "The silent switch set must survive the launch-mode change unchanged.");

        string fakeInstaller = Path.Combine(dlDir, "OmtCaptureStudio-v1.0.0.23-Setup.exe");
        File.WriteAllBytes(fakeInstaller, payload);

        // installer channel + auto-install ON -> download, verify, re-verify ON DISK, then launch
        var onTransport = new FakeUpdateTransport();
        onTransport.Strings[UpdateService.ReleasesApiUrl] = feed;
        onTransport.Strings["https://dl.example/" + shaAsset] = goodHash + "  OmtCaptureStudio-v1.0.0.23-Setup.exe";
        onTransport.Bytes["https://dl.example/OmtCaptureStudio-v1.0.0.23-Setup.exe"] = payload;

        string? launchedPath = null;
        var onSvc = new UpdateService(onTransport, currentVersion: () => "1.0.0.22",
            executablePath: () => @"C:\Users\op\AppData\Local\Programs\OMT Capture Studio\OmtCaptureStudio.exe",
            launchProcess: p => { launchedPath = p; return true; },
            tempDirectory: () => dlDir,
            stagingDirectory: () => dlDir,
            environment: env);

        UpdateRelease onRel = onSvc.CheckForUpdateAsync().GetAwaiter().GetResult().Release!;
        string? staged = onSvc.DownloadAndVerifyAsync(onRel).GetAwaiter().GetResult();
        Expect(staged is not null, "The install path requires a download that verified first.");

        // R27: the staged name carries a nonce, so it cannot be pre-created or predicted
        Expect(Path.GetFileName(staged!).EndsWith("-OmtCaptureStudio-v1.0.0.23-Setup.exe", StringComparison.Ordinal),
            $"The staged file must keep the asset name behind a nonce; got '{Path.GetFileName(staged)}'.");
        Expect(Path.GetFileName(staged!) != "OmtCaptureStudio-v1.0.0.23-Setup.exe",
            "The staged name must be non-predictable, not the bare asset name.");

        // R27/R19: an installer this session never verified must not be launched at all
        Expect(!onSvc.TryStartInstall(fakeInstaller, out _),
            "R27: an installer this session did not verify must be refused, even though it exists.");

        Expect(onSvc.TryStartInstall(staged, out string onDetail), $"Auto-install ON must start the verified installer; got '{onDetail}'.");
        Expect(launchedPath == staged, $"The launcher must receive the VERIFIED staged path; got {launchedPath}.");

        // R27 TOCTOU: swap the staged file AFTER verification -> the launch must be refused
        File.WriteAllBytes(staged!, System.Text.Encoding.UTF8.GetBytes("swapped-after-verification"));
        Expect(!onSvc.TryStartInstall(staged, out string swapDetail),
            "R27: a staged installer swapped after verification must be refused (the verify->launch TOCTOU).");
        Expect(swapDetail.Contains("changed on disk"), $"The refusal must say why; got '{swapDetail}'.");

        // ---- R28/amendment 3: the ERROR_ELEVATION_REQUIRED (740) path --------------------------------
        // If PrivilegesRequired ever moves from 'lowest' to 'admin', the compiled manifest becomes
        // requireAdministrator, CreateProcess fails with 740, and auto-update would die for machine-wide
        // installs while every per-user test still passed. These two cases pin that it does not go silent.
        var psiElevated = UpdateService.BuildElevatedInstallStartInfo(@"C:\Temp\OmtCaptureStudio-v1.0.0.23-Setup.exe");
        Expect(psiElevated.UseShellExecute && psiElevated.Verb == "runas",
            $"The elevated relaunch must use the runas verb through the shell; got verb='{psiElevated.Verb}'.");
        Expect(psiElevated.Arguments == "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            "The elevated relaunch must carry the same silent switch set.");

        int normalLaunches740 = 0;
        int elevatedLaunches = 0;
        var elevationTransport = new FakeUpdateTransport();
        elevationTransport.Strings[UpdateService.ReleasesApiUrl] = feed;
        elevationTransport.Strings["https://dl.example/" + shaAsset] = goodHash + "  OmtCaptureStudio-v1.0.0.23-Setup.exe";
        elevationTransport.Bytes["https://dl.example/OmtCaptureStudio-v1.0.0.23-Setup.exe"] = payload;

        var elevDir = Path.Combine(Path.GetTempPath(), "omt-elev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elevDir);
        var elevSvc = new UpdateService(elevationTransport, currentVersion: () => "1.0.0.22",
            executablePath: () => @"C:\Users\op\AppData\Local\Programs\OMT Capture Studio\OmtCaptureStudio.exe",
            launchProcess: _ =>
            {
                normalLaunches740++;
                // exactly what CreateProcess does when the manifest requires elevation
                throw new System.ComponentModel.Win32Exception(740);
            },
            launchElevated: _ => { elevatedLaunches++; return true; },
            tempDirectory: () => elevDir,
            stagingDirectory: () => elevDir,
            environment: env);

        UpdateRelease elevRel = elevSvc.CheckForUpdateAsync().GetAwaiter().GetResult().Release!;
        string? elevStaged = elevSvc.DownloadAndVerifyAsync(elevRel).GetAwaiter().GetResult();
        Expect(elevStaged is not null, "The 740 case needs a verified staged installer.");

        Expect(elevSvc.TryStartInstall(elevStaged, out string elevDetail),
            $"A 740 must trigger the runas relaunch and report success; got '{elevDetail}'.");
        Expect(elevatedLaunches == 1, $"The runas relaunch must be attempted exactly ONCE; got {elevatedLaunches}.");
        Expect(normalLaunches740 == 1, $"The normal launch must be attempted once and not retried; got {normalLaunches740}.");
        Expect(elevDetail.Contains("elevation"), $"The detail must say elevation was involved; got '{elevDetail}'.");

        // and when the runas relaunch ALSO fails: report loudly, never loop
        int normalLaunchesFail = 0;
        int elevatedFailures = 0;
        var failSvc = new UpdateService(elevationTransport, currentVersion: () => "1.0.0.22",
            executablePath: () => @"C:\Users\op\AppData\Local\Programs\OMT Capture Studio\OmtCaptureStudio.exe",
            launchProcess: _ => { normalLaunchesFail++; throw new System.ComponentModel.Win32Exception(740); },
            launchElevated: _ => { elevatedFailures++; return false; },
            tempDirectory: () => elevDir,
            stagingDirectory: () => elevDir,
            environment: env);

        UpdateRelease failRel = failSvc.CheckForUpdateAsync().GetAwaiter().GetResult().Release!;
        string? failStaged = failSvc.DownloadAndVerifyAsync(failRel).GetAwaiter().GetResult();
        Expect(!failSvc.TryStartInstall(failStaged, out string failDetail),
            "A failed runas relaunch must be reported as a failure, not swallowed.");
        Expect(normalLaunchesFail == 1 && elevatedFailures == 1,
            $"Neither launcher may be retried; got normal={normalLaunchesFail}, elevated={elevatedFailures}.");
        Expect(failDetail.Contains("administrator") && failDetail.Contains("manually"),
            $"The operator must be told to install it manually; got '{failDetail}'.");
        Directory.Delete(elevDir, recursive: true);

        // R27 traversal: a release-supplied name with separators or '..' must never reach a path
        // R27: the REAL asset name, spelled as build-release.ps1 and the .iss actually write it
        // (lowercase 'mt'). This is the shape that must never fail closed - a case-SENSITIVE prefix check
        // against a misspelled literal would reject every update forever, silently reporting "up to date".
        Expect(UpdateService.IsSafeAssetName("OmtCaptureStudio-v1.0.0.23-Setup.exe"),
            "The REAL release asset name must be accepted.");
        // CANARY: the misspelling the brief carried. The contract is suffix+version based, not a fixed
        // prefix, so this is ALSO accepted - deliberately. Pinning that here documents the decision: a
        // rename upstream degrades to 'still works', never to 'fails closed with no error surface'.
        Expect(UpdateService.IsSafeAssetName("OMTCaptureStudio-v1.0.0.23-Setup.exe"),
            "A differently-cased name must not be rejected - matching is OrdinalIgnoreCase by contract.");
        Expect(!UpdateService.IsSafeAssetName(@"..\..\evil\OmtCaptureStudio-v1.0.0.23-Setup.exe"), "A traversing name is refused.");
        Expect(!UpdateService.IsSafeAssetName("sub/dir/OmtCaptureStudio-v1.0.0.23-Setup.exe"), "A slashed name is refused.");
        Expect(!UpdateService.IsSafeAssetName(".."), "A bare '..' is refused.");
        Expect(!UpdateService.IsSafeAssetName(null), "A null name is refused.");

        // the same, through the whole feed: the real name must yield an installer asset
        var realFeed = new FakeUpdateTransport();
        realFeed.Strings[UpdateService.ReleasesApiUrl] = FakeReleaseJson(
            "v1.0.0.23", "OmtCaptureStudio-v1.0.0.23-Setup.exe", 73017804,
            "OmtCaptureStudio-v1.0.0.23-Setup.exe.sha256");
        var realSvc = new UpdateService(realFeed, currentVersion: () => "1.0.0.22");
        UpdateRelease realRel = realSvc.CheckForUpdateAsync().GetAwaiter().GetResult().Release!;
        Expect(realRel.SetupAssetName == "OmtCaptureStudio-v1.0.0.23-Setup.exe",
            $"The real release shape must resolve to an installer; got '{realRel.SetupAssetName}'.");
        Expect(realRel.SetupSha256Url is not null, "and its sidecar must be located.");

        var trav = new FakeUpdateTransport();
        trav.Strings[UpdateService.ReleasesApiUrl] = FakeReleaseJson(
            "v1.0.0.23", @"..\..\evil\OmtCaptureStudio-v1.0.0.23-Setup.exe", 1, "x.sha256");
        var travSvc = new UpdateService(trav, currentVersion: () => "1.0.0.22");
        UpdateRelease? travRel = travSvc.CheckForUpdateAsync().GetAwaiter().GetResult().Release;
        Expect(travRel is null || travRel.SetupAssetName is null,
            $"A release naming a traversing asset must yield no installer at all; got '{travRel?.SetupAssetName}'.");

        // PORTABLE: notify-only. The launcher must not even be called.
        bool portableLaunched = false;
        var portableSvc = new UpdateService(transport, currentVersion: () => "1.0.0.22",
            executablePath: () => @"C:\Apps\OMT\OmtCaptureStudio.exe",
            launchProcess: _ => { portableLaunched = true; return true; },
            tempDirectory: () => dlDir,
            environment: env);
        Expect(!portableSvc.TryStartInstall(staged, out string portableDetail),
            "A portable copy must never self-replace.");
        Expect(!portableLaunched, "The portable path must not launch the installer at all.");
        Expect(portableDetail.Contains("portable", StringComparison.OrdinalIgnoreCase),
            $"The portable refusal must say so plainly; got '{portableDetail}'.");

        // no verified installer -> refused
        Expect(!onSvc.TryStartInstall(null, out _), "An install with no verified installer must be refused.");

        Directory.Delete(dlDir, recursive: true);
        Directory.Delete(badDir, recursive: true);

        Console.WriteLine(" -> version policy pure and four-part, feed parsed without a JSON dependency, once-per-launch cache honoured,");
        Console.WriteLine("    SHA-256 verified BEFORE any write, install refused on portable and without a sidecar, arguments exact!");
    }
}

