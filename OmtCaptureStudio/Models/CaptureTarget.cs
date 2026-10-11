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

    /// <summary>For <see cref="CaptureTargetKind.Monitor"/>: the DXGI output index.</summary>
    public int MonitorIndex { get; init; }

    /// <summary>For <see cref="CaptureTargetKind.Window"/>: the window handle (HWND).</summary>
    public IntPtr WindowHandle { get; init; }
}
