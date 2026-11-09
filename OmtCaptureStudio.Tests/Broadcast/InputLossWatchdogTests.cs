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
    /// Exercises the input-loss watchdog directly with a controllable clock (ADR-0002) — no real
    /// 5 s sleeps. Verifies the threshold edge, the arm/disarm gate, and single-shot transitions.
    /// </summary>
    static void TestInputLossWatchdog()
    {
        Console.WriteLine("\n[Step 0.67] Verifying InputHealthWatchdog (threshold, arming, edges)...");

        long now = 10_000;
        var watchdog = new InputHealthWatchdog(thresholdMs: 5000, clock: () => now);

        // Not armed => never fires, however long the silence.
        now += 60_000;
        if (watchdog.Poll() != InputHealthTransition.None || watchdog.IsLost)
        {
            throw new Exception("A disarmed watchdog must never report input loss.");
        }

        watchdog.Arm();
        if (!watchdog.IsArmed || watchdog.IsLost)
        {
            throw new Exception("Arm() failed to arm the watchdog.");
        }

        // Just under the threshold => still healthy.
        now += 4999;
        if (watchdog.Poll() != InputHealthTransition.None)
        {
            throw new Exception("Watchdog fired before the threshold elapsed.");
        }

        // At the threshold => Lost, exactly once.
        now += 1;
        if (watchdog.Poll() != InputHealthTransition.Lost || !watchdog.IsLost)
        {
            throw new Exception("Watchdog failed to fire at the threshold.");
        }
        if (watchdog.Poll() != InputHealthTransition.None)
        {
            throw new Exception("Watchdog must report Lost only on the transition edge.");
        }

        // A frame arrives => Restored, exactly once.
        watchdog.NotifyVideoFrame();
        if (watchdog.Poll() != InputHealthTransition.Restored || watchdog.IsLost)
        {
            throw new Exception("Watchdog failed to report restore when frames returned.");
        }
        if (watchdog.Poll() != InputHealthTransition.None)
        {
            throw new Exception("Watchdog must report Restored only on the transition edge.");
        }

        // The threshold is operator-configurable.
        var custom = new InputHealthWatchdog(thresholdMs: 1500, clock: () => now);
        custom.Arm();
        now += 1500;
        if (custom.Poll() != InputHealthTransition.Lost)
        {
            throw new Exception("A configured threshold was not honoured.");
        }

        // Disarm clears the state.
        custom.Disarm();
        if (custom.IsArmed || custom.IsLost || custom.Poll() != InputHealthTransition.None)
        {
            throw new Exception("Disarm() must clear the armed/lost state.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> InputHealthWatchdog threshold, arming, and edge transitions verified!");
        Console.ResetColor();
    }

    /// <summary>Verifies the deterministic reconnect backoff schedule (ADR-0002, no reconnect storms).</summary>
    static void TestReconnectBackoff()
    {
        Console.WriteLine("\n[Step 0.671] Verifying ReconnectBackoff schedule...");

        double[] expectedMs = { 250, 500, 1000, 2000, 4000, 5000, 5000, 5000 };
        for (int attempt = 1; attempt <= expectedMs.Length; attempt++)
        {
            double actual = ReconnectBackoff.DelayFor(attempt).TotalMilliseconds;
            if (Math.Abs(actual - expectedMs[attempt - 1]) > 0.001)
            {
                throw new Exception($"Backoff attempt {attempt}: expected {expectedMs[attempt - 1]} ms, got {actual} ms");
            }
        }

        for (int attempt = 2; attempt <= 6; attempt++)
        {
            if (ReconnectBackoff.DelayFor(attempt) < ReconnectBackoff.DelayFor(attempt - 1))
            {
                throw new Exception($"Backoff must never decrease (attempt {attempt}).");
            }
        }

        if (ReconnectBackoff.DelayFor(99) != ReconnectBackoff.MaxDelay)
        {
            throw new Exception("Backoff must stay capped at MaxDelay.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> ReconnectBackoff 250ms -> 500ms -> 1s -> 2s -> 4s -> 5s(cap) verified!");
        Console.ResetColor();
    }
}

