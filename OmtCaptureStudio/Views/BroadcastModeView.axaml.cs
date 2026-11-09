using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OmtCaptureStudio.Views;

/// <summary>
/// ADR-0011: Broadcast mode — the host-broadcast controls. Binds the <c>MainWindowViewModel</c>
/// facade (AppModeShell / BroadcastWorkspace / BroadcastChrome partials); do not invent a second
/// Broadcast VM. No logic lives here.
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
