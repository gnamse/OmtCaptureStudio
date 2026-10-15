using System;
using System.Collections.Generic;
using Microsoft.Win32;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Discovers ASIO drivers registered on this machine.
///
/// Unlike WASAPI (see <see cref="AudioDeviceEnumerator"/>), ASIO drivers are plain COM
/// objects: a driver publishes itself under <c>HKLM\SOFTWARE\ASIO</c> and registers an
/// in-process server under <c>HKLM\SOFTWARE\Classes\CLSID\{clsid}\InprocServer32</c>. This
/// class joins those two registry halves into <see cref="AsioDriverInfo"/> records.
///
/// Two layouts exist in the wild and both are handled: some drivers key the subkey after the
/// CLSID itself, while most (Realtek, VB-Matrix, ASIO4ALL, ...) use a friendly subkey name and
/// store the GUID in a <c>CLSID</c> string value beside an optional <c>Description</c> and
/// <c>Manufacturer</c>.
///
/// Everything is read through <see cref="Microsoft.Win32.Registry"/>, which is fully Native
/// AOT compatible — no reflection, no COM activation, so nothing here loads or locks a driver
/// (ASIO grants exclusive access, so merely probing one would be hostile to a running DAW).
/// Every registry touch is guarded: a broken, inaccessible or half-written driver entry is
/// skipped rather than thrown, so <see cref="Enumerate"/> never fails — it just returns fewer
/// drivers.
/// </summary>
public static class AsioDriverEnumerator
{
    /// <summary>Registry path holding one subkey per registered ASIO driver.</summary>
    private const string AsioDriversPath = @"SOFTWARE\ASIO";

    /// <summary>Value holding the driver's COM class id, used when the subkey is a friendly name.</summary>
    private const string ClsidValueName = "CLSID";

    /// <summary>Driver-supplied human-readable name. Preferred label when present.</summary>
    private const string DescriptionValueName = "Description";

    /// <summary>Vendor name. Used as the label when the driver omits a description.</summary>
    private const string ManufacturerValueName = "Manufacturer";

    /// <summary>Subkey under a CLSID holding the in-process COM server path.</summary>
    private const string InprocServer32Path = "InprocServer32";

    /// <summary>
    /// Returns every discoverable ASIO driver, sorted by display name (ordinal,
    /// case-insensitive) and deduplicated by CLSID.
    /// </summary>
    /// <remarks>
    /// Never throws. Returns an empty list on a machine with no ASIO drivers installed, or if
    /// the registry cannot be read.
    /// </remarks>
    public static IReadOnlyList<AsioDriverInfo> Enumerate()
    {
        var drivers = new Dictionary<Guid, AsioDriverInfo>();

        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<AsioDriverInfo>();
        }

        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? asioRoot = hklm.OpenSubKey(AsioDriversPath);
            if (asioRoot is null)
            {
                // No ASIO drivers have ever been installed here.
                return Array.Empty<AsioDriverInfo>();
            }

            foreach (string subKeyName in asioRoot.GetSubKeyNames())
            {
                TryAddDriver(drivers, asioRoot, subKeyName);
            }
        }
        catch
        {
            // Registry unavailable or access denied (locked-down/hardened hosts): report what we have.
        }

        var results = new List<AsioDriverInfo>(drivers.Values);
        results.Sort(static (left, right) =>
            string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>
    /// Reads one <c>HKLM\SOFTWARE\ASIO</c> subkey and, if it holds a loadable driver, adds it to
    /// <paramref name="drivers"/>. Malformed or unresolvable entries are silently skipped.
    /// </summary>
    /// <param name="drivers">Accumulator keyed by CLSID, so later duplicates are ignored.</param>
    /// <param name="asioRoot">The open <c>HKLM\SOFTWARE\ASIO</c> key.</param>
    /// <param name="subKeyName">Driver subkey to read: either the CLSID itself or a friendly name.</param>
    private static void TryAddDriver(
        Dictionary<Guid, AsioDriverInfo> drivers,
        RegistryKey asioRoot,
        string subKeyName)
    {
        try
        {
            using RegistryKey? driverKey = asioRoot.OpenSubKey(subKeyName);
            if (driverKey is null)
            {
                return;
            }

            // Some drivers key the GUID into the subkey name; the common case stores it in a value.
            Guid clsid;
            if (Guid.TryParse(subKeyName, out Guid keyedClsid))
            {
                clsid = keyedClsid;
            }
            else if (driverKey.GetValue(ClsidValueName) is string clsidText && Guid.TryParse(clsidText, out Guid storedClsid))
            {
                clsid = storedClsid;
            }
            else
            {
                return;
            }

            if (drivers.ContainsKey(clsid))
            {
                return;
            }

            string? description = driverKey.GetValue(DescriptionValueName) as string;
            string? manufacturer = driverKey.GetValue(ManufacturerValueName) as string;

            // Prefer the driver's own label; fall back to vendor, then the friendly key name, then the raw CLSID.
            string displayName = FirstNonBlank(description, manufacturer, subKeyName) ?? clsid.ToString("B");

            // Without a server binary there is nothing we could load later, so drop the entry.
            string? dllPath = TryResolveServerPath(clsid);
            if (string.IsNullOrWhiteSpace(dllPath))
            {
                return;
            }

            drivers[clsid] = new AsioDriverInfo(displayName, clsid, dllPath.Trim());
        }
        catch
        {
            // Unreadable value types / permissions on a single entry must not sink the whole scan.
        }
    }

    /// <summary>
    /// Resolves the driver binary for <paramref name="clsid"/> from its COM registration.
    /// The 64-bit classes root is tried first, then the 32-bit (WOW6432Node) view, because plenty
    /// of shipped ASIO drivers are still 32-bit.
    /// </summary>
    /// <returns>The server path, or <c>null</c> when neither view registers one.</returns>
    private static string? TryResolveServerPath(Guid clsid)
    {
        // Registry CLSID keys carry their braces: CLSID\{xxxxxxxx-....}\InprocServer32
        string registrationPath = $@"CLSID\{{{clsid:D}}}\{InprocServer32Path}";

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                using RegistryKey? serverKey = classesRoot.OpenSubKey(registrationPath);

                // The unnamed (default) value is the DLL path.
                string? path = serverKey?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
            catch
            {
                // Try the other view, then give up.
            }
        }

        return null;
    }

    /// <summary>Returns the first candidate that is neither null nor whitespace, trimmed; else <c>null</c>.</summary>
    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}
