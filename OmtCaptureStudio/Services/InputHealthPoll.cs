using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.Services;

/// <summary>
/// The UI health-tick hop. It does not live in <see cref="CaptureSession"/> or
/// <see cref="TakeSupervisor"/>: those types already have their one allowed thread hop
/// (NET-018), and a second one there fails the locality check. This object is the hop
/// the dispatcher timer uses so a segment finalize cannot freeze the shell, and so a
/// tick that arrives while a finalize is still running is dropped.
/// </summary>
public sealed class InputHealthPoll
{
    private readonly CaptureSession _session;
    private int _inFlight;

    public InputHealthPoll(CaptureSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// Starts one health tick on the thread pool and returns immediately. A call that overlaps
    /// a tick still in flight returns a completed task and does not start another.
    /// </summary>
    public Task Run()
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            try
            {
                _session.CheckInputHealth();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Input-health poll failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        });
    }
}
