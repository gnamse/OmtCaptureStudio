using System;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using OmtCaptureStudio.Interop;
using OmtCaptureStudio.Services.Forensics;
namespace OmtCaptureStudio.Services;

/// <summary>
/// Captures the primary monitor via DXGI Desktop Duplication and copies each frame
/// into a reusable pinned BGRA buffer (tight stride = width * 4) suitable for OMTSend.
/// Single-threaded: <see cref="TryCaptureFrame"/> is called from one sender thread.
/// </summary>
public sealed class DesktopFrameCapturer : IFrameSource
{
    /// <summary>How long to wait before retrying a failed duplication recreate, so a locked session cannot spin DuplicateOutput at frame rate.</summary>
    private const int RecoveryBackoffMs = 500;

    /// <summary>Recovery is logged at most this often. The attempt itself still runs on the backoff above.</summary>
    private const int RecoveryLogIntervalMs = 5_000;

    private readonly int _monitorIndex;
    private int _left;
    private int _top;
    private ID3D11Device? _device;
    private ID3D11Texture2D? _staging;
    private IDXGIOutputDuplication? _duplication;
    private byte[] _buffer = Array.Empty<byte>();
    private GCHandle _bufferHandle;
    private bool _disposed;
    private bool _hasFrame;
    private bool _sessionHealthy = true;
    private long _nextRecoveryTicks;
    private long _lastRecoveryLogTicks;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * 4;
    public IntPtr BufferPtr => _bufferHandle.IsAllocated ? _bufferHandle.AddrOfPinnedObject() : IntPtr.Zero;

    /// <summary>
    /// DIAG-BROADCAST: true when Desktop Duplication is not in use, so every frame comes from the GDI
    /// path. Set both when DXGI init throws (HRESULT or otherwise) AND when the display has no DXGI
    /// output at all - in every one of those states this capturer is degraded, and the operator needs to
    /// know that rather than just "no frames".
    /// </summary>
    public bool IsUsingFallbackCapture => _duplication == null;

    /// <inheritdoc />
    public bool IsCaptureSessionHealthy => _sessionHealthy;

