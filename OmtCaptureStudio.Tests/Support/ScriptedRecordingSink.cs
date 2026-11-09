using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Xml.Linq;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services;
using OmtCaptureStudio.Services.Audio;
using OmtCaptureStudio.Services.Sinks;
using OmtCaptureStudio.Services.Sources;
using OmtCaptureStudio.Services.Forensics;
using OmtCaptureStudio.ViewModels;

namespace OmtCaptureStudio.Tests;

partial class Program
{
    /// <summary>NET-011 test child: scriptable initialize/viability/shed, and it CAN rent a pooled buffer.</summary>
    private sealed class ScriptedRecordingSink : IRecordingSink
    {
        private readonly bool _rentsPooledBuffer;

        public ScriptedRecordingSink(string name, bool rentsPooledBuffer = false, bool initializeResult = true)
        {
            Name = name;
            _rentsPooledBuffer = rentsPooledBuffer;
            InitializeResult = initializeResult;
        }

        public string Name { get; }
        public bool InitializeResult { get; }
        public bool Survives { get; set; }
        public string? ShedTo { get; set; }
        public Exception? ThrowOnVideo { get; set; }
        public IntPtr LastVideoPointer { get; private set; }

        public int InitCount;
        public int FinalizeCount;
        public int DisposeCount;
        public int ShedCount;
        public int VideoFrames;
        public int AudioBytes;
        public int Rents;
        public int Returns;

        public long VideoFramesDropped => 0;
        public long AudioChunksDropped => 0;
        public string ResolvedEncoderName => Name;
        public string DiagnosticTail => string.Empty;
        public event Action<string>? SinkError;

        public void RaiseError(string message) => SinkError?.Invoke(message);

        public bool Initialize(RecordingConfig config, StreamFormat format, string outputPath)
        {
            InitCount++;
            return InitializeResult;
        }

        public void WriteVideo(IntPtr pData, int dataLength)
        {
            if (ThrowOnVideo != null) throw ThrowOnVideo;

            LastVideoPointer = pData;
            if (_rentsPooledBuffer)
            {
                byte[] rented = MediaBufferPool.Video.Rent(dataLength);
                Rents++;
                try { }
                finally { MediaBufferPool.Video.Return(rented); Returns++; }
            }

            VideoFrames++;
        }

        public void WriteAudio(byte[] buffer, int offset, int count)
        {
            if (_rentsPooledBuffer)
            {
                byte[] rented = MediaBufferPool.Audio.Rent(count);
                Rents++;
                try { }
                finally { MediaBufferPool.Audio.Return(rented); Returns++; }
            }

            AudioBytes += count;
        }

        public void FinalizeSink(out long totalBytesWritten)
        {
            FinalizeCount++;
            totalBytesWritten = VideoFrames * 100L;
        }

        public bool HasSurvivingDestination => Survives;

        public bool TryShedFailedDestination(string reason, out string? survivingPath)
        {
            ShedCount++;
            survivingPath = ShedTo;
            return ShedTo != null;
        }

        public void Dispose() => DisposeCount++;
    }
}

