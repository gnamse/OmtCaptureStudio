using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// One destination inside a <see cref="CompositeRecordingSink"/> (NET-011): a display name for
/// reporting, the adapter, and the path it writes.
/// </summary>
public sealed record CompositeChild(string Name, IRecordingSink Sink, string OutputPath);

/// <summary>
/// Fans one recording out to several destinations (NET-011) - dual-folder first, a network re-stream
/// later. Today a single destination that fails takes the whole take down; this turns that into a
/// degrade.
///
/// <para><b>Buffer ownership: the composite is COPY-TRANSPARENT.</b> It rents nothing and copies
/// nothing - the source pointer/array is passed straight to each child, one at a time. That matters:
/// an implementation that rented a buffer of its own and handed it to several children would both add a
/// copy and create double ownership, breaking the exactly-once return that NET-004 established. Each
/// child keeps its own buffer and returns it exactly once, so the ledger stays balanced by
/// construction. (Collapsing N child copies into one would require the children to adopt a
/// caller-owned buffer; that is a change to the adapters, not to this module.)</para>
///
/// <para><b>Hot path:</b> the children array is fixed at construction and never reallocated, the fan-out
/// loop allocates nothing, and the only allocation on a write is the one-off reporting string when a
/// child fails - which is not the hot path.</para>
///
/// <para><b>Failure model.</b> A child that throws or reports an error is DETACHED and reported exactly
/// once, and the take continues on the survivors. When the last child goes, <see cref="IsViable"/>
/// turns false and the composite reports the real failure, so the arm's single finalize path stops the
/// take (ADR-0005).</para>
/// </summary>
public sealed class CompositeRecordingSink : IRecordingSink
{
    private sealed class ChildState
    {
        public ChildState(CompositeChild child) => Child = child;

        public CompositeChild Child { get; }
        public string Name => Child.Name;
        public string Path => Child.OutputPath;
        public IRecordingSink Sink => Child.Sink;

        /// <summary>Read on the hot path; flipped once, under _childLock, by Detach.</summary>
        public volatile bool Alive;

        /// <summary>Detach reports exactly once per child (0 -> 1).</summary>
        public int Detached;

        /// <summary>Every child is disposed exactly once, whether detached or torn down with the composite.</summary>
        public int Disposed;
    }

    private readonly ChildState[] _children;
    private readonly object _childLock = new();
    private volatile bool _disposed;
    private int _disposedOnce;
    private long _videoFramesWritten;
    private long _audioBytesWritten;

    public CompositeRecordingSink(params CompositeChild[] children)
    {
        if (children == null || children.Length == 0)
        {
            throw new ArgumentException("A composite sink needs at least one child.", nameof(children));
        }

        _children = new ChildState[children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            CompositeChild child = children[i] ?? throw new ArgumentException("Child sinks cannot be null.", nameof(children));
            _children[i] = new ChildState(child);
        }
    }

