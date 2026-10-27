using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Sinks;

namespace OmtCaptureStudio.Services;

/// <summary>
/// The recording arm/disarm sequence as ONE deep module (ADR-0005).
///
/// <para>This module owns the pipeline's most load-bearing invariant so that it is a property of the
/// code rather than a convention spread across three classes:</para>
/// <list type="bullet">
/// <item>the arm order — <b>reset the A/V clock → initialize the sink → flip the gate</b> — happens
/// inside <see cref="Arm"/>, so the clock can never be armed after recording is live;</item>
/// <item><b>one authoritative gate</b> (<see cref="IsRecording"/>) that this class is the only
/// mutation point for, read by the media frame callbacks, the recorder facade, the sink's stop edge
/// and the watchdog's boundary finalize;</item>
/// <item><b>one finalize path</b> (<see cref="Disarm"/>) that every stop trigger routes through —
/// user stop, low disk, input-loss boundary, sink failure and disposal differ only in the
/// <see cref="RecordingStopReason"/> they report.</item>
/// </list>
///
/// <para><b>Threading.</b> Arm and disarm take <c>_gateLock</c>. The gate itself is volatile and is
/// closed <i>before</i> the drain wait and the transport teardown, then <see cref="Drain"/> waits
/// (bounded) for a media thread already inside a write to return — replacing the previously
/// documented-but-unenforced stale-gate race with an enforced, bounded one.</para>
///
/// <para><b>Hot path.</b> <see cref="WriteVideoFrame"/> / <see cref="WriteAudioData"/> add two
/// allocation-free interlocked operations on the writers-in-flight counter and nothing else: no lock,
/// no allocation, no branch beyond the existing gate check.</para>
/// </summary>
public sealed class RecordingArm : IDisposable
{
    /// <summary>Bounded wait for in-flight writes to finish before the transport is torn down.</summary>
    public const int DrainTimeoutMs = 50;

    private readonly IRecordingSink _sink;
    private readonly bool _ownsSink;

    /// <summary>
    /// UTC-now source for the scheduled/segmented-capture boundaries (NET-008). Injected so the
    /// boundary tests drive a local clock and need zero real waits; the default reads the system clock.
    /// </summary>
    private readonly Func<long> _utcNowTicks;

    /// <summary>The A/V recording clock. It lives here because the arm must reset it before the gate flips.</summary>
    private readonly AudioClockResampler _avClock = new();

    private readonly object _gateLock = new();

    // The ONE gate. volatile: read on the media/source threads (writes, IsRecording, ElapsedTime) while
    // written under _gateLock on the disarm edge — a stale read would let a source thread walk into
    // sink teardown. This class is the only place it is ever assigned.
    private volatile bool _isRecording;

    // Writers currently inside a sink write. Disarm closes the gate, then drains this to zero (bounded).
    private int _writersInFlight;

    private int _disposed;

    // Take/segment state (ADR-0002 segment-per-outage).
    private string? _takeBasePath;
    private int _segmentIndex;
    private string? _currentRecordingPath;
    private long _recordStartTimestamp;

    // NET-008 boundary state. _takeStartUtcTicks survives a segment rollover (a rollover continues the
    // SAME take); _segmentStartUtcTicks is reset by every arm, including a rollover.
    private long _takeStartUtcTicks;
    private long _segmentStartUtcTicks;
    private long _framesWritten;
    private long _audioBytesWritten;
    private DateTime _lastDiskCheckUtc = DateTime.MinValue;

    // Stamped format of the active segment: the basis for MEDIA duration (frames / stamped fps) and
    // audio duration (PCM bytes / bytes-per-sample-frame). Never wall clock — that includes dead air.
    private double _recordFrameRate;
    private int _recordSampleRate;
    private int _recordChannels;

    public RecordingArm(IRecordingSink sink, bool ownsSink = false, Func<long>? utcNowTicks = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _ownsSink = ownsSink;
        _utcNowTicks = utcNowTicks ?? (static () => DateTime.UtcNow.Ticks);
        _sink.SinkError += OnSinkError;
    }

