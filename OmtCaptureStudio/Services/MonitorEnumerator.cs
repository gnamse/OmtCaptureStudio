using System.Collections.Generic;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using OmtCaptureStudio.Models;

using OmtCaptureStudio.Services.Forensics;
namespace OmtCaptureStudio.Services;

/// <summary>Enumerates the host's DXGI monitor outputs as selectable capture targets.</summary>
public static class MonitorEnumerator
{
    public static IReadOnlyList<CaptureTarget> Enumerate()
    {
        var targets = new List<CaptureTarget>();

        try
        {
            using var device = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 });
            using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();

            for (int i = 0; ; i++)
            {
                var result = adapter.EnumOutputs((uint)i, out IDXGIOutput output);
                if (result.Failure) break;
                using (output)
                {
                    var bounds = output.Description.DesktopCoordinates;
                    int width = bounds.Right - bounds.Left;
                    int height = bounds.Bottom - bounds.Top;
                    targets.Add(new CaptureTarget
                    {
                        Kind = CaptureTargetKind.Monitor,
                        DisplayName = $"Screen {i + 1} · {width}×{height}",
                        MonitorIndex = i
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Monitor enumeration failed: {ex.Message}");
        }

        return targets;
    }
}
