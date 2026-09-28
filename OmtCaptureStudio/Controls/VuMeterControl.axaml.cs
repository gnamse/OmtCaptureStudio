using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Controls;

public partial class VuMeterControl : UserControl
{
    private float _peakHoldLeft = -60f;
    private float _peakHoldRight = -60f;
    private DateTime _lastClipLeft = DateTime.MinValue;
    private DateTime _lastClipRight = DateTime.MinValue;

    public event Action<bool>? MonitoringToggled;

    public VuMeterControl()
    {
        InitializeComponent();

        MeterAreaGrid.SizeChanged += (s, e) => RedrawScale();
        BtnMonitor.IsCheckedChanged += BtnMonitor_IsCheckedChanged;
    }

    public static double DbToNormalized(float db)
    {
        // Broadcast standard dBFS scale (-60 dB to 0 dB)
        if (db <= -60f) return 0.0;
        if (db >= 0f) return 1.0;
        return (db + 60.0) / 60.0;
    }

    private void RedrawScale()
    {
        ScaleCanvas.Children.Clear();
        double height = TrackLeft.Bounds.Height;
        if (height <= 20) return;

        // Keep the full-height gradient bars synced to actual track height
        GradientBarLeft.Height = height;
        GradientBarRight.Height = height;

        // Calibrated scale marks (dBFS)
        float[] marks = new float[] { 0f, -6f, -12f, -18f, -24f, -36f, -48f, -60f };

        foreach (float db in marks)
        {
            double norm = DbToNormalized(db);
            double y = (1.0 - norm) * height;

            Color markColor = db switch
            {
                0f => Color.FromRgb(239, 68, 68),
                >= -6f => Color.FromRgb(245, 158, 11),
                >= -12f => Color.FromRgb(234, 179, 8),
                >= -36f => Color.FromRgb(34, 197, 94),
                _ => Color.FromRgb(113, 113, 122)
            };

            var tb = new TextBlock
            {
                Text = db == 0f ? "0" : $"{db:0}",
                Foreground = new SolidColorBrush(markColor),
                FontSize = 9,
                FontFamily = new FontFamily("Consolas, monospace"),
                FontWeight = FontWeight.SemiBold,
                Width = 24,
                TextAlignment = TextAlignment.Right
            };

            Canvas.SetLeft(tb, 0);
            Canvas.SetTop(tb, Math.Max(0, Math.Min(height - 12, y - 6)));
            ScaleCanvas.Children.Add(tb);

            var tick = new Rectangle
            {
                Width = 4,
                Height = 1,
                Fill = new SolidColorBrush(markColor),
                Opacity = 0.8
            };
            Canvas.SetLeft(tick, 26);
            Canvas.SetTop(tick, Math.Max(0, Math.Min(height - 1, y)));
            ScaleCanvas.Children.Add(tick);
        }
    }

    public void UpdateLevels(AudioLevelData? levelData)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (levelData == null || levelData.Channels.Length == 0)
            {
                ResetMeters();
                return;
            }

            double trackHeight = TrackLeft.Bounds.Height;
            if (trackHeight <= 0) return;

            // Channel 0 (Left)
            var chL = levelData.Channels.Length > 0 ? levelData.Channels[0] : null;
            if (chL != null)
            {
                double normPeak = DbToNormalized(chL.PeakDb);
                BarClipperLeft.Height = Math.Max(0, normPeak * trackHeight);

                if (chL.PeakDb > _peakHoldLeft)
                    _peakHoldLeft = chL.PeakDb;
                else
                    _peakHoldLeft = Math.Max(-60f, _peakHoldLeft - 0.75f);

                double normHold = DbToNormalized(_peakHoldLeft);
                PeakLineLeft.Margin = new Thickness(0, 0, 0, Math.Max(0, normHold * trackHeight));

                if (chL.IsClipping || chL.PeakDb >= -0.1f) _lastClipLeft = DateTime.UtcNow;
                bool isClipActive = (DateTime.UtcNow - _lastClipLeft).TotalMilliseconds < 500;
                ClipLeft.Background = new SolidColorBrush(isClipActive ? Color.FromRgb(239, 68, 68) : Color.FromRgb(63, 63, 70));
            }

            // Channel 1 (Right)
            var chR = levelData.Channels.Length > 1 ? levelData.Channels[1] : chL;
            if (chR != null)
            {
                double normPeak = DbToNormalized(chR.PeakDb);
                BarClipperRight.Height = Math.Max(0, normPeak * trackHeight);

                if (chR.PeakDb > _peakHoldRight)
                    _peakHoldRight = chR.PeakDb;
                else
                    _peakHoldRight = Math.Max(-60f, _peakHoldRight - 0.75f);

                double normHold = DbToNormalized(_peakHoldRight);
                PeakLineRight.Margin = new Thickness(0, 0, 0, Math.Max(0, normHold * trackHeight));

                if (chR.IsClipping || chR.PeakDb >= -0.1f) _lastClipRight = DateTime.UtcNow;
                bool isClipActive = (DateTime.UtcNow - _lastClipRight).TotalMilliseconds < 500;
                ClipRight.Background = new SolidColorBrush(isClipActive ? Color.FromRgb(239, 68, 68) : Color.FromRgb(63, 63, 70));
            }

            float maxCurrentPeak = Math.Max(chL?.PeakDb ?? -60f, chR?.PeakDb ?? -60f);
            TxtDbReadout.Text = maxCurrentPeak <= -59.5f ? "-inf dB" : $"{maxCurrentPeak:F1} dB";
        });
    }

    private void ResetMeters()
    {
        _peakHoldLeft = -60f;
        _peakHoldRight = -60f;
        BarClipperLeft.Height = 0;
        BarClipperRight.Height = 0;
        PeakLineLeft.Margin = new Thickness(0);
        PeakLineRight.Margin = new Thickness(0);
        TxtDbReadout.Text = "-inf dB";
        ClipLeft.Background = new SolidColorBrush(Color.FromRgb(63, 63, 70));
        ClipRight.Background = new SolidColorBrush(Color.FromRgb(63, 63, 70));
    }

    private void BtnMonitor_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        bool isChecked = BtnMonitor.IsChecked == true;
        BtnMonitor.Background = new SolidColorBrush(isChecked ? Color.FromRgb(34, 197, 94) : Color.FromRgb(39, 39, 42));
        BtnMonitor.Foreground = new SolidColorBrush(isChecked ? Colors.Black : Color.FromRgb(228, 228, 231));
        MonitoringToggled?.Invoke(isChecked);
    }
}
