using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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
    }

    public static double DbToNormalized(float db)
    {
        // Broadcast standard dBFS scale (-60 dB to 0 dB)
        if (db <= -60f) return 0.0;
        if (db >= 0f) return 1.0;
        return (db + 60.0) / 60.0;
    }

    private void MeterAreaGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawScale();
    }

    private void RedrawScale()
    {
        ScaleCanvas.Children.Clear();
        double height = TrackLeft.ActualHeight;
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

            // Color code labels matching the calibrated broadcast zones:
            // 0 dB: Red
            // -6 dB: Orange/Amber
            // -12 dB: Yellow
            // -18 to -36 dB: Green
            // -48 to -60 dB: Slate Gray
            Color markColor = db switch
            {
                0f => Color.FromRgb(239, 68, 68),
                >= -6f => Color.FromRgb(245, 158, 11),
                >= -12f => Color.FromRgb(234, 179, 8),
                >= -36f => Color.FromRgb(34, 197, 94),
                _ => Color.FromRgb(113, 113, 122)
            };

            // Number Text Label
            var tb = new TextBlock
            {
                Text = db == 0f ? "0" : $"{db:0}",
                Foreground = new SolidColorBrush(markColor),
                FontSize = 9,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Width = 24,
                TextAlignment = TextAlignment.Right
            };

            Canvas.SetLeft(tb, 0);
            Canvas.SetTop(tb, Math.Max(0, Math.Min(height - 12, y - 6)));
            ScaleCanvas.Children.Add(tb);

            // Tick mark line pointing directly into the meter bar track
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
        Dispatcher.InvokeAsync(() =>
        {
            if (levelData == null || levelData.Channels.Length == 0)
            {
                ResetMeters();
                return;
            }

            double trackHeight = TrackLeft.ActualHeight;
            if (trackHeight <= 0) return;

            // Channel 0 (Left)
            var chL = levelData.Channels.Length > 0 ? levelData.Channels[0] : null;
            if (chL != null)
            {
                // Main bar reveals calibrated gradient up to current Peak level
                double normPeak = DbToNormalized(chL.PeakDb);
                BarClipperLeft.Height = Math.Max(0, normPeak * trackHeight);

                // Peak hold decay
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

            // Numerical Peak Readout matches the bar top
            float maxCurrentPeak = Math.Max(chL?.PeakDb ?? -60f, chR?.PeakDb ?? -60f);
            TxtDbReadout.Text = maxCurrentPeak <= -59.5f ? "-inf dB" : $"{maxCurrentPeak:F1} dB";
        });
    }

    private void ResetMeters()
    {
        BarClipperLeft.Height = 0;
        BarClipperRight.Height = 0;
        PeakLineLeft.Margin = new Thickness(0);
        PeakLineRight.Margin = new Thickness(0);
        TxtDbReadout.Text = "-inf dB";
        ClipLeft.Background = new SolidColorBrush(Color.FromRgb(63, 63, 70));
        ClipRight.Background = new SolidColorBrush(Color.FromRgb(63, 63, 70));
    }

    private void BtnMonitor_Click(object sender, RoutedEventArgs e)
    {
        bool isChecked = BtnMonitor.IsChecked ?? false;
        BtnMonitor.Background = new SolidColorBrush(isChecked ? Color.FromRgb(34, 197, 94) : Color.FromRgb(39, 39, 42));
        BtnMonitor.Foreground = new SolidColorBrush(isChecked ? Colors.Black : Color.FromRgb(228, 228, 231));
        MonitoringToggled?.Invoke(isChecked);
    }
}
