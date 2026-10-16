using System;
using System.Threading.Tasks;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Sinks;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Compatibility facade over <see cref="RecordingArm"/> (ADR-0005).
///
/// <para>This type used to own recording orchestration: the volatile gate, sink lifecycle, segment
/// bookkeeping, the stop summary, the A/V clock ordering convention AND the disk policy — all in one
/// 556-line class, with three different threads able to finalize it. All of that now lives in
/// <see cref="RecordingArm"/> (sequence + gate + finalize) and <see cref="RecordingStorageGuard"/>
/// (storage policy). What remains here is the public surface existing callers were written against,
/// forwarded verbatim.</para>
///
/// <para>Deliberately NOT a place to add recording logic: if you are tempted to put a decision in
/// this file, it belongs in the arm.</para>
/// </summary>
public class StreamRecorderService : IDisposable
{
    private readonly RecordingArm _arm;

    /// <summary>Default constructor utilizing the production FFmpeg named pipe sink.</summary>
    public StreamRecorderService()
        : this(new FfmpegPipeSink(), ownsSink: true)
    {
    }

    /// <summary>Constructor supporting dependency injection of custom or test recording sinks.</summary>
    public StreamRecorderService(IRecordingSink sink, bool ownsSink = false)
    {
        _arm = new RecordingArm(sink, ownsSink);

        _arm.RecordingStarted += path => RecordingStarted?.Invoke(path);
        _arm.RecordingStopped += summary => RecordingStopped?.Invoke(summary);
        _arm.RecordingError += err => RecordingError?.Invoke(err);
    }

    public bool IsRecording => _arm.IsRecording;
    public string? CurrentRecordingPath => _arm.CurrentRecordingPath;
    public int CurrentSegmentIndex => _arm.CurrentSegmentIndex;
    public string? CurrentTakeBasePath => _arm.CurrentTakeBasePath;
    public TimeSpan ElapsedTime => _arm.ElapsedTime;
    public long FramesWritten => _arm.FramesWritten;
    public long AudioBytesWritten => _arm.AudioBytesWritten;
    public long VideoFramesDropped => _arm.VideoFramesDropped;
    public long AudioChunksDropped => _arm.AudioChunksDropped;
    public string ResolvedEncoderName => _arm.ResolvedEncoderName;
    public string DiagnosticTail => _arm.DiagnosticTail;
    public TimeSpan MediaDuration => _arm.MediaDuration;
    public TimeSpan AudioDuration => _arm.AudioDuration;
    public double AchievedBitrateMbps => _arm.AchievedBitrateMbps;

    /// <summary>Live A/V drift ratio, projected into the recording-health readout (FEAT-07).</summary>
    public double AvDriftRatio => _arm.AvDriftRatio;

    /// <summary>True when the drift ratio is pinned against its 0.9/1.1 safety clamp.</summary>
    public bool AvDriftClamped => _arm.AvDriftClamped;

    public event Action<string>? RecordingStarted;
    public event Action<RecordingStopSummary>? RecordingStopped;
    public event Action<string>? RecordingError;

    /// <summary>Arms a new take (segment 1) and flips the gate.</summary>
    public bool StartRecording(RecordingConfig config, StreamFormat format) => _arm.Arm(config, format);

    /// <summary>Overload for backwards compatibility with raw scalar stream parameters.</summary>
    public bool StartRecording(
        RecordingConfig config,
        int width,
        int height,
        double frameRate,
        int sampleRate,
        int channels)
    {
        var format = new StreamFormat
        {
            Width = width,
            Height = height,
            FrameRate = frameRate,
            SampleRate = sampleRate,
            Channels = channels
        };

        return StartRecording(config, format);
    }

    /// <summary>Resumes the active take into the next …_partNN segment (ADR-0002).</summary>
    public bool StartNextSegment(RecordingConfig config, StreamFormat format) => _arm.ArmNextSegment(config, format);

    /// <summary>Stops and finalizes through the arm's single finalize path.</summary>
    public void StopRecording() => _arm.Disarm(RecordingStopReason.Manual, out _);

    /// <summary>Stops and finalizes, reporting WHY (ADR-0005).</summary>
    public void StopRecording(RecordingStopReason reason) => _arm.Disarm(reason, out _);

    /// <summary>Stops without blocking the calling thread on transport teardown.</summary>
    public Task StopRecordingAsync() => _arm.DisarmAsync(RecordingStopReason.Manual);

    /// <summary>Periodic low-disk protection sweep. Cheap; call from a UI tick loop while recording.</summary>
    public void CheckDiskSpaceAndProtect() => _arm.CheckLowDisk();

    /// <summary>
    /// NET-008: evaluates the rollover / max-duration / scheduled-stop boundary. Pure comparison; cheap
    /// enough for the UI health tick. Forwarded, like the low-disk sweep above - no logic here.
    /// </summary>
    public RecordingBoundary EvaluateBoundary(RecordingConfig config) => _arm.EvaluateBoundary(config);

    /// <summary>NET-008: true when a take is live and can be rolled into its next segment.</summary>
    public bool CanRollSegment => _arm.CanRollSegment;

    /// <summary>
    /// NET-008: rolls into the next …_partNN - the arm's single finalize path followed by an arm of the
    /// continuation. Forwarded verbatim; the sequence itself lives in <see cref="RecordingArm"/>.
    /// </summary>
    public bool RollSegment(RecordingConfig config, StreamFormat format, RecordingStopReason reason)
        => _arm.RollSegment(config, format, reason);

    public void WriteVideoFrame(IntPtr pData, int dataLength) => _arm.WriteVideoFrame(pData, dataLength);

    public void WriteAudioData(byte[] interleavedBytes)
    {
        if (interleavedBytes != null)
        {
            _arm.WriteAudioData(interleavedBytes, 0, interleavedBytes.Length);
        }
    }

    public void WriteAudioData(byte[] buffer, int offset, int count) => _arm.WriteAudioData(buffer, offset, count);

    /// <summary>Ticks the A/V recording clock for one written video frame.</summary>
    public void OnRecordingVideoFrame() => _arm.OnVideoFrame();

    /// <summary>Applies the recording clock to an audio block and writes the converted bytes.</summary>
    public void ProcessAndWriteAudio(byte[] buffer, int count) => _arm.ProcessAndWriteAudio(buffer, count);

    /// <summary>Backwards-compatible forwarder; the estimation policy lives in <see cref="RecordingStorageGuard"/>.</summary>
    internal static long EstimateSessionSize(RecordingConfig config, StreamFormat format)
        => RecordingStorageGuard.EstimateSessionSize(config, format);

    public void Dispose() => _arm.Dispose();
}
