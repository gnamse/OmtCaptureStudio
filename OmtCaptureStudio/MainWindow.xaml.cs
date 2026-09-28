using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;

namespace OmtCaptureStudio;

public partial class MainWindow : Window
{
    private readonly OmtDiscoveryService _discoveryService;
    private readonly OmtReceiverService _receiverService;
    private readonly AudioProcessor _audioProcessor;
    private readonly StreamRecorderService _recorderService;
    private readonly OmtTestPatternGenerator _testGenerator;

    private WriteableBitmap? _videoBitmap;
    private readonly DispatcherTimer _recordUiTimer;
    private int _currentWidth;
    private int _currentHeight;
    private double _currentFps = 60.0;
    private int _currentSampleRate = 48000;
    private int _currentChannels = 2;
    private readonly object _bitmapLock = new();
    private bool _isUpdatingSources;

    public MainWindow()
    {
        InitializeComponent();

        _discoveryService = new OmtDiscoveryService();
        _receiverService = new OmtReceiverService();
        _audioProcessor = new AudioProcessor();
        _recorderService = new StreamRecorderService();
        _testGenerator = new OmtTestPatternGenerator();

        _recordUiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _recordUiTimer.Tick += RecordUiTimer_Tick;

        SetupUiDefaults();
        HookEvents();

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
    }

    private void HookEvents()
    {
        // Discovery
        _discoveryService.SourcesUpdated += OnSourcesUpdated;

        // Receiver
        _receiverService.VideoFrameReceived += OnVideoFrameReceived;
        _receiverService.AudioFrameReceived += OnAudioFrameReceived;
        _receiverService.TelemetryUpdated += OnTelemetryUpdated;
        _receiverService.ConnectionStatusChanged += OnConnectionStatusChanged;
        _receiverService.ErrorOccurred += OnReceiverError;

        // Recorder
        _recorderService.RecordingStarted += OnRecordingStarted;
        _recorderService.RecordingStopped += OnRecordingStopped;
        _recorderService.RecordingError += OnRecordingError;

        // VU Meter local audio monitor toggle
        VuMeter.MonitoringToggled += enabled =>
        {
            _audioProcessor.IsMonitoringEnabled = enabled;
        };
    }

    #region Discovery & Connection Handlers

    private void OnSourcesUpdated(List<OmtSourceInfo> sources)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // If the user currently has the dropdown open, don't interrupt them
            if (CmbSources.IsDropDownOpen) return;

            // Check if items are identical to avoid unnecessary rebinding
            if (CmbSources.ItemsSource is List<OmtSourceInfo> currentList &&
                currentList.Count == sources.Count &&
                currentList.Select(s => s.Address).SequenceEqual(sources.Select(s => s.Address)))
            {
                return;
            }

            var currentSelected = CmbSources.SelectedItem as OmtSourceInfo;
            string? prevAddress = currentSelected?.Address;

