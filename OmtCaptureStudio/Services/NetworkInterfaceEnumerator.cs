using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using OmtCaptureStudio.Models;

using OmtCaptureStudio.Services.Forensics;
namespace OmtCaptureStudio.Services;

/// <summary>
/// Enumerates active IPv4 network interfaces on the host machine so the operator can
/// choose which network OMT broadcasts on. Native AOT-safe (uses <see cref="NetworkInterface"/>).
/// </summary>
public static class NetworkInterfaceEnumerator
{
    public static IReadOnlyList<BroadcastNetworkOption> Enumerate()
    {
        var list = new List<BroadcastNetworkOption>
        {
            new()
            {
                DisplayName = "All Networks (Auto)",
                IpAddress = null,
                InterfaceIndex = 0
            }
        };

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties ipProps;
                try
                {
                    ipProps = nic.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                int ifIndex = 0;
                try
                {
                    ifIndex = ipProps.GetIPv4Properties()?.Index ?? 0;
                }
                catch
                {
                    // Some virtual adapters do not expose IPv4 properties
                }

                foreach (UnicastIPAddressInformation unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    string ipStr = unicast.Address.ToString();
                    list.Add(new BroadcastNetworkOption
                    {
                        DisplayName = $"{nic.Name} · {ipStr}",
                        IpAddress = ipStr,
                        InterfaceIndex = ifIndex
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Failed to enumerate network interfaces: {ex.Message}", ex);
        }

        list.Add(new BroadcastNetworkOption
        {
            DisplayName = "Localhost Only · 127.0.0.1",
            IpAddress = "127.0.0.1",
            InterfaceIndex = 0
        });

        return list;
    }

    /// <summary>
    /// Returns all active non-loopback IPv4 addresses on the host EXCEPT <paramref name="allowedIp"/>.
    /// Used by <see cref="OmtDesktopCaptureGenerator"/> to block wildcard socket connections on
    /// non-selected network interfaces.
    /// </summary>
    public static List<IPAddress> GetOtherLocalIpv4Addresses(string? allowedIp)
    {
        var others = new List<IPAddress>();
        if (string.IsNullOrWhiteSpace(allowedIp))
        {
            return others; // All Networks allowed -> block none
        }

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties ipProps;
                try { ipProps = nic.GetIPProperties(); }
                catch { continue; }

                foreach (UnicastIPAddressInformation unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    if (!string.Equals(unicast.Address.ToString(), allowedIp, StringComparison.Ordinal))
                    {
                        others.Add(unicast.Address);
                    }
                }
            }
        }
        catch
        {
            // Ignore transient adapter enumeration errors
        }

        return others;
    }
}
