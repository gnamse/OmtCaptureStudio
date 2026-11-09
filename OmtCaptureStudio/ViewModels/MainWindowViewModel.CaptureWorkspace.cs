using CommunityToolkit.Mvvm.Input;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Forensics;
using OmtCaptureStudio.Services.Sources;

namespace OmtCaptureStudio.ViewModels;

/// <summary>
/// Capture-mode workspace — Phase 1.6 named extract along ADR-0011.
/// Target/audio/ASIO population and capture-chrome handlers; facade bindings stay on MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{

    partial void OnIsCaptureTargetDropDownOpenChanged(bool value)
    {
        // Refresh the window list every time the picker opens (windows come and go);
        // monitors are static but re-enumerating is cheap and keeps the list current.
        if (value)
        {
            try { PopulateCaptureTargets(); } catch { /* headless / no-desktop */ }
        }
    }

    partial void OnIsCaptureAudioDropDownOpenChanged(bool value)
    {
        if (value)
        {
            try { PopulateCaptureAudioOptions(); } catch { /* headless */ }
        }
    }

    partial void OnSelectedCaptureAudioOptionChanged(HostAudioCaptureOption value)
    {
        OnPropertyChanged(nameof(SelectedCaptureAudioMode));
        _asioSelector.SyncSelection(value);
        if (_desktopCapture is { IsRunning: true } activeBroadcast && value != null)
        {
            try
            {
                // SetAudioSource disposes the outgoing source exactly once — critical for ASIO, which
                // holds the driver exclusively; if this throws, the generator disposes the new one.
                activeBroadcast.SetAudioSource(CreateCaptureAudioSource(value));
                StatusText = $"Host broadcast audio switched to: {value.DisplayName}";
                AppLogger.LogInfo($"Switched host broadcast audio to {value.Mode} ({value.DisplayName}).");
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Failed to switch host broadcast audio: {ex.Message}", ex);
                StatusText = $"Host audio switch error: {ex.Message}";
            }
        }
    }

    private void PopulateCaptureTargets()
    {
        // Monitors are cached after the first DXGI enumeration attempt (it spins up a
        // D3D11 device); only refresh them when not yet enumerated.
        if (!_monitorsEnumerated)
        {
            _cachedMonitors = MonitorEnumerator.Enumerate();
            _monitorsEnumerated = true;
        }

        var list = new List<CaptureTarget>(_cachedMonitors);
        list.AddRange(WindowEnumerator.Enumerate());
        CaptureTargets = list;

        if (SelectedCaptureTarget == null ||
            !list.Any(t => t.Kind == SelectedCaptureTarget.Kind &&
                           t.MonitorIndex == SelectedCaptureTarget.MonitorIndex &&
                           t.WindowHandle == SelectedCaptureTarget.WindowHandle))
        {
            SelectedCaptureTarget = list.FirstOrDefault();
        }
    }

    private void PopulateCaptureAudioOptions()
    {
        var previous = SelectedCaptureAudioOption;
        var list = AudioDeviceEnumerator.EnumerateOptions();
        if (list.Count == 0)
        {
            list = DefaultCaptureAudioOptions;
        }

        // Copy into a mutable list: the enumerator's result is shared state, and we splice the
        // ASIO drivers in below the built-in presets without disturbing either order.
        var options = new List<HostAudioCaptureOption>(list);
        InsertAsioOptions(options);

        CaptureAudioOptions = options;

        if (previous != null)
        {
            var match = options.FirstOrDefault(o =>
                o.Mode == previous.Mode &&
                string.Equals(o.DeviceId, previous.DeviceId, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault(o => o.Mode == previous.Mode && o.DeviceId == null)
                ?? options[0];
            SelectedCaptureAudioOption = match;
        }
        else
        {
            SelectedCaptureAudioOption = options[0];
        }
    }

    /// <summary>
    /// Splices one entry per registered ASIO driver into the broadcast-audio dropdown, keeping every
    /// existing WASAPI entry and its position: they land after the built-in presets and before the
    /// per-endpoint entries (or before "Mute" when no endpoints were enumerated).
    /// ASIO drivers grant exclusive access, so this only ever reads the registry — a driver is never
    /// opened here, only when the user actually selects it. Registry failures (headless, no drivers,
    /// locked-down hive) leave the dropdown exactly as it was.
    /// </summary>
    /// This never opens a driver: probing is deferred to the pair selector, which only ever runs once
    /// the user selects (or opens the dropdown of) an ASIO entry.
    /// </summary>
    private void InsertAsioOptions(List<HostAudioCaptureOption> options)
    {
        IReadOnlyList<AsioDriverInfo> drivers;
        try
        {
            drivers = AsioDriverEnumerator.Enumerate();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not enumerate ASIO drivers: {ex.Message}");
            return;
        }

        if (drivers.Count == 0)
        {
            return;
        }

        int insertAt = options.FindIndex(o => o.DeviceId != null && o.Mode != HostAudioCaptureMode.Mute);
        if (insertAt < 0)
        {
            int muteIndex = options.FindIndex(o => o.Mode == HostAudioCaptureMode.Mute);
            insertAt = muteIndex >= 0 ? muteIndex : options.Count;
        }

        var entries = new List<HostAudioCaptureOption>(drivers.Count);
        foreach (AsioDriverInfo driver in drivers)
        {
            _asioSelector.RegisterDriver(driver);

            // Remembered per driver (not per dropdown rebuild) so re-opening the audio picker never
            // silently resets a chosen 3/4 back to 1/2. The first pair stays the default.
            int first = _asioSelector.ChosenPairIndex(driver.Clsid);
            entries.Add(HostAudioCaptureOption.ForAsio(
                driver.Clsid,
                $"ASIO · {driver.DisplayName} ({_asioSelector.PairText(driver.Clsid, first)})",
                first));
        }

        options.InsertRange(insertAt, entries);
    }

    // ============================================================================================
    // ASIO input pair selection (NET-019 — the policy lives in AsioInputPairSelector)
    // ============================================================================================

    /// <summary>
    /// The ONE place the selector's projection reaches this view model's observables — the same
    /// shape as <see cref="ApplyBroadcastStateProjection"/>: the module decided everything, this
    /// only copies it across.
    /// </summary>
    private void ApplyAsioProjection(AsioPairProjection projection)
    {
        // This assignment is indistinguishable from an operator pick, so it has to be suppressed —
        // but the flag belongs to the selector now, and a `using` scope cannot be forgotten the way
        // a hand-paired set/reset can.
        using (_asioSelector.SuppressUserChoice())
        {
            IsAsioInputPairVisible = projection.IsVisible;
            AsioInputPairOptions = projection.Options;
            SelectedAsioInputPair = projection.SelectedPair;
            AsioInputPairHint = projection.Hint;
        }

        if (projection.Announcement is { Length: > 0 } announcement) StatusText = announcement;
    }

    /// <summary>Re-arms the probe when the operator opens the pair picker after an earlier failure.</summary>
    partial void OnIsAsioInputPairDropDownOpenChanged(bool value)
    {
        if (!value) return;
        _asioSelector.RetryIfUnusable(SelectedCaptureAudioOption);
    }

    /// <summary>
    /// Applies the operator's pair choice to the selected audio option. Mutating the dropdown entry
    /// in place (rather than just setting a derived value) keeps the new instance a member of
    /// <see cref="CaptureAudioOptions"/>, which is what the ComboBox's SelectedItem binding needs —
    /// and re-selecting it lets the existing handler hand the rebuilt option to a live broadcast.
    /// </summary>
    partial void OnSelectedAsioInputPairChanged(AsioInputPairOption? value)
    {
        if (value == null || _asioSelector.IsSuppressingUserChoice) return;

        HostAudioCaptureOption? current = SelectedCaptureAudioOption;
        if (current == null || !current.IsAsio || current.AsioDriverClsid is not Guid clsid) return;
        if (current.AsioFirstInputChannelIndex == value.FirstChannelIndex) return;

        HostAudioCaptureOption updated = current with
        {
            AsioFirstInputChannelIndex = value.FirstChannelIndex,
            DisplayName = $"ASIO · {_asioSelector.DriverDisplayName(clsid)} ({value.DisplayName})"
        };

        _asioSelector.RememberChoice(clsid, value.FirstChannelIndex);

        try
        {
            // Keep the dropdown entry in sync, then deliberately re-select so a running broadcast
            // switches to the new pair immediately.
            var list = new List<HostAudioCaptureOption>(CaptureAudioOptions ?? Array.Empty<HostAudioCaptureOption>());
            int index = list.FindIndex(o => o.Mode == HostAudioCaptureMode.Asio && o.AsioDriverClsid == clsid);
            if (index >= 0) list[index] = updated; else list.Add(updated);
            CaptureAudioOptions = list;

            using (_asioSelector.SuppressUserChoice())
            {
                SelectedAsioInputPair = value;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not apply ASIO input pair {value.DisplayName}: {ex.Message}", ex);
            return;
        }

        SelectedCaptureAudioOption = updated;
        AppLogger.LogInfo($"ASIO input pair set to {value.DisplayName} for '{updated.DisplayName}'.");
    }

    /// <summary>
    /// Like <see cref="PostToUIThread"/> but headless-safe: with no Avalonia platform there is no UI
    /// dispatcher to hand the callback to, so it runs inline rather than being lost.
    /// </summary>
    private static void PostToUIThreadSafely(Action action)
    {
        try
        {
            PostToUIThread(action);
        }
        catch
        {
            try { action(); } catch { /* Nothing left to try; the probe is best-effort. */ }
        }
    }

    /// <summary>
    /// Builds the live source for a dropdown option and, for ASIO only, schedules one deferred
    /// telemetry snapshot: the negotiated sample rate, whether we fell back to resampling, and the
    /// driver's reported input latency. Those are published by the driver's own worker thread during
    /// startup, so reading them straight after construction would always return zeros.
    /// </summary>
    private static IHostAudioSource CreateCaptureAudioSource(HostAudioCaptureOption option)
    {
        IHostAudioSource source = HostAudioSourceFactory.Create(option);

        // ADR-0003: no downcast to a concrete driver adapter — the negotiated state is on the
        // interface now, so every source can report it the same way.
        LogSourceStateDeferred(source, option.DisplayName);

        return source;
    }

    private static void LogSourceStateDeferred(IHostAudioSource source, string displayName)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AsioTelemetryDelayMs).ConfigureAwait(false);
                    AppLogger.LogInfo($"[HostAudio] {displayName}: {DescribeSourceState(source)}");
                }
                catch
                {
                    // Telemetry must never surface as a failure to the caller.
                }
            });
        }
        catch
        {
            // No thread pool available (headless shutdown); nothing to report.
        }
    }

    /// <summary>One-line summary of a host source's negotiated state, for logs and status text.</summary>
    private static string DescribeSourceState(IHostAudioSource source)
    {
        int sampleRate = source.SampleRate > 0 ? source.SampleRate : 48000;

        string rate = source.IsResamplingRequired
            ? $"device runs at {source.DeviceSampleRate} Hz, resampling to {sampleRate} Hz"
            : $"native {sampleRate} Hz (no resampling)";

        string note = string.IsNullOrWhiteSpace(source.RateNegotiationNote)
            ? string.Empty
            : $" [{source.RateNegotiationNote}]";

        string latency = source.InputLatencyFrames > 0
            ? $", input latency {source.InputLatencyFrames * 1000 / sampleRate} ms ({source.InputLatencyFrames} samples)"
            : string.Empty;

        string error = string.IsNullOrWhiteSpace(source.LastError)
            ? string.Empty
            : $", ERROR: {source.LastError}";

        string active = source.IsHardwareActive ? string.Empty : ", hardware INACTIVE (silence)";

        return $"{rate}{note}{latency}{error}{active}";
    }

    /// <summary>
    /// Re-enumerates only the window list (monitors stay cached) and merges it into the
    /// current targets, preserving the user's selection. Skips the update when nothing
    /// changed, so we don't churn the bound collection every tick.
    /// </summary>
    private void RefreshWindowTargets()
    {
        var windows = WindowEnumerator.Enumerate();

        // Preserve current selection identity across the refresh.
        var previous = SelectedCaptureTarget;

        var list = new List<CaptureTarget>(_cachedMonitors.Count + windows.Count);
        list.AddRange(_cachedMonitors);
        list.AddRange(windows);

        if (TargetListsEqual(CaptureTargets, list))
        {
            return;
        }

        // If the selected target was a window that has since closed, drop the selection.
        CaptureTargets = list;

        if (previous != null)
        {
            var match = list.FirstOrDefault(t => t.Kind == previous.Kind &&
                                                 t.MonitorIndex == previous.MonitorIndex &&
                                                 t.WindowHandle == previous.WindowHandle);
            SelectedCaptureTarget = match; // null when the selected window closed
        }
        else
        {
            SelectedCaptureTarget = list.FirstOrDefault();
        }

        // If we are broadcasting a window that has since closed, stop cleanly rather than
        // streaming a stale/black frame captured from a dead handle.
        if (_activeCaptureTarget is { Kind: CaptureTargetKind.Window } active &&
            !list.Any(t => t.Kind == active.Kind && t.WindowHandle == active.WindowHandle))
        {
            _ = SetDesktopCaptureActiveAsync(false);
            StatusText = "Host broadcast stopped: the captured window was closed.";
        }
    }

    private static bool TargetListsEqual(IReadOnlyList<CaptureTarget> a, IReadOnlyList<CaptureTarget> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Kind != b[i].Kind ||
                a[i].MonitorIndex != b[i].MonitorIndex ||
                a[i].WindowHandle != b[i].WindowHandle ||
                !string.Equals(a[i].DisplayName, b[i].DisplayName, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private void CaptureTargetsTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            RefreshWindowTargets();
        }
        catch
        {
            // Enumeration failure (e.g. session teardown) must never crash the UI thread.
        }
    }

    [RelayCommand]
    private void RefreshCaptureTargets()
    {
        try
        {
            PopulateCaptureTargets();
            PopulateCaptureAudioOptions();
            PopulateBroadcastNetworks();
            StatusText = $"Capture targets ({CaptureTargets.Count}), audio devices ({CaptureAudioOptions.Count}), and networks ({BroadcastNetworks.Count}) refreshed.";
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Error refreshing capture targets: {ex.Message}", ex);
            StatusText = $"Refresh capture targets failed: {ex.Message}";
        }
    }

}
