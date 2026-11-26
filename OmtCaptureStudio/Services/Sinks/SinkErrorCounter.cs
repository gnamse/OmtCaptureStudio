namespace OmtCaptureStudio.Services.Sinks;

/// <summary>
/// Session-wide count of swallowed errors on the recording HOT PATH (REVIEW-028 finding 2.3).
///
/// <para><b>Deliberately non-generic.</b> A <c>static</c> field declared on a generic type is
/// per-closed-instantiation, not per-process - a counter on <c>BoundedMediaQueue&lt;T&gt;</c> would give
/// one counter for <c>&lt;VideoFramePacket&gt;</c> and a different one for <c>&lt;AudioDataPacket&gt;</c>, and the
/// shutdown total would silently under-report. Holding it here keeps ONE counter for the session.</para>
///
/// <para><b>Hot-path contract:</b> incremented with a lock-free <see cref="System.Threading.Interlocked.Add(ref int, int)"/>
/// and nothing else - no allocation, no logging, no formatting. The value is read once at shutdown.</para>
/// </summary>
internal static class SinkErrorCounter
{
    /// <summary>Swallowed errors observed on the hot path since process start.</summary>
    public static int Count;

    /// <summary>Hot-path tally, called from a catch with no other work to do.</summary>
    public static void Increment() => System.Threading.Interlocked.Increment(ref Count);
}
