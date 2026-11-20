using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;

namespace OmtCaptureStudio.Tests;

partial class Program
{
    static void TestMonitorIdentity()
    {
        Console.WriteLine("\n[Step 0.707] Verifying multi-adapter monitor identity and legacy index mapping...");

        const long intel = 0x00000001_0000ABCD;
        const long nvidia = 0x00000002_0000DEF0;
        var outputs = new MonitorDescriptor[]
        {
            new()
            {
                AdapterLuid = intel, AdapterName = "Intel(R) UHD Graphics", IsDefaultAdapter = true,
                OutputIndex = 0, DeviceName = @"\\.\DISPLAY1", Width = 1920, Height = 1080
            },
            new()
            {
                AdapterLuid = intel, AdapterName = "Intel(R) UHD Graphics", IsDefaultAdapter = true,
                OutputIndex = 1, DeviceName = @"\\.\DISPLAY2", Width = 1920, Height = 1200, Left = 1920
            },
            new()
            {
                AdapterLuid = nvidia, AdapterName = "NVIDIA GeForce RTX 3080", IsDefaultAdapter = false,
                OutputIndex = 0, DeviceName = @"\\.\DISPLAY3", Width = 2560, Height = 1440, Left = 3840
            }
        };

        IReadOnlyList<CaptureTarget> targets = MonitorIdentity.ToTargets(outputs);
        Expect(targets.Count == 3, "Every adapter's outputs must be listed.");
        Expect(targets[0].IsDefaultAdapter && targets[0].MonitorIndex == 0 && targets[0].OutputIndex == 0,
            "The default adapter's first output keeps bare index 0.");
        Expect(targets[1].MonitorIndex == 1 && targets[1].OutputDeviceName == @"\\.\DISPLAY2",
            "The default adapter's second output keeps bare index 1.");
        Expect(targets[2].MonitorIndex == -1 && targets[2].AdapterLuid == nvidia && targets[2].OutputIndex == 0,
            "A second GPU's output 0 must not reuse bare index 0.");
        Expect(targets[0].DisplayName.Contains("Intel", StringComparison.Ordinal)
               && targets[2].DisplayName.Contains("NVIDIA", StringComparison.Ordinal),
            "When two adapters have outputs, each label must name its GPU.");

        CaptureTarget? legacy0 = MonitorIdentity.ResolveLegacyIndex(0, targets);
        CaptureTarget? legacy1 = MonitorIdentity.ResolveLegacyIndex(1, targets);
        CaptureTarget? legacy2 = MonitorIdentity.ResolveLegacyIndex(2, targets);
        Expect(legacy0 != null && MonitorIdentity.SameTarget(legacy0, targets[0]),
            "Saved index 0 must be the default adapter's first output.");
        Expect(legacy1 != null && MonitorIdentity.SameTarget(legacy1, targets[1]),
            "Saved index 1 must be the default adapter's second output.");
        Expect(legacy2 == null,
            "Saved index 2 must NOT become the second GPU's first output. That index did not exist before.");

        var bare = new CaptureTarget { Kind = CaptureTargetKind.Monitor, MonitorIndex = 0 };
        Expect(MonitorIdentity.SameTarget(targets[0], bare),
            "An old index-only selection must still match the default adapter's output 0.");
        Expect(!MonitorIdentity.SameTarget(targets[2], bare),
            "Output 0 on the other GPU must not match a saved bare index of 0.");
        Expect(MonitorIdentity.SameTarget(targets[2], targets[2]),
            "A monitor matches itself by device name.");
        Expect(!MonitorIdentity.SameTarget(targets[0], targets[2]),
            "Two outputs that both have output index 0 on different adapters are different monitors.");

        MonitorDescriptor? foundLegacy = MonitorIdentity.Find(outputs, MonitorRef.Legacy(1));
        Expect(foundLegacy?.DeviceName == @"\\.\DISPLAY2",
            "A legacy ref must open the default adapter's output, which is what device reset recreates.");
        MonitorDescriptor? foundByName = MonitorIdentity.Find(outputs, new MonitorRef
        {
            AdapterLuid = nvidia,
            OutputIndex = 5,
            DeviceName = @"\\.\DISPLAY3"
        });
        Expect(foundByName?.AdapterLuid == nvidia && foundByName?.OutputIndex == 0,
            "After a reset, the device name must win over a stale output index.");
        Expect(MonitorIdentity.Find(outputs, MonitorRef.Legacy(2)) == null,
            "A legacy index past the default adapter's outputs must miss, not wrap onto the next GPU.");

        CaptureTarget? savedLegacy = MonitorIdentity.MatchSaved(targets, null, null, null, legacyIndex: 1);
        Expect(savedLegacy != null && savedLegacy.OutputDeviceName == @"\\.\DISPLAY2",
            "A settings file that only stored the integer index must map onto the new identity.");
        CaptureTarget? savedGpu = MonitorIdentity.MatchSaved(
            targets, nvidia, 0, @"\\.\DISPLAY3", legacyIndex: 0);
        Expect(savedGpu != null && savedGpu.AdapterLuid == nvidia,
            "A stored adapter LUID must win over a stale bare index of 0.");

        string root = Path.Combine(Path.GetTempPath(), "omt-mon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, """
                {
                  "Version": 2,
                  "SelectedMonitorIndex": 1
                }
                """);
            OperatorSettings loaded = new SettingsStore(path).Load();
            Expect(loaded.SelectedMonitorIndex == 1, "A version 2 file's bare monitor index must still load.");
            CaptureTarget? fromFile = MonitorIdentity.MatchSaved(
                targets,
                loaded.SelectedMonitorAdapterLuid,
                loaded.SelectedMonitorOutputIndex,
                loaded.SelectedMonitorDeviceName,
                loaded.SelectedMonitorIndex);
            Expect(fromFile != null && string.Equals(fromFile.OutputDeviceName, @"\\.\DISPLAY2", StringComparison.OrdinalIgnoreCase),
                "Loading that file must resolve index 1 to DISPLAY2, not to another GPU.");

            var store = new SettingsStore(Path.Combine(root, "round.json"));
            Expect(store.Save(SettingsStore.FromConfig(new RecordingConfig(), "Custom", selectedMonitor: targets[2])),
                "Saving a second-GPU monitor must succeed.");
            OperatorSettings round = new SettingsStore(Path.Combine(root, "round.json")).Load();
            Expect(round.SelectedMonitorAdapterLuid == nvidia && round.SelectedMonitorDeviceName == @"\\.\DISPLAY3",
                "The saved identity must be the adapter LUID and the device name.");
            Expect(round.SelectedMonitorIndex is null,
                "A second-GPU monitor must not write a bare index an older build would apply to the default adapter.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        string? capturerPath = FindRepoSource(Path.Combine("OmtCaptureStudio", "Services", "DesktopFrameCapturer.cs"));
        if (capturerPath != null)
        {
            string capturer = File.ReadAllText(capturerPath);
            Expect(!capturer.Contains("DriverType.Hardware", StringComparison.Ordinal),
                "Device creation, including the device-reset path, must target the output's adapter rather than the default hardware device.");
            Expect(capturer.Contains("TryRecreateDevice", StringComparison.Ordinal)
                    && capturer.Contains("CreateDevice()", StringComparison.Ordinal),
                "ACCESS_LOST / device-reset recovery must still recreate the device.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Adapters stay distinct, a legacy index stays on the default adapter, and a reset follows the device name!");
        Console.ResetColor();
    }
}
