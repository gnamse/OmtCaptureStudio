using System;

namespace OmtCaptureStudio.Services.Sinks;

public readonly struct VideoFramePacket
{
    public readonly byte[] Buffer;
    public readonly int Length;

    public VideoFramePacket(byte[] buffer, int length)
    {
        Buffer = buffer;
        Length = length;
    }
}

public readonly struct AudioDataPacket
{
    public readonly byte[] Buffer;
    public readonly int Length;

    public AudioDataPacket(byte[] buffer, int length)
    {
        Buffer = buffer;
        Length = length;
    }
}
