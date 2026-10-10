using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Captures the primary monitor via DXGI Desktop Duplication and copies each frame
/// into a reusable pinned BGRA buffer (tight stride = width * 4) suitable for OMTSend.
/// Single-threaded: <see cref="TryCaptureFrame"/> is called from one sender thread.
/// </summary>
public sealed class DesktopFrameCapturer : IDisposable
{
    private readonly ID3D11Device _device;
    private ID3D11Texture2D? _staging;
    private IDXGIOutputDuplication? _duplication;
    private byte[] _buffer = Array.Empty<byte>();
    private GCHandle _bufferHandle;
    private bool _disposed;
    private bool _hasFrame;

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public IntPtr BufferPtr => _bufferHandle.IsAllocated ? _bufferHandle.AddrOfPinnedObject() : IntPtr.Zero;

    public DesktopFrameCapturer()
    {
        // Hardware device with BGRA support is required for Desktop Duplication.
        _device = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 });

        using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        var enumResult = adapter.EnumOutputs(0, out IDXGIOutput output); // primary monitor (v1)
        if (enumResult.Failure)
        {
            throw new InvalidOperationException("Desktop capture: failed to enumerate the primary monitor output.");
        }

        using (output)
        {
            var bounds = output.Description.DesktopCoordinates;
            Width = bounds.Right - bounds.Left;
            Height = bounds.Bottom - bounds.Top;
            if (Width <= 0 || Height <= 0)
            {
                throw new InvalidOperationException("Desktop capture: invalid monitor bounds.");
            }

            using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(_device);
        }

        // Staging texture for CPU readback in the exact desktop format.
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });

        _buffer = new byte[Width * Height * 4];
        _bufferHandle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
    }

    /// <summary>
    /// Copies the latest desktop frame into the pinned buffer. Returns false when no
    /// newer frame is available within the timeout (or the session was lost). Before the
    /// first Desktop Duplication frame arrives, a static desktop is seeded with a GDI
    /// grab so the broadcaster can start streaming immediately.
    /// </summary>
    public unsafe bool TryCaptureFrame()
    {
        if (_disposed || _duplication == null || _staging == null) return false;

        var result = _duplication.AcquireNextFrame(50, out _, out IDXGIResource resource);
        if (!result.Failure && resource != null)
        {
            try
            {
                using ID3D11Texture2D desktopImage = resource.QueryInterface<ID3D11Texture2D>();
                _device.ImmediateContext.CopyResource(_staging, desktopImage);

                var mapped = _device.ImmediateContext.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    byte* src = (byte*)mapped.DataPointer;
                    byte* dst = (byte*)BufferPtr;
                    int rowBytes = Width * 4;
                    for (int y = 0; y < Height; y++)
                    {
                        Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
                        src += mapped.RowPitch;
                        dst += rowBytes;
                    }
                }
                finally
                {
                    _device.ImmediateContext.Unmap(_staging, 0);
                }
            }
            finally
            {
                _duplication.ReleaseFrame();
            }
            _hasFrame = true;
            return true;
        }

        // No new frame: WAIT_TIMEOUT (desktop unchanged) or ACCESS_LOST (session dead).
        if (_hasFrame) return false; // buffer still holds the latest frame

        // First frame and the desktop is static — Desktop Duplication never fires until
        // something changes, so seed the buffer with a GDI grab of the current screen.
        _hasFrame = CaptureViaGdi();
        return _hasFrame;
    }

    // GDI fallback used only to seed the first frame on a static desktop (Desktop
    // Duplication is change-driven and yields nothing until the screen changes).
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, uint rop);

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

    private const uint SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

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

    private unsafe bool CaptureViaGdi()
    {
        IntPtr hdcScreen = IntPtr.Zero, hdcMem = IntPtr.Zero, hbm = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero) return false;

            hdcMem = CreateCompatibleDC(hdcScreen);
            hbm = CreateCompatibleBitmap(hdcScreen, Width, Height);
            if (hdcMem == IntPtr.Zero || hbm == IntPtr.Zero) return false;

            old = SelectObject(hdcMem, hbm);
            if (!BitBlt(hdcMem, 0, 0, Width, Height, hdcScreen, 0, 0, SrcCopy)) return false;

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
        _staging?.Dispose();
        _duplication?.Dispose();
        _device.Dispose();
    }
}
