using CommunityToolkit.Mvvm.Input;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Forensics;
using OmtCaptureStudio.Services.Sources;

namespace OmtCaptureStudio.ViewModels;

/// <summary>
/// Broadcast-mode workspace — Phase 1.6 named extract along ADR-0011.
/// Host-broadcast arm/disarm and network population; chrome projection remains in BroadcastChrome.
/// Facade bindings stay on MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{

    partial void OnIsBroadcastNetworkDropDownOpenChanged(bool value)
    {
        if (value)
        {
            try { PopulateBroadcastNetworks(); } catch { /* headless */ }
        }
    }

    partial void OnSelectedBroadcastNetworkChanged(BroadcastNetworkOption value)
    {
        if (_desktopCapture is { IsRunning: true } activeBroadcast && value != null)
        {
            try
            {
                activeBroadcast.SetBindIpAddress(value.IpAddress);
                string endpoint = activeBroadcast.BroadcastEndpointUrl ?? activeBroadcast.Address ?? "OMT";
                StatusText = $"Host broadcast network switched to: {value.DisplayName} ({endpoint})";
                AppLogger.LogInfo($"Switched host broadcast network to {value.DisplayName} ({endpoint}).");
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Failed to switch host broadcast network: {ex.Message}", ex);
                StatusText = $"Host network switch error: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// The in-row action (NET-014). Start arms the live state through the SAME single gate as before -
    /// <see cref="SetDesktopCaptureActiveAsync"/> still owns the OmtDesktopCaptureGenerator lifecycle, so
    /// the send path is unchanged and this is a UI-state change only.
    ///
    /// <para>Stop leaves the panel REVEALED so the operator can adjust and restart without the controls
    /// vanishing under the cursor.</para>
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task StartStopBroadcastAsync()
    {
        if (IsRecordingCloseOverlayVisible) return;

        await SetDesktopCaptureActiveAsync(!IsBroadcasting);
    }

    public async Task SetDesktopCaptureActiveAsync(bool active)
    {
        try
        {
            if (active)
            {
                if (CaptureTargets.Count == 0)
                {
                    PopulateCaptureTargets();
                }

                var target = SelectedCaptureTarget
                    ?? MonitorEnumerator.Enumerate().FirstOrDefault()
                    ?? throw new InvalidOperationException("No capture target available.");
                IFrameSource source = FrameSourceFactory.Create(target);
                IHostAudioSource audioSource;
                try
                {
                    audioSource = CreateCaptureAudioSource(SelectedCaptureAudioOption);
                }
                catch
                {
                    source.Dispose();
                    throw;
                }

                string? bindIp = SelectedBroadcastNetwork?.IpAddress;

                // Once the generator is constructed it owns both sources and disposes each exactly
                // once; anything thrown before that would leave an ASIO driver locked exclusively,
                // so every failure branch below releases what it still owns.
                OmtDesktopCaptureGenerator? generator = null;
                try
                {
                    generator = new OmtDesktopCaptureGenerator(
                        source,
                        sourceName: "OmtCaptureStudio_DesktopCapture",
                        fps: 30,
                        audioSource: audioSource,
                        bindIpAddress: bindIp);
                    // DIAG-BROADCAST: the generator has no error funnel of its own (no event existed at
                    // all before this pass), so the stall report is subscribed here and marshalled to the
                    // UI thread - the event is raised on the SEND thread.
                    generator.CaptureStalled += m => PostToUIThread(() => StatusText = m);
                    generator.Start();
                    _desktopCapture = generator;
                }
                catch
                {
                    if (generator != null)
                    {
                        try { generator.Dispose(); } catch { }
                    }
                    else
                    {
                        try { source.Dispose(); } catch { }
                        try { audioSource.Dispose(); } catch { }
                    }
                    throw;
                }

                _activeCaptureTarget = target;
                _broadcastState.StartBroadcasting();
                string audioLabel = SelectedCaptureAudioOption?.DisplayName ?? "System Audio";
                string networkLabel = SelectedBroadcastNetwork?.DisplayName ?? "All Networks (Auto)";
                string endpointUrl = _desktopCapture.BroadcastEndpointUrl ?? _desktopCapture.Address ?? "OMT";
                StatusText = $"Broadcasting host capture ({audioLabel}) on {networkLabel} ({endpointUrl}) — select it in Source and Connect.";
                AppLogger.LogInfo($"Started host desktop capture: {endpointUrl} (Audio: {audioLabel}, Network: {networkLabel})");
            }
            else
            {
                _desktopCapture?.Dispose();
                _desktopCapture = null;
                _activeCaptureTarget = null;
                _broadcastState.StopBroadcasting();
                StatusText = "Host capture stopped.";
                AppLogger.LogInfo("Stopped host desktop capture.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Error toggling desktop capture: {ex.Message}", ex);
            _broadcastState.StopBroadcasting();
            StatusText = $"Host capture error: {ex.Message}";
        }
    }

    private void PopulateBroadcastNetworks()
    {
        var previous = SelectedBroadcastNetwork;
        var list = NetworkInterfaceEnumerator.Enumerate();
        if (list.Count == 0)
        {
            list = DefaultBroadcastNetworks;
        }

        BroadcastNetworks = list;

        if (previous != null)
        {
            var match = list.FirstOrDefault(n =>
                n.IsAllNetworks == previous.IsAllNetworks &&
                n.IsLoopbackOnly == previous.IsLoopbackOnly &&
                string.Equals(n.IpAddress, previous.IpAddress, StringComparison.OrdinalIgnoreCase))
                ?? list[0];
            SelectedBroadcastNetwork = match;
        }
        else
        {
            SelectedBroadcastNetwork = list[0];
        }
    }

}
