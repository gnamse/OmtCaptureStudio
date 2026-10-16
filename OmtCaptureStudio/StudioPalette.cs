using Avalonia.Media;

namespace OmtCaptureStudio;

/// <summary>
/// Single source of truth for runtime (code-behind / ViewModel) brushes.
/// Static so ViewModels and controls can construct without a live Avalonia
/// Application (headless automated tests construct them from a plain console).
/// Colors intentionally mirror the Studio* tokens in App.axaml; the two layers
/// cover different lookup contexts (declarative XAML vs imperative code).
/// </summary>
public static class StudioPalette
{
    // --- Action / State Brushes ---
    public static readonly IBrush Connect = Solid(16, 185, 129);            // StudioAccentSuccess
    public static readonly IBrush Disconnect = Solid(220, 38, 38);          // StudioAccentDanger (#DC2626, >= 4.83:1 with white)
    public static readonly IBrush TestSignalActive = Solid(79, 70, 229);    // StudioAccentPrimary (#4F46E5, >= 5.9:1 with white)
    public static readonly IBrush TestSignalInactive = Solid(39, 39, 42);   // StudioSurfaceElevated
    public static readonly IBrush RecordStart = Solid(220, 38, 38);         // StudioAccentDanger
    public static readonly IBrush RecordStop = Solid(185, 28, 28);

    // --- Live / Status Indicators ---
    public static readonly IBrush LiveActive = Solid(34, 197, 94);
    public static readonly IBrush LiveInactive = Solid(128, 128, 138);      // StudioTextMuted (#80808A)

    // --- VideoViewport ---
    public static readonly IBrush Backdrop = Solid(15, 15, 18);              // matches StudioBg #0F0F12 letterbox

    // --- VU Meter Scale ---
    public static readonly IBrush MeterDanger = Solid(239, 68, 68);
    public static readonly IBrush MeterWarnHigh = Solid(245, 158, 11);
    public static readonly IBrush MeterWarnMid = Solid(234, 179, 8);
    public static readonly IBrush MeterNormal = Solid(34, 197, 94);
    public static readonly IBrush MeterMuted = Solid(128, 128, 138);        // StudioTextMuted (#80808A)
    public static readonly IBrush ClipInactive = Solid(63, 63, 70);
    public static readonly IBrush ClipActive = Solid(185, 28, 28);          // #B91C1C (>= 6.4:1 contrast with white)
    public static readonly IBrush MonitorActive = Solid(34, 197, 94);
    public static readonly IBrush MonitorInactive = Solid(39, 39, 42);
    public static readonly IBrush MonitorTextActive = new SolidColorBrush(Colors.Black);
    public static readonly IBrush MonitorTextInactive = Solid(228, 228, 231);
    public static readonly IBrush ButtonDarkForeground = new SolidColorBrush(Colors.Black);
    public static readonly IBrush ButtonLightForeground = Solid(244, 244, 245); // StudioTextPrimary
    public static readonly IBrush ButtonWhiteForeground = new SolidColorBrush(Colors.White);

    private static IBrush Solid(byte r, byte g, byte b)
        => new SolidColorBrush(Color.FromRgb(r, g, b));
}