            _isUpdatingSources = true;
            try
            {
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
                    if (string.IsNullOrWhiteSpace(TxtManualUrl.Text) || TxtManualUrl.Text == "omt://127.0.0.1:5000")
                    {
                        TxtManualUrl.Text = sources[0].Address;
                    }
                }
            }
            finally
            {
                _isUpdatingSources = false;
            }
        });
    }

    private void CmbSources_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbSources.SelectedItem is OmtSourceInfo info)
        {
            CmbSources.ToolTip = $"{info.DisplayName}\nAddress: {info.Address}";
            // Do not overwrite manual URL if updating programmatically or if user is typing in manual URL
            if (!_isUpdatingSources && !TxtManualUrl.IsFocused)
            {
                TxtManualUrl.Text = info.Address;
            }
        }
        else
        {
            CmbSources.ToolTip = null;
        }
    }

    private void BtnRefreshSources_Click(object sender, RoutedEventArgs e)
    {
        _discoveryService.RefreshNow();
        TxtStatus.Text = "Scanning network for OMT sources...";
    }

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_receiverService.IsConnected)
        {
            _receiverService.Disconnect();
            BtnConnect.Content = "Connect";
            BtnConnect.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            BtnConnect.Foreground = new SolidColorBrush(Colors.Black);
            NoSignalOverlay.Visibility = Visibility.Visible;
            LiveIndicator.Fill = new SolidColorBrush(Color.FromRgb(113, 113, 122));
            TxtLiveStatus.Text = "OFFLINE";
        }
        else
        {
            string url = TxtManualUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("Please select a source or enter a valid OMT URL.", "OMT Capture Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CmbSources.SelectedItem is OmtSourceInfo info && (info.Address == url || info.DisplayName == url))
            {
                TxtSourceName.Text = info.DisplayName;
            }
            else
            {
                TxtSourceName.Text = url;
            }

            _receiverService.Connect(url);
            BtnConnect.Content = "Disconnect";
            BtnConnect.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            BtnConnect.Foreground = new SolidColorBrush(Colors.White);
        }
    }

    private void BtnTestSignal_Click(object sender, RoutedEventArgs e)
    {
        bool isStarting = BtnTestSignal.IsChecked ?? false;

        if (isStarting)
        {
            _testGenerator.Start();
            BtnTestSignal.Background = new SolidColorBrush(Color.FromRgb(99, 102, 241));
            BtnTestSignal.Foreground = new SolidColorBrush(Colors.White);
            TxtStatus.Text = "Local test pattern generator started. Connecting...";

            // Auto connect to the local test pattern source
            Task.Delay(500).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    string target = _testGenerator.Address ?? _testGenerator.Url ?? _testGenerator.SourceName;
                    TxtManualUrl.Text = target;
                    TxtSourceName.Text = target;
                    _receiverService.Connect(target);
                    BtnConnect.Content = "Disconnect";
                    BtnConnect.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    BtnConnect.Foreground = new SolidColorBrush(Colors.White);
                });
            });
        }
        else
        {
            _testGenerator.Stop();
            BtnTestSignal.Background = new SolidColorBrush(Color.FromRgb(39, 39, 42));
            BtnTestSignal.Foreground = new SolidColorBrush(Color.FromRgb(244, 244, 245));
            TxtStatus.Text = "Test pattern generator stopped.";

            if (_receiverService.IsConnected)
            {
                _receiverService.Disconnect();
                BtnConnect.Content = "Connect";
                BtnConnect.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                BtnConnect.Foreground = new SolidColorBrush(Colors.Black);
            }
        }
    }

    private void OnConnectionStatusChanged(string status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TxtStatus.Text = status;
        });
    }

    private void OnReceiverError(string error)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TxtStatus.Text = $"Error: {error}";
        });
    }

    #endregion

    #region Video & Audio Processing Handlers

    private void OnVideoFrameReceived(IntPtr pData, int dataLength, int width, int height, int stride, double fps, long timestamp)
    {
        _currentWidth = width;
        _currentHeight = height;
        _currentFps = fps;

        // 1. Forward frame to Recorder if recording is active
        if (_recorderService.IsRecording)
        {
            _recorderService.WriteVideoFrame(pData, dataLength);
        }

        // 2. Render to GUI Video Preview
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (_videoBitmap == null || _videoBitmap.PixelWidth != width || _videoBitmap.PixelHeight != height)
                {
                    _videoBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    VideoPreviewImage.Source = _videoBitmap;
                }

                _videoBitmap.Lock();
                _videoBitmap.WritePixels(new Int32Rect(0, 0, width, height), pData, dataLength, stride);
                _videoBitmap.Unlock();

                if (NoSignalOverlay.Visibility != Visibility.Collapsed)
                {
                    NoSignalOverlay.Visibility = Visibility.Collapsed;
                    LiveIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                    TxtLiveStatus.Text = "LIVE";
                }

                TxtVideoSpecs.Text = $"{width}x{height} @ {fps:F2} fps";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Render Error] {ex.Message}");
            }
        }, DispatcherPriority.Render);
    }

    private void OnAudioFrameReceived(IntPtr pPlanarData, int channels, int samplesPerChannel, int sampleRate, long timestamp)
    {
        _currentChannels = channels;
        _currentSampleRate = sampleRate;

        // 1. Calculate VU meter levels
        var levelData = _audioProcessor.ComputeLevels(pPlanarData, channels, samplesPerChannel);
        VuMeter.UpdateLevels(levelData);

        // 2. Interleave planar audio
        byte[] interleavedBytes = _audioProcessor.InterleaveFloatAudio(pPlanarData, channels, samplesPerChannel);

        // 3. Audio monitoring
        _audioProcessor.FeedMonitoringAudio(interleavedBytes, sampleRate, channels);

        // 4. Forward to recording pipe
        if (_recorderService.IsRecording)
        {
            _recorderService.WriteAudioData(interleavedBytes);
        }
    }

    private void OnTelemetryUpdated(StreamTelemetry t)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TxtAudioTelemetry.Text = $"Audio: {t.AudioFormatText}";
            TxtBitrate.Text = $"Bitrate: {t.BitrateMbps:F1} Mbps";
            TxtDroppedFrames.Text = $"Dropped: {t.FramesDropped}";
        });
    }

    #endregion

    #region Recording Controls

    private void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_recorderService.IsRecording)
        {
            _recorderService.StopRecording();
        }
        else
        {
            if (!_receiverService.IsConnected || _currentWidth <= 0 || _currentHeight <= 0)
            {
                MessageBox.Show("Please connect to an active OMT stream before recording.", "OMT Capture Studio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var config = new RecordingConfig
            {
                ContainerFormat = CmbContainer.SelectedItem is OutputContainerFormat cf ? cf : OutputContainerFormat.MP4,
                EncoderChoice = CmbEncoder.SelectedItem is VideoEncoderChoice ec ? ec : VideoEncoderChoice.AutoHardware,
                Quality = CmbQuality.SelectedItem is QualityPreset qp ? qp : QualityPreset.High,
                OutputDirectory = TxtOutputDir.Text.Trim()
            };

            bool started = _recorderService.StartRecording(
                config,
                _currentWidth,
                _currentHeight,
                _currentFps,
                _currentSampleRate,
                _currentChannels
            );

            if (!started)
            {
                MessageBox.Show("Failed to initiate recording. Ensure FFmpeg is available and destination folder is writable.", "Recording Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnRecordingStarted(string filePath)
    {
        Dispatcher.InvokeAsync(() =>
        {
            BtnRecord.Content = "■ STOP RECORDING";
            BtnRecord.Background = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            TxtRecordStats.Text = $"Recording to {Path.GetFileName(filePath)}";
            _recordUiTimer.Start();
        });
    }

    private void OnRecordingStopped(string filePath, TimeSpan duration, long fileSize)
    {
        Dispatcher.InvokeAsync(() =>
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
        Dispatcher.InvokeAsync(() =>
        {
            TxtStatus.Text = $"Recording Error: {error}";
            MessageBox.Show(error, "Recording Error", MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    private void RecordUiTimer_Tick(object? sender, EventArgs e)
    {
        if (_recorderService.IsRecording)
        {
            var elapsed = _recorderService.ElapsedTime;
            TxtRecordDuration.Text = elapsed.ToString(@"hh\:mm\:ss");

            long frames = _recorderService.FramesWritten;
            TxtRecordStats.Text = $"Writing... {frames:N0} frames";
        }
    }

    private void BtnBrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Destination Folder for Recordings",
            InitialDirectory = TxtOutputDir.Text
        };

        if (dialog.ShowDialog() == true)
        {
            TxtOutputDir.Text = dialog.FolderName;
        }
    }

    private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
    {
        string dir = TxtOutputDir.Text.Trim();
        if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
    }

    private void CmbContainer_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_recorderService.IsRecording)
        {
            _recorderService.StopRecording();
        }

        _discoveryService.Dispose();
        _receiverService.Dispose();
        _audioProcessor.Dispose();
        _testGenerator.Dispose();

        base.OnClosing(e);
    }
}