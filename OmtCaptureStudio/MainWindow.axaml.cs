using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Sources;

namespace OmtCaptureStudio;

public partial class MainWindow : Window
{
    private readonly CaptureSession _session;
    private readonly OmtDiscoveryService _discoveryService;
    private readonly DispatcherTimer _recordUiTimer;
    private long _lastHudUpdateTick;
    private int _lastWidth;
    private int _lastHeight;
    private double _lastFps;

    public MainWindow()
    {
        InitializeComponent();

        _session = new CaptureSession();
        _discoveryService = new OmtDiscoveryService();

        _recordUiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _recordUiTimer.Tick += RecordUiTimer_Tick;

        SetupUiDefaults();
        HookEvents();

        Closing += (s, e) =>
        {
            _discoveryService.Dispose();
            _session.Dispose();
        };

        _discoveryService.Start();
    }

    private void SetupUiDefaults()
    {
        // Container formats
        CmbContainer.ItemsSource = Enum.GetValues<OutputContainerFormat>();
        CmbContainer.SelectedItem = OutputContainerFormat.MP4;

        // Encoders
        CmbEncoder.ItemsSource = Enum.GetValues<VideoEncoderChoice>();
        CmbEncoder.SelectedItem = VideoEncoderChoice.AutoHardware;

        // Quality Presets
        CmbQuality.ItemsSource = Enum.GetValues<QualityPreset>();
        CmbQuality.SelectedItem = QualityPreset.High;

        // Default Output Directory
        string defaultVideos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "OMT_Captures");
        if (!Directory.Exists(defaultVideos))
        {
            try { Directory.CreateDirectory(defaultVideos); } catch { }
        }
        TxtOutputDir.Text = defaultVideos;

