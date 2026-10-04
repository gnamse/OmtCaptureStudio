using System;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OmtCaptureStudio;

/// <summary>
/// Lightweight, Native AOT-safe logger that writes to rolling daily files.
/// </summary>
public static class AppLogger
{
    private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
    private static readonly Channel<string> _logChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });

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
        _logChannel.Writer.TryWrite(logLine);
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
                    try
                    {
                        File.AppendAllText(GetLogFilePath(), logLine);
                    }
                    catch
                    {
                        // Ignore logging errors to prevent crashing the app
                    }
                }
            }
        }
        catch
        {
            // Background loop shouldn't crash
        }
    }
}
