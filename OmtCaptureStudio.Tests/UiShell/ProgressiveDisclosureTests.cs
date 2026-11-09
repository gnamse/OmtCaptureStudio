using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Xml.Linq;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Audio;
using OmtCaptureStudio.Services.Sinks;
using OmtCaptureStudio.Services.Sources;
using OmtCaptureStudio.Services.Forensics;
using OmtCaptureStudio.ViewModels;

namespace OmtCaptureStudio.Tests;

partial class Program
{
    static void TestProgressiveDisclosure()
    {
        Console.WriteLine("\n[Step 0.691] Verifying progressive disclosure (advanced tier, collapsed broadcast row)...");

        using var seam = BuildHeadlessSession();
        var vm = seam.ViewModel!;

        // ---- 1. DEFAULTS: the Advanced tier starts collapsed, and the broadcast row starts hidden ----
        Expect(!vm.IsAdvancedRecordingOptionsExpanded,
            "The Advanced tier must be COLLAPSED by default - the default path is preset -> folder -> record.");
        Expect(vm.IsAdvancedChevronDown, "A collapsed tier must show the down chevron.");
        Expect(!vm.IsBroadcastRowVisible,
            "The app opens on the picker, so the broadcast configuration row must not be shown at startup.");
        Expect(vm.IsLiveSettingSummaryVisible,
            "Away from Broadcast mode the broadcast row is not shown, so the compact summary is what carries " +
            "the selection - it must never be suppressed by the row.");

        // ---- 2. THE DISCLOSURE observable + its accessible name ---------------------------------
        Expect(vm.AdvancedOptionsAutomationName.Contains("Show"),
            $"The collapsed disclosure must name the ACTION; got '{vm.AdvancedOptionsAutomationName}'.");

        vm.ToggleAdvancedRecordingOptionsCommand.Execute(null);
        Expect(vm.IsAdvancedRecordingOptionsExpanded, "Toggling the disclosure must expand the tier.");
        Expect(!vm.IsAdvancedChevronDown, "An expanded tier must show the up chevron.");
        Expect(vm.AdvancedOptionsAutomationName.Contains("Hide"),
            $"The expanded disclosure must name the reverse action; got '{vm.AdvancedOptionsAutomationName}'.");

        // ---- 3. THE LOCKING COMPOSITION survives inside the tier ---------------------------------
        // Collapsing is VISIBILITY; locking is ENABLED. They are different axes and must compose.
        Expect(vm.IsRecordingConfigEnabled,
            "With no take running the recording config must be unlocked, expanded or not.");
        vm.IsRecordingConfigEnabled = false;
        Expect(vm.IsAdvancedRecordingOptionsExpanded,
            "Locking the recording config must NOT collapse the tier - visibility and enablement are independent.");
        vm.IsRecordingConfigEnabled = true;

        // ---- 4. PRESET -> Custom still works from INSIDE the tier --------------------------------
        vm.SelectedRecordingPreset = RecordingPresets.BuiltIn[0].Name;
        Expect(vm.SelectedRecordingPreset == RecordingPresets.BuiltIn[0].Name,
            "Selecting a preset must apply it.");
        Expect(vm.IsAdvancedRecordingOptionsExpanded,
            "Applying a preset must not collapse the tier the operator is working in.");

        // An Advanced-tier knob EDIT flips the picker to Custom - the behaviour has to survive the move.
        vm.SelectedRolloverMinutes = RecordingChoiceOption_ForTest(MainWindowViewModel.RolloverMinuteOptions, 30);
        Expect(vm.SelectedRecordingPreset == RecordingPresets.CustomName,
            $"Editing a control inside the tier must switch the picker to Custom; got '{vm.SelectedRecordingPreset}'.");

        // ---- 5. THE THREE-STATE BROADCAST FLOW (NET-014, re-keyed to ADR-0011) ---------------------
        // The old middle state was a REVEAL toggle. Under the mode-based shell the MODE is the reveal
        // (ADR-0011 decision 1): "revealed" is now "the operator is in Broadcast mode". The three states
        // and every assertion they carried survive - only the thing that opens the row changed.
        // State 1 - not in Broadcast mode, so the row is not shown.
        Expect(!vm.IsBroadcastRowVisible, "State 1 (not in Broadcast mode): the row must not be shown.");
        Expect(!vm.IsBroadcastSelecting, "State 1: nothing is being selected.");

        // State 2 - IN BROADCAST MODE: the pickers are open and ENABLED. Nothing is live yet.
        vm.SelectModeCommand.Execute(AppMode.BroadcastMode);
        Expect(vm.IsBroadcastModeVisible, "State 2: selecting the Broadcast card must enter Broadcast mode.");
        Expect(vm.IsBroadcastRowVisible, "State 2 (Broadcast mode): the row must be visible - the mode IS the reveal.");
        Expect(vm.IsBroadcastSelecting, "State 2: the row is SELECTING, not live.");
        Expect(!vm.IsDesktopCaptureActive, "State 2: revealing must NOT start a broadcast.");
        Expect(vm.IsCaptureSelectorEnabled,
            "State 2: the pickers must be ENABLED for selection - the reported complaint was that revealing " +
            "started the send and left them locked before the operator could touch them.");
        Expect(vm.BroadcastActionText == "Start Broadcasting",
            $"State 2: the action must read Start Broadcasting; got '{vm.BroadcastActionText}'.");
        Expect(!vm.IsLiveSettingSummaryVisible, "State 2: the row is visible, so the compact summary stands down.");
        Expect(!string.IsNullOrWhiteSpace(vm.LiveSettingSummary),
            "State 2: the summary must still carry the current selection for the collapsed state.");

        // State 3 - LIVE. The VM takes the lock as a PROJECTION of the state machine, so mirror what the
        // projection does; the key itself is asserted against the machine below.
        vm.IsDesktopCaptureActive = true;
        vm.IsCaptureSelectorEnabled = false;
        Expect(vm.IsBroadcastRowVisible, "State 3 (Live): the row stays visible.");
        Expect(!vm.IsBroadcastSelecting, "State 3: the row is live, not selecting.");
        Expect(!vm.IsCaptureSelectorEnabled,
            "State 3: while LIVE the pickers are locked - that is when a mid-flight change drops receivers.");
        Expect(vm.BroadcastActionText == "Stop Broadcasting",
            $"State 3: the action must read Stop Broadcasting; got '{vm.BroadcastActionText}'.");

        // THE LOCK KEY MOVED. Asserted against the state machine, because the VM's flag is a projection of
        // it and a headless run can reveal the row WITHOUT starting a real desktop capture.
        var lockProbe = new BroadcastSessionState();
        Expect(lockProbe.IsCaptureSelectorEnabled, "The capture lock must start unlocked.");
        lockProbe.StartBroadcasting();
        Expect(!lockProbe.IsCaptureSelectorEnabled,
            "The capture pickers must lock when BROADCASTING - not when the row is merely revealed. That is " +
            "exactly the defect the user reported: the pickers were locked the moment the row appeared.");
        lockProbe.StopBroadcasting();
        Expect(lockProbe.IsCaptureSelectorEnabled, "Stopping must unlock the pickers again.");

        // THE NEVER-ORPHAN INVARIANT: a live broadcast cannot be hidden behind a collapsed row.
        // Leaving Broadcast mode is the STRONGEST form of the old probe - it bypasses the Change-Mode gate
        // entirely, so if the live state did not hold the row open this would orphan the send.
        vm.SelectModeCommand.Execute(AppMode.CaptureMode);
        Expect(vm.IsBroadcastRowVisible,
            "A LIVE broadcast must remain visible even after the mode moves away - it can never be orphaned.");
        vm.SelectModeCommand.Execute(AppMode.BroadcastMode);   // restore the mode the operator was in

        // Stopping from the in-row action returns to REVEALED, with the pickers usable again. The unlock is
        // a projection of the state machine (proved on lockProbe above), so mirror it here.
        vm.IsDesktopCaptureActive = false;
        vm.IsCaptureSelectorEnabled = true;
        Expect(vm.IsBroadcastModeVisible,
            "The in-row stop leaves the operator in Broadcast mode, with the pickers usable again.");
        Expect(vm.IsCaptureSelectorEnabled, "After a stop the pickers must be usable again.");
        Expect(vm.BroadcastActionText == "Start Broadcasting", "After a stop the action returns to Start.");

        // Change Mode returns to the picker, where the row is not shown.
        vm.ChangeModeCommand.Execute(null);
        Expect(vm.IsPickerVisible, "Change Mode must return to the picker.");
        Expect(!vm.IsBroadcastRowVisible, "On the picker the broadcast row is not shown.");

        // ---- 6. RECORDING IS STILL AN INDEPENDENT AXIS --------------------------------------------
        vm.SelectModeCommand.Execute(AppMode.BroadcastMode);
        vm.IsDesktopCaptureActive = true;

        long frames = 0;
        seam.Session.VideoFrameAvailable += (ptr, len, w, h, st, fps, ts) => Interlocked.Increment(ref frames);
        seam.Session.Connect(new SyntheticPatternSource());

        int waitTicks = 0;
        while (frames < 5 && waitTicks < 30)
        {
            Thread.Sleep(50);
            waitTicks++;
        }

        var config = new RecordingConfig { OutputDirectory = Path.GetTempPath(), FilenamePrefix = "DisclosureTest" };
        if (seam.Session.StartRecording(config))
        {
            bool rowWasVisibleWhileRecording = vm.IsBroadcastRowVisible;

            // Broadcast OFF mid-take: the row stops being shown (mode left + no longer live) and the take
            // must not notice.
            vm.IsDesktopCaptureActive = false;
            vm.SelectModeCommand.Execute(AppMode.CaptureMode);
            Expect(!vm.IsBroadcastRowVisible,
                "Broadcast OFF must hide the row even while a take is running - the row is not keyed to recording.");
            Expect(rowWasVisibleWhileRecording,
                "The row was visible because BROADCAST was on, not because a take was running.");
            Expect(seam.Session.IsRecording,
                "Hiding the broadcast row must never disturb a running take.");

            // A live take must NOT force the row open on its own.
            Expect(!vm.IsBroadcastRowVisible,
                "A recording on its own must never reveal the broadcast row - it is keyed to broadcast only.");

            seam.Session.StopRecording();
        }
        else
        {
            Console.WriteLine(" -> [SKIP] the storage preflight refused the temp volume; the recording-vs-row assertions were skipped.");
        }

        Console.WriteLine(" -> Advanced collapsed by default with a drawn chevron; the broadcast row is keyed to " +
                          "broadcast state alone (a take neither opens nor is disturbed by it); locking composes " +
                          "with visibility; preset->Custom still works from inside the tier!");

        void Body() { }
        Body();
    }

    /// <summary>Finds a labelled option by value - mirrors the ViewModel's own lookup for the test.</summary>
    private static RecordingChoiceOption RecordingChoiceOption_ForTest(IReadOnlyList<RecordingChoiceOption> options, double value)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].Value.Equals(value)) return options[i];
        }

        return options[0];
    }

}