        // Initial selection changed handler
        CmbSources.SelectionChanged += CmbSources_SelectionChanged;
    }

    private void HookEvents()
    {
        // Network Source Discovery
        _discoveryService.SourcesUpdated += OnSourcesUpdated;

        // Capture Engine Events
        _session.VideoFrameAvailable += OnVideoFrameAvailable;
        _session.AudioLevelsUpdated += OnAudioLevelsUpdated;
        _session.TelemetryUpdated += OnTelemetryUpdated;
        _session.StatusChanged += OnStatusChanged;
        _session.ErrorOccurred += OnSessionError;

        // Recording Lifecycle Events
        _session.RecordingStarted += OnRecordingStarted;
        _session.RecordingStopped += OnRecordingStopped;
        _session.RecordingError += OnRecordingError;

        // VU Meter local audio monitor toggle
        VuMeter.MonitoringToggled += enabled =>
        {
            _session.IsAudioMonitoringEnabled = enabled;
        };
    }

    #region Discovery & Connection Handlers

    private void OnSourcesUpdated(List<OmtSourceInfo> sources)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (CmbSources.IsDropDownOpen) return;

            if (CmbSources.ItemsSource is List<OmtSourceInfo> currentList &&
                currentList.Count == sources.Count &&
                currentList.Select(s => s.Address).SequenceEqual(sources.Select(s => s.Address)))
            {
                return;
            }

            var currentSelected = CmbSources.SelectedItem as OmtSourceInfo;
            string? prevAddress = currentSelected?.Address;

            CmbSources.ItemsSource = sources;

            if (!string.IsNullOrEmpty(prevAddress))
            {
                var match = sources.FirstOrDefault(s => s.Address == prevAddress);
                if (match != null)
                {
                    CmbSources.SelectedItem = match;
                }
            }
            else if (sources.Count > 0 && CmbSources.SelectedItem == null)
            {
                CmbSources.SelectedIndex = 0;
            }
        });
    }

    private void CmbSources_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbSources.SelectedItem is OmtSourceInfo info)
        {
            ToolTip.SetTip(CmbSources, $"{info.DisplayName}\nAddress: {info.Address}");
        }
        else
        {
            ToolTip.SetTip(CmbSources, null);
        }
    }

    private void BtnRefreshSources_Click(object? sender, RoutedEventArgs e)
    {
        _discoveryService.RefreshNow();
        TxtStatus.Text = "Scanning network for OMT sources...";
    }

    private void BtnConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (_session.IsConnected || BtnConnect.Content?.ToString() == "Disconnect")
        {
            _session.Disconnect();
            VideoViewport.Clear();
            BtnConnect.Content = "Connect";
            BtnConnect.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            BtnConnect.Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 0));
            NoSignalOverlay.IsVisible = true;
            LiveIndicator.Fill = new SolidColorBrush(Color.FromRgb(113, 113, 122));
            TxtLiveStatus.Text = "OFFLINE";
            TxtVideoSpecs.Text = "-- x -- @ -- fps";

            BtnTestSignal.IsChecked = false;
            BtnTestSignal.Background = new SolidColorBrush(Color.FromRgb(39, 39, 42));
            BtnTestSignal.Foreground = new SolidColorBrush(Color.FromRgb(244, 244, 245));
        }
        else
        {
            if (CmbSources.SelectedItem is not OmtSourceInfo info || string.IsNullOrWhiteSpace(info.Address))
            {
                TxtStatus.Text = "Please select an OMT source from the dropdown.";
                return;
            }

            TxtSourceName.Text = info.DisplayName;
            _session.Connect(info.Address);
            BtnConnect.Content = "Disconnect";
            BtnConnect.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            BtnConnect.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        }
    }

    private void BtnTestSignal_Click(object? sender, RoutedEventArgs e)
    {
        bool isStarting = BtnTestSignal.IsChecked == true;

        if (isStarting)
        {
            if (_session.IsConnected)
            {
                _session.Disconnect();
            }

            BtnTestSignal.Background = new SolidColorBrush(Color.FromRgb(99, 102, 241));
            BtnTestSignal.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            TxtSourceName.Text = "Local Test Pattern (SMPTE)";
            TxtStatus.Text = "Connected to internal synthetic test generator.";

            _session.Connect(new SyntheticPatternSource());

            BtnConnect.Content = "Disconnect";
            BtnConnect.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            BtnConnect.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        }
        else
        {
            BtnTestSignal.Background = new SolidColorBrush(Color.FromRgb(39, 39, 42));
            BtnTestSignal.Foreground = new SolidColorBrush(Color.FromRgb(244, 244, 245));
            TxtStatus.Text = "Test pattern stopped.";

            if (_session.IsConnected)
            {
                _session.Disconnect();
                VideoViewport.Clear();
                BtnConnect.Content = "Connect";
                BtnConnect.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                BtnConnect.Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 0));
                NoSignalOverlay.IsVisible = true;
                LiveIndicator.Fill = new SolidColorBrush(Color.FromRgb(113, 113, 122));
                TxtLiveStatus.Text = "OFFLINE";
                TxtVideoSpecs.Text = "-- x -- @ -- fps";
            }
        }
    }

    private void OnStatusChanged(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TxtStatus.Text = status;
        });
    }

    private void OnSessionError(string error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TxtStatus.Text = $"Error: {error}";
        });
    }

    #endregion

    #region Video & Audio Processing Handlers

    private void OnVideoFrameAvailable(IntPtr pData, int dataLength, int width, int height, int stride, double fps, long timestamp)
    {
        // 1. Submit raw video buffer to the hardware/Skia viewport control
        VideoViewport.UpdateFrame(pData, dataLength, width, height, stride);

        // 2. Throttle HUD overlay updates (at most once every 500ms or on format change)
        long now = Environment.TickCount64;
        if (NoSignalOverlay.IsVisible || width != _lastWidth || height != _lastHeight || Math.Abs(fps - _lastFps) > 0.5 || now - _lastHudUpdateTick >= 500)
        {
            _lastHudUpdateTick = now;
            _lastWidth = width;
            _lastHeight = height;
            _lastFps = fps;

            Dispatcher.UIThread.Post(() =>
            {
                if (NoSignalOverlay.IsVisible)
                {
                    NoSignalOverlay.IsVisible = false;
                    LiveIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                    TxtLiveStatus.Text = "LIVE";
                }

                TxtVideoSpecs.Text = $"{width}x{height} @ {fps:F2} fps";
            }, DispatcherPriority.Normal);
        }
    }

    private void OnAudioLevelsUpdated(AudioLevelData levels)
    {
        VuMeter.UpdateLevels(levels);
    }

    private void OnTelemetryUpdated(StreamTelemetry t)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TxtAudioTelemetry.Text = $"Audio: {t.AudioFormatText}";
            TxtBitrate.Text = $"Bitrate: {t.BitrateMbps:F1} Mbps";
            TxtDroppedFrames.Text = $"Dropped: {t.FramesDropped}";
        });
    }

    #endregion

    #region Recording Controls

    private void BtnRecord_Click(object? sender, RoutedEventArgs e)
    {
        if (_session.IsRecording)
        {
            _session.StopRecording();
        }
        else
        {
            if (!_session.IsConnected || !_session.CurrentFormat.HasVideo)
            {
                TxtStatus.Text = "Please connect to an active OMT stream before recording.";
                return;
            }

            var config = new RecordingConfig
            {
                ContainerFormat = CmbContainer.SelectedItem is OutputContainerFormat cf ? cf : OutputContainerFormat.MP4,
                EncoderChoice = CmbEncoder.SelectedItem is VideoEncoderChoice ec ? ec : VideoEncoderChoice.AutoHardware,
                Quality = CmbQuality.SelectedItem is QualityPreset qp ? qp : QualityPreset.High,
                OutputDirectory = TxtOutputDir.Text?.Trim() ?? ""
            };

            bool started = _session.StartRecording(config);
            if (!started)
            {
                TxtStatus.Text = "Failed to initiate recording. Ensure FFmpeg is available and destination folder is writable.";
            }
        }
    }

    private void OnRecordingStarted(string filePath)
    {
        Dispatcher.UIThread.Post(() =>
        {
            BtnRecord.Content = "■ STOP RECORDING";
            BtnRecord.Background = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            TxtRecordStats.Text = $"Recording to {Path.GetFileName(filePath)}";
            _recordUiTimer.Start();
        });
    }

    private void OnRecordingStopped(string filePath, TimeSpan duration, long fileSize)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _recordUiTimer.Stop();
            BtnRecord.Content = "● START RECORDING";
            BtnRecord.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));

            double mb = fileSize / (1024.0 * 1024.0);
            TxtRecordDuration.Text = "00:00:00";
            TxtRecordStats.Text = $"Saved: {Path.GetFileName(filePath)} ({mb:F1} MB, {duration:mm\\:ss})";

            TxtStatus.Text = $"Recording completed and saved to {filePath}";
        });
    }

    private void OnRecordingError(string error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TxtStatus.Text = $"Recording Error: {error}";
        });
    }

    private void RecordUiTimer_Tick(object? sender, EventArgs e)
    {
        if (_session.IsRecording)
        {
            var elapsed = _session.RecordingElapsedTime;
            TxtRecordDuration.Text = elapsed.ToString(@"hh\:mm\:ss");

            long frames = _session.RecordingFramesWritten;
            TxtRecordStats.Text = $"Writing... {frames:N0} frames";
        }
    }

    private async void BtnBrowseDir_Click(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions
        {
            Title = "Select Destination Folder for Recordings",
            AllowMultiple = false
        };

        if (!string.IsNullOrWhiteSpace(TxtOutputDir.Text) && Directory.Exists(TxtOutputDir.Text))
        {
            var folder = await StorageProvider.TryGetFolderFromPathAsync(TxtOutputDir.Text);
            if (folder != null)
            {
                options.SuggestedStartLocation = folder;
            }
        }

        var result = await StorageProvider.OpenFolderPickerAsync(options);
        if (result != null && result.Count > 0)
        {
            TxtOutputDir.Text = result[0].Path.LocalPath;
        }
    }

    private void BtnOpenDir_Click(object? sender, RoutedEventArgs e)
    {
        string dir = TxtOutputDir.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(dir))
        {
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); } catch { }
            }

            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
    }

    private void CmbContainer_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbContainer.SelectedItem is OutputContainerFormat fmt && CmbEncoder != null)
        {
            if (fmt == OutputContainerFormat.MOV)
            {
                CmbEncoder.SelectedItem = VideoEncoderChoice.ProRes;
            }
            else if (CmbEncoder.SelectedItem is VideoEncoderChoice currentEncoder && currentEncoder == VideoEncoderChoice.ProRes)
            {
                CmbEncoder.SelectedItem = VideoEncoderChoice.AutoHardware;
            }
        }
    }

    #endregion
}
