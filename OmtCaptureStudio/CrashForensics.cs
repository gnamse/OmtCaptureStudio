using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;

namespace OmtCaptureStudio;

/// <summary>
/// What the app was doing when it died - the part a stack trace cannot tell you (NET-009).
/// </summary>
/// <param name="IsRecording">True when a take was live at the moment of the fault.</param>
/// <param name="RecordingPath">The file the transport was writing, or null when idle.</param>
/// <param name="FramesWritten">Video frames that reached the transport for the ACTIVE segment.</param>
/// <param name="SegmentIndex">Active segment (1 for the first; later ones are …_partNN).</param>
/// <param name="BroadcastState">The broadcast state machine's state (ADR-0001) as text.</param>
/// <param name="MediaDuration">Media duration so far - frames over stamped fps, not wall clock.</param>
/// <param name="EncoderName">Encoder actually in use (AutoHardware resolved), when known.</param>
/// <param name="IsConnected">True when a media source was connected.</param>
public readonly record struct CrashState(
    bool IsRecording,
    string? RecordingPath,
    long FramesWritten,
    int SegmentIndex,
    string BroadcastState,
    TimeSpan MediaDuration,
    string EncoderName,
    bool IsConnected)
{
    /// <summary>Used when no state provider is registered, or the provider itself faulted.</summary>
    public static CrashState Unknown { get; } =
        new(false, null, 0, 0, "(unknown)", TimeSpan.Zero, string.Empty, false);
}

/// <summary>
/// Crash forensics (NET-009). The app's riskiest code is direct COM vtable interop - ASIO, WASAPI,
/// XAudio2, D3D11/DXGI - where a fault surfaces as an <see cref="AccessViolationException"/> or a hard
/// native crash. Under Native AOT there is no .NET crash dialog, so without this a field crash leaves
/// only whatever the async <see cref="AppLogger"/> happened to flush, and a crash mid-recording leaves
/// the finalize unexplained.
///
/// <para><b>What it writes.</b> A dedicated <c>crash-*.log</c> per fault - exception plus the LIVE
/// recording state - and, for a fatal fault, a minidump via <c>MiniDumpWriteDump</c>. Both live under
/// the application directory, next to <see cref="AppLogger"/>'s <c>logs</c> folder: the app ships as a
/// portable ZIP and already resolves its log directory from <c>AppDomain.CurrentDomain.BaseDirectory</c>,
/// so forensics stay with the install and travel with a bug report. Keeping logs and dumps in one tree
/// is deliberate - a report is only useful with both.</para>
///
/// <para><b>Every entry point here is total.</b> This runs while the process is already dying: nothing
/// may throw, and a dump failure must never mask the original exception. All paths are wrapped, and
/// <see cref="HandleException"/> returns the report path (or null) instead of throwing.</para>
///
/// <para><b>AOT-safe.</b> One plain <c>DllImport</c>, no reflection, no dynamic code.</para>
/// </summary>
public static class CrashForensics
{
    /// <summary>Crash artifacts kept per kind. Older ones are deleted on every new fault and at prune time.</summary>
    public const int KeepLastCount = 10;

    // MiniDumpType: a NORMAL dump plus thread info. Deliberately NOT MiniDumpWithFullMemory - a full
    // dump of a live capture process can be gigabytes and slow, which is the wrong trade inside a
    // crash handler that may be racing process teardown.
    private const uint MiniDumpNormal = 0x00000000;
    private const uint MiniDumpWithThreadInfo = 0x00001000;

    private static string _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
    private static string _dumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crashdumps");

    private static Func<CrashState>? _stateProvider;
    private static int _handling;
    private static int _installed;

    /// <summary>Where crash reports are written. Follows <see cref="AppLogger"/>'s <c>logs</c> folder.</summary>
    public static string LogDir => _logDir;

    /// <summary>Where minidumps are written.</summary>
    public static string DumpDir => _dumpDir;

    /// <summary>True once <see cref="Install"/> has wired the handlers.</summary>
    public static bool IsInstalled => Volatile.Read(ref _installed) != 0;

    /// <summary>
    /// Points the crash directories somewhere else. Exists so the headless suite can assert the writer
    /// without littering its own output directory; production never calls it, and the defaults are the
    /// app directory.
    /// </summary>
    public static void UseDirectories(string logDir, string dumpDir)
    {
        _logDir = logDir ?? _logDir;
        _dumpDir = dumpDir ?? _dumpDir;
    }

    /// <summary>
    /// Registers the live-state readout. Supplied by the composition root, which is the only place
    /// that holds both the session and the broadcast state.
    /// </summary>
    public static void RegisterStateProvider(Func<CrashState>? provider) => _stateProvider = provider;

