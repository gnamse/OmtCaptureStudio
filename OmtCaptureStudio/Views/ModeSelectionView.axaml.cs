using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OmtCaptureStudio.Views;

/// <summary>
/// ADR-0011: the mode picker — the app's first impression. Two equally-weighted cards; the card
/// matching <c>LastUsedAppMode</c> carries a "LAST USED" badge. Pure projection of the
/// <c>MainWindowViewModel</c> AppModeShell facade (<c>SelectModeCommand</c>); no second picker VM.
/// </summary>
public partial class ModeSelectionView : UserControl
{
    public ModeSelectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