    /// <summary>How many destinations are still being written to.</summary>
    public int LiveChildCount
    {
        get
        {
            ChildState[] children = _children;
            int live = 0;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].Alive) live++;
            }

            return live;
        }
    }

    /// <summary>Names of the destinations that have been detached, in detach order. For reporting.</summary>
    public IReadOnlyList<string> DetachedChildNames
    {
        get
        {
            var names = new List<string>();
            ChildState[] children = _children;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].Detached != 0) names.Add(children[i].Name);
            }

            return names;
        }
    }

    /// <summary>Frames the composite actually fanned out.</summary>
    public long VideoFramesWritten => Interlocked.Read(ref _videoFramesWritten);

    /// <summary>Audio bytes the composite actually fanned out.</summary>
    public long AudioBytesWritten => Interlocked.Read(ref _audioBytesWritten);

    /// <summary>
    /// Initializes every child. A child that cannot start is dropped and the others carry on - the take
    /// only fails when NO destination came up, which is the same total failure as before.
    /// </summary>
    public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
    {
        if (_disposed) return false;

        ChildState[] children = _children;
        int live = 0;

        for (int i = 0; i < children.Length; i++)
        {
            ChildState child = children[i];
            try
            {
                // Each child gets its OWN output path (primary folder vs backup folder, same file name).
                if (child.Sink.Initialize(config, format, child.Path))
                {
                    child.Alive = true;
                    live++;
                }
                else
                {
                    Drop(child, $"destination '{child.Name}' failed to initialize");
                }
            }
            catch (Exception ex)
            {
                Drop(child, $"destination '{child.Name}' threw while initializing: {ex.Message}");
            }
        }

        return live > 0;
    }

    public void WriteVideo(IntPtr pData, int dataLength)
    {
        if (_disposed || pData == IntPtr.Zero || dataLength <= 0) return;

        ChildState[] children = _children;
        for (int i = 0; i < children.Length; i++)
        {
            ChildState child = children[i];
            if (!child.Alive) continue;

            try
            {
                // Same pointer, child by child: the composite adds no copy of its own.
                child.Sink.WriteVideo(pData, dataLength);
            }
            catch (Exception ex)
            {
                Detach(child, $"destination '{child.Name}' failed writing video: {ex.Message}");
            }
        }

        Interlocked.Increment(ref _videoFramesWritten);
    }

    public void WriteAudio(byte[] buffer, int offset, int count)
    {
        if (_disposed || buffer == null || count <= 0) return;

        ChildState[] children = _children;
        for (int i = 0; i < children.Length; i++)
        {
            ChildState child = children[i];
            if (!child.Alive) continue;

            try
            {
                // Same array and offsets, child by child - no intermediate buffer.
                child.Sink.WriteAudio(buffer, offset, count);
            }
            catch (Exception ex)
            {
                Detach(child, $"destination '{child.Name}' failed writing audio: {ex.Message}");
            }
        }

        Interlocked.Add(ref _audioBytesWritten, count);
    }

    public void FinalizeSink(out long totalBytesWritten)
    {
        totalBytesWritten = 0;

        ChildState[] children = _children;
        lock (_childLock)
        {
            for (int i = 0; i < children.Length; i++)
            {
                ChildState child = children[i];
                if (!child.Alive) continue;

                child.Alive = false;

                // Best-effort per child: one destination failing to finalize must not rob the others of
                // their container. The caller still gets the sum of whatever finalized.
                try
                {
                    child.Sink.FinalizeSink(out long childBytes);
                    totalBytesWritten += childBytes;
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"Composite recording sink: destination '{child.Name}' failed to finalize: {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>Summed across the destinations still live - the recording path's own drop counters.</summary>
    public long VideoFramesDropped
    {
        get
        {
            ChildState[] children = _children;
            long total = 0;
            for (int i = 0; i < children.Length; i++)
            {
                if (!children[i].Alive) continue;
                try { total += children[i].Sink.VideoFramesDropped; } catch { }
            }

            return total;
        }
    }

    public long AudioChunksDropped
    {
        get
        {
            ChildState[] children = _children;
            long total = 0;
            for (int i = 0; i < children.Length; i++)
            {
                if (!children[i].Alive) continue;
                try { total += children[i].Sink.AudioChunksDropped; } catch { }
            }

            return total;
        }
    }

    /// <summary>The primary destination's encoder (AutoHardware resolved), or the first live child's.</summary>
    public string ResolvedEncoderName
    {
        get
        {
            ChildState[] children = _children;
            for (int i = 0; i < children.Length; i++)
            {
                if (!children[i].Alive) continue;
                try { return children[i].Sink.ResolvedEncoderName ?? string.Empty; } catch { }
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Every live destination's diagnostic tail, labelled and bounded - a dual-folder take has two
    /// encoders and a field report needs both.
    /// </summary>
    public string DiagnosticTail
    {
        get
        {
            ChildState[] children = _children;
            var sb = new StringBuilder();
            for (int i = 0; i < children.Length; i++)
            {
                if (!children[i].Alive) continue;

                try
                {
                    string tail = children[i].Sink.DiagnosticTail;
                    if (string.IsNullOrEmpty(tail)) continue;

                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append('[').Append(children[i].Name).Append("] ").Append(tail);
                }
                catch { }
            }

            return sb.Length <= 4000 ? sb.ToString() : sb.ToString(0, 4000);
        }
    }

    public event Action<string>? SinkError;

    /// <summary>
    /// True while at least one destination survives - the composite's answer to "can the take carry on
    /// after the failure you just reported?". False once the last child has gone, which is the real
    /// failure and stops the take.
    /// </summary>
    public bool HasSurvivingDestination => LiveChildCount > 0;

    /// <summary>
    /// Sheds the PRIMARY (first live) destination and continues on the next one - the degrade path the
    /// arm uses when the primary's volume fills. Returns false when there is no survivor, in which case
    /// the caller's normal low-disk stop runs.
    /// </summary>
    public bool TryShedFailedDestination(string reason, out string? survivingPath)
    {
        survivingPath = null;

        ChildState? victim = null;
        ChildState? survivor = null;

        lock (_childLock)
        {
            ChildState[] children = _children;
            for (int i = 0; i < children.Length; i++)
            {
                if (!children[i].Alive) continue;

                if (victim == null) victim = children[i];
                else { survivor = children[i]; break; }
            }

            if (victim == null || survivor == null) return false;

            // Mark dead under the lock so a concurrent write cannot fan out to it again.
            victim.Alive = false;
        }

        Detach(victim, reason);
        survivingPath = survivor.Path;
        return true;
    }

    /// <summary>Detaches a child and reports it exactly once. Safe to call from the hot path.</summary>
    private void Detach(ChildState child, string reason)
    {
        if (Interlocked.Exchange(ref child.Detached, 1) != 0) return;

        child.Alive = false;

        AppLogger.LogError(
            $"Composite recording sink: {reason}. The take continues on the remaining destination(s).");

        // Best-effort: keep the detached destination's file playable, then let it go exactly once.
        try { child.Sink.FinalizeSink(out _); } catch { }
        DisposeChild(child);

        SinkError?.Invoke($"Recording destination '{child.Name}' was detached: {reason}");
    }

    /// <summary>Detaches a child during Initialize, where there is no SinkError report to make yet.</summary>
    private void Drop(ChildState child, string reason)
    {
        if (Interlocked.Exchange(ref child.Detached, 1) != 0) return;

        child.Alive = false;
        AppLogger.LogError($"Composite recording sink: {reason}.");
        DisposeChild(child);
    }

    private void DisposeChild(ChildState child)
    {
        if (Interlocked.Exchange(ref child.Disposed, 1) != 0) return;
        try { child.Sink.Dispose(); } catch { }
    }

    /// <summary>Disposes every child EXACTLY once, whether it was detached earlier or not. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedOnce, 1) != 0) return;

        _disposed = true;

        ChildState[] children = _children;
        for (int i = 0; i < children.Length; i++)
        {
            DisposeChild(children[i]);
        }

        SinkError = null;
        GC.SuppressFinalize(this);
    }
}
/// <summary>
/// Selects the recording sink when a take STARTS, and forwards everything to the choice (NET-011 wiring).
///
/// <para>This seam exists because of an ordering fact, not a design preference: the arm holds its sink
/// from construction, while the destination configuration is only known when
/// <c>IRecordingSink.Initialize(config, format, path)</c> is called. So the decision cannot be made in a
/// constructor - it has to be made here.</para>
///
/// <para>It is deliberately thin: the choice is one boolean (<see
/// cref="RecordingConfig.HasBackupDestination"/>) and every other member is a forward. The composite's
/// <see cref="HasSurvivingDestination"/> and <see cref="TryShedFailedDestination"/> reach the arm
/// through this type unchanged, so the guard interplay is identical whether or not a backup is set.</para>
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

    /// <summary>The production selector: a real FFmpeg sink, single or fanned out to a backup folder.</summary>
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

        try { _inner.SinkError -= OnInnerSinkError; } catch { }
        try { _inner.Dispose(); } catch { }
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
/// Builders for the composite wiring (NET-011).
/// </summary>
public static class CompositeRecordingSinkFactory
{
    /// <summary>
    /// Builds the dual-destination recording sink: the primary file the arm named, plus the same file
    /// name in the configured backup folder.
    /// </summary>
    /// <remarks>
    /// The backup directory is created HERE rather than left to the child, because the composite DROPS a
    /// child that cannot initialize - a missing folder would silently reduce every take to a single
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
