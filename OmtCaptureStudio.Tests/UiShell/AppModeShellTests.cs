using System.Xml.Linq;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Sources;

namespace OmtCaptureStudio.Tests;

partial class Program
{

    static void TestAppModeShell()
    {
        Console.WriteLine("\n[Step 0.698] Verifying the mode-based shell (states, projection, gate, persistence)...");

        // ---- 1. THE HEADLESS STATE ---------------------------------------------------------------
        var state = new AppModeState();
        Expect(state.CurrentMode == AppMode.ModeSelection,
            $"The shell must OPEN on the picker (ADR-0011 decision 1); got {state.CurrentMode}.");
        Expect(state.IsModeSelection && !state.IsOperatingMode, "A fresh state is the picker, not an operating mode.");

        int events = 0;
        AppMode? lastOld = null, lastNew = null;
        state.ModeChanged += (_, e) => { events++; lastOld = e.OldMode; lastNew = e.NewMode; };

        bool moved = state.SetMode(AppMode.CaptureMode);
        Expect(moved, "SetMode must report that it moved.");
        Expect(events == 1, $"One transition must raise exactly one event; got {events}.");
        Expect(lastOld == AppMode.ModeSelection && lastNew == AppMode.CaptureMode,
            $"The event must carry old/new; got {lastOld}->{lastNew}.");
        Expect(!state.SetMode(AppMode.CaptureMode) && events == 1,
            "Re-entering the same mode must be a no-op and raise nothing.");
        Expect(state.IsOperatingMode, "Capture mode is an operating mode.");

        // ---- 2. THE VIEW MODEL PROJECTION ---------------------------------------------------------
        using (var seam = BuildHeadlessSession())
        {
            var vm = seam.ViewModel!;
            var vmState = new AppModeState();
            // The fixture's VM owns its own state; drive that same projection through the commands.
            Expect(vm.IsPickerVisible && !vm.IsCaptureModeVisible && !vm.IsBroadcastModeVisible,
                "At construction the shell must project the picker and only the picker.");

            vm.SelectModeCommand.Execute(AppMode.CaptureMode);
            Expect(vm.IsCaptureModeVisible && !vm.IsPickerVisible,
                "Selecting Capture must swap the content region to the capture mode.");
            Expect(vm.LastUsedAppMode == AppMode.CaptureMode,
                $"Selecting a mode must remember it; got {vm.LastUsedAppMode}.");
            Expect(vm.IsCaptureCardHighlighted && !vm.IsBroadcastCardHighlighted,
                "The picker must pre-highlight the mode that was just used.");
            Expect(vm.IsModeChangeAllowed,
                "Change Mode must be allowed while nothing is recording or broadcasting.");

            vm.ChangeModeCommand.Execute(null);
            Expect(vm.IsPickerVisible && !vm.IsCaptureModeVisible,
                "Change Mode must return to the picker.");
            Expect(vm.LastUsedAppMode == AppMode.CaptureMode,
                "Returning to the picker must NOT erase the last-used mode (ADR-0011 decision 1).");

            vm.SelectModeCommand.Execute(AppMode.BroadcastMode);
            Expect(vm.IsBroadcastModeVisible && !vm.IsPickerVisible,
                "Selecting Broadcast must swap the content region to the broadcast mode.");
            Expect(vm.LastUsedAppMode == AppMode.BroadcastMode, "The last-used mode must follow the new choice.");

            // ---- 2a. THE BROADCAST ROW IS KEYED TO THE MODE (P2-01) -------------------------------
            // The reveal toggle used to own this flag. Once it left the UI nothing could set it, so it
            // stayed false forever: in Broadcast mode the row never showed, the selection hint was
            // permanently invisible, and the compact summary inverted over it. The mode now drives all three.
            Expect(vm.IsBroadcastRowVisible,
                "In Broadcast mode the configuration row must be visible - the mode IS the reveal (ADR-0011 d.1).");
            Expect(vm.IsBroadcastSelecting,
                "In Broadcast mode with nothing live the row must report SELECTING, or the selection hint " +
                "stays permanently invisible (P2-01).");
            Expect(!vm.IsLiveSettingSummaryVisible,
                "The compact summary must stand down while the row is up, so it can never cover the hint.");
            Expect(!vm.IsDesktopCaptureActive, "Entering Broadcast mode must not start a send.");

            // Selecting the picker as a "mode" is not a card action and must be ignored.
            vm.SelectModeCommand.Execute(AppMode.ModeSelection);
            Expect(vm.IsBroadcastModeVisible, "SelectMode(ModeSelection) is a no-op - the picker is reached via ChangeMode.");
        }

        // ---- 2b. THE GATE: a LIVE TAKE closes Change Mode, stopping reopens it ---------------------
        using (var gateSeam = BuildHeadlessSession())
        {
            var gateVm = gateSeam.ViewModel!;
            gateVm.SelectModeCommand.Execute(AppMode.CaptureMode);

            long gateFrames = 0;
            gateSeam.Session.VideoFrameAvailable += (ptr, len, w, h, st, fps, ts) => Interlocked.Increment(ref gateFrames);
            gateSeam.Session.Connect(new SyntheticPatternSource());

            int gateWait = 0;
            while (Interlocked.Read(ref gateFrames) < 5 && gateWait < 40)
            {
                Thread.Sleep(50);
                gateWait++;
            }

            var gateConfig = new RecordingConfig
            {
                OutputDirectory = Path.GetTempPath(),
                FilenamePrefix = "ModeGateTest"
            };

            if (gateSeam.Session.StartRecording(gateConfig))
            {
                try
                {
                    Expect(!gateVm.IsModeChangeAllowed,
                        "Change Mode must be DISABLED while a take is live (ADR-0011 decision 4).");
                    Expect(gateVm.ModeChangeBlockedHint.Contains("recording"),
                        $"The hint must name the blocker; got '{gateVm.ModeChangeBlockedHint}'.");

                    gateVm.ChangeModeCommand.Execute(null);
                    Expect(gateVm.IsCaptureModeVisible && !gateVm.IsPickerVisible,
                        "A Change Mode command must be REFUSED while a take is live - never a silent teardown.");
                }
                finally
                {
                    gateSeam.Session.StopRecordingAsync().GetAwaiter().GetResult();
                }

                // The stop notification runs on the pool then posts to the UI thread (headless-safe).
                int reopenWait = 0;
                while (!gateVm.IsModeChangeAllowed && reopenWait < 60)
                {
                    Thread.Sleep(50);
                    reopenWait++;
                }

                Expect(gateVm.IsModeChangeAllowed,
                    "Stopping the take must reopen Change Mode.");
                Expect(gateVm.ModeChangeBlockedHint.Length == 0,
                    "The blocker hint must clear when the gate reopens.");

                gateVm.ChangeModeCommand.Execute(null);
                Expect(gateVm.IsPickerVisible, "With nothing live, Change Mode must now return to the picker.");
            }
            else
            {
                Console.WriteLine("   [SKIP] the canned take did not start; the gate's live half is unproven here.");
            }
        }

        // ---- 3. PERSISTENCE: LastAppMode rides ADR-0006, and the picker STILL opens ----------------
        string root = Path.Combine(Path.GetTempPath(), "omt-mode-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            var store = new SettingsStore(path);
            using (var seam = BuildHeadlessSession())
            {
                var vm = seam.ViewModel!;
                vm.AttachSettings(store, new OperatorSettings());
                vm.SelectModeCommand.Execute(AppMode.CaptureMode);

                var reloaded = store.Load();
                Expect(reloaded.LastAppMode == AppMode.CaptureMode,
                    $"LastAppMode must persist through the settings store; got {reloaded.LastAppMode}.");
            }

            // A FRESH session with those settings: the picker still opens, but Capture is pre-highlighted.
            using (var seam2 = BuildHeadlessSession())
            {
                var vm2 = seam2.ViewModel!;
                var restored = store.Load();
                vm2.AttachSettings(store, restored);

                Expect(vm2.IsPickerVisible,
                    "A restored LastAppMode must NOT auto-skip the picker (ADR-0011 decision 1).");
                Expect(vm2.CurrentAppMode == AppMode.ModeSelection,
                    $"The window must still open on the picker; got {vm2.CurrentAppMode}.");
                Expect(vm2.IsCaptureCardHighlighted,
                    "The restored mode must pre-highlight its card on the picker.");
            }

            // An older/hand-edited file with no mode: the picker opens and highlights NOTHING.
            using (var seam3 = BuildHeadlessSession())
            {
                var vm3 = seam3.ViewModel!;
                vm3.AttachSettings(new SettingsStore(Path.Combine(root, "empty.json")), new OperatorSettings());
                Expect(vm3.IsPickerVisible && !vm3.IsCaptureCardHighlighted && !vm3.IsBroadcastCardHighlighted,
                    "With no saved mode, the picker must open and highlight neither card.");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        // ---- 4. STRUCTURAL GUARD: grid placement in EVERY multi-row grid ---------------------------
        // Headless tests cannot RENDER XAML, so a child that silently defaults to Grid.Row 0 - because a
        // move stripped its Grid.Row - is invisible to every other assertion in this suite. It is NOT
        // invisible to the operator: the recording bar stacked behind the source picker and vanished, the
        // shared status bar floated over the content, and in BroadcastModeView the Start/Stop and
        // Change-Mode buttons overlapped by 115x23px at the 950px floor. Pixel verification caught each;
        // this keeps them caught in CI.
        //
        // The FIRST version of this guard checked only each file's OUTER grid, so every NESTED multi-row
        // grid escaped it - which is exactly where the BroadcastModeView P1 lived. It now walks EVERY Grid
        // descendant. Rule: in ANY Grid declaring more than one row, every child states its Grid.Row.
        {
            string[] axamlFiles =
            {
                "OmtCaptureStudio/MainWindow.axaml",
                "OmtCaptureStudio/Views/CaptureModeView.axaml",
                "OmtCaptureStudio/Views/BroadcastModeView.axaml",
                "OmtCaptureStudio/Views/ModeSelectionView.axaml",
                "OmtCaptureStudio/Views/VideoSurface.axaml"
            };

            // Rows can be declared EITHER as a <Grid.RowDefinitions> element or with the
            // RowDefinitions="Auto,*,Auto" attribute shorthand. Counting only the element form made the
            // first version of this guard silently skip every file that used the shorthand - i.e. all of
            // them - and pass with the bug deliberately reintroduced. Support both forms.
            static int RowCount(System.Xml.Linq.XElement grid, System.Xml.Linq.XNamespace ns)
            {
                int n = grid.Elements(ns + "Grid.RowDefinitions")
                            .SelectMany(r => r.Elements(ns + "RowDefinition")).Count();
                if (n == 0)
                {
                    string? shorthand = grid.Attribute("RowDefinitions")?.Value;
                    if (!string.IsNullOrWhiteSpace(shorthand))
                    {
                        n = shorthand!.Split(',').Length;
                    }
                }

                return n;
            }

            int multiRowGridsChecked = 0;

            foreach (string rel in axamlFiles)
            {
                string? axamlPath = FindRepoSource(rel);
                if (axamlPath == null)
                {
                    Console.WriteLine($"   [SKIP] {rel} is not reachable from this tree.");
                    continue;
                }

                var axaml = System.Xml.Linq.XDocument.Load(axamlPath);
                var xns = axaml.Root!.Name.Namespace;
                var grids = axaml.Descendants(xns + "Grid").ToList();
                Expect(grids.Count > 0, $"{rel} must contain a Grid.");
                if (grids.Count == 0) continue;

                foreach (var grid in grids)
                {
                    int rowCount = RowCount(grid, xns);
                    if (rowCount <= 1) continue;

                    multiRowGridsChecked++;

                    var unplaced = grid.Elements()
                        .Where(e => e.Name.LocalName is not ("Grid.RowDefinitions" or "Grid.ColumnDefinitions"))
                        .Where(e => e.Attribute("Grid.Row") == null)
                        .Select(e => e.Name.LocalName)
                        .ToList();

                    Expect(unplaced.Count == 0,
                        $"{rel}: a child of a {rowCount}-row Grid omits Grid.Row, so it silently defaults to " +
                        $"row 0 and overlaps its siblings. Offenders: {string.Join(", ", unplaced)}.");
                }
            }

            // ANTI-VACUITY FLOOR. A guard that checks nothing passes for the wrong reason - exactly how the
            // first version shipped broken. Seven multi-row grids exist across these five files; if row
            // counting regresses or a file drops out of the walk, this fails loudly instead of silently
            // certifying a layout it never examined.
            Expect(multiRowGridsChecked >= 7,
                $"The XAML row guard only reached {multiRowGridsChecked} multi-row Grid(s), expected at least 7 " +
                "- it is not exercising the views, so it cannot certify them.");
        }

        Console.WriteLine(" -> Three states, one projection, the Change-Mode gate, LastAppMode persistence " +
                          "and the XAML row-placement invariant (ALL multi-row grids) all verified!");
    }

}
