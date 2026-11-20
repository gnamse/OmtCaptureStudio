using SharpGen.Runtime;
using Vortice;
using Vortice.DXGI;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Enumerates every DXGI adapter's outputs. The default (primary) adapter is the first
/// non-software adapter <c>EnumAdapters1</c> returns, which is the adapter the old
/// single-adapter capturer was bound to. Software adapters are skipped.
/// </summary>
public static class DxgiOutputCatalog
{
    public static IReadOnlyList<MonitorDescriptor> Enumerate()
    {
        var outputs = new List<MonitorDescriptor>();
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            bool defaultAssigned = false;
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                Result adapterResult = factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter);
                if (adapterResult.Failure || adapter is null) break;

                using (adapter)
                {
                    AdapterDescription1 description = adapter.Description1;
                    if ((description.Flags & AdapterFlags.Software) != 0) continue;

                    bool isDefault = !defaultAssigned;
                    defaultAssigned = true;
                    long luid = PackLuid(description.Luid);
                    string adapterName = Clean(description.Description);

                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        Result outputResult = adapter.EnumOutputs(outputIndex, out IDXGIOutput? output);
                        if (outputResult.Failure || output is null) break;
                        using (output)
                        {
                            OutputDescription outputDescription = output.Description;
                            var bounds = outputDescription.DesktopCoordinates;
                            int width = bounds.Right - bounds.Left;
                            int height = bounds.Bottom - bounds.Top;
                            if (width <= 0 || height <= 0) continue;

                            outputs.Add(new MonitorDescriptor
                            {
                                AdapterLuid = luid,
                                AdapterName = adapterName,
                                IsDefaultAdapter = isDefault,
                                OutputIndex = (int)outputIndex,
                                DeviceName = Clean(outputDescription.DeviceName),
                                Left = bounds.Left,
                                Top = bounds.Top,
                                Width = width,
                                Height = height
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Monitor enumeration failed: {ex.Message}");
        }

        return outputs;
    }

    internal static long PackLuid(Luid luid)
        => ((long)luid.HighPart << 32) | luid.LowPart;

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.TrimEnd('\0').Trim();
    }
}
