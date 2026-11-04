using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.Services;

/// <summary>
/// NET-023: how an update check concluded. Every outcome is a first-class result rather than an
/// exception, because "we could not reach GitHub" is an ordinary event on a broadcast machine that may
/// be on an isolated network — and it must never surface as an error the operator has to dismiss.
/// </summary>
public enum UpdateCheckOutcome
{
    /// <summary>The running build is the newest release (or newer). Nothing is shown.</summary>
    UpToDate,

    /// <summary>A newer release exists. The operator is offered it; installation is still opt-in.</summary>
    UpdateAvailable,

    /// <summary>The network call failed or GitHub could not be reached. Silent skip, logged once.</summary>
    Unreachable,

    /// <summary>This launch has already performed its one permitted check.</summary>
    AlreadyCheckedThisLaunch
}

/// <summary>NET-023: the install channel, which decides whether we may replace ourselves at all.</summary>
public enum InstallChannel
{
    /// <summary>Placed by the Inno Setup installer — it controls the files and can replace them.</summary>
    Installer,

    /// <summary>A portable/ZIP or development copy. We are not allowed to overwrite a running app.</summary>
    Portable
}

/// <summary>NET-023: the parsed shape of a GitHub "latest release" response.</summary>
public sealed record UpdateRelease(
    string Version,
    string TagName,
    string ReleaseUrl,
    string? SetupAssetName,
    string? SetupDownloadUrl,
    string? SetupSha256Url,
    long SetupSize);

/// <summary>The verdict of a check, plus the release when one is available.</summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateRelease? Release, string Detail)
{
    public static UpdateCheckResult UpToDate(string detail) => new(UpdateCheckOutcome.UpToDate, null, detail);
}

/// <summary>
/// NET-023: transport seam so the whole update flow is drivable headlessly with a fake feed.
/// Production uses <see cref="HttpUpdateTransport"/>; the suite substitutes a dictionary.
/// </summary>
public interface IUpdateTransport
{
    Task<string?> GetStringAsync(string url, CancellationToken cancellationToken);
    Task<byte[]?> GetBytesAsync(string url, CancellationToken cancellationToken);
}

/// <summary>NET-023: the real <see cref="HttpClient"/> transport. GitHub requires a User-Agent.</summary>
public sealed class HttpUpdateTransport : IUpdateTransport, IDisposable
{
    private readonly HttpClient _client;

    public HttpUpdateTransport(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        handler ??= new HttpClientHandler();
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        };
        // GitHub's REST API rejects requests without a User-Agent (403), so it is not optional here.
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateService.UserAgent);
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Offline, DNS failure, TLS failure, 60/hr rate limit - all the same answer to the caller.
            return null;
        }
    }

    public async Task<byte[]?> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// NET-023: the auto-update service. Ask-first by design: it detects and offers, and only the operator
