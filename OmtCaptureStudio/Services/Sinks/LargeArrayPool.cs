using System.Buffers;

namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// A dedicated ArrayPool for video frame payloads.
///
/// <see cref="ArrayPool{T}.Shared"/> caps its largest bucket at 1 MB (2^20), so
/// a 1080p BGRA frame (~8.29 MB) never pools on Shared: every Rent allocated a
/// fresh LOH array on the small-object heap's large-object region and every
/// Return immediately discarded it. At 60 fps that is ~500 MB/s of gen2/LOH
/// churn plus address-space fragmentation, which spikes malloc latency on
/// multi-hour captures.
///
/// This pool is sized so full video frames pool instead of allocate. Audio
/// chunks are always &lt; 1 MB and genuinely pool on <see cref="ArrayPool{T}.Shared"/>,
/// so the audio path is intentionally left on Shared.
///
/// Oversized frames (larger than <see cref="MaxFrameBuffer"/>, e.g. a future
/// resolution change) are handled safely by ArrayPool.Create itself: Rent falls
/// back to a fresh untracked array and Return on an oversized array is a no-op,
/// so exactly-once return semantics are preserved even past the bucket limit.
/// </summary>
internal static class LargeArrayPool
{
    /// <summary>Largest poolable array: 16 MB (2^24), covering 1080p BGRA (~8.29 MB) and headroom.</summary>
    private const int MaxFrameBuffer = 1 << 24;

    /// <summary>Max arrays retained per bucket.</summary>
    private const int MaxArraysPerBucket = 8;

    /// <summary>
    /// Reusable, thread-safe pool for per-frame video buffers. Held static so the
    /// sink (Rent + queue drop callback) and the pipe worker (Return) share one pool.
    /// </summary>
    public static readonly ArrayPool<byte> Shared =
        ArrayPool<byte>.Create(MaxFrameBuffer, MaxArraysPerBucket);
}
