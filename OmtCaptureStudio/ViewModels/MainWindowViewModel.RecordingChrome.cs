using CommunityToolkit.Mvvm.Input;

namespace OmtCaptureStudio.ViewModels;

/// <summary>
/// Recording chrome affordances — Phase 1.6 low-risk slice.
/// Advanced-options disclosure toggle/chevron stay on the facade binding surface.
/// </summary>
public partial class MainWindowViewModel
{

    /// <summary>The expander's accessible name, which states the action rather than the state.</summary>
    public string AdvancedOptionsAutomationName => IsAdvancedRecordingOptionsExpanded
        ? "Hide advanced recording options"
        : "Show advanced recording options";

    [RelayCommand]
    private void ToggleAdvancedRecordingOptions()
        => IsAdvancedRecordingOptionsExpanded = !IsAdvancedRecordingOptionsExpanded;

    /// <summary>Chevron direction for the Advanced disclosure (down = collapsed, up = expanded).</summary>
    public bool IsAdvancedChevronDown => !IsAdvancedRecordingOptionsExpanded;

    partial void OnIsAdvancedRecordingOptionsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(AdvancedOptionsAutomationName));
        OnPropertyChanged(nameof(IsAdvancedChevronDown));
    }

}
