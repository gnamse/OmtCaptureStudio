using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using OmtCaptureStudio.ViewModels;
using OmtCaptureStudio.Views;

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

        // ADR-0011: the viewport and VU meter now live inside the mode views (through VideoSurface),
        // so the window wires whichever surface is LIVE — and re-wires when the shell mode changes.
        // The ViewModel raises the same frame/level events as before; only their destination moved.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        WireVideoSurface();

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

    private VideoSurface? _wiredSurface;

    private void OnMonitoringToggled(bool enabled)
    {
        _viewModel.SetAudioMonitoring(enabled);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsCaptureModeVisible)
            or nameof(MainWindowViewModel.IsBroadcastModeVisible))
        {
            WireVideoSurface();
        }
    }

    /// <summary>
    /// ADR-0011: points the frame/level events at the surface of whichever mode view is now showing.
    /// Idempotent — re-wiring to the same surface is a no-op, so a duplicate notification costs nothing.
    /// In the picker there is no surface, and the events are simply unhooked.
    /// </summary>
    private void WireVideoSurface()
    {
        VideoSurface? surface = _viewModel.IsCaptureModeVisible ? CaptureView?.Surface
                              : _viewModel.IsBroadcastModeVisible ? BroadcastView?.Surface
                              : null;

        if (ReferenceEquals(surface, _wiredSurface))
        {
            return;
        }

        UnwireVideoSurface();
        _wiredSurface = surface;
        if (surface == null)
        {
            return;
        }

        _viewModel.VideoFrameReceived += surface.Viewport.UpdateFrame;
        _viewModel.ViewportClearRequested += surface.Viewport.Clear;
        _viewModel.AudioLevelsUpdated += surface.Meter.UpdateLevels;
        _viewModel.MetersResetRequested += surface.Meter.ResetMeters;
        surface.Meter.MonitoringToggled += OnMonitoringToggled;
    }

    private void UnwireVideoSurface()
    {
        if (_wiredSurface == null)
        {
            return;
        }

        _viewModel.VideoFrameReceived -= _wiredSurface.Viewport.UpdateFrame;
        _viewModel.ViewportClearRequested -= _wiredSurface.Viewport.Clear;
        _viewModel.AudioLevelsUpdated -= _wiredSurface.Meter.UpdateLevels;
        _viewModel.MetersResetRequested -= _wiredSurface.Meter.ResetMeters;
        _wiredSurface.Meter.MonitoringToggled -= OnMonitoringToggled;
        _wiredSurface = null;
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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        UnwireVideoSurface();
        _viewModel.RequestClose -= OnRequestClose;
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
            else if (e.Key == Key.Tab)
            {
                // Focus trap: toggle focus between the two modal buttons so Tab / Shift+Tab
                // cycles cleanly inside the dialog and never escapes to background controls.
                if (BtnKeepRecording.IsFocused)
                {
                    BtnStopAndExit.Focus();
                }
                else
                {
                    BtnKeepRecording.Focus();
                }
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
