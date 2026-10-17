using System;
using System.Buffers;
using System.Collections.Generic;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// The single owner of media-buffer rent/return for the recording transport, and the home of the
/// exactly-once return invariant.
///
/// <para><b>Why this exists.</b> Before this module the exactly-once return was correct only by
/// repetition: TEN <c>Return</c> call sites spread across <see cref="FfmpegPipeSink"/> and
/// <see cref="FfmpegProcessHost"/> (copy-failure, stop-edge guard, enqueue-failure, queue-drop/drain,
/// and each writer loop's <c>finally</c>), and the two media kinds return to <b>different pools</b> —
/// video to <see cref="LargeArrayPool"/> (16 MB buckets, because a 1080p BGRA frame never pools on
/// ArrayPool&lt;byte&gt;.Shared) and audio to ArrayPool&lt;byte&gt;.Shared. Every one of
/// those sites was individually correct only because the pool was hard-coded there.</para>
///
/// <para><b>Why the pool travels with the buffer.</b> A pool mix-up is SILENT, not a crash:
/// returning an oversized video frame to ArrayPool&lt;byte&gt;.Shared is a no-op (its largest
/// bucket is 1 MB) and returning a small audio chunk to <see cref="LargeArrayPool"/> falls outside
/// that pool's bucket table. Both leak quietly and resurface as the LOH churn LargeArrayPool was
/// created to eliminate. So the rental carries its pool: <see cref="Packet.Release"/> can only return
/// to the pool that rented it, making the mix-up impossible by construction rather than by care.</para>
///
/// <para><b>Cost.</b> One volatile bool read per rent/return in production
/// (<see cref="MediaBufferPoolLedger.Enabled"/> is false); the ledger's dictionary is only touched
/// when a test explicitly turns it on.</para>
/// </summary>
public sealed class MediaBufferPool
{
    private readonly ArrayPool<byte> _pool;

    private MediaBufferPool(string name, ArrayPool<byte> pool)
    {
        Name = name;
        _pool = pool;
    }

    /// <summary>Human-readable name, for ledger faults.</summary>
    public string Name { get; }

    /// <summary>
    /// Video frames: 16 MB buckets via <see cref="LargeArrayPool"/>. Audio chunks are always under
    /// 1 MB and genuinely pool on ArrayPool&lt;byte&gt;.Shared, so the two kinds stay separate.
    /// </summary>
    public static MediaBufferPool Video { get; } = new("video", LargeArrayPool.Shared);

    /// <summary>Audio chunks: the shared pool, which pools them correctly (always under 1 MB).</summary>
    public static MediaBufferPool Audio { get; } = new("audio", ArrayPool<byte>.Shared);

    /// <summary>Rents a buffer of at least <paramref name="minimumSize"/> bytes, recording the lease.</summary>
    public byte[] Rent(int minimumSize)
    {
        byte[] buffer = _pool.Rent(minimumSize);
        MediaBufferPoolLedger.OnRent(this, buffer);
        return buffer;
    }

    /// <summary>
    /// Returns a buffer. Call this (or a packet's <see cref="Packet.Release"/>) instead of
    /// touching an <see cref="ArrayPool{T}"/> directly — the ledger can only prove exactly-once
    /// semantics if every return goes through here.
    /// </summary>
    public void Return(byte[] buffer)
    {
        MediaBufferPoolLedger.OnReturn(this, buffer);
        _pool.Return(buffer);
    }

    /// <summary>A rented media buffer travelling with the pool that owns it (see the class remarks).</summary>
    public readonly struct Packet
    {
        /// <summary>The rented array.</summary>
        public readonly byte[] Buffer;

        /// <summary>The pool that rented <see cref="Buffer"/>, and the only one that may return it.</summary>
        public readonly MediaBufferPool Pool;

        public Packet(byte[] buffer, MediaBufferPool pool)
        {
            Buffer = buffer;
            Pool = pool;
        }

        /// <summary>True when this packet actually holds a rental.</summary>
        public bool IsRented => Buffer != null && Pool != null;

        /// <summary>The one return implementation: returns to the pool that rented it.</summary>
        public void Release()
        {
            if (IsRented)
            {
                Pool.Return(Buffer);
            }
        }
    }
}

/// <summary>
/// Opt-in lease ledger that makes the exactly-once return invariant OBSERVABLE (ADR-0005's sibling
/// concern: a use-after-free or a leaked rental is invisible to greps and to structural tests).
///
/// <para>Off by default — production pays one volatile bool read per rent/return and never touches the
/// dictionary. Tests (and a fault-injection run) set <see cref="Enabled"/> and then assert
/// <see cref="ActiveLeases"/> is zero and <see cref="FaultCount"/> is zero.</para>
///
/// <para>Deliberately runtime-opt-in rather than <c>#if DEBUG</c>: the suite runs Release, so a
/// compile-time ledger would be inert in exactly the configuration that tests it.</para>
/// </summary>
public static class MediaBufferPoolLedger
{
    private static readonly object Sync = new();
    private static readonly Dictionary<byte[], MediaBufferPool> Live = new();
    private static readonly List<string> FaultList = new();

    private static long _rents;
    private static long _returns;

    /// <summary>Master switch. False in production; set true by tests before driving the transport.</summary>
    public static volatile bool Enabled;

    /// <summary>Rentals not yet returned.</summary>
    public static int ActiveLeases
    {
        get { lock (Sync) { return Live.Count; } }
    }

    public static long RentCount => System.Threading.Interlocked.Read(ref _rents);
    public static long ReturnCount => System.Threading.Interlocked.Read(ref _returns);

    public static int FaultCount
    {
        get { lock (Sync) { return FaultList.Count; } }
    }

    /// <summary>Human-readable descriptions of every violation seen since the last <see cref="Reset"/>.</summary>
    public static IReadOnlyList<string> Faults
    {
        get { lock (Sync) { return FaultList.ToArray(); } }
    }

    /// <summary>Clears all ledger state. Call before a measured region.</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            Live.Clear();
            FaultList.Clear();
            _rents = 0;
            _returns = 0;
        }
    }

    internal static void OnRent(MediaBufferPool pool, byte[] buffer)
    {
        if (!Enabled || buffer == null) return;

        System.Threading.Interlocked.Increment(ref _rents);
        lock (Sync)
        {
            if (Live.ContainsKey(buffer))
            {
                // A pool handed out an array it already considers on loan: a re-rent of a live lease.
                FaultList.Add($"re-rent of a live lease from {pool.Name} (the same array was never returned)");
                return;
            }
            Live[buffer] = pool;
        }
    }

    internal static void OnReturn(MediaBufferPool pool, byte[] buffer)
    {
        if (!Enabled || buffer == null) return;

        System.Threading.Interlocked.Increment(ref _returns);
        lock (Sync)
        {
            if (!Live.TryGetValue(buffer, out MediaBufferPool? owner))
            {
                // Either a double return, or a buffer this pool never rented.
                FaultList.Add(
                    $"returned to {pool.Name} but it was not on loan (double return, or a foreign/unrented buffer)");
                return;
            }

            if (!ReferenceEquals(owner, pool))
            {
                FaultList.Add($"WRONG POOL: rented from {owner.Name}, returned to {pool.Name}");
                return;
            }

            Live.Remove(buffer);
        }
    }
}
