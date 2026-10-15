using System;

namespace OmtCaptureStudio.Models;

/// <summary>
/// A registered ASIO driver discovered under <c>HKLM\SOFTWARE\ASIO</c>.
///
/// ASIO is the pro-audio driver model: low latency and multichannel, unlike WASAPI.
/// It grants <b>exclusive</b> access — only one client may hold a driver open at a
/// time, so the owning <c>IHostAudioSource</c> must be disposed promptly when the
/// broadcast stops.
/// </summary>
/// <param name="DisplayName">Human-readable driver name.</param>
/// <param name="Clsid">COM class id used to instantiate the driver.</param>
/// <param name="DllPath">Resolved path to the driver's in-process COM server.</param>
public sealed record AsioDriverInfo(
    string DisplayName,
    Guid Clsid,
    string DllPath);

/// <summary>
/// A user-selected ASIO capture configuration: which driver, and which pair of input
/// channels to broadcast (channels are zero-based; default pair is 0/1).
/// </summary>
public sealed record AsioCaptureOption(
    AsioDriverInfo Driver,
    int FirstInputChannelIndex = 0);
