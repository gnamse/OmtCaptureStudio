using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// One DXGI output, already copied off the COM object so identity math can be tested with no GPU.
/// </summary>
public readonly record struct MonitorDescriptor
{
    public long AdapterLuid { get; init; }
    public string AdapterName { get; init; }
    public bool IsDefaultAdapter { get; init; }
    public int OutputIndex { get; init; }
    public string DeviceName { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public MonitorDescriptor()
    {
        AdapterLuid = 0;
        AdapterName = "";
        IsDefaultAdapter = false;
        OutputIndex = 0;
        DeviceName = "";
        Left = 0;
        Top = 0;
        Width = 0;
        Height = 0;
    }
}

/// <summary>
/// Which output a capturer should open. A legacy value is only an output index on the default
/// adapter — the index <see cref="DesktopFrameCapturer"/> used to take — and is resolved against
/// a live enumeration before a device is created.
/// </summary>
public readonly record struct MonitorRef
{
    public long AdapterLuid { get; init; }
    public int OutputIndex { get; init; }
    public string DeviceName { get; init; }
    public bool IsLegacyIndex { get; init; }

    public MonitorRef()
    {
        AdapterLuid = 0;
        OutputIndex = 0;
        DeviceName = "";
        IsLegacyIndex = false;
    }

    public static MonitorRef Legacy(int outputIndexOnDefaultAdapter) => new()
    {
        OutputIndex = outputIndexOnDefaultAdapter,
        IsLegacyIndex = true
    };

    public static MonitorRef FromTarget(CaptureTarget target) => new()
    {
        AdapterLuid = target.AdapterLuid,
        OutputIndex = target.OutputIndex,
        DeviceName = target.OutputDeviceName ?? "",
        IsLegacyIndex = target.AdapterLuid == 0 && string.IsNullOrEmpty(target.OutputDeviceName)
    };

    public static MonitorRef FromDescriptor(MonitorDescriptor descriptor) => new()
    {
        AdapterLuid = descriptor.AdapterLuid,
        OutputIndex = descriptor.OutputIndex,
        DeviceName = descriptor.DeviceName ?? ""
    };
}

/// <summary>
/// Monitor identity that does not touch DXGI. A saved bare index is an output index on the
/// default adapter only: index 2 must not silently become the first output of a second GPU.
/// </summary>
public static class MonitorIdentity
{
    public static IReadOnlyList<CaptureTarget> ToTargets(IReadOnlyList<MonitorDescriptor> outputs)
    {
        bool multipleAdapters = false;
        if (outputs.Count > 1)
        {
            long first = outputs[0].AdapterLuid;
            for (int i = 1; i < outputs.Count; i++)
            {
                if (outputs[i].AdapterLuid != first)
                {
                    multipleAdapters = true;
                    break;
                }
            }
        }

        var targets = new List<CaptureTarget>(outputs.Count);
        for (int i = 0; i < outputs.Count; i++)
        {
            MonitorDescriptor output = outputs[i];
            string adapter = output.AdapterName.Trim();
            string display = multipleAdapters && adapter.Length > 0
                ? $"Screen {i + 1} · {adapter} · {output.Width}×{output.Height}"
                : $"Screen {i + 1} · {output.Width}×{output.Height}";
            targets.Add(new CaptureTarget
            {
                Kind = CaptureTargetKind.Monitor,
                DisplayName = display,
                MonitorIndex = output.IsDefaultAdapter ? output.OutputIndex : -1,
                AdapterLuid = output.AdapterLuid,
                OutputIndex = output.OutputIndex,
                OutputDeviceName = output.DeviceName ?? "",
                IsDefaultAdapter = output.IsDefaultAdapter
            });
        }

        return targets;
    }

    /// <summary>
    /// Maps a pre-identity saved index onto the output that index used to mean: output
    /// <paramref name="legacyOutputIndex"/> of the default adapter, not the Nth output overall.
    /// </summary>
    public static CaptureTarget? ResolveLegacyIndex(int legacyOutputIndex, IReadOnlyList<CaptureTarget> targets)
    {
        if (legacyOutputIndex < 0) return null;
        for (int i = 0; i < targets.Count; i++)
        {
            CaptureTarget target = targets[i];
            if (target.Kind == CaptureTargetKind.Monitor
                && target.IsDefaultAdapter
                && target.OutputIndex == legacyOutputIndex)
            {
                return target;
            }
        }

        return null;
    }

    public static MonitorDescriptor? Find(IReadOnlyList<MonitorDescriptor> outputs, MonitorRef monitor)
    {
        if (monitor.IsLegacyIndex || (monitor.AdapterLuid == 0 && string.IsNullOrEmpty(monitor.DeviceName)))
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                MonitorDescriptor output = outputs[i];
                if (output.IsDefaultAdapter && output.OutputIndex == monitor.OutputIndex)
                {
                    return output;
                }
            }

            return null;
        }

        if (!string.IsNullOrEmpty(monitor.DeviceName))
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                MonitorDescriptor output = outputs[i];
                if (string.Equals(output.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return output;
                }
            }
        }

        for (int i = 0; i < outputs.Count; i++)
        {
            MonitorDescriptor output = outputs[i];
            if (output.AdapterLuid == monitor.AdapterLuid && output.OutputIndex == monitor.OutputIndex)
            {
                return output;
            }
        }

        return null;
    }

    public static bool SameTarget(CaptureTarget a, CaptureTarget b)
    {
        if (a.Kind != b.Kind) return false;
        if (a.Kind == CaptureTargetKind.Window)
        {
            return a.WindowHandle == b.WindowHandle;
        }

        if (a.OutputDeviceName.Length > 0 && b.OutputDeviceName.Length > 0)
        {
            return string.Equals(a.OutputDeviceName, b.OutputDeviceName, StringComparison.OrdinalIgnoreCase);
        }

        bool aIdentified = a.AdapterLuid != 0;
        bool bIdentified = b.AdapterLuid != 0;
        if (aIdentified && bIdentified)
        {
            return a.AdapterLuid == b.AdapterLuid && a.OutputIndex == b.OutputIndex;
        }

        if (aIdentified)
        {
            return a.IsDefaultAdapter && a.OutputIndex == b.MonitorIndex;
        }

        if (bIdentified)
        {
            return b.IsDefaultAdapter && b.OutputIndex == a.MonitorIndex;
        }

        return a.MonitorIndex == b.MonitorIndex;
    }

    /// <summary>
    /// Restores a saved monitor. A device name or adapter LUID wins. A file that only has the old
    /// bare index is resolved with <see cref="ResolveLegacyIndex"/>.
    /// </summary>
    public static CaptureTarget? MatchSaved(
        IReadOnlyList<CaptureTarget> targets,
        long? adapterLuid,
        int? outputIndex,
        string? deviceName,
        int? legacyIndex)
    {
        bool hasIdentity = !string.IsNullOrEmpty(deviceName) || adapterLuid is not null;
        if (hasIdentity)
        {
            var probe = new CaptureTarget
            {
                Kind = CaptureTargetKind.Monitor,
                AdapterLuid = adapterLuid ?? 0,
                OutputIndex = outputIndex ?? 0,
                OutputDeviceName = deviceName ?? ""
            };
            for (int i = 0; i < targets.Count; i++)
            {
                CaptureTarget target = targets[i];
                if (target.Kind == CaptureTargetKind.Monitor && SameTarget(target, probe))
                {
                    return target;
                }
            }
        }

        if (legacyIndex is int index)
        {
            return ResolveLegacyIndex(index, targets);
        }

        return null;
    }
}
