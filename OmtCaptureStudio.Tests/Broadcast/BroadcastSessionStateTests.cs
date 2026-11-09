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
    /// <summary>Records the effect calls the state machine makes, so their order can be asserted.</summary>
    private sealed class BroadcastTestEffects : IBroadcastSessionEffects
    {
        private readonly List<string> _calls;

        public BroadcastTestEffects(List<string> calls) => _calls = calls;

        public bool IsRecording { get; set; }

        public Task StopRecordingAsync()
        {
            _calls.Add("stop-recording");
            return Task.CompletedTask;
        }

        public void DisconnectSession() => _calls.Add("disconnect");
    }

    /// <summary>
    /// Exercises the headless <see cref="BroadcastSessionState"/> module directly (ADR-0001): the
    /// enum contract, the transition rules, the stop-recording-before-disconnect ordering, and the
    /// selector-lock derivation. No Avalonia Application and no media session are involved — the
    /// media effects are injected as a recording fake.
    /// </summary>
    static void TestBroadcastSessionState()
    {
        Console.WriteLine("\n[Step 0.68] Verifying BroadcastSessionState module (headless state machine)...");

        // --- Enum contract: the five operating modes + the reserved Reconnecting state. ---
        var states = Enum.GetValues<BroadcastState>();
        foreach (BroadcastState required in new[]
                 {
                     BroadcastState.Offline, BroadcastState.Connecting, BroadcastState.NetworkConnected,
                     BroadcastState.TestSignal, BroadcastState.Broadcasting, BroadcastState.Reconnecting,
                     BroadcastState.InputLost
                 })
        {
            if (!states.Contains(required))
            {
                throw new Exception($"BroadcastState is missing the required member '{required}'.");
            }
        }
        if (states.Length != 7)
        {
            throw new Exception($"BroadcastState exposed {states.Length} members; expected exactly 7: {string.Join(",", states)}");
        }

        // --- Defaults: headless construct, Offline, both selectors unlocked. ---
        var module = new BroadcastSessionState();
        if (module.State != BroadcastState.Offline || module.IsBroadcasting || module.IsReceiving ||
            !module.IsSourceSelectorEnabled || !module.IsCaptureSelectorEnabled)
        {
            throw new Exception($"BroadcastSessionState defaults mismatch: State={module.State}, Broadcasting={module.IsBroadcasting}");
        }

        int events = 0;
        module.StateChanged += (_, __) => events++;
        var calls = new List<string>();
        var effects = new BroadcastTestEffects(calls);

        // --- Connect from Offline: nothing to leave, passes through Connecting (2 transitions). ---
        module.EnterNetworkConnectedAsync(effects, () => calls.Add("connect-network")).GetAwaiter().GetResult();
        if (module.State != BroadcastState.NetworkConnected || events != 2 ||
            string.Join(",", calls) != "connect-network")
        {
            throw new Exception($"EnterNetworkConnectedAsync failed: State={module.State}, events={events}, calls={string.Join(",", calls)}");
        }
        if (module.IsSourceSelectorEnabled || !module.IsCaptureSelectorEnabled || !module.IsReceiving)
        {
            throw new Exception($"Selector locks wrong while NetworkConnected: source={module.IsSourceSelectorEnabled}, capture={module.IsCaptureSelectorEnabled}");
        }

        // --- Switch NetworkConnected -> TestSignal: stop-recording-first, then disconnect, then connect (one transition). ---
        events = 0;
        calls.Clear();
        effects.IsRecording = true;
        module.EnterTestSignalAsync(effects, () => calls.Add("connect-test-signal")).GetAwaiter().GetResult();
        if (string.Join(",", calls) != "stop-recording,disconnect,connect-test-signal")
        {
            throw new Exception($"Stop-recording-first ordering violated: {string.Join(",", calls)}");
        }
        if (module.State != BroadcastState.TestSignal || events != 1)
        {
            throw new Exception($"Switching receive mode must be a single transition: State={module.State}, events={events}");
        }

        // --- Not recording => no stop-recording call, but still disconnects; selectors unlock. ---
        events = 0;
        calls.Clear();
        effects.IsRecording = false;
        module.LeaveReceiveModeAsync(effects).GetAwaiter().GetResult();
        if (string.Join(",", calls) != "disconnect" || module.State != BroadcastState.Offline || events != 1)
        {
            throw new Exception($"LeaveReceiveModeAsync (not recording) failed: calls={string.Join(",", calls)}, State={module.State}, events={events}");
        }
        if (!module.IsSourceSelectorEnabled || !module.IsCaptureSelectorEnabled)
        {
            throw new Exception("Selector locks must reset once Offline again.");
        }

        // --- Host broadcast is orthogonal: sender-only mode, survives receiving, and returns to Broadcasting. ---
        var broadcastModule = new BroadcastSessionState();
        var noCalls = new List<string>();
        broadcastModule.StartBroadcasting();
        if (broadcastModule.State != BroadcastState.Broadcasting || !broadcastModule.IsBroadcasting ||
            !broadcastModule.IsSourceSelectorEnabled || broadcastModule.IsCaptureSelectorEnabled)
        {
            throw new Exception($"StartBroadcasting (sender-only) failed: State={broadcastModule.State}, capture={broadcastModule.IsCaptureSelectorEnabled}");
        }

        broadcastModule.EnterNetworkConnectedAsync(new BroadcastTestEffects(noCalls), () => { }).GetAwaiter().GetResult();
        if (broadcastModule.State != BroadcastState.NetworkConnected || !broadcastModule.IsBroadcasting ||
            broadcastModule.IsSourceSelectorEnabled || broadcastModule.IsCaptureSelectorEnabled)
        {
            throw new Exception($"Broadcast must survive connecting to monitor it: State={broadcastModule.State}, Broadcasting={broadcastModule.IsBroadcasting}");
        }

        broadcastModule.LeaveReceiveModeAsync(new BroadcastTestEffects(noCalls)).GetAwaiter().GetResult();
        if (broadcastModule.State != BroadcastState.Broadcasting || !broadcastModule.IsBroadcasting)
        {
            throw new Exception($"Leaving the receive mode must return to Broadcasting, not Offline: State={broadcastModule.State}");
        }

        broadcastModule.StopBroadcasting();
        if (broadcastModule.State != BroadcastState.Offline || broadcastModule.IsBroadcasting || !broadcastModule.IsCaptureSelectorEnabled)
        {
            throw new Exception($"StopBroadcasting failed to return Offline: State={broadcastModule.State}, Broadcasting={broadcastModule.IsBroadcasting}");
        }

        // --- ADR-0002 outage states: NetworkConnected -> Reconnecting -> (NetworkConnected | InputLost). ---
        var outage = new BroadcastSessionState();
        int outageEvents = 0;
        outage.StateChanged += (_, __) => outageEvents++;
        outage.EnterNetworkConnectedAsync(new BroadcastTestEffects(new List<string>()), () => { }).GetAwaiter().GetResult();
        outageEvents = 0;

        outage.BeginReconnecting();
        if (outage.State != BroadcastState.Reconnecting || !outage.IsSessionActive ||
            outage.IsSourceSelectorEnabled || outageEvents != 1)
        {
            throw new Exception($"BeginReconnecting failed: State={outage.State}, sourceSelector={outage.IsSourceSelectorEnabled}, events={outageEvents}");
        }

        outage.EndReconnecting();
        if (outage.State != BroadcastState.NetworkConnected || outageEvents != 2)
        {
            throw new Exception($"EndReconnecting failed: State={outage.State}, events={outageEvents}");
        }

        outage.BeginReconnecting();
        outage.MarkInputLost();
        if (outage.State != BroadcastState.InputLost || !outage.IsSessionActive || outage.IsSourceSelectorEnabled)
        {
            throw new Exception($"MarkInputLost failed: State={outage.State}, sourceSelector={outage.IsSourceSelectorEnabled}");
        }

        // Leaving the session from the InputLost state still runs stop-recording-first, then disconnects.
        var outageCalls = new List<string>();
        outage.LeaveReceiveModeAsync(new BroadcastTestEffects(outageCalls) { IsRecording = true }).GetAwaiter().GetResult();
        if (string.Join(",", outageCalls) != "stop-recording,disconnect" || outage.State != BroadcastState.Offline)
        {
            throw new Exception($"Leaving an InputLost session failed: calls={string.Join(",", outageCalls)}, State={outage.State}");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> BroadcastSessionState transitions, stop-first ordering, and selector-lock derivation verified!");
        Console.ResetColor();
    }
}