    public DesktopFrameCapturer(int monitorIndex = 0)
    {
        _monitorIndex = monitorIndex;

        try
        {
            _device = CreateDevice();
            BoundsRead bounds = TryReadBounds(out int left, out int top, out int width, out int height);
            if (bounds == BoundsRead.Missing)
            {
                throw new InvalidOperationException($"Desktop capture: monitor index {_monitorIndex} not found.");
            }

            if (bounds == BoundsRead.Invalid)
            {
                throw new InvalidOperationException("Desktop capture: invalid monitor bounds.");
            }

            _left = left;
            _top = top;
            Width = width;
            Height = height;
            EnsureBuffer();

            // Initial open keeps the historical log text so a field report that greps for it still matches.
            try
            {
                OpenDuplication();
                if (_duplication != null)
                {
                    EnsureStaging();
                }
            }
            catch (SharpGenException ex)
            {
                AppLogger.LogInfo($"DXGI Desktop Duplication unavailable (HRESULT 0x{ex.HResult:X8}): falling back to GDI capture.");
                _duplication = null;
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"DXGI Desktop Duplication unavailable ({ex.Message}): falling back to GDI capture.");
                _duplication = null;
            }
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

    /// <summary>
    /// Copies the latest desktop frame into the pinned buffer. Returns false when no
    /// newer frame is available within the timeout. A lost duplication (resolution change,
    /// UAC secure desktop, lock screen, fullscreen mode switch) is recreated; it is not
    /// treated as a static-desktop timeout. Before the first Desktop Duplication frame
    /// arrives, a static desktop is seeded with a GDI grab so the broadcaster can start
    /// streaming immediately.
    /// </summary>
    public unsafe bool TryCaptureFrame()
    {
        if (_disposed) return false;

        DxgiRead read = ReadOrRecover();
        if (read == DxgiRead.Frame)
        {
            _hasFrame = true;
            _sessionHealthy = true;
            return true;
        }

        // Healthy timeout with a frame already in the buffer: the desktop is unchanged.
        // Do not spend a GDI blit, and do not report the session lost.
        if (read == DxgiRead.Timeout && _hasFrame)
        {
            _sessionHealthy = true;
            return false;
        }

        if (CaptureViaGdi())
        {
            _hasFrame = true;
            _sessionHealthy = true;
            return true;
        }

        // GDI could not seed or fill in. A live duplication that merely timed out is still
        // healthy (static desktop). Anything else means the session is down.
        _sessionHealthy = read == DxgiRead.Timeout;
        return false;
    }

    private enum DxgiRead
    {
        None,
        Frame,
        Timeout
    }

    private unsafe DxgiRead ReadOrRecover()
    {
        if (_device == null) return DxgiRead.None;

        if (_duplication == null || _staging == null)
        {
            if (!TryRecover(DxgiAcquireKind.AccessLost)) return DxgiRead.None;
        }

        if (ReadOnce(out DxgiAcquireKind kind)) return DxgiRead.Frame;
        if (kind == DxgiAcquireKind.Timeout) return DxgiRead.Timeout;
        if (!DxgiFrameAcquire.NeedsDuplicationReset(kind)) return DxgiRead.None;

        if (!TryRecover(kind)) return DxgiRead.None;
        if (ReadOnce(out DxgiAcquireKind after)) return DxgiRead.Frame;
        if (after == DxgiAcquireKind.Timeout) return DxgiRead.Timeout;
        return DxgiRead.None;
    }

    private unsafe bool ReadOnce(out DxgiAcquireKind kind)
    {
        kind = DxgiAcquireKind.OtherFailure;
        if (_duplication == null || _staging == null || _device == null) return false;

        try
        {
            var result = _duplication.AcquireNextFrame(50, out _, out IDXGIResource resource);
            kind = DxgiFrameAcquire.Classify(result.Code);
            if (kind != DxgiAcquireKind.Success || resource == null) return false;

            using (resource)
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
            }

            return true;
        }
        catch (Exception ex)
        {
            // A lost session can surface as an exception rather than an HRESULT. Drop the
            // duplication so the next pass recreates it instead of calling into a dead object.
            kind = DxgiAcquireKind.AccessLost;
            LogRecovery($"DXGI AcquireNextFrame failed ({ex.Message}); the duplication will be recreated.");
            try { _duplication?.Dispose(); } catch { }
            _duplication = null;
            return false;
        }
    }

    private bool TryRecover(DxgiAcquireKind kind)
    {
        long now = Environment.TickCount64;
        if (now < _nextRecoveryTicks) return false;
        _nextRecoveryTicks = now + RecoveryBackoffMs;

        try
        {
            if (kind == DxgiAcquireKind.DeviceLost || _device == null)
            {
                if (!TryRecreateDevice()) return false;
            }
            else
            {
                try { _duplication?.Dispose(); } catch { }
                _duplication = null;
            }

            if (TryReadBounds(out int left, out int top, out int width, out int height) != BoundsRead.Ok)
            {
                LogRecovery("DXGI output could not be re-enumerated after the capture session was lost.");
                return false;
            }

            if (width != Width || height != Height || _buffer.Length != width * height * 4)
            {
                _left = left;
                _top = top;
                Width = width;
                Height = height;
                EnsureBuffer();
                try { _staging?.Dispose(); } catch { }
                _staging = null;
            }
            else
            {
                _left = left;
                _top = top;
            }

            OpenDuplication();
            if (_duplication == null) return false;
            EnsureStaging();
            LogRecovery("DXGI desktop duplication recreated after the capture session was lost.");
            return true;
        }
        catch (Exception ex)
        {
            try { _duplication?.Dispose(); } catch { }
            _duplication = null;
            LogRecovery($"DXGI duplication recovery failed ({ex.Message}).");
            return false;
        }
    }

