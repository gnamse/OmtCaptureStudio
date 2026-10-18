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

            // ADR-0006: restore the operator's settings BEFORE the window binds, so the first render is
            // already the restored configuration rather than flashing defaults. Load never throws; a
            // corrupt file is quarantined and defaults stand.
            var settingsStore = new SettingsStore(
                SettingsStore.DefaultPath,
                logInfo: AppLogger.LogInfo,
                logError: AppLogger.LogError);

            OperatorSettings restoredSettings = settingsStore.Load();
            viewModel.AttachSettings(settingsStore, restoredSettings);

            switch (settingsStore.LastOutcome)
            {
                case SettingsLoadOutcome.Loaded:
                    AppLogger.LogInfo(
                        $"[settings] restored from {settingsStore.Path} " +
                        $"(destination '{restoredSettings.OutputDirectory ?? "<default>"}', " +
                        $"preset '{restoredSettings.SelectedPreset ?? "<none>"}', " +
                        $"container {restoredSettings.ContainerFormat ?? "<default>"}, " +
                        $"encoder {restoredSettings.EncoderChoice ?? "<default>"}, " +
                        $"quality {restoredSettings.Quality ?? "<default>"})");
                    break;

                case SettingsLoadOutcome.Corrupt:
                    viewModel.StatusText = "Settings file was unreadable - defaults restored (previous file kept as .corrupt)";
                    break;

                case SettingsLoadOutcome.Unreadable:
                    viewModel.StatusText = "Settings file could not be read - defaults restored";
                    break;
            }

            // NET-010: prove ffmpeg RUNS, once, at startup. FFmpeg is the recording feature's only
            // external dependency and nothing validated it before now - a missing binary used to surface
            // as a transport error the first time somebody pressed Record. Cheap (~tens of ms) and
            // bounded, so it cannot delay the window.
            viewModel.ApplyFfmpegPreflight(FfmpegPreflight.Run(AppContext.BaseDirectory));

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
