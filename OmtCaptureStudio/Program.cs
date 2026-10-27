using System;
using System.Runtime.InteropServices;
using Avalonia;

using OmtCaptureStudio.Services.Forensics;
namespace OmtCaptureStudio;

public static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // NET-009: arm crash forensics FIRST, before anything that can fault. The app's riskiest code is
        // direct COM vtable interop (ASIO/WASAPI/XAudio2/D3D11/DXGI) and under Native AOT a fault is a
        // hard native crash with no .NET crash dialog - a handler installed later is a handler that
        // misses the crash it exists for.
        CrashForensics.Install();

        try
        {
            // Ensure all native C++ runtimes and codecs are ready (extracts from single-exe if needed)
            NativePayloadBootstrapper.Initialize();

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // A fault escaping the desktop lifetime - window construction, the first frame, teardown -
            // would otherwise just make the process disappear. Record it, then let it propagate so the
            // exit code stays non-zero for scripts and the installer.
            CrashForensics.HandleException("Program.Main", ex);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
