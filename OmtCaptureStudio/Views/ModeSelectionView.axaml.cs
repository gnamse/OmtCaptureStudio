using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OmtCaptureStudio.Views;

/// <summary>
/// ADR-0011: the mode picker — the app's first impression. Two equally-weighted cards; the card
/// matching <c>LastUsedAppMode</c> carries a "LAST USED" badge. It is a pure projection of the
/// ViewModel's <c>SelectModeCommand</c>; no logic lives here.
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
