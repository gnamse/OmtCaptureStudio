using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Builds a concrete <see cref="IFrameSource"/> from a user-selected
/// <see cref="CaptureTarget"/>. Monitors map to DXGI Desktop Duplication; windows
/// map to GDI PrintWindow capture.
/// </summary>
public static class FrameSourceFactory
{
    public static IFrameSource Create(CaptureTarget target)
    {
        return target.Kind == CaptureTargetKind.Monitor
            ? new DesktopFrameCapturer(target.MonitorIndex)
            : new WindowFrameCapturer(target.WindowHandle);
    }
}
