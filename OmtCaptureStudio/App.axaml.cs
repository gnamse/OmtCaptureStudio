using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.ViewModels;

namespace OmtCaptureStudio;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // NET-009: build the engine stack explicitly instead of the shorthand `new MainWindow()`.
            // MainWindowViewModel's own parameterless constructor does exactly this internally
            // (CaptureSession + OmtDiscoveryService, ownsDependencies: true) - the only difference is
            // that we keep the reference, because crash forensics has to read the LIVE recording state
            // when a native fault kills the process. Nothing else about composition changes.
            var session = new CaptureSession();
            var viewModel = new MainWindowViewModel(session, new OmtDiscoveryService(), ownsDependencies: true);
            desktop.MainWindow = new MainWindow(viewModel);

            CrashForensics.RegisterStateProvider(() => CrashForensics.CaptureState(session, viewModel.BroadcastState));

            // NET-009: surface a dump left behind by the previous run, so a field operator knows to
            // attach it to a bug report.
            int pendingDumps = CrashForensics.ReportPendingDumps();
            if (pendingDumps > 0)
            {
                viewModel.StatusText = $"A crash dump was saved ({pendingDumps}) - see {CrashForensics.DumpDir}";
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
