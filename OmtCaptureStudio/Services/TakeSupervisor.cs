using System;
using System.Threading.Tasks;

using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Forensics;
using OmtCaptureStudio.Services.Sources;

namespace OmtCaptureStudio.Services;

/// <summary>
/// The recorder operations the take supervisor drives. Deliberately narrow: it is exactly the
/// boundary evaluation and the single stop/roll path the ADR-0002 recovery policy needs, so a
/// headless fake can stand in for the whole audio and transport stack.
/// <para>
/// <see cref="StreamRecorderService"/> satisfies this as-is. Every member it exposes here already
/// forwards into <see cref="RecordingArm"/>, so a stop routed through the supervisor still goes
/// through the arm's single finalize path and never beside it (ADR-0005).
/// </para>
/// </summary>
public interface ITakeRecorder
{
    bool IsRecording { get; }

    int CurrentSegmentIndex { get; }

    string? CurrentRecordingPath { get; }

    RecordingBoundary EvaluateBoundary(RecordingConfig config);

    bool RollSegment(RecordingConfig config, StreamFormat format, RecordingStopReason reason);

    bool StartNextSegment(RecordingConfig config, StreamFormat format);

    void StopRecording(RecordingStopReason reason);
}

/// <summary>
/// The ADR-0002 take-supervisor state machine, extracted from <see cref="CaptureSession"/> so the
/// recovery policy can be exercised without an audio engine or a transport. Owns the take
/// lifecycle, the watchdog arming decision, the scheduled/segmented boundary evaluation, the
/// outage boundary, the reconnect backoff loop and the bounded grace expiry.
/// <para>
/// Driven by ONE entry point, <see cref="Tick"/>, which the host calls from its health tick — the
/// same mirror of the ADR-0005 RecordingArm/facade split that <see cref="StreamRecorderService"/>
/// performs for the recorder: the host owns the transport and the frame path, this owns the policy.
/// </para>
/// <para>
/// Everything the policy needs arrives through the constructor: the recorder seam, the watchdog,
/// a source probe and the clock. It never reaches back into the host, so there is nothing to keep
/// in step between the two but the tick itself. The tick is 1 Hz and allocation-free on the
/// detection path; the frame callbacks are untouched.
/// </para>
/// </summary>
public sealed class TakeSupervisor : IDisposable
{
    private readonly ITakeRecorder _recorder;
    private readonly InputHealthWatchdog _watchdog;
    private readonly Func<IMediaSource?> _sourceProvider;
    private readonly Func<long> _clock;

    // A "take" spans one StartRecording…StopRecording, possibly across several segments when an
    // input-loss outage forces a finalize/resume. Config/format are retained so the take can resume
    // into the next …_partNN segment; _takeInProgress stays true through an outage (recording is not
    // active, but the take is) so the watchdog keeps watching for restored frames.
    private RecordingConfig? _activeRecordingConfig;
    private StreamFormat? _activeRecordingFormat;
    private bool _takeInProgress;
    private long _outageStartMs;
    private int _reconnectAttempt;
    private long _nextReconnectMs;
    private bool _recoveryExpired;
    private volatile bool _reconnectInFlight;
    private volatile bool _disposed;

    /// <summary>Raised when an outage boundary is crossed and the current segment has been finalized.</summary>
    public event Action? InputLost;

    /// <summary>Raised when frames return within the grace period and the take resumes.</summary>
    public event Action? InputRecovered;

    /// <summary>Raised when the bounded grace period expires without a restored feed.</summary>
    public event Action? InputRecoveryFailed;

    /// <summary>Raised when the take rolls into its next …_partNN segment; carries the new index.</summary>
    public event Action<int>? SegmentRolledOver;

    /// <summary>Raised when a take ends on the max-duration boundary.</summary>
    public event Action? MaxDurationReached;

    /// <summary>Raised when a take ends on the wall-clock scheduled-stop boundary.</summary>
    public event Action? ScheduledStopReached;

    /// <summary>True while a take is in progress — including across an outage that has not yet expired.</summary>
    public bool TakeInProgress => _takeInProgress;

    /// <summary>
    /// Bounded grace period to keep attempting auto-reconnect before giving up. Default 30 s,
    /// applied per take from <see cref="RecordingConfig.InputRecoveryGraceSeconds"/>.
    /// </summary>
    public TimeSpan InputRecoveryGracePeriod { get; set; }

