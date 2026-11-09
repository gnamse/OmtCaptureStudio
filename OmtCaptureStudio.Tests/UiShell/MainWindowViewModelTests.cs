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
    /// <summary>NET-005: [Step 0.75] MainWindowViewModel MVVM state, commands and seams. The ~330-line presentation-assertion block migrated to the fixture + `Expect`, so each fact is one assertion instead of a four-line throw block.</summary>
    static void TestMainWindowViewModel()
    {
        Console.WriteLine("\n[Step 0.75] Verifying MainWindowViewModel (MVVM State, Commands & Seams)...");
        using var seam = BuildHeadlessSession();
        var vmSession = seam.Session;
        var vm = seam.ViewModel!;

        // Verify default UI state
        Expect(!(vm.SelectedContainerFormat != OutputContainerFormat.MP4 || vm.SelectedEncoderChoice != VideoEncoderChoice.AutoHardware || vm.SelectedQualityPreset != QualityPreset.High), "ViewModel recording config defaults mismatch!");

        Expect(!(!vm.IsNoSignalOverlayVisible || vm.LiveStatusText != "OFFLINE"), "ViewModel live status defaults mismatch!");

        Expect(!(vm.CaptureAudioOptions.Count < 4 || vm.SelectedCaptureAudioMode != HostAudioCaptureMode.SystemAudio), $"ViewModel host audio capture defaults mismatch: count={vm.CaptureAudioOptions.Count}, mode={vm.SelectedCaptureAudioMode}");
        Expect(!(vm.BroadcastNetworks.Count < 2 || !vm.SelectedBroadcastNetwork.IsAllNetworks), $"ViewModel broadcast network defaults mismatch: count={vm.BroadcastNetworks.Count}, selected={vm.SelectedBroadcastNetwork?.DisplayName}");
        vm.SelectedCaptureAudioMode = HostAudioCaptureMode.SystemAndMicrophone;
        Expect(vm.SelectedCaptureAudioOption.Mode == HostAudioCaptureMode.SystemAndMicrophone, $"SelectedCaptureAudioOption did not sync with SelectedCaptureAudioMode: {vm.SelectedCaptureAudioOption.Mode}");
        vm.SelectedCaptureAudioMode = HostAudioCaptureMode.SystemAudio;

        // Test MOV auto-switches encoder to ProRes
        vm.SelectedContainerFormat = OutputContainerFormat.MOV;
        Expect(vm.SelectedEncoderChoice == VideoEncoderChoice.ProRes, $"Expected ProRes for MOV, got: {vm.SelectedEncoderChoice}");

        // Test switching away from MOV restores AutoHardware
        vm.SelectedContainerFormat = OutputContainerFormat.MP4;
        Expect(vm.SelectedEncoderChoice == VideoEncoderChoice.AutoHardware, $"Expected AutoHardware for MP4, got: {vm.SelectedEncoderChoice}");

        // Test Source ToolTip generation
        var dummySource = OmtSourceInfo.Parse("PC (Camera 1)");
        vm.SelectedSource = dummySource;
        Expect(!(string.IsNullOrWhiteSpace(vm.SelectedSourceToolTip) || !vm.SelectedSourceToolTip.Contains("Camera 1")), $"ViewModel tooltip not updated correctly: '{vm.SelectedSourceToolTip}'");
        vm.SelectedSource = null;
        Expect(vm.SelectedSourceToolTip == null, "ViewModel tooltip should be null when SelectedSource is null!");

        // Test Commands
        vm.RefreshSourcesCommand.Execute(null);
        Expect(vm.StatusText.Contains("Scanning network"), $"RefreshSourcesCommand failed to update status: '{vm.StatusText}'");

        vm.IsRecordingCloseOverlayVisible = true;
        vm.KeepRecordingCommand.Execute(null);
        Expect(!(vm.IsRecordingCloseOverlayVisible || !vm.IsKeepRecordingEnabled), "KeepRecordingCommand failed to dismiss overlay or restore button state!");

        // Connect validation without source
        vm.ToggleConnectCommand.Execute(null);
        Expect(vm.StatusText.Contains("Please select an OMT source"), $"ToggleConnectCommand without source failed validation: '{vm.StatusText}'");

        // Test StudioPalette token identity in default state
        Expect(vm.ConnectButtonForeground == StudioPalette.ButtonDarkForeground, "ViewModel ConnectButtonForeground default state is not StudioPalette.ButtonDarkForeground!");
        Expect(vm.TestSignalForeground == StudioPalette.ButtonLightForeground, "ViewModel TestSignalForeground default state is not StudioPalette.ButtonLightForeground!");
        Expect(vm.DesktopCaptureForeground == StudioPalette.ButtonLightForeground, "ViewModel DesktopCaptureForeground default state is not StudioPalette.ButtonLightForeground!");
        Expect(!(!vm.IsRecordingConfigEnabled || !vm.IsMainContentEnabled), "ViewModel IsRecordingConfigEnabled and IsMainContentEnabled should default to true!");

        // ADR-0002: the input-loss banner is part of the state projection and starts hidden.
        Expect(!(vm.IsInputLossBannerVisible || vm.BroadcastState != BroadcastState.Offline), $"ViewModel must start Offline with the input-loss banner hidden: state={vm.BroadcastState}, banner={vm.IsInputLossBannerVisible}");

        // FEAT-07: the recording-health panel, stop verdict and diagnostics drawer all start hidden
        // (checked BEFORE any recording below, so nothing has populated a verdict yet).
        Expect(!(vm.IsRecordingHealthVisible || vm.IsStopSummaryVisible || vm.IsDiagnosticsDrawerOpen || vm.IsRecordingDropWarning || vm.IsRecordingDriftWarning),  $"Recording-health surfaces must start hidden: panel={vm.IsRecordingHealthVisible}, " + $"verdict={vm.IsStopSummaryVisible}, drawer={vm.IsDiagnosticsDrawerOpen}");
        vm.ToggleDiagnosticsDrawerCommand.Execute(null);
        Expect(vm.IsDiagnosticsDrawerOpen, "ToggleDiagnosticsDrawerCommand did not open the drawer.");
        vm.ToggleDiagnosticsDrawerCommand.Execute(null);
        Expect(!(vm.IsDiagnosticsDrawerOpen), "ToggleDiagnosticsDrawerCommand did not close the drawer.");

        // Test ToggleTestSignalCommand (Turn ON)
        vm.ToggleTestSignalCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Expect(!(!vm.IsTestSignalActive || !vm.IsConnected || vm.ConnectButtonText != "Disconnect"), $"ToggleTestSignalCommand failed to activate test signal! IsActive={vm.IsTestSignalActive}, IsConnected={vm.IsConnected}");
        Expect(vm.StatusText.Contains("Internal Test Pattern Generator", StringComparison.OrdinalIgnoreCase), $"Unexpected status after starting test signal: '{vm.StatusText}'");
        Expect(vm.TestSignalForeground == StudioPalette.ButtonWhiteForeground, "ViewModel TestSignalForeground active state is not StudioPalette.ButtonWhiteForeground!");
        Expect(vm.DesktopCaptureForeground == StudioPalette.ButtonLightForeground, "ViewModel DesktopCaptureForeground must remain ButtonLightForeground when only TestSignal is active!");
        Expect(vm.ConnectButtonForeground == StudioPalette.ButtonWhiteForeground, "ViewModel ConnectButtonForeground connected state is not StudioPalette.ButtonWhiteForeground!");
        Expect(vm.BroadcastState == BroadcastState.TestSignal, $"ViewModel state must be TestSignal while the test signal is active: {vm.BroadcastState}");

        // Test ToggleTestSignalCommand (Turn OFF)
        vm.ToggleTestSignalCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Expect(!(vm.IsTestSignalActive || vm.IsConnected || vm.ConnectButtonText != "Connect"), $"ToggleTestSignalCommand failed to deactivate test signal! IsActive={vm.IsTestSignalActive}, IsConnected={vm.IsConnected}");
        Expect(vm.TestSignalForeground == StudioPalette.ButtonLightForeground, "ViewModel TestSignalForeground inactive state is not StudioPalette.ButtonLightForeground!");
        Expect(vm.ConnectButtonForeground == StudioPalette.ButtonDarkForeground, "ViewModel ConnectButtonForeground disconnected state is not StudioPalette.ButtonDarkForeground!");
        Expect(!(vm.BroadcastState != BroadcastState.Offline || vm.IsInputLossBannerVisible), $"ViewModel must return Offline with the banner hidden: state={vm.BroadcastState}, banner={vm.IsInputLossBannerVisible}");

        // Test Folder Picker seam & exception resilience
        vm.OutputDirectory = "";
        vm.OpenOutputDirectoryCommand.Execute(null);
        Expect(vm.StatusText.Contains("not set", StringComparison.OrdinalIgnoreCase), $"OpenOutputDirectoryCommand with empty path should report not set, got: '{vm.StatusText}'");

        vm.PickFolderAsync = _ => throw new InvalidOperationException("Simulated picker error");
        vm.BrowseOutputDirectoryCommand.Execute(null);
        Expect(vm.StatusText.Contains("cancelled or failed", StringComparison.OrdinalIgnoreCase), $"BrowseOutputDirectoryCommand failed to catch error gracefully: '{vm.StatusText}'");

        string mockFolder = Path.Combine(Path.GetTempPath(), "OMT_Mock_Capture_Dir");
        vm.PickFolderAsync = _ => Task.FromResult<string?>(mockFolder);
        vm.BrowseOutputDirectoryCommand.Execute(null);
        Expect(vm.OutputDirectory == mockFolder, $"BrowseOutputDirectoryCommand failed to update OutputDirectory! Got: '{vm.OutputDirectory}'");

        // Test audio monitoring toggle
        vm.SetAudioMonitoring(true);
        Expect(vmSession.IsAudioMonitoringEnabled, "SetAudioMonitoring(true) failed to update session audio monitoring state!");
        vm.SetAudioMonitoring(false);
        Expect(!(vmSession.IsAudioMonitoringEnabled), "SetAudioMonitoring(false) failed to disable session audio monitoring state!");

        // Test ToggleRecordCommand validation when disconnected
        vm.ToggleRecordCommand.Execute(null);
        Expect(vm.StatusText.Contains("Please connect to an active OMT stream"), $"ToggleRecordCommand without active stream failed validation: '{vm.StatusText}'");

        // Connect live synthetic pattern generator to verify HUD overlay and live recording
        vm.SetTestSignalActiveAsync(true).GetAwaiter().GetResult();
        Expect(!(!vm.IsTestSignalActive || !vm.IsConnected || vm.ConnectButtonText != "Disconnect"), "Failed to activate test signal generator for recording test!");

        // Wait briefly for synthetic frames to negotiate stream format
        int frameWait = 0;
        while (!vmSession.CurrentFormat.HasVideo && frameWait < 30)
        {
            Thread.Sleep(50);
            frameWait++;
        }
        Expect(vmSession.CurrentFormat.HasVideo, "Synthetic pattern generator failed to deliver video stream format!");
        Expect(!(vm.IsNoSignalOverlayVisible || vm.LiveStatusText != "LIVE"), $"Video frame HUD overlay failed to transition to LIVE! OverlayVisible={vm.IsNoSignalOverlayVisible}, LiveStatus='{vm.LiveStatusText}'");
        Expect(vm.VideoSpecsText.Contains("1920x1080"), $"VideoSpecsText does not reflect synthetic 1080p stream! Got: '{vm.VideoSpecsText}'");

        // Test empty output directory pre-flight validation while connected
        vm.OutputDirectory = "   ";
        vm.ToggleRecordCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Expect(!(!vm.StatusText.Contains("valid destination folder", StringComparison.OrdinalIgnoreCase) || vm.IsRecording), $"ToggleRecordCommand with empty directory failed pre-flight validation: '{vm.StatusText}'");

        // Test active recording lifecycle through ViewModel
        vm.OutputDirectory = Path.GetTempPath();
        vm.ToggleRecordCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Expect(!(!vm.IsRecording || !vm.RecordButtonText.Contains("STOP RECORDING")), $"ToggleRecordCommand failed to start recording! IsRecording={vm.IsRecording}, BtnText='{vm.RecordButtonText}', Status='{vm.StatusText}'");
        Expect(!(vm.IsRecordingConfigEnabled), "IsRecordingConfigEnabled must be false while recording is active!");

        // Test close protection modal while active recording is in flight
        vm.IsRecordingCloseOverlayVisible = true;
        Expect(!(vm.IsMainContentEnabled), "IsMainContentEnabled must be false while RecordingCloseOverlay is visible!");
        vm.KeepRecordingCommand.Execute(null);
        Expect(!(vm.IsRecordingCloseOverlayVisible || vm.IsSafeExitConfirmed || !vm.IsMainContentEnabled), "KeepRecordingCommand failed to dismiss modal, restore IsMainContentEnabled, or left IsSafeExitConfirmed true!");

        // Test StopAndExitCommand safe finalization while recording
        bool closeRequested = false;
        vm.RequestClose += () => closeRequested = true;
        vm.StopAndExitCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        int exitWait = 0;
        while ((!vm.IsSafeExitConfirmed || vm.IsRecording) && exitWait < 30)
        {
            Thread.Sleep(50);
            exitWait++;
        }
        Expect(!(!vm.IsSafeExitConfirmed || !closeRequested || vm.IsRecording || !vm.IsRecordingConfigEnabled), $"StopAndExitCommand failed safe recording finalization! Confirmed={vm.IsSafeExitConfirmed}, CloseReq={closeRequested}, IsRec={vm.IsRecording}, ConfigEnabled={vm.IsRecordingConfigEnabled}");

        // Deactivate test signal generator and verify offline state
        vm.SetTestSignalActiveAsync(false).GetAwaiter().GetResult();
        Expect(!(vm.IsTestSignalActive || vm.IsConnected || vm.ConnectButtonText != "Connect" || !vm.IsNoSignalOverlayVisible || vm.LiveStatusText != "OFFLINE"), $"Deactivating test signal failed to restore offline state! IsActive={vm.IsTestSignalActive}, Live={vm.LiveStatusText}");
        Expect(!(vm.AudioTelemetryText != "Audio: --" || vm.BitrateText != "Bitrate: 0.0 Mbps" || vm.DroppedFramesText != "Dropped: 0" || vm.SourceNameText != ""), $"Deactivating test signal failed to reset telemetry! Audio='{vm.AudioTelemetryText}', Bitrate='{vm.BitrateText}', Dropped='{vm.DroppedFramesText}', Source='{vm.SourceNameText}'");

        // Test OmtSourceInfo equality
        var srcA = OmtSourceInfo.Parse("PC (Camera 1)");
        var srcB = OmtSourceInfo.Parse("PC (Camera 1)");
        var srcC = OmtSourceInfo.Parse("PC (Camera 2)");
        Expect(!(!srcA.Equals(srcB) || srcA != srcB || srcA == srcC || srcA.GetHashCode() != srcB.GetHashCode()), "OmtSourceInfo equality implementation failed!");

        // Test Dispose idempotency & seam cleanup
        vm.PickFolderAsync = _ => Task.FromResult<string?>("mock");
        vm.Dispose();
        Expect(vm.PickFolderAsync == null, "PickFolderAsync hook was not cleared on Dispose!");
        vm.Dispose();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> MainWindowViewModel MVVM patterns, bindings, commands, and seams verified!");
        Console.ResetColor();
    }

}
