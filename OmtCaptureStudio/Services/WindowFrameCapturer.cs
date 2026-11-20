using System;
using System.Runtime.InteropServices;
using OmtCaptureStudio.Interop;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Captures a specific top-level window into a reusable pinned BGRA buffer (tight
/// stride = width * 4) suitable for OMTSend.
///
/// Primary path is GDI <c>PrintWindow</c> (PW_RENDERFULLCONTENT), which renders the
/// window's own content off-screen and therefore also captures occluded windows.
/// Some GPU/DirectComposition surfaces render black through PrintWindow; when that is
/// detected we fall back to a screen-region BitBlt of the window's on-screen rect, which
/// captures the composited pixels (works only while the window is visible/on top).
/// Fully DRM-protected output remains un-capturable without Windows Graphics Capture.
///
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
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.Rect rect))
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
    /// Captures the current window contents into the pinned buffer. Returns false only when
    /// every path fails (invalid DC/bitmap, failed blit, or failed readback).
    /// </summary>
    public unsafe bool TryCaptureFrame()
    {
        if (_disposed) return false;

        bool printOk = CaptureViaPrintWindow();
        if (printOk && !IsEffectivelyBlack())
        {
            return true;
        }

        // PrintWindow failed or produced an all-black frame (typical of GPU /
        // DirectComposition windows). Fall back to grabbing the window's on-screen region,
        // which yields the actually-composited pixels while the window is visible.
        if (CaptureViaScreenRegion())
        {
            return true;
        }

        return printOk; // a (black) PrintWindow frame is all we could get
    }

    private unsafe bool CaptureViaPrintWindow()
    {
        if (!GdiSurface.TryCreate(Width, Height, out GdiSurface surface)) return false;
        using (surface)
        {
            bool ok = NativeMethods.PrintWindow(_hwnd, surface.MemoryHandle, NativeMethods.PwRenderFullContent);
            if (!ok)
            {
                // Some windows refuse PW_RENDERFULLCONTENT; retry with no flags.
                ok = NativeMethods.PrintWindow(_hwnd, surface.MemoryHandle, 0);
            }
            if (!ok) return false;

            return ReadDibIntoBuffer(surface.MemoryHandle, surface.BitmapHandle);
        }
    }

    private unsafe bool CaptureViaScreenRegion()
    {
        if (!NativeMethods.IsWindowVisible(_hwnd)) return false;
        if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.Rect rect)) return false;
        if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return false;

        if (!GdiSurface.TryCreate(Width, Height, out GdiSurface surface)) return false;
        using (surface)
        {
            // Copy the window's screen region (GDI clips anything off-screen). CAPTUREBLT
            // includes layered windows; the destination bitmap is buffer-sized so the
            // readback stride always matches the pinned buffer.
            if (!NativeMethods.BitBlt(
                    surface.MemoryHandle, 0, 0, Width, Height,
                    surface.ScreenHandle, rect.Left, rect.Top,
                    NativeMethods.SrcCopy | NativeMethods.CaptureBlt))
            {
                return false;
            }

            return ReadDibIntoBuffer(surface.MemoryHandle, surface.BitmapHandle);
        }
    }

    /// <summary>Reads an in-memory 32bpp DIB into the pinned buffer and forces opaque alpha.</summary>
    private unsafe bool ReadDibIntoBuffer(IntPtr hdcMem, IntPtr hbm)
    {
        var bmi = new NativeMethods.BitmapInfo();
        bmi.Header.Size = (uint)sizeof(NativeMethods.BitmapInfoHeader);
        bmi.Header.Width = Width;
        bmi.Header.Height = -Height; // negative => top-down
        bmi.Header.Planes = 1;
        bmi.Header.BitCount = 32;
        bmi.Header.Compression = NativeMethods.BiRgb;

        if (NativeMethods.GetDIBits(hdcMem, hbm, 0, (uint)Height, BufferPtr, ref bmi, NativeMethods.DibRgbColors) == 0)
            return false;

        // GDI 32bpp DIBs are BGRX; make alpha opaque for OMT BGRA.
        byte* p = (byte*)BufferPtr;
        int total = Width * Height * 4;
        for (int i = 3; i < total; i += 4) p[i] = 0xFF;

        return true;
    }

    /// <summary>
    /// Sampled check for an all-black frame (channels below <see cref="BlackThreshold"/>).
    /// Used to detect PrintWindow's black-frame failure without scanning every pixel.
    /// </summary>
    private unsafe bool IsEffectivelyBlack()
    {
        byte* p = (byte*)BufferPtr;
        int pixelCount = Width * Height;

        // Sample roughly 4096 spread pixels across the frame.
        int step = Math.Max(1, pixelCount / 4096);
        for (int px = 0; px < pixelCount; px += step)
        {
            int i = px * 4;
            if (p[i] > BlackThreshold || p[i + 1] > BlackThreshold || p[i + 2] > BlackThreshold)
            {
                return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_bufferHandle.IsAllocated) _bufferHandle.Free();
    }

    private const byte BlackThreshold = 8;
}