    /// <summary>
    /// Wall-clock window with no video frame that counts as input loss. Operator-configurable;
    /// default 5 s, applied per take from <see cref="RecordingConfig.InputLossThresholdSeconds"/>.
    /// </summary>
    public TimeSpan InputLossThreshold
    {
        get => TimeSpan.FromMilliseconds(_watchdog.ThresholdMs);
        set => _watchdog.ThresholdMs = (long)Math.Max(1, value.TotalMilliseconds);
    }

    /// <summary>
    /// The watchdog is injected rather than created here: the frame path already notifies it
    /// directly on the media thread (zero-alloc, no lock), and that path is deliberately not
    /// routed through this object.
    /// </summary>
    public TakeSupervisor(
        ITakeRecorder recorder,
        InputHealthWatchdog watchdog,
        Func<IMediaSource?> sourceProvider,
        Func<long> clock,
        TimeSpan? defaultGracePeriod = null)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
        _sourceProvider = sourceProvider ?? throw new ArgumentNullException(nameof(sourceProvider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        InputRecoveryGracePeriod = defaultGracePeriod ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Starts a take: remembers how to resume it and applies the operator's health policy.
    /// </summary>
    public void BeginTake(RecordingConfig config, StreamFormat format)
    {
        _activeRecordingConfig = config;
        _activeRecordingFormat = format;
        _takeInProgress = true;
        _recoveryExpired = false;

        InputLossThreshold = TimeSpan.FromSeconds(
            config.InputLossThresholdSeconds > 0 ? config.InputLossThresholdSeconds : 5.0);
        InputRecoveryGracePeriod = TimeSpan.FromSeconds(
            config.InputRecoveryGraceSeconds > 0 ? config.InputRecoveryGraceSeconds : 30.0);

        UpdateWatchdogArming();
    }

    /// <summary>Ends the take and disarms the watchdog. A no-op on the segment-resume path.</summary>
    public void EndTake()
    {
        _takeInProgress = false;
        _activeRecordingConfig = null;
        _activeRecordingFormat = null;
        _recoveryExpired = false;
        _reconnectAttempt = 0;
        _nextReconnectMs = 0;
        _outageStartMs = 0;
        _watchdog.Disarm();
    }

    /// <summary>
    /// The single health-tick entry point. Order is load-bearing and preserved from ADR-0002: the
    /// scheduled/segmented boundaries are evaluated first, then arming, then the watchdog edge —
    /// so a take that ends on a boundary this tick disarms before the poll can report a false loss.
    /// Detection is allocation-free; only a real boundary allocates (its log line).
    /// </summary>
    public void Tick()
    {
        if (_disposed) return;

        // NET-008: scheduled/segmented-capture boundaries ride the same tick (no timer of their own).
        EvaluateRecordingBoundaries();

        UpdateWatchdogArming();

        switch (_watchdog.Poll())
        {
            case InputHealthTransition.Lost:
                HandleOutageBoundary();
                break;
            case InputHealthTransition.Restored:
                HandleRecovery();
                break;
        }

        if (!_watchdog.IsLost) return;

        long now = _clock();
        if (now >= _nextReconnectMs)
        {
            _reconnectAttempt++;
            _nextReconnectMs = now + (long)ReconnectBackoff.DelayFor(_reconnectAttempt).TotalMilliseconds;
            RequestSourceReconnect();
        }

        if (!_recoveryExpired && now - _outageStartMs >= (long)InputRecoveryGracePeriod.TotalMilliseconds)
        {
            _recoveryExpired = true;
            AppLogger.LogError(
                $"Input lost for over {InputRecoveryGracePeriod.TotalSeconds:0}s with no frames; giving up " +
                "auto-reconnect and stopping the take. The finalized segment remains playable.");
            EndTake();
            InputRecoveryFailed?.Invoke();
        }
    }

    /// <summary>
    /// NET-008: the scheduled / segmented-capture boundary, evaluated on the health tick beside the
    /// input-loss poll. Pure comparisons — no timer, no allocation, no I/O beyond the size probe a
    /// configured GB rollover asks for.
    ///
    /// <para>A rollover is a Disarm+Arm cycle through the arm's single finalize path and leaves the take
    /// OPEN, exactly like the ADR-0002 outage boundary. Max-duration and scheduled-stop END the take:
    /// they run the same <see cref="EndTake"/> plus stop sequence as an operator stop, so the watchdog
    /// and the take bookkeeping converge on one path.</para>
    /// </summary>
    private void EvaluateRecordingBoundaries()
    {
        if (!_recorder.IsRecording) return;
        if (_activeRecordingConfig is not { } config || _activeRecordingFormat is not { } format) return;

        RecordingBoundary boundary = _recorder.EvaluateBoundary(config);
        if (boundary == RecordingBoundary.None) return;

        switch (boundary)
        {
            case RecordingBoundary.SegmentRollover:
                if (_recorder.RollSegment(config, format, RecordingStopReason.SegmentRollover))
                {
                    AppLogger.LogInfo(
                        $"Segment rollover: recording continues in segment {_recorder.CurrentSegmentIndex}: " +
                        $"{_recorder.CurrentRecordingPath}");
                    SegmentRolledOver?.Invoke(_recorder.CurrentSegmentIndex);
                }
                break;

            case RecordingBoundary.MaxDuration:
                AppLogger.LogInfo(
                    $"Max take duration ({config.MaxDurationMinutes:0.#} min) reached; stopping the take.");
                EndTake();
                _recorder.StopRecording(RecordingStopReason.MaxDuration);
                MaxDurationReached?.Invoke();
                break;

            case RecordingBoundary.ScheduledStop:
                AppLogger.LogInfo("Scheduled stop time reached; stopping the take.");
                EndTake();
                _recorder.StopRecording(RecordingStopReason.ScheduledStop);
                ScheduledStopReached?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Arms the watchdog only while a take is in progress over a recoverable (network) source.
    /// In-process sources — test signal, host desktop capture — are never watched (ADR-0002).
    /// </summary>
    private void UpdateWatchdogArming()
    {
        bool shouldArm = _takeInProgress
            && _sourceProvider() is IRecoverableMediaSource { RequiresInputHealthWatch: true };

        if (shouldArm && !_watchdog.IsArmed)
        {
            _watchdog.Arm();
        }
        else if (!shouldArm && _watchdog.IsArmed)
        {
            _watchdog.Disarm();
        }
    }

    private void HandleOutageBoundary()
    {
        long now = _clock();
        _outageStartMs = now;
        _reconnectAttempt = 0;
        _nextReconnectMs = now;
        _recoveryExpired = false;

        AppLogger.LogError(
            $"Input lost: no video frame for {InputLossThreshold.TotalSeconds:0.#}s while recording. " +
            "Finalizing the current segment before attempting to reconnect.");

        // Finalize AT the boundary, before anything else: the frozen last frame can never be written
        // again, and the outage becomes an explicit named discontinuity on disk rather than a silent
        // stitch (ADR-0002 decision 3). Recording resumes into the next …_partNN segment only if frames
        // return within the grace period.
        if (_recorder.IsRecording)
        {
            _recorder.StopRecording(RecordingStopReason.InputLoss);
        }

        InputLost?.Invoke();
    }

    private void HandleRecovery()
    {
        _recoveryExpired = false;
        _reconnectAttempt = 0;

        bool resumed = false;
        if (_takeInProgress && _activeRecordingConfig != null && _activeRecordingFormat != null)
        {
            resumed = _recorder.StartNextSegment(_activeRecordingConfig, _activeRecordingFormat);
        }

        AppLogger.LogInfo(resumed
            ? $"Input restored; recording resumed into segment {_recorder.CurrentSegmentIndex}: {_recorder.CurrentRecordingPath}"
            : "Input restored; recording was not resumed (take is no longer active).");

        InputRecovered?.Invoke();
    }

    private void RequestSourceReconnect()
    {
        if (_reconnectInFlight) return;
        if (_sourceProvider() is not IRecoverableMediaSource { RequiresInputHealthWatch: true } recoverable) return;

        _reconnectInFlight = true;
        AppLogger.LogInfo($"Auto-reconnect attempt {_reconnectAttempt} for the network feed.");

        // Off the calling (UI health-tick) thread: OmtReceiverService.Connect bounds-joins the previous
        // receive loop, and blocking the UI thread on media teardown is forbidden (CODING_STANDARDS §2.3).
        Task.Run(() =>
        {
            try
            {
                if (!_disposed)
                {
                    recoverable.EnsureConnected();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Auto-reconnect attempt failed: {ex.Message}", ex);
            }
            finally
            {
                _reconnectInFlight = false;
            }
        });
    }

    /// <summary>
    /// Idempotent. Stops any in-flight reconnect from starting a new one; the policy's own state is
    /// left alone so a host that disposes mid-take still runs its normal end-of-take teardown.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _reconnectInFlight = false;
    }
}
