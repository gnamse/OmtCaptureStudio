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
    /// NET-005: the headless session fixture — the CaptureSession + NullRecordingSink + recorder + audio
    /// (+ discovery and MainWindowViewModel) wiring that every presentation step used to re-type by hand.
    /// Dispose order mirrors the original nested `using` blocks.
    /// </summary>
    private sealed class HeadlessSessionFixture : IDisposable
    {
        public NullRecordingSink Sink { get; }
        public AudioEngine Audio { get; }
        public StreamRecorderService Recorder { get; }
        public CaptureSession Session { get; }
        public OmtDiscoveryService? Discovery { get; }
        public MainWindowViewModel? ViewModel { get; }

        private HeadlessSessionFixture(bool withViewModel)
        {
            Sink = new NullRecordingSink();
            Audio = new AudioEngine();
            Recorder = new StreamRecorderService(Sink);
            Session = new CaptureSession(Audio, Recorder);

            if (withViewModel)
            {
                Discovery = new OmtDiscoveryService();
                ViewModel = new MainWindowViewModel(Session, Discovery);
            }
        }

        public static HeadlessSessionFixture Build() => new(withViewModel: false);
        public static HeadlessSessionFixture BuildWithViewModel() => new(withViewModel: true);

        public void Dispose()
        {
            ViewModel?.Dispose();
            Discovery?.Dispose();
            Session?.Dispose();
            Audio?.Dispose();
            Recorder?.Dispose();
        }
    }

    /// <summary>NET-005: one builder for the headless wiring; see <see cref="HeadlessSessionFixture"/>.</summary>
    static HeadlessSessionFixture BuildHeadlessSession(bool withViewModel = true)
        => withViewModel ? HeadlessSessionFixture.BuildWithViewModel() : HeadlessSessionFixture.Build();

    /// <summary>
    /// Fully DECODES the recorded file with FFmpeg (not just a header probe) — the real "playable"
    /// check. Fails loudly on a non-zero exit or any stderr output at <c>-v error</c>.
    /// </summary>
    static void VerifyDecodeClean(string path, string label)
    {
        if (!File.Exists(path))
        {
            throw new Exception($"{label}: expected recorded file is missing: {path}");
        }

        long size = new FileInfo(path).Length;
        if (size < 10_000)
        {
            throw new Exception($"{label}: recorded file suspiciously small ({size} bytes): {path}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-v error -i \"{path}\" -f null -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process p = Process.Start(psi) ?? throw new Exception($"{label}: could not start ffmpeg for decode verification.");
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);

        if (p.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
        {
            throw new Exception($"{label}: decode is NOT clean (exit={p.ExitCode}). ffmpeg said: {stderr.Trim()}");
        }

        Console.WriteLine($"[{Now()}] {label}: decode-clean, {size / 1024.0 / 1024.0:F2} MB -> {Path.GetFileName(path)}");
    }

    /// <summary>Poll a condition, ticking a side effect (e.g. CheckInputHealth) between attempts.</summary>
    static void WaitUntil(Func<bool> condition, int timeoutMs, string what, Action? tick = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            tick?.Invoke();
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new Exception($"Timed out after {timeoutMs} ms waiting for {what}.");
            }
            Thread.Sleep(100);
        }
    }

    /// <summary>Writes a file with an explicit timestamp, so keep-last-N is deterministic.</summary>
    static void WriteTemp(string path, string content, DateTime stampUtc)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, stampUtc);
    }

    private static void WaitForFormat(CaptureSession session, string what)
    {
        int tries = 0;
        while (!session.CurrentFormat.HasVideo && tries < 100)
        {
            Thread.Sleep(50);
            tries++;
        }
        if (!session.CurrentFormat.HasVideo)
        {
            throw new Exception($"{what} failed to deliver a video format in time.");
        }
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        int tries = 0;
        while (!condition() && tries < 100)
        {
            Thread.Sleep(25);
            tries++;
        }
        if (!condition())
        {
            throw new Exception($"{what} did not happen in time.");
        }
    }

    static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");

}
