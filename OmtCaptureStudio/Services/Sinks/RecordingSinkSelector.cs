using System;
using System.Collections.Generic;
using System.IO;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Forensics;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// Selects the recording sink when a take STARTS, and forwards everything to the choice (NET-011 wiring).
///
/// <para><b>Sole production construction point for concrete sinks.</b> Callers that need a real take
/// (not a test stub) go through <see cref="ForProduction"/> → <see cref="StreamRecorderService"/>'s
/// default ctor. Do not <c>new FfmpegPipeSink()</c> from orchestration code. A future Media Foundation
/// sink (Phase 3 ADR) plugs in by changing the factories here — still behind
/// <see cref="IRecordingSink"/> — never by branching inside the arm or the facade.</para>
///
/// <para>This seam exists because of an ordering fact, not a design preference: the arm holds its sink
/// from construction, while the destination configuration is only known when
/// <c>IRecordingSink.Initialize(config, format, path)</c> is called. So the decision cannot be made in a
/// constructor — it has to be made here.</para>
///
/// <para>It is deliberately thin: the choice is one boolean (<see
/// cref="RecordingConfig.HasBackupDestination"/>) and every other member is a forward. The composite's
/// <see cref="HasSurvivingDestination"/>, <see cref="TryShedFailedDestination"/>, and
/// <see cref="DetachedDestinationNames"/> reach the arm / diagnostics chain through this type
/// unchanged, so the guard interplay is identical whether or not a backup is set.</para>
/// </summary>
public sealed class RecordingSinkSelector : IRecordingSink
{
    private readonly Func<RecordingConfig, string, IRecordingSink> _singleFactory;
    private readonly Func<RecordingConfig, string, IRecordingSink> _compositeFactory;
    private readonly object _lock = new();

    private IRecordingSink? _inner;
    private volatile bool _disposed;

    public RecordingSinkSelector(
        Func<RecordingConfig, string, IRecordingSink> singleFactory,
        Func<RecordingConfig, string, IRecordingSink> compositeFactory)
    {
        _singleFactory = singleFactory ?? throw new ArgumentNullException(nameof(singleFactory));
        _compositeFactory = compositeFactory ?? throw new ArgumentNullException(nameof(compositeFactory));
    }

    /// <summary>
    /// Production selector: FFmpeg pipe sink today — single destination, or dual via
    /// <see cref="CompositeRecordingSinkFactory.CreateDualDestinationFfmpegSink"/>. This is the only
    /// shipping place that constructs <see cref="FfmpegPipeSink"/>.
    /// </summary>
    public static RecordingSinkSelector ForProduction() => new(
        singleFactory: static (_, _) => new FfmpegPipeSink(),
        compositeFactory: static (config, outputPath) => CompositeRecordingSinkFactory.CreateDualDestinationFfmpegSink(config, outputPath));

    /// <summary>The sink currently selected, for diagnostics. Null before the first Initialize.</summary>
    public IRecordingSink? Current => _inner;

    /// <summary>True when the take that started is running as a composite (i.e. a backup is in use).</summary>
    public bool IsComposite => _inner is CompositeRecordingSink;

    public event Action<string>? SinkError;

    public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
    {
        lock (_lock)
        {
            if (_disposed) return false;

            // A fresh sink per take: FfmpegPipeSink already tears itself down on FinalizeSink, and the arm
            // snapshots encoder/diagnostics/drops BEFORE finalizing, so nothing is lost by rebuilding.
            Detach();
            _inner = config.HasBackupDestination
                ? _compositeFactory(config, outputPath)
                : _singleFactory(config, outputPath);

            _inner.SinkError += OnInnerSinkError;
            return _inner.Initialize(config, format, outputPath);
        }
    }

    public void WriteVideo(IntPtr pData, int dataLength) => _inner?.WriteVideo(pData, dataLength);

    public void WriteAudio(byte[] buffer, int offset, int count) => _inner?.WriteAudio(buffer, offset, count);

    public void FinalizeSink(out long totalBytesWritten)
    {
        lock (_lock)
        {
            if (_inner == null)
            {
                totalBytesWritten = 0;
                return;
            }

            _inner.FinalizeSink(out totalBytesWritten);
        }
    }

    public long VideoFramesDropped => _inner?.VideoFramesDropped ?? 0;

    public long AudioChunksDropped => _inner?.AudioChunksDropped ?? 0;

    public string ResolvedEncoderName => _inner?.ResolvedEncoderName ?? string.Empty;

    public string DiagnosticTail => _inner?.DiagnosticTail ?? string.Empty;

    /// <summary>Forwarded: the arm's degrade-vs-stop decision is identical with or without a backup.</summary>
    public bool HasSurvivingDestination => _inner?.HasSurvivingDestination ?? false;

    /// <summary>
    /// Forwarded: production diagnostics read this through
    /// <c>StreamRecorderService</c> → selector → inner composite (NET-020). Without this forward the
    /// production path would always report an empty detach list even after a real shed.
    /// </summary>
    public IReadOnlyList<string> DetachedDestinationNames =>
        _inner?.DetachedDestinationNames ?? Array.Empty<string>();

    /// <summary>Forwarded: a composite with a survivor degrades; a single sink refuses and the arm stops.</summary>
    public bool TryShedFailedDestination(string reason, out string? survivingPath)
    {
        IRecordingSink? inner = _inner;
        if (inner == null)
        {
            survivingPath = null;
            return false;
        }

        return inner.TryShedFailedDestination(reason, out survivingPath);
    }

    private void OnInnerSinkError(string message) => SinkError?.Invoke(message);

    private void Detach()
    {
        if (_inner == null) return;

        try { _inner.SinkError -= OnInnerSinkError; } catch { /* idempotent unhook */ }
        try { _inner.Dispose(); } catch { /* best-effort teardown */ }
        _inner = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
            SinkError = null;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Builders for the composite wiring (NET-011). Kept next to the selector so FFmpeg dual-destination
/// construction stays in one file — the only production place that <c>new</c>s <see cref="FfmpegPipeSink"/>.
/// </summary>
public static class CompositeRecordingSinkFactory
{
    /// <summary>
    /// Builds the dual-destination recording sink: the primary file the arm named, plus the same file
    /// name in the configured backup folder.
    /// </summary>
    /// <remarks>
    /// The backup directory is created HERE rather than left to the child, because the composite DROPS a
    /// child that cannot initialize — a missing folder would silently reduce every take to a single
    /// destination. Creating it up front is what makes "the backup failed to start" mean the volume is
    /// genuinely unusable rather than merely unprepared.
    /// </remarks>
    public static CompositeRecordingSink CreateDualDestinationFfmpegSink(RecordingConfig config, string primaryPath)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        string backupDirectory = (config.BackupOutputDirectory ?? string.Empty).Trim();
        if (backupDirectory.Length == 0)
        {
            throw new ArgumentException("No backup destination is configured.", nameof(config));
        }

        try
        {
            if (!Directory.Exists(backupDirectory)) Directory.CreateDirectory(backupDirectory);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not create the backup destination '{backupDirectory}': {ex.Message}", ex);
        }

        string fileName = Path.GetFileName(primaryPath);
        string backupPath = Path.Combine(backupDirectory, fileName);

        return new CompositeRecordingSink(
            new CompositeChild("primary", new FfmpegPipeSink(), primaryPath),
            new CompositeChild("backup", new FfmpegPipeSink(), backupPath));
    }
}
