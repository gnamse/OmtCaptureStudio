using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OmtCaptureStudio.Views;

/// <summary>
/// ADR-0011: Capture mode — the record stack. Binds the <c>MainWindowViewModel</c> facade
/// (AppModeShell / CaptureWorkspace / RecordingChrome partials); do not invent a second Capture VM.
/// No logic lives here.
/// </summary>
public partial class CaptureModeView : UserControl
{
    public CaptureModeView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>ADR-0011: the live video surface this mode owns — the window wires frame/level events to it.</summary>
    public VideoSurface Surface => this.FindControl<VideoSurface>("CaptureSurface")!;
}