    private bool TryRecreateDevice()
    {
        try
        {
            try { _staging?.Dispose(); } catch { }
            _staging = null;
            try { _duplication?.Dispose(); } catch { }
            _duplication = null;
            try { _device?.Dispose(); } catch { }
            _device = null;
            _device = CreateDevice();
            return _device != null;
        }
        catch (Exception ex)
        {
            _device = null;
            LogRecovery($"DXGI device recreate failed ({ex.Message}).");
            return false;
        }
    }

    private enum BoundsRead
    {
        Ok,
        Missing,
        Invalid
    }

    private BoundsRead TryReadBounds(out int left, out int top, out int width, out int height)
    {
        left = 0;
        top = 0;
        width = 0;
        height = 0;
        if (_device == null) return BoundsRead.Missing;

        using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        var enumResult = adapter.EnumOutputs((uint)_monitorIndex, out IDXGIOutput output);
        if (enumResult.Failure || output == null) return BoundsRead.Missing;

        using (output)
        {
            var bounds = output.Description.DesktopCoordinates;
            left = bounds.Left;
            top = bounds.Top;
            width = bounds.Right - bounds.Left;
            height = bounds.Bottom - bounds.Top;
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return BoundsRead.Invalid;
            return BoundsRead.Ok;
        }
    }

    private void OpenDuplication()
    {
        try { _duplication?.Dispose(); } catch { }
        _duplication = null;
        if (_device == null) return;

        using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        var enumResult = adapter.EnumOutputs((uint)_monitorIndex, out IDXGIOutput output);
        if (enumResult.Failure || output == null)
        {
            throw new InvalidOperationException($"Desktop capture: monitor index {_monitorIndex} not found.");
        }

        using (output)
        {
            using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(_device);
        }
    }

    private void EnsureBuffer()
    {
        int bytes = checked(Width * Height * 4);
        if (_buffer.Length == bytes && _bufferHandle.IsAllocated) return;
        if (_bufferHandle.IsAllocated) _bufferHandle.Free();
        _buffer = new byte[bytes];
        _bufferHandle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
    }

    private void EnsureStaging()
    {
        if (_device == null || _staging != null) return;

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
    }

    private static ID3D11Device CreateDevice()
        => D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 });

    private void LogRecovery(string message)
    {
        long now = Environment.TickCount64;
        if (now - _lastRecoveryLogTicks < RecoveryLogIntervalMs) return;
        _lastRecoveryLogTicks = now;
        AppLogger.LogInfo(message);
    }

    private unsafe bool CaptureViaGdi()
    {
        if (Width <= 0 || Height <= 0 || BufferPtr == IntPtr.Zero) return false;
        if (!GdiSurface.TryCreate(Width, Height, out GdiSurface surface)) return false;

        using (surface)
        {
            if (!NativeMethods.BitBlt(
                    surface.MemoryHandle, 0, 0, Width, Height,
                    surface.ScreenHandle, _left, _top,
                    NativeMethods.SrcCopy | NativeMethods.CaptureBlt))
            {
                return false;
            }

            var bmi = new NativeMethods.BitmapInfo();
            bmi.Header.Size = (uint)sizeof(NativeMethods.BitmapInfoHeader);
            bmi.Header.Width = Width;
            bmi.Header.Height = -Height; // negative => top-down
            bmi.Header.Planes = 1;
            bmi.Header.BitCount = 32;
            bmi.Header.Compression = NativeMethods.BiRgb;

            if (NativeMethods.GetDIBits(
                    surface.MemoryHandle, surface.BitmapHandle, 0, (uint)Height,
                    BufferPtr, ref bmi, NativeMethods.DibRgbColors) == 0)
            {
                return false;
            }

            // GDI 32bpp DIBs are BGRX; make alpha opaque for OMT BGRA.
            byte* p = (byte*)BufferPtr;
            for (int i = 3; i < Width * Height * 4; i += 4) p[i] = 0xFF;
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeResources();
    }

    private void DisposeResources()
    {
        if (_bufferHandle.IsAllocated) _bufferHandle.Free();
        try { _staging?.Dispose(); } catch { }
        try { _duplication?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _staging = null;
        _duplication = null;
        _device = null;
    }
}
