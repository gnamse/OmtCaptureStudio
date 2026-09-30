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

namespace OmtCaptureStudio.Controls;

/// <summary>
/// High-performance Skia/WriteableBitmap video viewport for Avalonia UI.
/// Features stride-aware line-by-line copy, aspect ratio preservation,
/// and frame-drop guard to prevent UI dispatcher queue backlog.
/// </summary>
public class VideoViewportControl : Control
{
    private static readonly IBrush s_backdropBrush = new SolidColorBrush(Color.FromRgb(9, 9, 11));

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
    /// </summary>
    public unsafe void UpdateFrame(IntPtr pData, int dataLength, int width, int height, int stride)
    {
        if (pData == IntPtr.Zero || width <= 0 || height <= 0 || stride <= 0)
            return;

        int requiredBytes = stride * height;

        // Stage the incoming frame into the back buffer under lock
        lock (_stagingLock)
        {
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
