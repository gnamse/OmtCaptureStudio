using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OmtCaptureStudio.Controls;

namespace OmtCaptureStudio.Views;

/// <summary>
/// ADR-0011: the shared video surface — viewport, HUD overlays, diagnostics drawer and VU meter.
/// It is the one piece of the content region that is identical in both operating modes, so it lives
/// in one control rather than being duplicated into <see cref="CaptureModeView"/> and
/// <see cref="BroadcastModeView"/>.
///
/// <para>
/// The <see cref="Viewport"/> and <see cref="Meter"/> accessors exist because the frame/level events
/// are raised by the ViewModel and must reach the LIVE control — which is whichever mode view is
/// currently loaded. The window wires them through <c>MainWindow.RefreshVideoSurface()</c> when the
/// mode changes.
/// </para>
/// </summary>
public partial class VideoSurface : UserControl
{
    public VideoSurface()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>The drawing surface that receives BGRA frames from the ViewModel.</summary>
    public VideoViewportControl Viewport => this.FindControl<VideoViewportControl>("VideoViewport")!;

    /// <summary>The VU meter that receives audio levels from the ViewModel.</summary>
    public VuMeterControl Meter => this.FindControl<VuMeterControl>("VuMeter")!;
}
