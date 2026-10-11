using System.Runtime.InteropServices;
using System.Text;
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
    private static List<CaptureTarget>? _results;
    private static uint _currentProcessId;

    public static IReadOnlyList<CaptureTarget> Enumerate()
    {
        var results = new List<CaptureTarget>();
        _results = results;
        _currentProcessId = GetCurrentProcessId();
        try
        {
            EnumWindows(Callback, IntPtr.Zero);
        }
        finally
        {
            _results = null;
        }
        return results;
    }

    private static bool Callback(IntPtr hWnd, IntPtr lParam)
    {
        var results = _results;
        if (results == null) return false;

        if (!IsWindowVisible(hWnd)) return true;
        if (IsIconic(hWnd)) return true;
        if (GetWindowTextLengthW(hWnd) == 0) return true;
        if ((GetWindowLong(hWnd, GwlExStyle) & WsExToolWindow) != 0) return true;

        if (!GetWindowRect(hWnd, out RECT rect)) return true;
        if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return true;

        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == _currentProcessId) return true;

        int length = GetWindowTextLengthW(hWnd);
        var sb = new StringBuilder(length + 1);
        GetWindowTextW(hWnd, sb, length + 1);

        results.Add(new CaptureTarget
        {
            Kind = CaptureTargetKind.Window,
            WindowHandle = hWnd,
            DisplayName = sb.ToString(),
        });

        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
