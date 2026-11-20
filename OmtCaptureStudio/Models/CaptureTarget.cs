namespace OmtCaptureStudio.Models;

public enum CaptureTargetKind
{
    Monitor,
    Window
}

/// <summary>
/// A user-selectable host-capture target: a specific monitor or a specific window.
/// Immutable; used by the UI to enumerate choices and by the frame-source factory.
/// </summary>
public sealed record CaptureTarget
{
    public CaptureTargetKind Kind { get; init; }

    /// <summary>Friendly name: monitor "Screen 1 · 1920×1080"; window its title.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// Legacy output index on the <b>default</b> adapter. -1 when this monitor is on another GPU,
    /// so a bare index can never be confused with "output 0 of whichever adapter came next".
    /// New code identifies a monitor by <see cref="AdapterLuid"/> + <see cref="OutputIndex"/>
    /// or by <see cref="OutputDeviceName"/>.
    /// </summary>
    public int MonitorIndex { get; init; }

    /// <summary>Packed DXGI adapter LUID of the GPU that owns this output. 0 means "not identified".</summary>
    public long AdapterLuid { get; init; }

    /// <summary>Output index on <see cref="AdapterLuid"/>, not a flat index across adapters.</summary>
    public int OutputIndex { get; init; }

    /// <summary>DXGI output device name, for example <c>\\.\DISPLAY1</c>. Empty for a legacy index-only value.</summary>
    public string OutputDeviceName { get; init; } = "";

    /// <summary>True when this output is on the adapter the old single-GPU enumeration used.</summary>
    public bool IsDefaultAdapter { get; init; }

    /// <summary>For <see cref="CaptureTargetKind.Window"/>: the window handle (HWND).</summary>
    public IntPtr WindowHandle { get; init; }
}
