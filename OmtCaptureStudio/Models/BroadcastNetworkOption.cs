using System;

namespace OmtCaptureStudio.Models;

/// <summary>
/// Represents a host network interface option that <see cref="Services.OmtDesktopCaptureGenerator"/>
/// can bind and advertise its OMT broadcast stream on.
/// </summary>
public sealed record BroadcastNetworkOption
{
    /// <summary>Friendly display string, e.g. "All Networks (Auto)" or "Ethernet · 192.168.1.50".</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Selected IPv4 address string, or null when <see cref="IsAllNetworks"/> is true.</summary>
    public string? IpAddress { get; init; }

    /// <summary>OS IPv4 interface index, or 0 for all/loopback.</summary>
    public int InterfaceIndex { get; init; }

    /// <summary>True when broadcasting on all available network interfaces.</summary>
    public bool IsAllNetworks => string.IsNullOrEmpty(IpAddress);

    /// <summary>True when restricted to 127.0.0.1 localhost only.</summary>
    public bool IsLoopbackOnly => string.Equals(IpAddress, "127.0.0.1", StringComparison.Ordinal);
}
