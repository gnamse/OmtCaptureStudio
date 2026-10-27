using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OmtCaptureStudio.Views;

/// <summary>
/// ADR-0011: Capture mode — the record stack. The controls are the originals with their original
/// bindings; only the container changed. No logic lives here.
/// </summary>
public partial class BroadcastModeView : UserControl
{
    public BroadcastModeView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>ADR-0011: the live video surface this mode owns — the window wires frame/level events to it.</summary>
    public VideoSurface Surface => this.FindControl<VideoSurface>("BroadcastSurface")!;
}
