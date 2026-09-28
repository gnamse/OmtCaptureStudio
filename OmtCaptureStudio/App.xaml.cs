using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace OmtCaptureStudio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ensure Windows searches the application root directory for native libomt.dll and libvmx.dll
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        SetDllDirectory(baseDir);
    }
}
