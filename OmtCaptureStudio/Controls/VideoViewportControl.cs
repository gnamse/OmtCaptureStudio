using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OmtCaptureStudio.Services;

namespace OmtCaptureStudio.Controls;

/// <summary>
/// High-performance Skia/WriteableBitmap video viewport for Avalonia UI.
/// Features stride-aware line-by-line copy, aspect ratio preservation,
/// and frame-drop guard to prevent UI dispatcher queue backlog.
/// </summary>
public class VideoViewportControl : Control
{
    private static readonly IBrush s_backdropBrush = StudioPalette.Backdrop;

    private WriteableBitmap? _bitmap;
    private readonly object _bitmapLock = new();

    // Ping-pong double buffers to prevent tearing and concurrent read/write
    private byte[]? _backBuffer;
    private byte[]? _frontBuffer;
    private int _stagedWidth;
    private int _stagedHeight;
    private int _stagedStride;
    private bool _hasNewFrame;
    private int _renderPending = 0;
    private readonly object _stagingLock = new();

    public VideoViewportControl()
    {
        ClipToBounds = true;
    }

    /// <summary>
    /// Thread-safe entry point called by CaptureSession when a new raw BGRA video frame is decoded.
    ///
    /// <para>SEC-OMT-01 SEC-02 (R12-R16). This runs on <c>OMT_ReceiveThread</c> via a DIRECT delegate
    /// (no dispatcher marshal), and every field it is handed is chosen by the remote peer. It therefore
    /// re-validates the geometry itself rather than trusting that the receiver did: the overflow below
    /// used to be <c>int requiredBytes = stride * height</c>, which a peer could drive to ~126 MB out of a
    /// ≤10 MB buffer, or wrap to 0 (copy silently skipped) or negative (allocation throws).</para>
    /// </summary>
    public unsafe void UpdateFrame(IntPtr pData, int dataLength, int width, int height, int stride)
    {
        if (pData == IntPtr.Zero || width <= 0 || height <= 0 || stride <= 0)
            return;

        // R12/R13/R14, in long arithmetic so the product cannot wrap. This may look redundant with the
        // receiver's OmtVideoFrameGuard, and it is deliberately so: this is a PUBLIC method on a control,
        // and one caller bypassing the receiver must not be able to re-open the finding.
        if (width > OmtVideoFrameGuard.MaxDimension || height > OmtVideoFrameGuard.MaxDimension)
            return;
        if (stride < (long)width * OmtVideoFrameGuard.BytesPerBgraPixel)
            return;

        long required = (long)stride * height;
        if (required > dataLength)              // R13: never read past the declared payload
        {
            return;
        }

        // R14 RE-SHAPED: byte ceiling -> PIXEL ceiling. This MUST mirror OmtVideoFrameGuard exactly,
        // because it is handed the same frames. Keeping the old 10 MiB BYTE bound here silently discarded
        // what the guard had just accepted: 2560x1440 is 14,745,600 bytes and 3840x2160 is 33,177,600,
        // both above the byte ceiling, so every conformant 1440p/4K frame was dropped AT THE VIEWPORT
        // after passing the guard - and no guard-only test could observe it.
        if ((long)width * height > OmtVideoFrameGuard.MaxPixels)
        {
            return;
        }

        int requiredBytes = (int)required;

        // Stage the incoming frame into the back buffer under lock
        lock (_stagingLock)
        {
            // R16: grow when needed, but do NOT reallocate on every geometry change. `!= requiredBytes`
            // (the first version) re-sized on every alternation, and at 60 fps a 1080p<->4K flip is
            // ~600 MiB/s of Large-Object-Heap churn - a real cost, not a theoretical one. `< ` keeps the
            // buffer bounded by whatever the largest ACCEPTED frame was (now capped by the 8K pixel
            // bound, and only reachable by a frame that really delivered its bytes).
            if (_backBuffer == null || _backBuffer.Length < requiredBytes)
            {
                _backBuffer = new byte[requiredBytes];
            }

            fixed (byte* pDst = _backBuffer)
            {
                Buffer.MemoryCopy((void*)pData, pDst, _backBuffer.Length, requiredBytes);
            }

            _stagedWidth = width;
            _stagedHeight = height;
            _stagedStride = stride;
            _hasNewFrame = true;
        }

        // Frame-drop guard: if a render dispatch is already scheduled on UI thread, skip scheduling another.
        if (Interlocked.CompareExchange(ref _renderPending, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(ProcessPendingFrame, DispatcherPriority.Normal);
        }
    }

    private unsafe void ProcessPendingFrame()
    {
        try
        {
            int w, h, srcStride;
            byte[]? activeBuffer;

            lock (_stagingLock)
            {
                if (!_hasNewFrame)
                    return;

                // Ping-pong swap: UI thread takes ownership of back buffer data in front buffer
                (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
                _hasNewFrame = false;

                w = _stagedWidth;
                h = _stagedHeight;
                srcStride = _stagedStride;
                activeBuffer = _frontBuffer;
            }

            if (activeBuffer == null || w <= 0 || h <= 0 || srcStride <= 0)
                return;

            lock (_bitmapLock)
            {
                // Reallocate bitmap only when dimensions change
                if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
                {
                    _bitmap?.Dispose();
                    _bitmap = new WriteableBitmap(
                        new PixelSize(w, h),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);
                }

                // Stride-aware line-by-line copy into Avalonia WriteableBitmap framebuffer
                using (var fb = _bitmap.Lock())
                {
                    int dstStride = fb.RowBytes;
                    int rowBytesToCopy = Math.Min(w * 4, Math.Min(srcStride, dstStride));
                    byte* dstPtr = (byte*)fb.Address;

                    fixed (byte* srcBase = activeBuffer)
                    {
                        byte* srcPtr = srcBase;
                        if (srcStride == dstStride && srcStride == w * 4)
                        {
                            Buffer.MemoryCopy(srcPtr, dstPtr, (long)dstStride * h, (long)dstStride * h);
                        }
                        else
                        {
                            for (int y = 0; y < h; y++)
                            {
                                Buffer.MemoryCopy(srcPtr, dstPtr, dstStride, rowBytesToCopy);
                                srcPtr += srcStride;
                                dstPtr += dstStride;
                            }
                        }
                    }
                }
            }

            // Invalidate to trigger Skia DrawingContext Render
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoViewport Render Error] {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _renderPending, 0);

            // If a frame arrived while we were processing, schedule UI update immediately
            lock (_stagingLock)
            {
                if (_hasNewFrame && Interlocked.CompareExchange(ref _renderPending, 1, 0) == 0)
                {
                    Dispatcher.UIThread.Post(ProcessPendingFrame, DispatcherPriority.Normal);
                }
            }
        }
    }

    /// <summary>
    /// Resets and clears the viewport surface.
    /// </summary>
    public void Clear()
    {
        lock (_bitmapLock)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }
        lock (_stagingLock)
        {
            _hasNewFrame = false;
        }
        InvalidateVisual();
    }

    /// <summary>
    /// Skia DrawingContext rendering with aspect ratio preservation and letterboxing.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        // Render solid dark backdrop using cached brush
        context.FillRectangle(s_backdropBrush, bounds);

        lock (_bitmapLock)
        {
            if (_bitmap == null) return;

            var pixelSize = _bitmap.PixelSize;
            if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return;

            // Preserve aspect ratio (Uniform scaling)
            double scale = Math.Min(bounds.Width / pixelSize.Width, bounds.Height / pixelSize.Height);
            double destW = pixelSize.Width * scale;
            double destH = pixelSize.Height * scale;
            double destX = (bounds.Width - destW) / 2.0;
            double destY = (bounds.Height - destH) / 2.0;

            var srcRect = new Rect(0, 0, pixelSize.Width, pixelSize.Height);
            var destRect = new Rect(destX, destY, destW, destH);

            context.DrawImage(_bitmap, srcRect, destRect);
        }
    }
}
