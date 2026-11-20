using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OmtCaptureStudio.Interop;

/// <summary>
/// Owns one screen DC, one memory DC and one compatible bitmap for a single GDI capture.
/// Disposal order matches the previous hand-written finally blocks: restore the previous
/// selection, delete the bitmap, delete the memory DC, release the screen DC.
/// </summary>
internal readonly struct GdiSurface : IDisposable
{
    private readonly SafeScreenDc? _screen;
    private readonly SafeMemoryDc? _memory;
    private readonly SafeGdiBitmap? _bitmap;
    private readonly IntPtr _previous;

    public IntPtr ScreenHandle => _screen?.DangerousGetHandle() ?? IntPtr.Zero;
    public IntPtr MemoryHandle => _memory?.DangerousGetHandle() ?? IntPtr.Zero;
    public IntPtr BitmapHandle => _bitmap?.DangerousGetHandle() ?? IntPtr.Zero;

    private GdiSurface(SafeScreenDc screen, SafeMemoryDc memory, SafeGdiBitmap bitmap, IntPtr previous)
    {
        _screen = screen;
        _memory = memory;
        _bitmap = bitmap;
        _previous = previous;
    }

    public static bool TryCreate(int width, int height, out GdiSurface surface)
    {
        surface = default;
        SafeScreenDc? screen = SafeScreenDc.Acquire(IntPtr.Zero);
        if (screen == null) return false;

        SafeMemoryDc? memory = SafeMemoryDc.Create(screen.DangerousGetHandle());
        SafeGdiBitmap? bitmap = memory == null
            ? null
            : SafeGdiBitmap.CreateCompatible(screen.DangerousGetHandle(), width, height);
        if (memory == null || bitmap == null)
        {
            bitmap?.Dispose();
            memory?.Dispose();
            screen.Dispose();
            return false;
        }

        IntPtr previous = NativeMethods.SelectObject(memory.DangerousGetHandle(), bitmap.DangerousGetHandle());
        surface = new GdiSurface(screen, memory, bitmap, previous);
        return true;
    }

    public void Dispose()
    {
        if (_memory != null && _previous != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_memory.DangerousGetHandle(), _previous);
        }

        _bitmap?.Dispose();
        _memory?.Dispose();
        _screen?.Dispose();
    }
}

/// <summary>An <c>HDC</c> from <c>GetDC</c>, released with <c>ReleaseDC</c> (not <c>DeleteDC</c>).</summary>
internal sealed class SafeScreenDc : SafeHandleZeroOrMinusOneIsInvalid
{
    private IntPtr _hwnd;

    private SafeScreenDc() : base(ownsHandle: true) { }

    public static SafeScreenDc? Acquire(IntPtr hwnd)
    {
        IntPtr hdc = NativeMethods.GetDC(hwnd);
        if (hdc == IntPtr.Zero) return null;
        var dc = new SafeScreenDc { _hwnd = hwnd };
        dc.SetHandle(hdc);
        return dc;
    }

    protected override bool ReleaseHandle() => NativeMethods.ReleaseDC(_hwnd, handle) != 0;
}

/// <summary>An <c>HDC</c> from <c>CreateCompatibleDC</c>, released with <c>DeleteDC</c>.</summary>
internal sealed class SafeMemoryDc : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeMemoryDc() : base(ownsHandle: true) { }

    public static SafeMemoryDc? Create(IntPtr hdc)
    {
        IntPtr memory = NativeMethods.CreateCompatibleDC(hdc);
        if (memory == IntPtr.Zero) return null;
        var dc = new SafeMemoryDc();
        dc.SetHandle(memory);
        return dc;
    }

    protected override bool ReleaseHandle() => NativeMethods.DeleteDC(handle);
}

/// <summary>An <c>HBITMAP</c> from <c>CreateCompatibleBitmap</c>, released with <c>DeleteObject</c>.</summary>
internal sealed class SafeGdiBitmap : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeGdiBitmap() : base(ownsHandle: true) { }

    public static SafeGdiBitmap? CreateCompatible(IntPtr hdc, int width, int height)
    {
        IntPtr bitmap = NativeMethods.CreateCompatibleBitmap(hdc, width, height);
        if (bitmap == IntPtr.Zero) return null;
        var owned = new SafeGdiBitmap();
        owned.SetHandle(bitmap);
        return owned;
    }

    protected override bool ReleaseHandle() => NativeMethods.DeleteObject(handle);
}