    /// <summary>
    /// Builds the state readout from a live session. Every read is guarded: a recording session in the
    /// middle of a native fault may throw on any of them, and forensics must still record something.
    /// </summary>
    public static CrashState CaptureState(CaptureSession? session, BroadcastState broadcastState)
    {
        if (session == null) return CrashState.Unknown;

        try
        {
            RecordingHealthSnapshot health = session.GetRecordingHealthSnapshot();
            return new CrashState(
                IsRecording: session.IsRecording,
                RecordingPath: session.CurrentRecordingPath,
                FramesWritten: session.RecordingFramesWritten,
                SegmentIndex: session.RecordingSegmentIndex,
                BroadcastState: broadcastState.ToString(),
                MediaDuration: health.MediaDuration,
                EncoderName: health.EncoderName ?? string.Empty,
                IsConnected: session.IsConnected);
        }
        catch (Exception ex)
        {
            return CrashState.Unknown with { EncoderName = $"state read failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Arms the process-wide handlers. Called first thing in <c>Program.Main</c>, before anything that
    /// can fault - a handler installed later is a handler that misses the crash it exists for.
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                HandleException(
                    "AppDomain.UnhandledException",
                    e.ExceptionObject as Exception ?? new Exception($"Non-CLR exception: {e.ExceptionObject}"),
                    fatal: true);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"CrashForensics: could not wire AppDomain.UnhandledException: {ex.Message}");
        }

        try
        {
            // Deliberately NOT fatal: an unobserved task exception is usually survivable, so it gets a
            // report and the state readout but no minidump - a dump per survivable event would be both
            // misleading and expensive. Marked observed so it cannot escalate later.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                HandleException("TaskScheduler.UnobservedTaskException", e.Exception, fatal: false);
                e.SetObserved();
            };
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"CrashForensics: could not wire TaskScheduler.UnobservedTaskException: {ex.Message}");
        }
    }

    /// <summary>
    /// Records one fault. Flushes the async log, writes the minidump when <paramref name="fatal"/>,
    /// writes <c>crash-*.log</c> synchronously, prunes to <see cref="KeepLastCount"/>, and returns the
    /// report path. Returns null when a report could not be produced. Never throws.
    /// </summary>
    public static string? HandleException(string source, Exception? exception, bool fatal = true)
    {
        // Re-entrancy guard: a fault raised while we are already reporting must not recurse.
        if (Interlocked.Exchange(ref _handling, 1) != 0) return null;

        try
        {
            string exceptionText = exception?.ToString() ?? "(no exception object)";
            CrashState state = CaptureCurrentState();

            // 1. Drain the async log queue first. The background writer may never run again, and the
            //    lines immediately before a crash are the most valuable ones in the file.
            AppLogger.Flush();

            // 2. Dump. best-effort by design: this may itself fail (a full disk, an unwritable
            //    directory, dbghelp missing) and that must never mask the original exception.
            string? dumpPath = fatal ? TryWriteDump() : null;

            // 3. The crash report, written synchronously rather than through the queue.
            string reportPath = Path.Combine(_logDir, $"crash-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
            string report = BuildCrashReport(source, exceptionText, state, dumpPath, DateTime.Now);
            if (!TryWriteAllText(reportPath, report))
            {
                reportPath = string.Empty;
            }

            // 4. Prune, then leave a breadcrumb in the normal log too.
            EnforceKeepLastCount();

            AppLogger.LogError(
                $"CRASH ({source}): {exception?.Message ?? "(no exception)"} | " +
                $"recording={(state.IsRecording ? "YES" : "no")}, segment={state.SegmentIndex}, " +
                $"frames={state.FramesWritten} | report={reportPath}, dump={dumpPath ?? "(none)"}");
            AppLogger.Flush();

            return reportPath.Length == 0 ? null : reportPath;
        }
        catch
        {
            // Nothing left to try: the process is dying and this path must not throw.
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _handling, 0);
        }
    }

    /// <summary>
    /// Renders the crash report. Pure apart from the caller-supplied timestamp, so the suite asserts
    /// the exact content without a real crash.
    /// </summary>
    public static string BuildCrashReport(
        string source, string exceptionText, CrashState state, string? dumpPath, DateTime whenLocal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("==== OMT Capture Studio crash report ====");
        sb.AppendLine($"When        : {whenLocal:yyyy-MM-dd HH:mm:ss.fff} (local)");
        sb.AppendLine($"Source      : {source}");
        sb.AppendLine();
        sb.AppendLine("---- exception ----");
        sb.AppendLine(exceptionText);
        sb.AppendLine();
        sb.AppendLine("---- active state at crash ----");
        sb.AppendLine($"Recording   : {(state.IsRecording ? "YES" : "no")}");
        sb.AppendLine($"File        : {state.RecordingPath ?? "(none)"}");
        sb.AppendLine($"Frames      : {state.FramesWritten}");
        sb.AppendLine($"Segment     : {state.SegmentIndex}");
        sb.AppendLine($"Broadcast   : {state.BroadcastState}");
        sb.AppendLine($"Connected   : {(state.IsConnected ? "YES" : "no")}");
        TimeSpan media = state.MediaDuration;
        sb.AppendLine($"Media       : {(int)media.TotalHours:00}:{media.Minutes:00}:{media.Seconds:00}.{media.Milliseconds / 10:00}");
        sb.AppendLine($"Encoder     : {(string.IsNullOrWhiteSpace(state.EncoderName) ? "(none)" : state.EncoderName)}");
        sb.AppendLine();
        sb.AppendLine($"Dump        : {dumpPath ?? "(not written)"}");
        return sb.ToString();
    }

    /// <summary>Number of minidumps currently on disk.</summary>
    public static int CountDumps() => CountMatching(_dumpDir, "crash-*.dmp");

    /// <summary>
    /// The next-launch notice. Returns the number of dumps present, logging a line naming the newest so
    /// a field operator knows what to attach to a bug report. Returns 0 (and stays silent) when clean.
    /// </summary>
    public static int ReportPendingDumps()
    {
        int count = CountDumps();
        if (count <= 0) return 0;

        AppLogger.LogError(
            $"A crash dump was saved ({count}) in {_dumpDir}. Newest: {NewestDumpPath() ?? "(unknown)"}. " +
            "Please attach the newest crash-*.log and crash-*.dmp to any bug report.");

        return count;
    }

    /// <summary>
    /// Enforces keep-last-N over BOTH crash logs and dumps, newest preserved. The cap is a count rather
    /// than a byte budget: dumps of this shape are small, and a count is what an operator can reason
    /// about. Returns how many files were removed.
    /// </summary>
    public static int EnforceKeepLastCount(int keep = KeepLastCount)
    {
        return Prune(_dumpDir, "crash-*.dmp", keep) + Prune(_logDir, "crash-*.log", keep);
    }

    private static CrashState CaptureCurrentState()
    {
        Func<CrashState>? provider = _stateProvider;
        if (provider == null) return CrashState.Unknown;

        try
        {
            return provider();
        }
        catch
        {
            // A provider that faults must not stop the report being written.
            return CrashState.Unknown;
        }
    }

    private static string? NewestDumpPath()
    {
        try
        {
            var files = new List<string>(Directory.GetFiles(_dumpDir, "crash-*.dmp"));
            if (files.Count == 0) return null;

            string newest = files[0];
            DateTime newestTime = File.GetLastWriteTimeUtc(newest);
            for (int i = 1; i < files.Count; i++)
            {
                DateTime t = File.GetLastWriteTimeUtc(files[i]);
                if (t > newestTime)
                {
                    newestTime = t;
                    newest = files[i];
                }
            }

            return newest;
        }
        catch
        {
            return null;
        }
    }

    private static int CountMatching(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int Prune(string dir, string pattern, int keep)
    {
        try
        {
            if (keep < 0 || !Directory.Exists(dir)) return 0;

            string[] files = Directory.GetFiles(dir, pattern);
            if (files.Length <= keep) return 0;

            var infos = new List<FileInfo>(files.Length);
            foreach (string f in files)
            {
                try { infos.Add(new FileInfo(f)); } catch { /* vanished; ignore */ }
            }

            infos.Sort(static (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            int removed = 0;
            for (int i = keep; i < infos.Count; i++)
            {
                try
                {
                    infos[i].Delete();
                    removed++;
                }
                catch { /* in use; better to keep one too many than to throw */ }
            }

            return removed;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryWriteAllText(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _logDir);
            File.WriteAllText(path, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryWriteDump()
    {
        try
        {
            Directory.CreateDirectory(_dumpDir);

            string path = Path.Combine(_dumpDir, $"crash-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.dmp");
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            bool written = MiniDumpWriteDump(
                Process.GetCurrentProcess().Handle,
                (uint)Environment.ProcessId,
                stream.SafeFileHandle.DangerousGetHandle(),
                MiniDumpNormal | MiniDumpWithThreadInfo,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (!written)
            {
                stream.Dispose();
                TryDelete(path);
                return null;
            }

            return path;
        }
        catch
        {
            // A dump failure is reported as "not written" and never escalates.
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    [DllImport("Dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);
}
