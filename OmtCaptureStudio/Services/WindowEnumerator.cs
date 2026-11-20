using System.Text;
using OmtCaptureStudio.Interop;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Enumerates captureable top-level application windows on the desktop, in z-order
/// (top-most first), as <see cref="CaptureTarget"/>s. Excludes minimized windows,
/// tool windows, invisible windows, windows with no title, empty-rect windows, and
/// this application's own windows.
/// </summary>
public static class WindowEnumerator
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    // EnumWindows callbacks cannot carry arbitrary state, so a single static slot is
    // used for the duration of the enumeration (Enumerate is not re-entrant by design).
    private static readonly object s_lock = new();
    private static List<CaptureTarget>? _results;
    private static uint _currentProcessId;

    public static IReadOnlyList<CaptureTarget> Enumerate()
    {
        lock (s_lock)
        {
            var results = new List<CaptureTarget>();
            _results = results;
            _currentProcessId = NativeMethods.GetCurrentProcessId();
            try
            {
                NativeMethods.EnumWindows(Callback, IntPtr.Zero);
            }
            finally
            {
                _results = null;
            }
            return results;
        }
    }

    private static bool Callback(IntPtr hWnd, IntPtr lParam)
    {
        var results = _results;
        if (results == null) return false;

        if (!NativeMethods.IsWindowVisible(hWnd)) return true;
        if (NativeMethods.IsIconic(hWnd)) return true;
        if (NativeMethods.GetWindowTextLengthW(hWnd) == 0) return true;
        if ((NativeMethods.GetWindowLong(hWnd, GwlExStyle) & WsExToolWindow) != 0) return true;

        if (!NativeMethods.GetWindowRect(hWnd, out NativeMethods.Rect rect)) return true;
        if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return true;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == _currentProcessId) return true;

        int length = NativeMethods.GetWindowTextLengthW(hWnd);
        var sb = new StringBuilder(length + 1);
        NativeMethods.GetWindowTextW(hWnd, sb, length + 1);

        results.Add(new CaptureTarget
        {
            Kind = CaptureTargetKind.Window,
            WindowHandle = hWnd,
            DisplayName = $"Window: {sb}",
        });

        return true;
    }
}