    /// <summary>The single authoritative recording gate. Every call site reads this.</summary>
    public bool IsRecording => _isRecording;

    public string? CurrentRecordingPath => _currentRecordingPath;
    public int CurrentSegmentIndex => _segmentIndex;
    public string? CurrentTakeBasePath => _takeBasePath;

    /// <summary>The active take's start (UTC ticks). Survives a segment rollover - the boundary clock.</summary>
    public long TakeStartUtcTicks => Interlocked.Read(ref _takeStartUtcTicks);

    /// <summary>The active SEGMENT's start (UTC ticks). Reset by every arm, including a rollover.</summary>
    public long SegmentStartUtcTicks => Interlocked.Read(ref _segmentStartUtcTicks);

    /// <summary>
    /// Bytes on disk for the ACTIVE segment (0 when idle or unreadable). MP4/MOV are written fragmented
    /// so the live length grows with the take; a read failure reports 0 rather than throwing on the
    /// UI tick path.
    /// </summary>
    public long CurrentSegmentBytes
    {
        get
        {
            string? path = _currentRecordingPath;
            if (path == null) return 0;

            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
    public TimeSpan ElapsedTime => _isRecording ? Stopwatch.GetElapsedTime(_recordStartTimestamp) : TimeSpan.Zero;
    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public long AudioBytesWritten => Interlocked.Read(ref _audioBytesWritten);

    /// <summary>Frames the recording transport discarded (drop-oldest) since recording started.</summary>
    public long VideoFramesDropped => _sink.VideoFramesDropped;

    /// <summary>Audio chunks the recording transport discarded since recording started.</summary>
    public long AudioChunksDropped => _sink.AudioChunksDropped;

    /// <summary>Encoder actually used this session (AutoHardware resolved), or empty when idle.</summary>
    public string ResolvedEncoderName => _sink.ResolvedEncoderName;

    /// <summary>Capped FFmpeg stderr tail, for the in-app diagnostics drawer.</summary>
    public string DiagnosticTail => _sink.DiagnosticTail;

    /// <summary>Live A/V drift ratio from the recording clock, for the health readout.</summary>
    public double AvDriftRatio => _avClock.Ratio;

    /// <summary>True when the drift ratio is pinned against its 0.9/1.1 safety clamp.</summary>
    public bool AvDriftClamped => _avClock.IsRatioClamped;

    /// <summary>
    /// Video track duration so far: frames written / stamped frame rate. Contains no dead air, which
    /// is exactly why it is the figure reported on stop (DEF-01).
    /// </summary>
    public TimeSpan MediaDuration => _recordFrameRate > 0
        ? TimeSpan.FromSeconds(Interlocked.Read(ref _framesWritten) / _recordFrameRate)
        : TimeSpan.Zero;

    /// <summary>Audio track duration so far, derived from interleaved 32-bit float PCM bytes.</summary>
    public TimeSpan AudioDuration => _recordSampleRate > 0 && _recordChannels > 0
        ? TimeSpan.FromSeconds(Interlocked.Read(ref _audioBytesWritten) / (double)(_recordSampleRate * _recordChannels * 4))
        : TimeSpan.Zero;

    /// <summary>
    /// Achieved output bitrate: on-disk file size over media duration. MP4/MOV are written fragmented
    /// (moof/mdat incrementally), so the live file size is a usable estimate mid-take.
    /// </summary>
    public double AchievedBitrateMbps
    {
        get
        {
            string? path = _currentRecordingPath;
            TimeSpan media = MediaDuration;
            if (path == null || media.TotalSeconds <= 0) return 0;
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length * 8.0 / 1_000_000.0 / media.TotalSeconds : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public event Action<string>? RecordingStarted;

    /// <summary>
    /// Raised once per stop with the full <see cref="RecordingStopSummary"/>, including WHY it stopped.
    /// <see cref="RecordingStopSummary.MediaDuration"/> is the media duration (DEF-01) — NOT wall clock,
    /// which rides along separately as <see cref="RecordingStopSummary.WallClockDuration"/>.
    /// </summary>
    public event Action<RecordingStopSummary>? RecordingStopped;
    public event Action<string>? RecordingError;

    // ============================================================================================
    // Arm
    // ============================================================================================

    /// <summary>Arms a new take (segment 1 with a fresh timestamped base name) and flips the gate.</summary>
    public bool Arm(RecordingConfig config, StreamFormat format)
        => ArmCore(config, format, isNewTake: true);

    /// <summary>
    /// Resumes the active take after an input-loss outage into the next sequential segment
    /// (…_partNN), reusing the take's original base name so the segments sort together and the outage
    /// boundary is explicit on disk (ADR-0002 decision 3). Falls back to a fresh take when no base
    /// name is known yet.
    /// </summary>
    public bool ArmNextSegment(RecordingConfig config, StreamFormat format)
        => ArmCore(config, format, isNewTake: _takeBasePath == null);

    private bool ArmCore(RecordingConfig config, StreamFormat format, bool isNewTake)
    {
        lock (_gateLock)
        {
            if (_disposed != 0 || _isRecording) return false;

            try
            {
                if (config == null || string.IsNullOrWhiteSpace(config.OutputDirectory))
                {
                    RecordingError?.Invoke("Cannot record: Output destination directory is not specified.");
                    return false;
                }

                if (format.Width <= 0 || format.Height <= 0)
                {
                    RecordingError?.Invoke("Cannot record: Invalid video dimensions.");
                    return false;
                }

                if (!Directory.Exists(config.OutputDirectory))
                {
                    Directory.CreateDirectory(config.OutputDirectory);
                }

                string ext = config.ContainerFormat switch
                {
                    OutputContainerFormat.MKV => ".mkv",
                    OutputContainerFormat.MOV => ".mov",
                    _ => ".mp4"
                };

                // Segment 1 uses the take's timestamped base name; later segments suffix the same stem
                // with _partNN so a post-outage gap is a named discontinuity, never an invisible stitch.
                bool startNewTake = isNewTake || _takeBasePath == null;
                string takeBasePath = startNewTake
                    ? Path.Combine(config.OutputDirectory, $"{config.FilenamePrefix}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{ext}")
                    : _takeBasePath!;
                int segmentIndex = startNewTake ? 1 : _segmentIndex + 1;

                string candidatePath = RecordingSegments.SegmentFilePath(takeBasePath, segmentIndex);

                // ---- Step 1 of the arm sequence: reset the A/V clock ------------------------------
                // Deliberately FIRST, and therefore strictly before the sink is live and before the
                // gate flips. The media thread only touches the clock while IsRecording is true, so
                // this is race-free and published to the media threads by the gate write below.
                _avClock.Reset(format.SampleRate, format.FrameRate, format.Channels);

                // ---- Step 2: storage preflight (policy lives in the guard) -------------------------
                _currentRecordingPath = candidatePath;
                StoragePreflightResult preflight = RecordingStorageGuard.Preflight(candidatePath, config, format);
                if (preflight.IsRefusal)
                {
                    RecordingError?.Invoke(preflight.Message ?? "Cannot record: storage preflight refused the destination.");
                    _currentRecordingPath = null;
                    return false;
                }
                if (preflight.Outcome == StoragePreflightOutcome.Undetermined)
                {
                    // Drive status could not be determined (e.g. a mapped/UNC path offline).
                    // Surfacing a clear warning is safer than faking a PASS or FAIL — proceed,
                    // the periodic guard will still protect the session once storage is reachable.
                    AppLogger.LogError($"Disk preflight could not determine free space for {candidatePath}; proceeding without preflight check.");
                }

                // ---- Step 3: initialize the sink ---------------------------------------------------
                if (!_sink.Initialize(config, format, candidatePath))
                {
                    AppLogger.LogError("Failed to initialize recording sink.");
                    RecordingError?.Invoke("Failed to initialize recording sink.");
                    _currentRecordingPath = null;
                    return false;
                }

                // Commit the segment bookkeeping only once the sink is live (a failed start must not
                // advance the segment sequence).
                _takeBasePath = takeBasePath;
                _segmentIndex = segmentIndex;
                _recordFrameRate = format.FrameRate;
                _recordSampleRate = format.SampleRate;
                _recordChannels = format.Channels;
                _recordStartTimestamp = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _framesWritten, 0);
                Interlocked.Exchange(ref _audioBytesWritten, 0);

                // NET-008: a NEW take resets the take clock; every arm (including a rollover) resets the
                // segment clock. Both commit here with the rest of the bookkeeping, so a failed arm can
                // advance neither.
                long armedUtcTicks = _utcNowTicks();
                if (startNewTake)
                {
                    _takeStartUtcTicks = armedUtcTicks;
                }
                _segmentStartUtcTicks = armedUtcTicks;

                // ---- Step 4: flip the gate LAST ----------------------------------------------------
                _isRecording = true;

                AppLogger.LogInfo($"Recording armed (segment {segmentIndex}): {candidatePath}");
                RecordingStarted?.Invoke(candidatePath);
                return true;
            }
            catch (Exception ex)
            {
                _isRecording = false;
                AppLogger.LogError($"Failed to arm recording: {ex.Message}", ex);
                RecordingError?.Invoke($"Failed to start recording: {ex.Message}");
                return false;
            }
        }
    }

    // ============================================================================================
    // Disarm — THE single finalize path
    // ============================================================================================

    /// <summary>
    /// The one finalize path. Every trigger — user stop, low disk, input-loss boundary, sink failure,
    /// disposal — calls this and differs only in <paramref name="reason"/>.
    ///
    /// <para>Sequence: flip the gate → drain in-flight writers → snapshot → finalize → publish the
    /// summary. Idempotent (returns false when the gate is already closed), never throws, and always
    /// publishes a summary with whatever state is known — a failed finalize is reported but never
    /// masks the reason the stop happened.</para>
    /// </summary>
    public bool Disarm(RecordingStopReason reason, out RecordingStopSummary? summary)
    {
        summary = null;

        lock (_gateLock)
        {
            if (!_isRecording) return false;

            // 1. Close the gate: no new writes, and every reader sees the closed gate immediately.
            _isRecording = false;

            // 2. Drain: bounded wait for a media thread already inside a write. Never unbounded —
            //    a wedged sink must not hang the caller (which may be the UI tick thread).
            int drainedAfterMs = Drain();

            // 3. Snapshot BEFORE finalizing: FinalizeSink tears the transport down, so afterwards the
            //    encoder name and diagnostics come back empty.
            string path = _currentRecordingPath ?? string.Empty;
            TimeSpan mediaDuration = MediaDuration;
            TimeSpan audioDuration = AudioDuration;
            TimeSpan wallClock = Stopwatch.GetElapsedTime(_recordStartTimestamp);
            long framesWritten = Interlocked.Read(ref _framesWritten);
            string encoderName = Snapshot(() => _sink.ResolvedEncoderName, string.Empty);
            string diagnosticTail = Snapshot(() => _sink.DiagnosticTail, string.Empty);
            long videoDrops = SnapshotLong(() => _sink.VideoFramesDropped, 0);
            long audioDrops = SnapshotLong(() => _sink.AudioChunksDropped, 0);
            int segmentIndex = _segmentIndex;

            // 4. Finalize. Best-effort: this may be called because the transport already died, so a
            //    secondary failure is reported and swallowed — it must never escape Disarm.
            var finalizeWatch = Stopwatch.StartNew();
            long fileSize = 0;
            string? finalizeError = null;
            try
            {
                _sink.FinalizeSink(out fileSize);
            }
            catch (Exception ex)
            {
                finalizeError = ex.Message;
                AppLogger.LogError($"Recording finalize failed ({reason}) for {path}: {ex.Message}", ex);
            }
            finalizeWatch.Stop();

            if (mediaDuration <= TimeSpan.Zero)
            {
                // No frames cleared the transport (or an unknown frame rate): fall back to wall clock
                // rather than reporting a bogus zero.
                mediaDuration = wallClock;
            }

            var stopSummary = new RecordingStopSummary(
                Path: path,
                MediaDuration: mediaDuration,
                WallClockDuration: wallClock,
                AudioDuration: audioDuration,
                FinalizeDuration: finalizeWatch.Elapsed,
                FileSizeBytes: fileSize,
                VideoFramesWritten: framesWritten,
                VideoFramesDropped: videoDrops,
                AudioChunksDropped: audioDrops,
                SegmentIndex: segmentIndex,
                EncoderName: encoderName,
                DiagnosticTail: diagnosticTail,
                Reason: reason);

            summary = stopSummary;

            AppLogger.LogInfo(
                $"Recording disarmed ({reason}): {path}, {fileSize} bytes, media {stopSummary.MediaDuration.TotalSeconds:F2}s, " +
                $"drained {drainedAfterMs} ms{(finalizeError == null ? string.Empty : $", FINALIZE FAILED: {finalizeError}")}, " +
                RecordingHealth.WallClockNote(stopSummary));

            // 5. Publish. Raised for every reason — a low-disk or sink-failure stop is a stop, and the
            //    operator gets the same summary/verdict as any other (ADR-0005).
            RecordingStopped?.Invoke(stopSummary);

            if (finalizeError != null)
            {
                RecordingError?.Invoke($"Recording {path} may be incomplete: finalization failed ({finalizeError}).");
            }

            return true;
        }
    }

    /// <summary>
    /// Disarms on a background thread, for callers that must not block (the UI tick loop). The async
    /// hop lives HERE so that no caller has to own it — and so it is visible in one place that the
    /// only thread hop around a finalize is this one.
    /// </summary>
    public Task<bool> DisarmAsync(RecordingStopReason reason)
        => Task.Run(() =>
        {
            try
            {
                return Disarm(reason, out _);
            }
            catch (Exception ex)
            {
                // Disarm already swallows finalize failures; this is belt-and-braces so a background
                // hop can never surface an unobserved exception.
                AppLogger.LogError($"Recording disarm ({reason}) failed: {ex.Message}", ex);
                return false;
            }
        });

    /// <summary>
    /// Bounded wait for in-flight writers to leave. Returns how long it waited, in ms.
    ///
    /// <para>The bound is measured with a Stopwatch, NOT by counting <see cref="Thread.Sleep(int)"/>
    /// iterations: on Windows a 1 ms sleep is quantised to the ~15 ms timer tick, so "50 iterations"
    /// is not 50 ms. Measured in time, the cap is the documented 50 ms plus at most one overshoot.</para>
    /// </summary>
    private int Drain()
    {
        var watch = Stopwatch.StartNew();
        while (Volatile.Read(ref _writersInFlight) > 0 && watch.ElapsedMilliseconds < DrainTimeoutMs)
        {
            Thread.Sleep(1);
        }
        return (int)watch.ElapsedMilliseconds;
    }

    /// <summary>Reads a sink property that may itself throw once the transport is dead.</summary>
    private static string Snapshot(Func<string> read, string fallback)
    {
        try { return read() ?? fallback; } catch { return fallback; }
    }

    private static long SnapshotLong(Func<long> read, long fallback)
    {
        try { return read(); } catch { return fallback; }
    }

    /// <summary>
    /// Periodic low-disk guard. Never blocks the calling (UI tick) thread on transport teardown: the
    /// actual finalize runs on the arm's own background disarm.
    /// </summary>
    public void CheckLowDisk()
    {
        string? path;
        lock (_gateLock)
        {
            if (_disposed != 0 || !_isRecording || _currentRecordingPath == null) return;

            var now = DateTime.UtcNow;
            if (now - _lastDiskCheckUtc < RecordingStorageGuard.CheckInterval) return;
            _lastDiskCheckUtc = now;
            path = _currentRecordingPath;
        }

        if (!RecordingStorageGuard.TryGetFreeSpace(path, out _, out string driveFormat, out long freeBytes))
        {
            return; // storage temporarily unreachable; retry on next interval
        }

        // Don't penalize already-low volumes (FAT32/exFAT can legitimately show little free).
        if (!RecordingStorageGuard.IsLowOnSpace(driveFormat, freeBytes)) return;

        string message = RecordingStorageGuard.LowDiskMessage(freeBytes, path);

        // NET-011: a sink with another destination turns a full volume into a DEGRADE rather than a stop.
        // The GUARD is unchanged and still watches the PRIMARY - only the question "what do we do about
        // it?" is now asked of the sink, which is the composite-health consultation ADR-0005 wants the
        // arm to own. A single-destination sink returns false and today's stop path runs untouched.
        if (_sink.TryShedFailedDestination(message, out string? survivingPath))
        {
            string degraded = RecordingStorageGuard.DegradedMessage(freeBytes, path, survivingPath);
            AppLogger.LogError(degraded);

            // Follow the survivor so the path/size/bitrate readouts and the stop summary stay truthful -
            // the primary's file stops growing the moment it is shed.
            if (!string.IsNullOrWhiteSpace(survivingPath))
            {
                _currentRecordingPath = survivingPath;
            }

            RecordingError?.Invoke(degraded);
            return;
        }

        AppLogger.LogError(message);

        StopLowDisk(message);
    }

    /// <summary>
    /// The low-disk stop. The error is raised in the DISARM CONTINUATION, not synchronously here.
    ///
    /// <para>DEF-02/OBS-03: raising it inline flashed "Recording Error: Low disk space…" at T+0 and
    /// then <see cref="DisarmAsync"/>'s stop summary overwrote the status line ~500 ms later, masking
    /// the reason in the bottom bar (the drawer verdict was already correct). This mirrors
    /// <see cref="OnSinkError"/>, whose <see cref="Disarm"/> runs synchronously first so its error
    /// stays last. The async hop lives in <see cref="DisarmAsync"/>, so the only thread hop around a
    /// finalize is still that one place.</para>
    /// </summary>
    public void StopLowDisk(string message)
        => _ = DisarmAsync(RecordingStopReason.LowDisk)
            .ContinueWith(_ => RecordingError?.Invoke(message), TaskScheduler.Default);

    /// <summary>
    /// A sink failure. The transport itself is sick, so this is best-effort: finalize what is on disk
    /// so the file stays playable and the operator still gets a summary, but the ORIGINAL sink error is
    /// always reported last and a secondary failure never masks it or escapes this handler.
    /// </summary>
    private void OnSinkError(string error)
    {
        // NET-011: a DEGRADE is not a failure. A composite that shed one destination while others survive
        // raises SinkError to REPORT the loss, and the take keeps recording - stopping here would throw
        // away the surviving file for no reason. Only a sink with nowhere left to write ends the take,
        // through the same single finalize path as everything else (ADR-0005).
        bool stillViable;
        try
        {
            stillViable = _sink.HasSurvivingDestination;
        }
        catch (Exception ex)
        {
            // A sink that cannot even answer is treated as gone.
            AppLogger.LogError($"Recording arm: sink survival check failed: {ex.Message}", ex);
            stillViable = false;
        }

        if (stillViable)
        {
            AppLogger.LogError($"Recording transport degraded but still writing: {error}");
            RecordingError?.Invoke(error);
            return;
        }

        try
        {
            Disarm(RecordingStopReason.SinkError, out _);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Recording arm: disarm after sink error failed: {ex.Message}", ex);
        }

        RecordingError?.Invoke(error);
    }

    // ============================================================================================
    // Scheduled / segmented capture boundaries (NET-008)
    // ============================================================================================

    /// <summary>True when a take is live and can be rolled - the guard callers check before rolling.</summary>
    public bool CanRollSegment => _disposed == 0 && _isRecording;

    /// <summary>
    /// The production overload: evaluates against the arm's own UTC clock. Tests drive
    /// <see cref="EvaluateBoundary(RecordingConfig, long)"/> with an injected tick instead.
    /// </summary>
    public RecordingBoundary EvaluateBoundary(RecordingConfig config)
        => EvaluateBoundary(config, _utcNowTicks());

    /// <summary>
    /// Evaluates the rollover / max-duration / scheduled-stop boundary for the active take. Pure
    /// arithmetic (see <see cref="RecordingBoundaries"/>) - no lock and no allocation - so it is safe
    /// on the UI health tick. The only I/O is the segment-size probe, and only when a size rollover is
    /// configured.
    /// </summary>
    public RecordingBoundary EvaluateBoundary(RecordingConfig config, long nowUtcTicks)
    {
        if (_disposed != 0 || !_isRecording) return RecordingBoundary.None;

        long segmentBytes = config.SegmentRolloverGigabytes > 0 ? CurrentSegmentBytes : 0;

        return RecordingBoundaries.Evaluate(
            config, TakeStartUtcTicks, SegmentStartUtcTicks, segmentBytes, nowUtcTicks);
    }

    /// <summary>
    /// Rolls the take into its next segment (NET-008): the SAME single finalize path
    /// (<see cref="Disarm"/>) followed by an arm of the next …_partNN via <see cref="ArmNextSegment"/>.
    /// This method touches no gate state itself, so a rollover cannot become a second stop path
    /// (ADR-0005) - it is a Disarm+Arm cycle and nothing more.
    ///
    /// <para>Returns false when there was nothing to roll, or when the next segment could not be armed.
    /// In the latter case the take is left stopped rather than silently recording into a dead
    /// transport, and the arm's own <see cref="RecordingError"/> has already reported why.</para>
    /// </summary>
    public bool RollSegment(RecordingConfig config, StreamFormat format, RecordingStopReason reason)
    {
        if (!CanRollSegment) return false;

        if (!Disarm(reason, out RecordingStopSummary? segmentSummary))
        {
            return false;
        }

        bool rearmed = ArmNextSegment(config, format);
        if (!rearmed)
        {
            AppLogger.LogError(
                $"Segment rollover armed no continuation after {segmentSummary?.Path}; the take is stopped.");
        }

        return rearmed;
    }

    // ============================================================================================
    // Hot path
    // ============================================================================================

    /// <summary>Writes a decoded BGRA video frame to the transport, under the gate.</summary>
    public void WriteVideoFrame(IntPtr pData, int dataLength)
    {
        if (_disposed != 0 || !_isRecording || pData == IntPtr.Zero || dataLength <= 0) return;

        Interlocked.Increment(ref _writersInFlight);
        try
        {
            _sink.WriteVideo(pData, dataLength);
            Interlocked.Increment(ref _framesWritten);
        }
        finally
        {
            Interlocked.Decrement(ref _writersInFlight);
        }
    }

    /// <summary>Writes interleaved 32-bit float audio bytes with offset and count to the transport.</summary>
    public void WriteAudioData(byte[] buffer, int offset, int count)
    {
        if (_disposed != 0 || !_isRecording || buffer == null || count <= 0) return;

        Interlocked.Increment(ref _writersInFlight);
        try
        {
            _sink.WriteAudio(buffer, offset, count);
            Interlocked.Add(ref _audioBytesWritten, count);
        }
        finally
        {
            Interlocked.Decrement(ref _writersInFlight);
        }
    }

    /// <summary>
    /// One video frame at the recording clock: advances the A/V drift estimate. Call once per frame
    /// that was actually written, under the same gate as the write itself.
    /// </summary>
    public void OnVideoFrame()
    {
        if (!_isRecording) return;
        _avClock.OnVideoFrame();
    }

    /// <summary>
    /// Applies the recording clock to an audio block and writes the converted bytes, reading the gate
    /// once so the clock and the transport can never disagree about whether a take is live.
    /// </summary>
    public void ProcessAndWriteAudio(byte[] buffer, int count)
    {
        if (_disposed != 0 || !_isRecording || buffer == null || count <= 0) return;

        byte[] outBuf = _avClock.Process(buffer, count, out int outCount);
        if (outCount > 0)
        {
            WriteAudioData(outBuf, 0, outCount);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Route disposal through the same single finalize path as every other trigger.
        try
        {
            Disarm(RecordingStopReason.Dispose, out _);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Recording arm: disarm on dispose failed: {ex.Message}", ex);
        }

        _sink.SinkError -= OnSinkError;

        RecordingStarted = null;
        RecordingStopped = null;
        RecordingError = null;

        if (_ownsSink)
        {
            _sink.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