/// (or an explicit opt-in toggle) authorises an install.
///
/// <para>Every dependency is injected - transport, clock, version source, executable path, process
/// launcher and temp directory - so the entire policy is asserted headlessly with a fake release feed
/// and no network, no download and, critically, NO PROCESS LAUNCH. A test must never start an installer.</para>
///
/// <para><b>Version source (a deliberate deviation from the brief, see the report).</b> The brief
/// specifies the entry assembly's <c>ProductVersion</c>. That value is <c>1.0.0+&lt;sha&gt;</c> THREE
/// numeric segments, because no <c>&lt;Version&gt;</c> property exists in the build; every release tag is
/// FOUR (<c>v1.0.0.22</c>). Comparing them would report "update available" on every launch forever. The
/// default source therefore reads <c>version.txt</c>, which <c>build-release.ps1</c> stamps and the csproj
/// copies next to the executable, so it carries the same four-part number as the tag. The assembly value
/// remains the fallback when that file is absent (a dev run from an unusual layout).</para>
/// </summary>
public sealed class UpdateService
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/gnamse/OmtCaptureStudio/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/gnamse/OmtCaptureStudio/releases";
    public const string UserAgent = "OmtCaptureStudio-UpdateCheck";

    /// <summary>The silent-install switch set. Inno closes us, replaces the files, relaunches.</summary>
    public const string SilentInstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS";

    /// <summary>The version file stamped by <c>build-release.ps1</c> and copied beside the executable.</summary>
    public const string VersionFileName = "version.txt";

    private readonly IUpdateTransport _transport;
    private readonly Func<string> _currentVersion;
    private readonly Func<string> _executablePath;
    private readonly Func<string, bool> _launchProcess;
    private readonly Func<string> _tempDirectory;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string, string?> _environment;

    private bool _checkedThisLaunch;

    public UpdateService(
        IUpdateTransport transport,
        Func<string>? currentVersion = null,
        Func<string>? executablePath = null,
        Func<string, bool>? launchProcess = null,
        Func<string>? tempDirectory = null,
        Func<DateTime>? utcNow = null,
        Func<string, string?>? environment = null)
    {
        _transport = transport;
        _currentVersion = currentVersion ?? (() => ResolveInstalledVersion());
        _executablePath = executablePath ?? (() => Environment.ProcessPath ?? string.Empty);
        _launchProcess = launchProcess ?? LaunchProcessDefault;
        _tempDirectory = tempDirectory ?? Path.GetTempPath;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        // Injected so the channel decision is a pure function of the path AND a known set of roots. Read
        // live, it silently depended on the developer's own %LOCALAPPDATA%, which made the policy
        // untestable and the behaviour machine-dependent.
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>True once this instance has spent its one permitted network check.</summary>
    public bool CheckedThisLaunch => _checkedThisLaunch;

    /// <summary>The version this build is running, normalised to four numeric parts.</summary>
    public string CurrentVersion => _currentVersion();

    // ---------------------------------------------------------------- version source

    /// <summary>
    /// Reads the stamped four-part version, falling back to the assembly value. Public and static so the
    /// suite can prove the file is preferred and the fallback is used when it is missing.
    /// </summary>
    public static string ResolveInstalledVersion(string? baseDirectory = null, Func<string>? assemblyVersion = null)
    {
        string dir = baseDirectory ?? AppContext.BaseDirectory;
        try
        {
            string path = Path.Combine(dir, VersionFileName);
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path).Trim();
                if (TryParseVersion(text, out Version? parsed) && parsed is not null)
                    return parsed.ToString();
            }
        }
        catch (Exception)
        {
            // An unreadable version file is not worth an error; the assembly value is the fallback.
        }

        return assemblyVersion?.Invoke() ?? AssemblyVersionValue();
    }

    private static string AssemblyVersionValue()
    {
        try
        {
            var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return v is null ? "0.0.0.0" : v.ToString();
        }
        catch (Exception)
        {
            return "0.0.0.0";
        }
    }

    // ---------------------------------------------------------------- pure version policy

    /// <summary>
    /// Parses a version from either a release tag (<c>v1.0.0.22</c>) or an informational string
    /// (<c>1.0.0.22+sha</c>): strips a leading <c>v</c>, drops any <c>+build</c> suffix, and rejects
    /// anything that is not purely numeric. Pure, so the edge cases are asserted with no I/O.
    /// </summary>
    public static bool TryParseVersion(string? text, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();

        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];

        s = s.Trim();
        if (s.Length == 0) return false;

        // Only digits and dots. Version.TryParse would accept neither "1.0.0-beta" nor "1..0", but being
        // explicit keeps a malformed tag from being silently coerced into something comparable.
        foreach (char c in s)
            if (!char.IsDigit(c) && c != '.') return false;

        return Version.TryParse(s, out version);
    }

    /// <summary>
    /// The update predicate. Strictly newer wins; equal or older means up to date. <see cref="Version"/>
    /// orders a missing component BELOW a present one, so <c>1.0.0 &lt; 1.0.0.22</c> - which is exactly the
    /// comparison that would have nagged forever had the three-part assembly value been used.
    /// </summary>
    public static bool IsNewerVersion(Version candidate, Version current) => candidate > current;

    // ---------------------------------------------------------------- check

    /// <summary>
    /// The once-per-launch check. Returns a result for every path; never throws and never retries.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_checkedThisLaunch)
            return new UpdateCheckResult(UpdateCheckOutcome.AlreadyCheckedThisLaunch, null,
                "An update check has already run this launch.");

        _checkedThisLaunch = true;
        long started = Stopwatch.GetTimestamp();

        string? json = await _transport.GetStringAsync(ReleasesApiUrl, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            AppLogger.LogInfo("Update check: no response from the releases API (offline, blocked or rate-limited). Skipping.");
            return new UpdateCheckResult(UpdateCheckOutcome.Unreachable, null, "Could not reach the releases API.");
        }

        UpdateRelease? release = TryParseRelease(json);
        if (release is null)
        {
            AppLogger.LogInfo("Update check: the releases response did not carry a usable tag_name. Skipping.");
            return new UpdateCheckResult(UpdateCheckOutcome.Unreachable, null, "The releases response was not understood.");
        }

        if (!TryParseVersion(_currentVersion(), out Version? current) || current is null)
        {
            AppLogger.LogInfo($"Update check: the running version could not be read. Skipping.");
            return new UpdateCheckResult(UpdateCheckOutcome.Unreachable, null, "The running version could not be read.");
        }

        if (!TryParseVersion(release.Version, out Version? latest) || latest is null)
        {
            AppLogger.LogInfo($"Update check: the release tag '{release.TagName}' is not a comparable version. Skipping.");
            return new UpdateCheckResult(UpdateCheckOutcome.Unreachable, null, "The release tag is not a comparable version.");
        }

        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        AppLogger.LogInfo($"Update check: running {current}, latest {latest} (checked in {ms:F0} ms).");

        return IsNewerVersion(latest, current)
            ? new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, release, $"v{latest} is available.")
            : UpdateCheckResult.UpToDate($"Running the latest release (v{current}).");
    }

    /// <summary>
    /// Parses the GitHub "latest release" payload. Deliberately minimal and dependency-free: the app
    /// ships Native AOT, where a reflection-based JSON binder is trimmed away. Returns null when the
    /// shape is not what we expect, so a GitHub change degrades to "no update" rather than to a crash.
    /// </summary>
    public static UpdateRelease? TryParseRelease(string json)
    {
        string? tagName = ReadJsonString(json, "tag_name");
        if (string.IsNullOrWhiteSpace(tagName)) return null;

        if (!TryParseVersion(tagName, out Version? parsed) || parsed is null) return null;

        string version = parsed.ToString();
        string? releaseUrl = ReadJsonString(json, "html_url");

        // The installer asset is named by the release script as OmtCaptureStudio-v<version>-Setup.exe,
        // with a matching .sha256 sidecar. Match on the version we parsed rather than assuming the run's
        // own version, so a release whose assets lag its tag is reported as having no installer.
        string setupSuffix = $"-Setup.exe";
        string? setupName = null, setupUrl = null, shaUrl = null;
        long setupSize = 0;

        foreach (var (name, url, size) in EnumerateAssets(json))
        {
            if (!name.EndsWith(setupSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains($"v{version}", StringComparison.OrdinalIgnoreCase)) continue;
            setupName = name;
            setupUrl = url;
            setupSize = size;
            break;
        }

        if (setupUrl is not null)
        {
            string shaName = setupName + ".sha256";
            foreach (var (name, url, _) in EnumerateAssets(json))
            {
                if (string.Equals(name, shaName, StringComparison.OrdinalIgnoreCase)) { shaUrl = url; break; }
            }
        }

        return new UpdateRelease(
            version,
            tagName!,
            releaseUrl ?? ReleasesPageUrl,
            setupName,
            setupUrl,
            shaUrl,
            setupSize);
    }

    private static IEnumerable<(string Name, string Url, long Size)> EnumerateAssets(string json)
    {
        // A tiny scanner over the "assets" array: enough for {name,browser_download_url,size} triples
        // without pulling a JSON dependency into an AOT binary.
        int assets = json.IndexOf("\"assets\"", StringComparison.Ordinal);
        if (assets < 0) yield break;

        int i = assets;
        while (true)
        {
            int nameAt = json.IndexOf("\"name\"", i, StringComparison.Ordinal);
            if (nameAt < 0) break;

            string? name = ReadJsonString(json, "name", nameAt);
            string? url = ReadJsonString(json, "browser_download_url", nameAt);
            // Advance strictly past the name we just consumed. Anything cleverer can stall when an
            // asset omits browser_download_url, and a build scanner that can spin forever is worse
            // than one that reads a field twice.
            int next = nameAt + 6;
            i = next;

            if (name is null || url is null) continue;

            long size = 0;
            int sizeAt = json.IndexOf("\"size\"", next, StringComparison.Ordinal);
            if (sizeAt >= 0)
            {
                int colon = json.IndexOf(':', sizeAt);
                if (colon > 0)
                {
                    int end = colon + 1;
                    while (end < json.Length && (char.IsDigit(json[end]) || json[end] == ' ')) end++;
                    long.TryParse(json[(colon + 1)..end].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                }
            }

            yield return (name, url, size);
        }
    }

    private static string? ReadJsonString(string json, string key, int from = 0)
    {
        int keyAt = json.IndexOf($"\"{key}\"", from, StringComparison.Ordinal);
        if (keyAt < 0) return null;

        int colon = json.IndexOf(':', keyAt + key.Length + 2);
        if (colon < 0) return null;

        int open = json.IndexOf('"', colon + 1);
        if (open < 0) return null;

        var sb = new System.Text.StringBuilder();
        for (int i = open + 1; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                char esc = json[++i];
                sb.Append(esc switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    _ => esc
                });
                continue;
            }
            if (c == '"') return sb.ToString();
            sb.Append(c);
        }

        return null;
    }

    // ---------------------------------------------------------------- channel

    /// <summary>
    /// Which install this is. A portable copy must never self-replace - we would be overwriting the
    /// executable while it runs, and a ZIP has no uninstaller to clean up after us.
    ///
    /// <para><b>Deviation from the brief, see the report.</b> The brief treats only
    /// <c>%LOCALAPPDATA%\Programs\</c> as an installer. The .iss sets
    /// <c>DefaultDirName={autopf}\{#MyAppName}</c> with <c>PrivilegesRequiredOverridesAllowed=dialog</c>,
    /// so the SAME installer also installs system-wide into <c>Program Files</c> when the operator picks
    /// the admin option. Those users would be misread as portable and never offered an install, so both
    /// machine-wide roots are recognised.</para>
    /// </summary>
    public static InstallChannel DetectChannel(string? executablePath, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(executablePath)) return InstallChannel.Portable;

        // Tests pass a path that does not exist on disk; the comparison is purely textual, so a
        // non-existent path still classifies correctly and no probe of the filesystem is needed.
        string full;
        try { full = Path.GetFullPath(executablePath); }
        catch (Exception) { return InstallChannel.Portable; }

        string perUser = environment("LOCALAPPDATA") is { Length: > 0 } la
            ? Path.Combine(la, "Programs")
            : string.Empty;

        var roots = new List<string>();
        if (perUser.Length > 0) roots.Add(perUser);

        // {autopf} resolves to Program Files for a machine-wide install.
        if (environment("ProgramFiles") is { Length: > 0 } pf) roots.Add(pf);
        if (environment("ProgramFiles(x86)") is { Length: > 0 } pf86) roots.Add(pf86);

        foreach (string root in roots)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return InstallChannel.Installer;
        }

        return InstallChannel.Portable;
    }

    public InstallChannel Channel => DetectChannel(_executablePath(), _environment);

    // ---------------------------------------------------------------- download + verify

    /// <summary>
    /// Downloads the installer to the temp directory and verifies it against the release's published
    /// .sha256 BEFORE anything may execute it. Returns the verified path, or null with a loud log line.
    /// A hash mismatch deletes the download: an unverified installer is not left on disk for a later
    /// accident to run.
    /// </summary>
    public async Task<string?> DownloadAndVerifyAsync(UpdateRelease release, CancellationToken cancellationToken = default)
    {
        if (release.SetupDownloadUrl is null || release.SetupAssetName is null)
        {
            AppLogger.LogError("Update download refused: the release carries no Setup asset to download.");
            return null;
        }

        if (release.SetupSha256Url is null)
        {
            AppLogger.LogError($"Update download refused: the release publishes no .sha256 for '{release.SetupAssetName}'.");
            return null;
        }

        byte[]? payload = await _transport.GetBytesAsync(release.SetupDownloadUrl, cancellationToken).ConfigureAwait(false);
        if (payload is null || payload.Length == 0)
        {
            AppLogger.LogError($"Update download failed: no bytes returned for '{release.SetupAssetName}'.");
            return null;
        }

        string? sidecar = await _transport.GetStringAsync(release.SetupSha256Url, cancellationToken).ConfigureAwait(false);
        string? expected = ParseSha256Sidecar(sidecar);
        if (expected is null)
        {
            AppLogger.LogError($"Update download refused: the .sha256 sidecar for '{release.SetupAssetName}' could not be parsed.");
            return null;
        }

        string actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.LogError(
                $"Update verification FAILED for '{release.SetupAssetName}': expected {expected}, got {actual}. " +
                "The installer has been discarded and will not be run.");
            return null;
        }

        string path = Path.Combine(_tempDirectory(), release.SetupAssetName);
        try
        {
            await File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Update download could not be written to '{path}'.", ex);
            return null;
        }

        AppLogger.LogInfo($"Update verified: '{release.SetupAssetName}' ({payload.Length} bytes) SHA-256 {actual}.");
        return path;
    }

    /// <summary>
    /// Parses a <c>sha256sum</c> sidecar. The published files are <c>&lt;64 hex&gt;  &lt;filename&gt;</c>
    /// with no trailing newline, and that is the only shape we accept for a security-critical value.
    /// </summary>
    public static string? ParseSha256Sidecar(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string first = text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;

        if (first.Length != 64) return null;
        foreach (char c in first)
            if (!Uri.IsHexDigit(c)) return null;

        return first.ToLowerInvariant();
    }

    // ---------------------------------------------------------------- install

    /// <summary>
    /// Runs a verified installer. Refuses on the portable channel, refuses without a verified path, and
    /// otherwise starts the silent install and reports that the app should now shut down so Inno can
    /// replace it. The launcher is injected, so the suite asserts the argument shape without executing it.
    /// </summary>
    public bool TryStartInstall(string? verifiedInstallerPath, out string detail)
    {
        if (Channel == InstallChannel.Portable)
        {
            detail = "This is a portable copy - open the releases page to download a new build.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(verifiedInstallerPath) || !File.Exists(verifiedInstallerPath))
        {
            detail = "No verified installer is available.";
            return false;
        }

        try
        {
            if (_launchProcess(verifiedInstallerPath))
            {
                detail = "Installer started; the app will now close so it can be replaced.";
                return true;
            }

            detail = "The installer could not be started.";
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Update install failed to start.", ex);
            detail = $"The installer could not be started: {ex.Message}";
            return false;
        }
    }

    /// <summary>Builds the launch description. Split out so its ARGUMENT SHAPE is assertable.</summary>
    public static ProcessStartInfo BuildInstallStartInfo(string installerPath) => new(installerPath)
    {
        Arguments = SilentInstallArguments,
        UseShellExecute = true
    };

    private static bool LaunchProcessDefault(string path)
    {
        using var p = Process.Start(BuildInstallStartInfo(path));
        return p is not null;
    }

    /// <summary>Opens the releases page in the browser - the portable channel's only action.</summary>
    public static ProcessStartInfo BuildReleasesPageStartInfo() => new(ReleasesPageUrl) { UseShellExecute = true };

}
