using System.IO.Pipes;
using System.Text.RegularExpressions;
using OmtCaptureStudio.Services.Sinks;

namespace OmtCaptureStudio.Tests;

partial class Program
{
    static void TestFfmpegPipes()
    {
        Console.WriteLine("\n[Step 0.706] Verifying ffmpeg named-pipe hardening...");

        string video = FfmpegPipeServer.CreatePipeName("video");
        string audio = FfmpegPipeServer.CreatePipeName("audio");
        Expect(video != audio, "Each pipe name must be independently random.");
        Expect(Regex.IsMatch(video, "^omt_video_[0-9a-f]{32}$"),
            $"Video pipe name must be a full GUID, not an 8-hex prefix. Got '{video}'.");
        Expect(Regex.IsMatch(audio, "^omt_audio_[0-9a-f]{32}$"),
            $"Audio pipe name must be a full GUID. Got '{audio}'.");

        string? helperPath = FindRepoSource(Path.Combine("OmtCaptureStudio", "Services", "Sinks", "FfmpegPipeServer.cs"));
        string? hostPath = FindRepoSource(Path.Combine("OmtCaptureStudio", "Services", "Sinks", "FfmpegProcessHost.cs"));
        if (helperPath != null && hostPath != null)
        {
            string helper = File.ReadAllText(helperPath);
            string host = File.ReadAllText(hostPath);
            Expect(helper.Contains("PipeOptions.FirstPipeInstance", StringComparison.Ordinal),
                "The pipe server must request FirstPipeInstance.");
            Expect(helper.Contains("PipeOptions.CurrentUserOnly", StringComparison.Ordinal),
                "The pipe server must restrict the DACL to the current user via CurrentUserOnly.");
            Expect(helper.Contains("NamedPipeServerStreamAcl.Create", StringComparison.Ordinal),
                "The server must be created through NamedPipeServerStreamAcl so ReadAttributes can be requested.");
            Expect(helper.Contains("maxNumberOfServerInstances: 1", StringComparison.Ordinal),
                "The pipe server must allow only one instance.");
            Expect(helper.Contains("PipeAccessRights.ReadAttributes", StringComparison.Ordinal),
                "The server handle needs ReadAttributes so the client process id can be read.");
            Expect(!helper.Contains("new PipeSecurity", StringComparison.Ordinal),
                "An explicit PipeSecurity cannot be combined with CurrentUserOnly — the runtime throws.");
            Expect(host.Contains("FfmpegPipeServer.Create", StringComparison.Ordinal),
                "The host must create pipes through FfmpegPipeServer.");
            Expect(host.Contains("VerifyPipeClient", StringComparison.Ordinal),
                "The host must verify the connected client is the ffmpeg process it spawned.");
            Expect(!host.Contains("[..8]", StringComparison.Ordinal) && !host.Contains("pipeSuffix", StringComparison.Ordinal),
                "The host must not truncate the pipe name to 8 hex digits.");
        }
        else
        {
            Console.WriteLine(" [SKIP] pipe sources not reachable from the test binary — structural check skipped.");
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine(" -> [SKIP] named-pipe ACL creation is Windows-only; name and source checks ran.");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" -> Pipe names are full GUIDs and the host goes through FfmpegPipeServer.");
            Console.ResetColor();
            return;
        }

        using (var first = FfmpegPipeServer.Create(video, 4096))
        {
            bool rejected = false;
            try
            {
                using var second = FfmpegPipeServer.Create(video, 4096);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rejected = true;
            }

            Expect(rejected, "A second server on the same name must fail (FirstPipeInstance, one instance).");

            using var client = new NamedPipeClientStream(".", video, PipeDirection.In);
            Exception? clientError = null;
            var clientThread = new Thread(() =>
            {
                try { client.Connect(2000); }
                catch (Exception ex) { clientError = ex; }
            })
            {
                IsBackground = true,
                Name = "FfmpegPipe_TestClient"
            };
            clientThread.Start();

            bool serverConnected = first.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(2)) && first.IsConnected;
            clientThread.Join(2000);
            if (clientError != null)
            {
                throw new Exception($"The same-user client must be able to connect to a CurrentUserOnly pipe: {clientError.Message}");
            }

            Expect(serverConnected && client.IsConnected, "The test client must connect to the server.");
            Expect(FfmpegPipeServer.TryGetClientProcessId(first, out int clientPid),
                "GetNamedPipeClientProcessId must succeed on the server handle.");
            Expect(clientPid == Environment.ProcessId,
                $"The connected client must be this process. Got {clientPid}, expected {Environment.ProcessId}.");
            Expect(FfmpegPipeServer.IsExpectedClient(first, Environment.ProcessId),
                "IsExpectedClient must accept the process that actually connected.");
            Expect(!FfmpegPipeServer.IsExpectedClient(first, Environment.ProcessId + 1),
                "IsExpectedClient must reject any other process id.");
            Expect(!FfmpegPipeServer.IsExpectedClient(first, 0),
                "A missing expected process id must fail closed.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" -> Pipe names are full GUIDs, a second instance is rejected, and the client PID is checked!");
        Console.ResetColor();
    }
}
