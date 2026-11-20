using System.Collections.Generic;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Enumerates the host's DXGI outputs on every adapter as selectable capture targets.
/// A monitor is identified by adapter LUID plus output index (and the output's device name).
/// <see cref="CaptureTarget.MonitorIndex"/> remains the output index on the default adapter
/// so a saved bare index still means what it meant when only that adapter was enumerated.
/// </summary>
public static class MonitorEnumerator
{
    public static IReadOnlyList<CaptureTarget> Enumerate()
        => MonitorIdentity.ToTargets(DxgiOutputCatalog.Enumerate());
}
