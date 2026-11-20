using System.IO.Pipes;
using OmtCaptureStudio.Interop;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// The named pipes ffmpeg reads raw video and audio from.
///
/// <para>The name is a full GUID. An 8-hex prefix is guessable; a second process that creates
/// that name first would receive the recording. <see cref="PipeOptions.FirstPipeInstance"/> makes
/// creation fail if the name already exists, and <c>maxNumberOfServerInstances</c> is 1 so the
/// name cannot be joined later either.</para>
///
/// <para><see cref="PipeOptions.CurrentUserOnly"/> is the ACL. The runtime builds a
/// <c>PipeSecurity</c> that grants full control to the current user and nobody else, which is
/// the token the ffmpeg child inherits. An explicit <c>PipeSecurity</c> cannot be combined with
/// that flag (the runtime throws), so the flag is the restriction.</para>
///
/// <para>The pipe is created through <see cref="NamedPipeServerStreamAcl.Create"/> only so the
/// server handle can also request <see cref="PipeAccessRights.ReadAttributes"/>. The ordinary
/// constructor has no access-rights parameter, and <c>GetNamedPipeClientProcessId</c> needs that
/// right to confirm the client is the ffmpeg process we spawned. Direct calls only — no
/// reflection — so the construction stays Native AOT safe.</para>
/// </summary>
public static class FfmpegPipeServer
{
    public static string CreatePipeName(string role)
    {
        return $"omt_{role}_{Guid.NewGuid():N}";
    }

    public static NamedPipeServerStream Create(string pipeName, int bufferBytes)
    {
        PipeOptions options = PipeOptions.Asynchronous
            | PipeOptions.CurrentUserOnly
            | PipeOptions.FirstPipeInstance;

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: bufferBytes,
            outBufferSize: bufferBytes,
            pipeSecurity: null,
            inheritability: HandleInheritability.None,
            additionalAccessRights: PipeAccessRights.ReadAttributes);
    }

    public static bool TryGetClientProcessId(NamedPipeServerStream pipe, out int processId)
    {
        processId = 0;
        if (pipe is null) return false;
        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint nativeId)) return false;
        if (nativeId == 0) return false;
        processId = unchecked((int)nativeId);
        return true;
    }

    /// <summary>
    /// True only when the connected client is <paramref name="expectedProcessId"/>.
    /// An unreadable id is a failure: the writer must not feed a pipe it cannot attribute.
    /// </summary>
    public static bool IsExpectedClient(NamedPipeServerStream pipe, int expectedProcessId)
    {
        return expectedProcessId > 0
            && TryGetClientProcessId(pipe, out int clientProcessId)
            && clientProcessId == expectedProcessId;
    }
}
