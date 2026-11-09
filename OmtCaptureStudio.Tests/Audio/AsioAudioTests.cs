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
    /// <summary>
    /// Exercises the ASIO host audio source against real registered ASIO drivers: registry
    /// discovery, opening the driver through the hand-rolled COM vtable interop, sample-rate
    /// negotiation (ask the driver for 48 kHz, resample only as a fallback), and continuous
    /// planar fill at 48 kHz stereo without NaN/Infinity.
    /// ASIO grants EXCLUSIVE driver access, so this must always dispose and release cleanly.
    /// </summary>
    static void TestAsioHostAudio()
    {
        Console.WriteLine("\n[Step 0.69] Verifying ASIO host audio capture (discovery, COM vtable, rate negotiation)...");

        var drivers = AsioDriverEnumerator.Enumerate();
        Console.WriteLine($" -> AsioDriverEnumerator resolved {drivers.Count} ASIO driver(s)");
        if (drivers.Count == 0)
        {
            Console.WriteLine(" -> [SKIP] no registered ASIO drivers on this host");
            return;
        }

        foreach (var d in drivers)
        {
            Console.WriteLine($"    * {d.DisplayName}");
        }

        AsioHostAudioSource? opened = null;
        AsioDriverInfo? openedDriver = null;
        foreach (var d in drivers)
        {
            AsioHostAudioSource? candidate = null;
            try
            {
                candidate = new AsioHostAudioSource(new AsioCaptureOption(d, 0));

                // A driver can construct yet be unusable (device not present, owned by another
                // app). Require evidence it is genuinely streaming before accepting it.
                bool streaming = false;
                for (int probe = 0; probe < 16 && !streaming; probe++)
                {
                    Thread.Sleep(25);
                    streaming = candidate.CallbacksInvoked > 0 &&
                                !string.IsNullOrEmpty(candidate.DriverReportedName);
                }

                if (streaming)
                {
                    opened = candidate;
                    openedDriver = d;
                    break;
                }

                Console.WriteLine($"    [skip] {d.DisplayName}: opened but not streaming (error: {candidate.LastError ?? "none reported"})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [skip] {d.DisplayName}: {ex.Message}");
            }
            finally
            {
                // Release the exclusive lock on any candidate we are not keeping.
                if (!ReferenceEquals(opened, candidate))
                {
                    candidate?.Dispose();
                }
            }
        }

        if (opened == null)
        {
            Console.WriteLine(" -> [SKIP] no registered ASIO driver is currently streaming (all unavailable or held exclusively)");
            return;
        }

        try
        {
            if (opened.SampleRate != 48000 || opened.Channels != 2)
                throw new Exception($"AsioHostAudioSource contract violation: {opened.SampleRate}Hz, {opened.Channels}ch");

            string path = opened.IsResamplingRequired
                ? $"native {opened.DriverActualSampleRate} Hz -> resampled to 48 kHz"
                : "running native 48 kHz (no resampling)";
            Console.WriteLine($" -> Opened '{opened.DriverReportedName}' ({path})");
            Console.WriteLine($" -> Latency: in {opened.InputLatencySamples} / out {opened.OutputLatencySamples} samples");
            if (opened.SampleRateNegotiationNote != null)
                Console.WriteLine($" -> Rate note: {opened.SampleRateNegotiationNote}");

            // Prove the driver is really streaming (callbacks arriving) and check for signal.
            int samplesPerChannel = opened.SampleRate / 30; // 30 fps cadence
            float[] planar = new float[2 * samplesPerChannel];
            float peak = 0f;

            for (int i = 0; i < 40; i++)
            {
                opened.FillPlanarAudio(planar, samplesPerChannel);
                foreach (float s in planar)
                {
                    if (float.IsNaN(s) || float.IsInfinity(s))
                        throw new Exception("AsioHostAudioSource produced a NaN/Infinity sample!");
                    float mag = Math.Abs(s);
                    if (mag > peak) peak = mag;
                }
                Thread.Sleep(25);
            }

            long callbacks = opened.CallbacksInvoked;
            if (callbacks <= 0)
                throw new Exception($"ASIO driver '{openedDriver!.DisplayName}' opened but delivered no callbacks (not streaming).");

            Console.WriteLine($" -> {callbacks} callbacks received; peak {peak:F6} ({(peak > 0f ? "non-silent" : "digital silence - no live signal into this driver")})");
            if (opened.LastError != null)
                Console.WriteLine($" -> Last driver error: {opened.LastError}");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" -> ASIO host audio capture verified!");
            Console.ResetColor();
        }
        finally
        {
            // Release the exclusive driver lock; double-dispose must be idempotent.
            opened.Dispose();
            opened.Dispose();
        }
    }

    /// <summary>
    /// NET-019: the ASIO pair selector in isolation — an injected probe and an inline UI hop, so the
    /// whole probe / retry / degrade lifecycle is covered WITHOUT constructing the 2,600-line view
    /// model and WITHOUT opening a driver.
    /// </summary>
    static void TestAsioInputPairSelector()
    {
        Console.WriteLine("\n[Step 0.696] Verifying AsioInputPairSelector in isolation (injected probe, no view model)....");

        var clsid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var driver = new AsioDriverInfo("Test ASIO", clsid, "C:/fake/test-asio.dll");
        var asioOption = HostAudioCaptureOption.ForAsio(clsid, "ASIO - Test ASIO (In 1/2)", 0);
        var muteOption = new HostAudioCaptureOption(HostAudioCaptureMode.Mute, "Mute");

        static IReadOnlyList<AsioInputChannel> Chans(int n) =>
            Enumerable.Range(0, n).Select(i => new AsioInputChannel(i, $"Input {i + 1}")).ToList();

        // ---- 1. THE PURE PAIR DERIVATION (the ONE place pairs come from) -----------------------
        Expect(AsioInputPairSelector.BuildPairs(null).Count == 0, "A null channel list yields no pairs.");
        Expect(AsioInputPairSelector.BuildPairs(Array.Empty<AsioInputChannel>()).Count == 0,
            "A driver with no input channels yields no pairs.");

        var two = AsioInputPairSelector.BuildPairs(Chans(2));
        Expect(two.Count == 1 && two[0].FirstChannelIndex == 0 && two[0].DisplayName == "In 1/2",
            "Two channels yield exactly one pair, In 1/2.");

        var four = AsioInputPairSelector.BuildPairs(Chans(4));
        Expect(four.Count == 2 && four[1].FirstChannelIndex == 2 && four[1].DisplayName == "In 3/4",
            "Four channels yield In 1/2 and In 3/4.");

        var five = AsioInputPairSelector.BuildPairs(Chans(5));
        Expect(five.Count == 3 && five[2].FirstChannelIndex == 4 && five[2].DisplayName == "In 5",
            "A trailing odd channel is offered on its own (the capture source duplicates it to L+R).");

        // ---- 2. SUPPRESSION IS A SCOPE, NOT A HAND-PAIRED BOOL ---------------------------------
        // This is the invariant that used to be hand-set and hand-reset at five sites. Getting it
        // wrong leaves the picker permanently deaf to the operator, so the nesting semantics matter.
        {
            var guard = new AsioInputPairSelector(_ => new AsioPairProbeResult(Chans(0), null),
                                                 action => action(), () => null);

            Expect(!guard.IsSuppressingUserChoice, "A fresh selector must not suppress.");

            using (guard.SuppressUserChoice())
            {
                Expect(guard.IsSuppressingUserChoice, "The scope must suppress.");

                using (guard.SuppressUserChoice())
                {
                    Expect(guard.IsSuppressingUserChoice, "A nested scope stays suppressed.");
                }

                Expect(guard.IsSuppressingUserChoice,
                    "The inner scope must NOT un-suppress the outer one — the guard is a DEPTH, not a bool.");
            }

            Expect(!guard.IsSuppressingUserChoice, "The outer scope must release on dispose.");

            try { using (guard.SuppressUserChoice()) throw new InvalidOperationException("boom"); }
            catch (InvalidOperationException) { }
            Expect(!guard.IsSuppressingUserChoice,
                "A scope must release even when its body throws — that is what `using` buys over a raw bool.");
        }

        // ---- 3. THE PROBE LIFECYCLE: lazy, cached, and it never runs during construction --------
        {
            int probes = 0;
            HostAudioCaptureOption? current = asioOption;
            var selector = new AsioInputPairSelector(
                _ => { Interlocked.Increment(ref probes); return new AsioPairProbeResult(Chans(8), null); },
                action => action(),
                () => current);
            selector.RegisterDriver(driver);

            var gate = new object();
            AsioPairProjection latest = AsioPairProjection.Hidden;
            bool selectionRaisedUnderSuppression = false;
            selector.ProjectionChanged += p =>
            {
                lock (gate) latest = p;
                if (p.SelectedPair != null && selector.IsSuppressingUserChoice)
                {
                    selectionRaisedUnderSuppression = true;
                }
            };
            AsioPairProjection Latest() { lock (gate) return latest; }

            Expect(probes == 0, "Constructing the selector must NOT probe — the whole path is lazy.");

            // A non-ASIO device hides and clears, and still probes nothing.
            selector.SyncSelection(muteOption);
            Expect(!Latest().IsVisible, "A non-ASIO device must hide the pair picker.");
            Expect(Latest().Options.Count == 0, "A non-ASIO device must clear the pair options.");
            Expect(probes == 0, "A non-ASIO device must never trigger a probe.");

            // Selecting an ASIO device shows the control immediately and starts one probe.
            selector.SyncSelection(asioOption);
            Expect(Latest().IsVisible, "An ASIO device must show the picker immediately, before the probe lands.");
            string? probingHint = Latest().Hint;
            Expect(probingHint != null && probingHint.Contains("Probing"),
                $"The hint must say a probe is running; got '{probingHint}'.");

            // The probe itself runs off-thread, so wait for it to LAND before asserting the count.
            for (int i = 0; i < 400 && Latest().Options.Count == 0; i++) Thread.Sleep(5);
            Expect(probes == 1, $"Selecting an ASIO device must start exactly one probe; got {probes}.");
            Expect(Latest().Options.Count == 4, $"8 channels must yield 4 pairs; got {Latest().Options.Count}.");
            Expect(Latest().SelectedPair?.FirstChannelIndex == 0, "The remembered pair (0 = In 1/2) must be selected.");
            Expect(selectionRaisedUnderSuppression,
                "A projection that carries a SELECTION must be raised while suppression is active, or the " +
                "host applying it looks like an operator pick and the picker reacts to itself.");

            // A second sync for the same driver is served from the cache — no new probe, no driver open.
            selector.SyncSelection(asioOption);
            Expect(probes == 1, $"A cached driver must not be re-probed; got {probes} probes.");

            // PairText derives its label from the CACHE once there is one.
            Expect(selector.PairText(clsid, 2) == "In 3/4",
                $"PairText must name a cached pair; got '{selector.PairText(clsid, 2)}'.");
            Expect(selector.PairText(Guid.Empty, 0) == "In 1/2",
                "PairText must fall back to the index when there is no cached probe.");
        }

        // ---- 4. DEGRADE: a failed probe hides the picker and names the reason -------------------
        {
            HostAudioCaptureOption? current = asioOption;
            var selector = new AsioInputPairSelector(
                _ => new AsioPairProbeResult(Chans(0), "held by another application"),
                action => action(),
                () => current);
            selector.RegisterDriver(driver);

            var gate = new object();
            AsioPairProjection latest = AsioPairProjection.Hidden;
            selector.ProjectionChanged += p => { lock (gate) latest = p; };
            AsioPairProjection Latest() { lock (gate) return latest; }

            selector.SyncSelection(asioOption);
            for (int i = 0; i < 400; i++)
            {
                string? h;
                lock (gate) h = latest.Hint;
                if (h != null && h.Contains("Input pairs unavailable")) break;
                Thread.Sleep(5);
            }

            Expect(!Latest().IsVisible, "A failed probe must hide the pair picker.");
            Expect(Latest().Options.Count == 0, "A failed probe must leave no pairs.");
            Expect(Latest().SelectedPair == null, "A failed probe must select nothing.");

            string? degradeHint = Latest().Hint;
            Expect(degradeHint != null && degradeHint.Contains("held by another application"),
                $"The failure reason must reach the hint; got '{degradeHint}'.");
        }

        // ---- 5. RETRY ON DROPDOWN OPEN, exactly once, and never while one is in flight ----------
        {
            int probes = 0;
            HostAudioCaptureOption? current = asioOption;
            var selector = new AsioInputPairSelector(
                _ => { Interlocked.Increment(ref probes); return new AsioPairProbeResult(Chans(0), "no channels"); },
                action => action(),
                () => current);
            selector.RegisterDriver(driver);

            selector.SyncSelection(asioOption);
            for (int i = 0; i < 200 && probes == 0; i++) Thread.Sleep(5);
            Thread.Sleep(60);

            int afterFirst = probes;
            selector.RetryIfUnusable(asioOption);
            for (int i = 0; i < 200 && probes == afterFirst; i++) Thread.Sleep(5);
            Expect(probes == afterFirst + 1, $"Opening the picker after a failure must retry ONCE; got {probes}.");

            // A usable cached result must never be thrown away for a retry.
            int before = probes;
            Thread.Sleep(60);
            selector.RetryIfUnusable(muteOption);
            Thread.Sleep(40);
            Expect(probes == before, "A non-ASIO selection must never trigger a retry.");
        }

        // ---- 6. A LATE PROBE FOR A DEVICE THE OPERATOR LEFT IS DROPPED, NOT APPLIED -------------
        {
            HostAudioCaptureOption? current = asioOption;
            var selector = new AsioInputPairSelector(
                _ => new AsioPairProbeResult(Chans(8), null),
                action => action(),
                () => current);
            selector.RegisterDriver(driver);

            var gate = new object();
            AsioPairProjection latest = AsioPairProjection.Hidden;
            selector.ProjectionChanged += p => { lock (gate) latest = p; };

            // The operator selects the ASIO device, then switches away before the probe lands. `current`
            // must move FIRST: SyncSelection takes its option as an argument, so setting it after the
            // call is a race against a probe that can already have landed.
            current = muteOption;
            selector.SyncSelection(asioOption);

            // PairText is derived from the cache, so it tells us the probe has landed without exposing
            // internals or racing a fixed sleep.
            for (int i = 0; i < 400 && selector.PairText(clsid, 0) != "In 1/2"; i++) Thread.Sleep(5);

            Expect(latest.Options.Count == 0 && latest.SelectedPair == null,
                "A probe that lands after the operator moved to another device must be cached, not applied " +
                "to the picker (the picker must still show no pairs and no selection).");
            Expect(selector.PairText(clsid, 0) == "In 1/2",
                "The dropped result must still have been CACHED, so the next visit is instant.");
        }

        Console.WriteLine(" -> Pair derivation, lazy probe, cache, degrade, one-shot retry and the suppression " +
                          "SCOPE all verified with no view model and no ASIO driver!");
    }
}

