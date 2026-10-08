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
    private static readonly float[] ScaleMarks = { 0f, -6f, -12f, -18f, -24f, -36f, -48f, -60f };
    private static readonly IBrush BrushDanger = StudioPalette.MeterDanger;
    private static readonly IBrush BrushWarnHigh = StudioPalette.MeterWarnHigh;
    private static readonly IBrush BrushWarnMid = StudioPalette.MeterWarnMid;
    private static readonly IBrush BrushNormal = StudioPalette.MeterNormal;
    private static readonly IBrush BrushMuted = StudioPalette.MeterMuted;
    private static readonly IBrush BrushClipInactive = StudioPalette.ClipInactive;
    private static readonly IBrush BrushClipActive = StudioPalette.ClipActive;
    private static readonly IBrush BrushMonitorActive = StudioPalette.MonitorActive;
    private static readonly IBrush BrushMonitorInactive = StudioPalette.MonitorInactive;
    private static readonly IBrush BrushMonitorTextActive = StudioPalette.MonitorTextActive;
    private static readonly IBrush BrushMonitorTextInactive = StudioPalette.MonitorTextInactive;
    private static readonly FontFamily MonoFontFamily = new FontFamily("Consolas, monospace");

    private readonly TextBlock[] _scaleLabels;
    private readonly Rectangle[] _scaleTicks;

    private float _peakHoldLeft = -60f;
    private float _peakHoldRight = -60f;
    private DateTime _lastClipLeft = DateTime.MinValue;
    private DateTime _lastClipRight = DateTime.MinValue;
    private long _lastMeterTick;

    public event Action<bool>? MonitoringToggled;

    public VuMeterControl()
    {
        InitializeComponent();

        _scaleLabels = new TextBlock[ScaleMarks.Length];
        _scaleTicks = new Rectangle[ScaleMarks.Length];
        InitializeScaleElements();

        MeterAreaGrid.SizeChanged += (s, e) => RedrawScale();
        TrackLeft.SizeChanged += (s, e) => RedrawScale();
        Loaded += (s, e) => RedrawScale();
        BtnMonitor.IsCheckedChanged += BtnMonitor_IsCheckedChanged;
    }

    public static double DbToNormalized(float db)
    {
        // Broadcast standard dBFS scale (-60 dB to 0 dB)
        if (db <= -60f) return 0.0;
        if (db >= 0f) return 1.0;
        return (db + 60.0) / 60.0;
    }

    private void InitializeScaleElements()
    {
        for (int i = 0; i < ScaleMarks.Length; i++)
        {
            float db = ScaleMarks[i];
            IBrush markBrush = db switch
            {
                0f => BrushDanger,
                >= -6f => BrushWarnHigh,
                >= -12f => BrushWarnMid,
                >= -36f => BrushNormal,
                _ => BrushMuted
            };

            var tb = new TextBlock
            {
                Text = db == 0f ? "0" : $"{db:0}",
                Foreground = markBrush,
                FontSize = 9,
                FontFamily = MonoFontFamily,
                FontWeight = FontWeight.SemiBold,
                Width = 24,
                TextAlignment = TextAlignment.Right
            };
            Canvas.SetLeft(tb, 0);
            ScaleCanvas.Children.Add(tb);
            _scaleLabels[i] = tb;

            var tick = new Rectangle
            {
                Width = 4,
                Height = 1,
                Fill = markBrush,
                Opacity = 0.8
            };
            Canvas.SetLeft(tick, 26);
            ScaleCanvas.Children.Add(tick);
            _scaleTicks[i] = tick;
        }
    }

    private void RedrawScale()
    {
        double height = TrackLeft.Bounds.Height;
        if (height <= 20)
        {
            ScaleCanvas.IsVisible = false;
            return;
        }

        ScaleCanvas.IsVisible = true;

        // Keep the full-height gradient bars synced to actual track height
        GradientBarLeft.Height = height;
        GradientBarRight.Height = height;

        // Zero-allocation positioning of pooled scale marks
        for (int i = 0; i < ScaleMarks.Length; i++)
        {
            double norm = DbToNormalized(ScaleMarks[i]);
            double y = (1.0 - norm) * height;

            Canvas.SetTop(_scaleLabels[i], Math.Max(0, Math.Min(height - 12, y - 6)));
            Canvas.SetTop(_scaleTicks[i], Math.Max(0, Math.Min(height - 1, y)));
        }
    }

    public void UpdateLevels(AudioLevelData? levelData)
    {
        long now = Environment.TickCount64;
        if (now - _lastMeterTick < 25) return; // Cap meter UI refresh rate to ~40 fps
        _lastMeterTick = now;

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
                ClipLeft.Background = isClipActive ? BrushClipActive : BrushClipInactive;
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
                ClipRight.Background = isClipActive ? BrushClipActive : BrushClipInactive;
            }

            float maxCurrentPeak = Math.Max(chL?.PeakDb ?? -60f, chR?.PeakDb ?? -60f);
            TxtDbReadout.Text = maxCurrentPeak <= -59.5f ? "-inf dB" : $"{maxCurrentPeak:F1} dB";
        });
    }

    public void ResetMeters()
    {
        _peakHoldLeft = -60f;
        _peakHoldRight = -60f;
        BarClipperLeft.Height = 0;
        BarClipperRight.Height = 0;
        PeakLineLeft.Margin = new Thickness(0);
        PeakLineRight.Margin = new Thickness(0);
        TxtDbReadout.Text = "-inf dB";
        ClipLeft.Background = BrushClipInactive;
        ClipRight.Background = BrushClipInactive;
    }

    private void BtnMonitor_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        bool isChecked = BtnMonitor.IsChecked == true;
        BtnMonitor.Background = isChecked ? BrushMonitorActive : BrushMonitorInactive;
        BtnMonitor.Foreground = isChecked ? BrushMonitorTextActive : BrushMonitorTextInactive;
        MonitoringToggled?.Invoke(isChecked);
    }
}
