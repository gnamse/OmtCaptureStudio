using System;

namespace OmtCaptureStudio.Services.Sinks;

public readonly struct VideoFramePacket
{
    public readonly byte[] Buffer;
    public readonly int Length;

    /// <summary>The pool that rented <see cref="Buffer"/>. It travels with the packet on purpose
    /// (see <see cref="MediaBufferPool"/>): a video frame must go back to the video pool, and a
    /// mix-up leaks silently rather than throwing.</summary>
    public readonly MediaBufferPool Pool;

    public VideoFramePacket(byte[] buffer, int length, MediaBufferPool pool)
    {
        Buffer = buffer;
        Length = length;
        Pool = pool;
    }

    /// <summary>Returns the rented buffer to the pool that owns it.</summary>
    public void Release()
    {
        if (Pool != null && Buffer != null) Pool.Return(Buffer);
    }
}

public readonly struct AudioDataPacket
{
    public readonly byte[] Buffer;
    public readonly int Length;

    /// <summary>The pool that rented <see cref="Buffer"/> — audio pools on the shared pool, not on
    /// <see cref="LargeArrayPool"/>. See <see cref="MediaBufferPool"/>.</summary>
    public readonly MediaBufferPool Pool;

    public AudioDataPacket(byte[] buffer, int length, MediaBufferPool pool)
    {
        Buffer = buffer;
        Length = length;
        Pool = pool;
    }

    /// <summary>Returns the rented buffer to the pool that owns it.</summary>
    public void Release()
    {
        if (Pool != null && Buffer != null) Pool.Return(Buffer);
    }
}
