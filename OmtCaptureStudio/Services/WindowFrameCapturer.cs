using System.Runtime.InteropServices;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Captures a specific top-level window via GDI PrintWindow into a reusable pinned
/// BGRA buffer (tight stride = width * 4) suitable for OMTSend.
/// Single-threaded: <see cref="TryCaptureFrame"/> is called from one sender thread.
/// </summary>
public sealed class WindowFrameCapturer : IFrameSource
{
    private readonly IntPtr _hwnd;
    private readonly byte[] _buffer;
    private GCHandle _bufferHandle;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public IntPtr BufferPtr => _bufferHandle.IsAllocated ? _bufferHandle.AddrOfPinnedObject() : IntPtr.Zero;

    public WindowFrameCapturer(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rect))
        {
            throw new InvalidOperationException("Window capture: GetWindowRect failed.");
        }

        Width = rect.Right - rect.Left;
        Height = rect.Bottom - rect.Top;
        if (Width <= 0 || Height <= 0)
        {
            throw new InvalidOperationException("Window capture: invalid or empty window rect.");
        }

        _hwnd = hwnd;
        _buffer = new byte[Width * Height * 4];
        _bufferHandle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
    }

    /// <summary>
    /// Captures the current window contents into the pinned buffer via PrintWindow.
    /// Returns false on any failure (invalid DC/bitmap, failed blit, or failed readback).
    /// </summary>
    public unsafe bool TryCaptureFrame()
    {
        if (_disposed) return false;

        IntPtr hdcScreen = IntPtr.Zero, hdcMem = IntPtr.Zero, hbm = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero) return false;

            hdcMem = CreateCompatibleDC(hdcScreen);
            hbm = CreateCompatibleBitmap(hdcScreen, Width, Height);
            if (hdcMem == IntPtr.Zero || hbm == IntPtr.Zero) return false;

            old = SelectObject(hdcMem, hbm);

            bool ok = PrintWindow(_hwnd, hdcMem, PwRenderFullContent);
            if (!ok)
            {
                // Some windows refuse PW_RENDERFULLCONTENT; retry with no flags.
                ok = PrintWindow(_hwnd, hdcMem, 0);
            }
            if (!ok) return false;

            var bmi = new BITMAPINFO();
            bmi.Header.Size = (uint)sizeof(BitmapInfoHeader);
            bmi.Header.Width = Width;
            bmi.Header.Height = -Height; // negative => top-down
            bmi.Header.Planes = 1;
            bmi.Header.BitCount = 32;
            bmi.Header.Compression = BiRgb;

            if (GetDIBits(hdcMem, hbm, 0, (uint)Height, BufferPtr, ref bmi, DibRgbColors) == 0)
                return false;

            // GDI 32bpp DIBs are BGRX; make alpha opaque for OMT BGRA.
            byte* p = (byte*)BufferPtr;
            for (int i = 3; i < Width * Height * 4; i += 4) p[i] = 0xFF;

            return true;
        }
        finally
        {
            if (old != IntPtr.Zero && hdcMem != IntPtr.Zero) SelectObject(hdcMem, old);
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_bufferHandle.IsAllocated) _bufferHandle.Free();
    }

    private const uint PwRenderFullContent = 0x00000002;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, IntPtr bits, ref BITMAPINFO bmi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }
}
