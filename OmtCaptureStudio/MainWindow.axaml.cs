using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using OmtCaptureStudio.ViewModels;

namespace OmtCaptureStudio;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow() : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        // View-specific bindings for hardware viewport and VU meter
        _viewModel.VideoFrameReceived += VideoViewport.UpdateFrame;
        _viewModel.ViewportClearRequested += VideoViewport.Clear;
        _viewModel.AudioLevelsUpdated += VuMeter.UpdateLevels;
        _viewModel.MetersResetRequested += VuMeter.ResetMeters;
        VuMeter.MonitoringToggled += OnMonitoringToggled;

        // Platform storage provider dialog hook
        _viewModel.PickFolderAsync = async currentFolder =>
        {
            var options = new FolderPickerOpenOptions
            {
                Title = "Select Destination Folder for Recordings",
                AllowMultiple = false
            };

            if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
            {
                var folder = await StorageProvider.TryGetFolderFromPathAsync(currentFolder);
                if (folder != null)
                {
                    options.SuggestedStartLocation = folder;
                }
            }

            var result = await StorageProvider.OpenFolderPickerAsync(options);
            return result is { Count: > 0 }
                ? result[0].TryGetLocalPath() ?? result[0].Path.LocalPath
                : null;
        };

        // Window close requested by ViewModel
        _viewModel.RequestClose += OnRequestClose;

        Closing += MainWindow_Closing;
        KeyDown += MainWindow_KeyDown;
    }

    private void OnMonitoringToggled(bool enabled)
    {
        _viewModel.SetAudioMonitoring(enabled);
    }

    private void OnRequestClose()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Close();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Close);
        }
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_viewModel.IsRecording && !_viewModel.IsSafeExitConfirmed)
        {
            e.Cancel = true;
            _viewModel.IsRecordingCloseOverlayVisible = true;
            _viewModel.IsStopAndExitEnabled = true;
            _viewModel.IsKeepRecordingEnabled = true;
            _viewModel.StopAndExitButtonText = "Stop & Exit";
            BtnKeepRecording.Focus();
            _viewModel.StatusText = "Warning: Active recording in progress. Safe exit confirmation required.";
            return;
        }

        // Cleanly unhook all view event subscriptions to prevent memory leaks
        _viewModel.VideoFrameReceived -= VideoViewport.UpdateFrame;
        _viewModel.ViewportClearRequested -= VideoViewport.Clear;
        _viewModel.AudioLevelsUpdated -= VuMeter.UpdateLevels;
        _viewModel.MetersResetRequested -= VuMeter.ResetMeters;
        _viewModel.RequestClose -= OnRequestClose;
        VuMeter.MonitoringToggled -= OnMonitoringToggled;
        Closing -= MainWindow_Closing;
        KeyDown -= MainWindow_KeyDown;
        _viewModel.PickFolderAsync = null;
        _viewModel.Dispose();
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;

        if (_viewModel.IsRecordingCloseOverlayVisible)
        {
            if (e.Key == Key.Escape)
            {
                _viewModel.KeepRecordingCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && (e.Key == Key.Enter || e.Key == Key.Return))
        {
            _viewModel.ToggleConnectCommand.Execute(null);
            e.Handled = true;
        }
    }
}
