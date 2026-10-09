using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OmtCaptureStudio;

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

    private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
    private static readonly Channel<string> _logChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedLogLines)
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private static long _droppedCount;


    static AppLogger()
    {
        if (!Directory.Exists(LogDir))
        {
            Directory.CreateDirectory(LogDir);
        }

        Task.Run(ProcessLogQueueAsync);
    }

    private static string GetLogFilePath()
    {
        return Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
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
