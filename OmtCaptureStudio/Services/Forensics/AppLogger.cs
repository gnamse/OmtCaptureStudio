using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OmtCaptureStudio.Services.Forensics;

/// <summary>
/// Lightweight, Native AOT-safe logger that writes to rolling daily files.
/// </summary>
public static class AppLogger
{
    // Bounded so an error burst (e.g. a flapping network source) can never grow an
    // unbounded in-memory backlog. Drop-oldest keeps the most recent line (the most
    // useful for diagnosis) and never blocks the caller; drops are summarized by a
    // single marker line so they aren't silently lost.
    private const int MaxQueuedLogLines = 4096;

    // NET-017: NOT readonly any more. The directory is delegated to LogLocation's resolution so a
    // read-only install redirects log lines to the fallback ALONGSIDE the crash artifacts. A field read
    // (not a re-computation), so the live logging path costs exactly what it did before.
    private static string _logDir = LogLocation.PrimaryLogDirectory;

    /// <summary>
    /// Where log lines are written. Follows <see cref="CrashForensics"/>'s location so a crash report is
    /// always surrounded by the lines that led to it - the link the read-only-install defect broke.
    /// </summary>
    public static string LogDir => _logDir;
    private static readonly Channel<string> _logChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedLogLines)
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private static long _droppedCount;


    static AppLogger()
    {
        try
        {
            if (!Directory.Exists(LogDir))
            {
                Directory.CreateDirectory(LogDir);
            }
        }
        catch { }

        Task.Run(ProcessLogQueueAsync);
    }

    private static string GetLogFilePath()
    {
        return Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
    }

    /// <summary>
    /// Best-effort bounded drain of the queued lines (NET-009). Called on the crash path: the process
    /// may not survive long enough for the background writer to reach them, and the lines immediately
    /// before a crash are the most valuable ones in the file. Never throws, never blocks longer than
    /// <paramref name="timeoutMs"/>.
    /// </summary>
    /// <summary>
    /// Applies a resolved location (NET-017). Called once by the resolver, never per write. The default
    /// - application directory - stays in place until then, so the headless suite works with no runtime
    /// resources and nothing to configure.
    /// </summary>
    public static void ConfigureLocation(string logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory)) return;

        _logDir = logDirectory;
        LogLocation.TryCreateDirectory(logDirectory);
    }

    public static void Flush(int timeoutMs = 1500)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (_logChannel.Reader.Count == 0) return;
                Thread.Sleep(5);
            }
        }
        catch
        {
            // Reader.Count is not supported by every channel implementation; fall back to a short fixed
            // grace period so the background writer still gets its chance.
            try { Thread.Sleep(50); } catch { }
        }
    }

    public static void LogInfo(string message) => Log("INFO", message);
    public static void LogError(string message) => Log("ERROR", message);
    public static void LogError(string message, Exception ex) => Log("ERROR", $"{message} | Exception: {ex}");
    
    private static void Log(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var logLine = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
        if (!_logChannel.Writer.TryWrite(logLine))
        {
            // Channel is full (burst): drop-oldest already discarded an older line.
            // Count it so the reader can emit a single diagnostic marker.
            Interlocked.Increment(ref _droppedCount);
        }
    }

    private static void FlushDroppedMarker()
    {
        var dropped = Interlocked.Exchange(ref _droppedCount, 0);
        if (dropped > 0)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            WriteLine($"[{timestamp}] [WARN] {dropped} log line(s) dropped (log channel full){Environment.NewLine}");
        }
    }

    private static void WriteLine(string logLine)
    {
        try
        {
            File.AppendAllText(GetLogFilePath(), logLine);
        }
        catch
        {
            // Ignore logging errors to prevent crashing the app
        }
    }

    private static async Task ProcessLogQueueAsync()
    {
        var reader = _logChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var logLine))
                {
                    WriteLine(logLine);
                }

                // After draining, summarize any drops from the burst in one marker line.
                FlushDroppedMarker();
            }
        }
        catch
        {
            // Background loop shouldn't crash
        }
    }
}
