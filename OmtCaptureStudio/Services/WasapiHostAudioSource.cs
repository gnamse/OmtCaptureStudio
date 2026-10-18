using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Audio;

using OmtCaptureStudio.Services.Forensics;
namespace OmtCaptureStudio.Services;

/// <summary>
/// Captures host desktop system audio (WASAPI loopback, excluding <c>OmtCaptureStudio</c>'s own
/// process tree on Windows 10 20348+ / Windows 11 to prevent monitoring feedback loops) and/or
/// the default microphone via direct unmanaged COM vtable interop (100% Native AOT compatible,
/// zero <c>[ComImport]</c> wrappers, zero per-frame allocations).
/// </summary>
public sealed class WasapiHostAudioSource : IHostAudioSource
{
    // Shared audio-layer vocabulary now lives in HostAudioFormat (ADR-0003).
    private const int MaxSamplesPerFill = HostAudioFormat.TargetSampleRate; // up to 1 second per read

    private readonly HostAudioCaptureMode _mode;
    private readonly WasapiEndpointCapture? _systemCapture;
    private readonly WasapiEndpointCapture? _micCapture;
    private readonly float[] _mixScratchL;
    private readonly float[] _mixScratchR;
    private int _disposed;

    public int SampleRate => HostAudioFormat.TargetSampleRate;
    public int Channels => HostAudioFormat.TargetChannels;

    /// <summary>True if at least one requested hardware capture endpoint started successfully.</summary>
    public bool IsHardwareActive =>
        (_systemCapture?.IsActive ?? false) || (_micCapture?.IsActive ?? false);

    // ---- IHostAudioSource (ADR-0003 interface widening; kills the caller downcast) --------------
    /// <summary>WASAPI capture failures are swallowed and surface as silence, so no error is latched.</summary>
    public string? LastError => null;

    /// <summary>Device mix rate in use (48000 when the endpoint already runs at the target rate).</summary>
    public int DeviceSampleRate =>
        _systemCapture?.DeviceSampleRate ?? _micCapture?.DeviceSampleRate ?? HostAudioFormat.TargetSampleRate;

    /// <summary>True when the endpoint negotiated a mix rate other than 48 kHz.</summary>
    public bool IsResamplingRequired => DeviceSampleRate != HostAudioFormat.TargetSampleRate;

    /// <summary>WASAPI mix-format negotiation has no operator-facing note.</summary>
    public string? RateNegotiationNote => null;

    /// <summary>WASAPI does not report input latency in frames.</summary>
    public int InputLatencyFrames => 0;

    public WasapiHostAudioSource(HostAudioCaptureMode mode, string? deviceId = null)
    {
        _mode = mode;

        if (mode == HostAudioCaptureMode.SystemAudio || mode == HostAudioCaptureMode.SystemAndMicrophone)
        {
            string? sysDeviceId = mode == HostAudioCaptureMode.SystemAudio ? deviceId : null;
            _systemCapture = new WasapiEndpointCapture(isLoopback: true, deviceId: sysDeviceId);
            _systemCapture.Start();
        }

        if (mode == HostAudioCaptureMode.Microphone || mode == HostAudioCaptureMode.SystemAndMicrophone)
        {
            _micCapture = new WasapiEndpointCapture(isLoopback: false, deviceId: deviceId);
            _micCapture.Start();
        }

        if (mode == HostAudioCaptureMode.SystemAndMicrophone)
        {
            _mixScratchL = new float[4800]; // supports down to 10 fps at 48 kHz
            _mixScratchR = new float[4800];
        }
        else
        {
            _mixScratchL = Array.Empty<float>();
            _mixScratchR = Array.Empty<float>();
        }
    }

    public void FillPlanarAudio(Span<float> planarDestination, int samplesPerChannel)
    {
        if (samplesPerChannel <= 0 || planarDestination.Length < samplesPerChannel * HostAudioFormat.TargetChannels)
        {
            planarDestination.Clear();
            return;
        }

        Span<float> left = planarDestination.Slice(0, samplesPerChannel);
        Span<float> right = planarDestination.Slice(samplesPerChannel, samplesPerChannel);

        if (Volatile.Read(ref _disposed) != 0 || _mode == HostAudioCaptureMode.Mute)
        {
            left.Clear();
            right.Clear();
            return;
        }

        if (_mode == HostAudioCaptureMode.SystemAudio)
        {
            if (_systemCapture != null)
            {
                _systemCapture.ReadPlanar(left, right);
            }
            else
            {
                left.Clear();
                right.Clear();
            }
            return;
        }

        if (_mode == HostAudioCaptureMode.Microphone)
        {
            if (_micCapture != null)
            {
                _micCapture.ReadPlanar(left, right);
            }
            else
            {
                left.Clear();
                right.Clear();
            }
            return;
        }

        // SystemAndMicrophone: read system audio into destination, read mic into scratch, and clamp-sum.
        if (_systemCapture != null)
        {
            _systemCapture.ReadPlanar(left, right);
        }
        else
        {
            left.Clear();
            right.Clear();
        }

        if (_micCapture != null && samplesPerChannel <= _mixScratchL.Length)
        {
            Span<float> micL = _mixScratchL.AsSpan(0, samplesPerChannel);
            Span<float> micR = _mixScratchR.AsSpan(0, samplesPerChannel);
            _micCapture.ReadPlanar(micL, micR);

            for (int i = 0; i < samplesPerChannel; i++)
            {
                left[i] = Math.Clamp(left[i] + micL[i], -1.0f, 1.0f);
                right[i] = Math.Clamp(right[i] + micR[i], -1.0f, 1.0f);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _systemCapture?.Dispose(); } catch { }
        try { _micCapture?.Dispose(); } catch { }
    }
}

/// <summary>
/// Manages a single WASAPI capture stream (either process-excluded / standard system loopback
/// or the default microphone) on a dedicated background pump thread feeding a bounded-latency
/// stereo ring buffer.
/// </summary>
internal sealed unsafe class WasapiEndpointCapture : IDisposable
{
    private readonly bool _isLoopback;
    private readonly string? _deviceId;

    // One shared ring implementation, rate-agnostic (ADR-0003).
    private readonly StereoCaptureRing _ring = new();

    // Negotiated device mix rate, published for the widened IHostAudioSource surface.
    private int _deviceSampleRate = HostAudioFormat.TargetSampleRate;

    // ADR-0003 (Slice 2): rate conversion moved to the CONSUMER thread, so the ring now holds
    // DEVICE-rate frames. Set on the capture thread before the stream goes active; null means the
    // device already runs at the stream rate, so the ring is read straight through (bit-exact).
    private volatile FixedRatioStereoResampler? _resampler;

    private float[] _pullLeft = Array.Empty<float>();      // resampler feed buffers (consumer thread)
    private float[] _pullRight = Array.Empty<float>();

    private Thread? _captureThread;
    private volatile bool _running;
    private volatile bool _isActive;
    private int _disposed;

    public bool IsActive => _isActive;

    /// <summary>Device mix rate this endpoint negotiated (published for the interface, ADR-0003).</summary>
    public int DeviceSampleRate => Volatile.Read(ref _deviceSampleRate);

    public WasapiEndpointCapture(bool isLoopback, string? deviceId = null)
    {
        _isLoopback = isLoopback;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _running) return;
        _running = true;

        _captureThread = new Thread(CaptureThreadMain)
        {
            Name = _isLoopback ? "WASAPI_SystemLoopback_Capture" : "WASAPI_Microphone_Capture",
            IsBackground = true
        };
        _captureThread.Start();
    }

    /// <summary>
    /// Consumer-side read at the stream rate (48 kHz). Ring policy (capacity, latency cap,
    /// underrun→silence) lives in <see cref="StereoCaptureRing"/>; rate conversion — when the device
    /// negotiated something other than 48 kHz — lives in the shared <see cref="FixedRatioStereoResampler"/>.
    /// </summary>
    public void ReadPlanar(Span<float> leftDest, Span<float> rightDest)
    {
        FixedRatioStereoResampler? resampler = _resampler;
        if (resampler == null)
        {
            // Device rate == stream rate: bit-exact passthrough.
            _ring.Read(leftDest, rightDest, leftDest.Length);
            return;
        }

        FillResampled(resampler, leftDest, rightDest, leftDest.Length);
    }

    /// <summary>
    /// ADR-0003 (Slice 2): pull device-rate frames from the ring and convert them on the CONSUMER
    /// thread, mirroring the ASIO path so both adapters share exactly one rate-conversion policy.
    /// This is what keeps the 96-tap sinc (~96 MACs/sample/channel) off the real-time capture callback.
    /// </summary>
    private void FillResampled(FixedRatioStereoResampler resampler, Span<float> leftDest, Span<float> rightDest, int wanted)
    {
        int maxFeed = Math.Min(resampler.MaxFeedFrames(wanted), _ring.CapacitySamples);
        EnsurePullCapacity(maxFeed);
        int feed = _ring.ReadUpTo(_pullLeft.AsSpan(0, maxFeed), _pullRight.AsSpan(0, maxFeed), maxFeed);
        if (feed > 0)
        {
            resampler.Append(_pullLeft, _pullRight, feed);
        }

        int produced = resampler.Process(leftDest, rightDest, wanted);
        if (produced < wanted)
        {
            leftDest.Slice(produced).Clear();
            rightDest.Slice(produced).Clear();
        }
    }

    private void EnsurePullCapacity(int frames)
    {
        if (_pullLeft.Length < frames)
        {
            _pullLeft = new float[frames];
            _pullRight = new float[frames];
        }
    }

    private void CaptureThreadMain()
    {
        int coInitHr = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        bool shouldUninit = coInitHr >= 0;

        IntPtr pEnumerator = IntPtr.Zero;
        IntPtr pDevice = IntPtr.Zero;
        IntPtr pAudioClient = IntPtr.Zero;
        IntPtr pCaptureClient = IntPtr.Zero;
        WaveFormatEx* pMixFormat = null;
        bool clientStarted = false;

        try
        {
            // 1. Acquire IAudioClient:
            // For Default System Loopback (_deviceId == null), try Windows 10 20348+ / Windows 11
            // Process-Excluded Loopback first so OmtCaptureStudio's own XAudio2Monitor playback
            // is excluded at the OS mixer level.
            if (_isLoopback && _deviceId == null)
            {
                pAudioClient = TryActivateProcessExcludedLoopbackClient((uint)Environment.ProcessId);
            }

            // Specific hardware device ID or fallback / Microphone path:
            // IMMDeviceEnumerator -> GetDevice(id) / GetDefaultAudioEndpoint -> Activate(IAudioClient).
            if (pAudioClient == IntPtr.Zero)
            {
                Guid clsidEnumerator = ClsidMMDeviceEnumerator;
                Guid iidEnumerator = IidIMMDeviceEnumerator;
                int hr = CoCreateInstance(&clsidEnumerator, IntPtr.Zero, ClsctxAll, &iidEnumerator, &pEnumerator);
                if (hr < 0 || pEnumerator == IntPtr.Zero) return;

                int dataFlow = _isLoopback ? ERender : ECapture;
                if (_deviceId != null)
                {
                    hr = GetDeviceById(pEnumerator, _deviceId, &pDevice);
                }

                if (pDevice == IntPtr.Zero)
                {
                    hr = GetDefaultAudioEndpoint(pEnumerator, dataFlow, EConsole, &pDevice);
                    if ((hr < 0 || pDevice == IntPtr.Zero) && !_isLoopback)
                    {
                        hr = GetDefaultAudioEndpoint(pEnumerator, dataFlow, ECommunications, &pDevice);
                    }
                }
                if (hr < 0 || pDevice == IntPtr.Zero) return;

                Guid iidAudioClient = IidIAudioClient;
                hr = ActivateDevice(pDevice, &iidAudioClient, ClsctxAll, IntPtr.Zero, &pAudioClient);
                if (hr < 0 || pAudioClient == IntPtr.Zero) return;
            }

            // 2. Initialize IAudioClient:
            // Prefer 48 kHz 2-channel 32-bit IEEE float directly via AUTOCONVERTPCM.
            WaveFormatEx desiredFormat = new()
            {
                FormatTag = WaveFormatIeeeFloat,
                Channels = HostAudioFormat.TargetChannels,
                SamplesPerSec = HostAudioFormat.TargetSampleRate,
                BitsPerSample = 32,
                BlockAlign = (ushort)(HostAudioFormat.TargetChannels * sizeof(float)),
                AvgBytesPerSec = HostAudioFormat.TargetSampleRate * HostAudioFormat.TargetChannels * sizeof(float),
                Size = 0
            };

            uint baseStreamFlags = _isLoopback ? AudclntStreamFlagsLoopback : 0u;
            uint autoConvertFlags = baseStreamFlags | AudclntStreamFlagsAutoConvertPcm | AudclntStreamFlagsSrcDefaultQuality;
            const long BufferDuration100Ns = 1_000_000; // 100 ms shared buffer

            int initHr = AudioClientInitialize(pAudioClient, AudclntShareModeShared, autoConvertFlags, BufferDuration100Ns, 0, &desiredFormat, IntPtr.Zero);

            int deviceSampleRate = HostAudioFormat.TargetSampleRate;
            int deviceChannels = HostAudioFormat.TargetChannels;
            int deviceBitsPerSample = 32;
            bool deviceIsFloat = true;

            if (initHr < 0)
            {
                // Fallback to device native mix format if driver/OS rejects AUTOCONVERTPCM
                int mixHr = AudioClientGetMixFormat(pAudioClient, &pMixFormat);
                if (mixHr < 0 || pMixFormat == null) return;

                initHr = AudioClientInitialize(pAudioClient, AudclntShareModeShared, baseStreamFlags, BufferDuration100Ns, 0, pMixFormat, IntPtr.Zero);
                if (initHr < 0) return;

                deviceSampleRate = (int)pMixFormat->SamplesPerSec;
                deviceChannels = pMixFormat->Channels;
                deviceBitsPerSample = pMixFormat->BitsPerSample;
                deviceIsFloat = IsWaveFormatFloat(pMixFormat);
            }

            // 3. Acquire IAudioCaptureClient service and start stream
            Guid iidCaptureClient = IidIAudioCaptureClient;
            int svcHr = AudioClientGetService(pAudioClient, &iidCaptureClient, &pCaptureClient);
            if (svcHr < 0 || pCaptureClient == IntPtr.Zero) return;

            int startHr = AudioClientStart(pAudioClient);
            if (startHr < 0) return;

            // ADR-0003 (Slice 2): publish the negotiated rate and the consumer-side resampler BEFORE the
            // endpoint reports itself active, so the consumer can never observe active-with-no-converter.
            // A null resampler means the device already runs at the stream rate (bit-exact passthrough).
            Volatile.Write(ref _deviceSampleRate, deviceSampleRate);
            _resampler = deviceSampleRate == HostAudioFormat.TargetSampleRate
                ? null
                : new FixedRatioStereoResampler(deviceSampleRate, HostAudioFormat.TargetSampleRate);

            clientStarted = true;
            _isActive = true;
            AppLogger.LogInfo($"WASAPI {(_isLoopback ? "System Loopback" : "Microphone")} capture active ({deviceSampleRate} Hz, {deviceChannels}ch, {deviceBitsPerSample}-bit {(deviceIsFloat ? "float" : "PCM")}; conversion on the consumer thread).");

            while (_running && Volatile.Read(ref _disposed) == 0)
            {
                uint packetLength = 0;
                int pktHr = CaptureClientGetNextPacketSize(pCaptureClient, &packetLength);
                if (pktHr < 0) break;

                if (packetLength == 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                while (packetLength > 0 && _running && Volatile.Read(ref _disposed) == 0)
                {
                    byte* pData = null;
                    uint numFrames = 0;
                    uint flags = 0;
                    ulong devPos = 0;
                    ulong qpcPos = 0;

                    int bufHr = CaptureClientGetBuffer(pCaptureClient, &pData, &numFrames, &flags, &devPos, &qpcPos);
                    if (bufHr < 0) break;

                    try
                    {
                        bool isSilent = (flags & AudclntBufferFlagsSilent) != 0 || pData == null;
                        if (numFrames > 0)
                        {
                            IngestPacket(pData, (int)numFrames, isSilent, deviceChannels, deviceBitsPerSample, deviceIsFloat);
                        }
                    }
                    finally
                    {
                        CaptureClientReleaseBuffer(pCaptureClient, numFrames);
                    }

                    pktHr = CaptureClientGetNextPacketSize(pCaptureClient, &packetLength);
                    if (pktHr < 0) break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"WASAPI {(_isLoopback ? "System Loopback" : "Microphone")} capture error: {ex.Message}", ex);
        }
        finally
        {
            _isActive = false;
            if (clientStarted && pAudioClient != IntPtr.Zero)
            {
                try { AudioClientStop(pAudioClient); } catch { }
            }
            if (pMixFormat != null)
            {
                CoTaskMemFree((IntPtr)pMixFormat);
            }
            ReleaseComObject(pCaptureClient);
            ReleaseComObject(pAudioClient);
            ReleaseComObject(pDevice);
            ReleaseComObject(pEnumerator);

            if (shouldUninit)
            {
                CoUninitialize();
            }
        }
    }

    /// <summary>
    /// Real-time capture callback path. ADR-0003 (Slice 2): this does format conversion ONLY —
    /// device frames are pushed into the ring at the DEVICE rate and no rate conversion happens here,
    /// which is what removes the ~96 MACs/sample/channel sinc from the driver callback.
    /// </summary>
    private void IngestPacket(
        byte* pData,
        int numFrames,
        bool isSilent,
        int deviceChannels,
        int deviceBitsPerSample,
        bool deviceIsFloat)
    {
        lock (_ring.SyncRoot)
        {
            for (int frame = 0; frame < numFrames; frame++)
            {
                ReadFrameStereo(pData, frame, isSilent, deviceChannels, deviceBitsPerSample, deviceIsFloat, out float l, out float r);
                _ring.WriteSampleLocked(l, r);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadFrameStereo(
        byte* pData,
        int frameIndex,
        bool isSilent,
        int channels,
        int bitsPerSample,
        bool isFloat,
        out float left,
        out float right)
    {
        if (isSilent || pData == null || channels <= 0)
        {
            left = 0.0f;
            right = 0.0f;
            return;
        }

        if (isFloat && bitsPerSample == 32)
        {
            float* pSamples = (float*)pData + (frameIndex * channels);
            left = Math.Clamp(pSamples[0], -1.0f, 1.0f);
            right = channels >= 2 ? Math.Clamp(pSamples[1], -1.0f, 1.0f) : left;
            return;
        }

        if (!isFloat && bitsPerSample == 16)
        {
            short* pSamples = (short*)pData + (frameIndex * channels);
            left = pSamples[0] / 32768.0f;
            right = channels >= 2 ? pSamples[1] / 32768.0f : left;
            return;
        }

        if (!isFloat && bitsPerSample == 32)
        {
            int* pSamples = (int*)pData + (frameIndex * channels);
            left = (float)(pSamples[0] / 2147483648.0);
            right = channels >= 2 ? (float)(pSamples[1] / 2147483648.0) : left;
            return;
        }

        if (!isFloat && bitsPerSample == 24)
        {
            int bytesPerSample = 3;
            byte* pFrame = pData + (frameIndex * channels * bytesPerSample);
            int s0 = (pFrame[0] << 8) | (pFrame[1] << 16) | (pFrame[2] << 24);
            left = (float)(s0 / 2147483648.0);
            if (channels >= 2)
            {
                int s1 = (pFrame[3] << 8) | (pFrame[4] << 16) | (pFrame[5] << 24);
                right = (float)(s1 / 2147483648.0);
            }
            else
            {
                right = left;
            }
            return;
        }

        left = 0.0f;
        right = 0.0f;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _running = false;
        Thread? thread = _captureThread;
        _captureThread = null;
        if (thread != null && thread.IsAlive)
        {
            thread.Join(500);
        }
    }

    // =========================================================================
    // Process-Excluded Loopback Activation (Windows 10 Build 20348+ / Win 11)
    // Excludes OmtCaptureStudio's own ProcessId so speaker monitoring never
    // feeds back into System Audio capture.
    // =========================================================================

    private static IntPtr TryActivateProcessExcludedLoopbackClient(uint excludedProcessId)
    {
        CompletionHandlerNative* handler = null;
        IntPtr operation = IntPtr.Zero;

        try
        {
            IntPtr hEvent = CreateEventW(IntPtr.Zero, bManualReset: 1, bInitialState: 0, IntPtr.Zero);
            if (hEvent == IntPtr.Zero) return IntPtr.Zero;

            handler = (CompletionHandlerNative*)NativeMemory.AllocZeroed((nuint)sizeof(CompletionHandlerNative));
            handler->Vtbl = GetCompletionHandlerVtbl();
            handler->RefCount = 1;
            handler->CompletionEvent = hEvent;
            handler->ActivateHr = -1;
            handler->AudioClientPtr = IntPtr.Zero;

            AudioClientActivationParams activationParams = new()
            {
                ActivationType = AudioClientActivationTypeProcessLoopback,
                TargetProcessId = excludedProcessId,
                ProcessLoopbackMode = ProcessLoopbackModeExcludeTargetProcessTree
            };

            PropVariantBlob propVariant = new()
            {
                Vt = VtBlob,
                CbSize = (uint)sizeof(AudioClientActivationParams),
                PBlobData = (IntPtr)(&activationParams)
            };

            Guid iidAudioClient = IidIAudioClient;
            int hr = ActivateAudioInterfaceAsync(
                "VAD\\Process_Loopback",
                ref iidAudioClient,
                ref propVariant,
                (IntPtr)handler,
                out operation);

            if (hr < 0)
            {
                return IntPtr.Zero;
            }

            uint waitRes = WaitForSingleObject(hEvent, 1500);
            if (waitRes != 0 || handler->ActivateHr < 0 || handler->AudioClientPtr == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // Take ownership of the activated IAudioClient pointer before releasing handler
            IntPtr client = handler->AudioClientPtr;
            handler->AudioClientPtr = IntPtr.Zero;
            return client;
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            ReleaseComObject(operation);
            if (handler != null)
            {
                ReleaseCompletionHandler(handler);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint CbSize;
        public uint Padding;
        public IntPtr PBlobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompletionHandlerNative
    {
        public IntPtr* Vtbl;
        public int RefCount;
        public IntPtr CompletionEvent;
        public int ActivateHr;
        public IntPtr AudioClientPtr;
    }

    private static IntPtr* s_completionVtbl;
    private static readonly object s_vtblLock = new();

    private static IntPtr* GetCompletionHandlerVtbl()
    {
        if (s_completionVtbl != null) return s_completionVtbl;
        lock (s_vtblLock)
        {
            if (s_completionVtbl == null)
            {
                IntPtr* vtbl = (IntPtr*)NativeMemory.Alloc((nuint)(4 * sizeof(IntPtr)));
                vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<CompletionHandlerNative*, Guid*, IntPtr*, int>)&CompletionQueryInterface;
                vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<CompletionHandlerNative*, uint>)&CompletionAddRef;
                vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<CompletionHandlerNative*, uint>)&CompletionRelease;
                vtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<CompletionHandlerNative*, IntPtr, int>)&CompletionActivateCompleted;
                s_completionVtbl = vtbl;
            }
            return s_completionVtbl;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CompletionQueryInterface(CompletionHandlerNative* self, Guid* riid, IntPtr* ppvObject)
    {
        if (ppvObject == null) return EPointer;
        if (*riid == IidIUnknown || *riid == IidIAgileObject || *riid == IidIActivateAudioInterfaceCompletionHandler)
        {
            *ppvObject = (IntPtr)self;
            Interlocked.Increment(ref self->RefCount);
            return 0; // S_OK
        }
        *ppvObject = IntPtr.Zero;
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint CompletionAddRef(CompletionHandlerNative* self)
    {
        return (uint)Interlocked.Increment(ref self->RefCount);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint CompletionRelease(CompletionHandlerNative* self)
    {
        return ReleaseCompletionHandler(self);
    }

    private static uint ReleaseCompletionHandler(CompletionHandlerNative* self)
    {
        int remaining = Interlocked.Decrement(ref self->RefCount);
        if (remaining == 0)
        {
            if (self->CompletionEvent != IntPtr.Zero)
            {
                CloseHandle(self->CompletionEvent);
                self->CompletionEvent = IntPtr.Zero;
            }
            if (self->AudioClientPtr != IntPtr.Zero)
            {
                ReleaseComObject(self->AudioClientPtr);
                self->AudioClientPtr = IntPtr.Zero;
            }
            NativeMemory.Free(self);
        }
        return (uint)Math.Max(0, remaining);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CompletionActivateCompleted(CompletionHandlerNative* self, IntPtr activateOperation)
    {
        if (activateOperation != IntPtr.Zero)
        {
            int hrResult = -1;
            IntPtr activatedInterface = IntPtr.Zero;
            void** vtbl = *(void***)activateOperation;
            var getActivateResult = (delegate* unmanaged[Stdcall]<IntPtr, int*, IntPtr*, int>)vtbl[3];
            int hr = getActivateResult(activateOperation, &hrResult, &activatedInterface);
            self->ActivateHr = hr >= 0 ? hrResult : hr;
            self->AudioClientPtr = activatedInterface;
        }

        if (self->CompletionEvent != IntPtr.Zero)
        {
            SetEvent(self->CompletionEvent);
        }
        return 0; // S_OK
    }

    // =========================================================================
    // Unmanaged COM VTable Helpers & Win32 / CoreAudio Constants
    // =========================================================================

    private const int CoInitMultithreaded = 0x0;
    private const uint ClsctxAll = 0x17;
    private const int ERender = 0;
    private const int ECapture = 1;
    private const int EConsole = 0;
    private const int ECommunications = 2;

    private const int AudclntShareModeShared = 0;
    private const uint AudclntStreamFlagsLoopback = 0x00020000;
    private const uint AudclntStreamFlagsAutoConvertPcm = 0x80000000;
    private const uint AudclntStreamFlagsSrcDefaultQuality = 0x08000000;
    private const uint AudclntBufferFlagsSilent = 0x2;

    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatTagExtensible = 0xFFFE;

    private const ushort VtBlob = 65;
    private const int AudioClientActivationTypeProcessLoopback = 1;
    private const int ProcessLoopbackModeExcludeTargetProcessTree = 1;

    private const int EPointer = unchecked((int)0x80004003);
    private const int ENoInterface = unchecked((int)0x80004002);

    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidIAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IidIActivateAudioInterfaceCompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort SamplesUnion;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    private static bool IsWaveFormatFloat(WaveFormatEx* fmt)
    {
        if (fmt->FormatTag == WaveFormatIeeeFloat) return true;
        if (fmt->FormatTag == WaveFormatTagExtensible && fmt->Size >= 22)
        {
            WaveFormatExtensible* ext = (WaveFormatExtensible*)fmt;
            return ext->SubFormat == SubTypeIeeeFloat;
        }
        return false;
    }

    private static void ReleaseComObject(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            void** vtbl = *(void***)ptr;
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
            release(ptr);
        }
    }

    private static int GetDefaultAudioEndpoint(IntPtr pEnumerator, int dataFlow, int role, IntPtr* ppDevice)
    {
        void** vtbl = *(void***)pEnumerator;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)vtbl[4];
        return fn(pEnumerator, dataFlow, role, ppDevice);
    }

    private static int GetDeviceById(IntPtr pEnumerator, string deviceId, IntPtr* ppDevice)
    {
        void** vtbl = *(void***)pEnumerator;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)vtbl[5];
        fixed (char* pId = deviceId)
        {
            return fn(pEnumerator, pId, ppDevice);
        }
    }

    private static int ActivateDevice(IntPtr pDevice, Guid* iid, uint clsCtx, IntPtr pActivationParams, IntPtr* ppInterface)
    {
        void** vtbl = *(void***)pDevice;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)vtbl[3];
        return fn(pDevice, iid, clsCtx, pActivationParams, ppInterface);
    }

    private static int AudioClientInitialize(IntPtr pAudioClient, int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, WaveFormatEx* pFormat, IntPtr sessionGuid)
    {
        void** vtbl = *(void***)pAudioClient;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, WaveFormatEx*, IntPtr, int>)vtbl[3];
        return fn(pAudioClient, shareMode, streamFlags, hnsBufferDuration, hnsPeriodicity, pFormat, sessionGuid);
    }

    private static int AudioClientGetMixFormat(IntPtr pAudioClient, WaveFormatEx** ppDeviceFormat)
    {
        void** vtbl = *(void***)pAudioClient;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, WaveFormatEx**, int>)vtbl[8];
        return fn(pAudioClient, ppDeviceFormat);
    }

    private static int AudioClientStart(IntPtr pAudioClient)
    {
        void** vtbl = *(void***)pAudioClient;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[10];
        return fn(pAudioClient);
    }

    private static int AudioClientStop(IntPtr pAudioClient)
    {
        void** vtbl = *(void***)pAudioClient;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[11];
        return fn(pAudioClient);
    }

    private static int AudioClientGetService(IntPtr pAudioClient, Guid* riid, IntPtr* ppv)
    {
        void** vtbl = *(void***)pAudioClient;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[14];
        return fn(pAudioClient, riid, ppv);
    }

    private static int CaptureClientGetBuffer(IntPtr pCaptureClient, byte** ppData, uint* pNumFramesToRead, uint* pdwFlags, ulong* pu64DevicePosition, ulong* pu64QPCPosition)
    {
        void** vtbl = *(void***)pCaptureClient;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, ulong*, ulong*, int>)vtbl[3];
        return fn(pCaptureClient, ppData, pNumFramesToRead, pdwFlags, pu64DevicePosition, pu64QPCPosition);
    }

    private static int CaptureClientReleaseBuffer(IntPtr pCaptureClient, uint numFramesRead)
    {
        void** vtbl = *(void***)pCaptureClient;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vtbl[4];
        return fn(pCaptureClient, numFramesRead);
    }

    private static int CaptureClientGetNextPacketSize(IntPtr pCaptureClient, uint* pNumFramesInNextPacket)
    {
        void** vtbl = *(void***)pCaptureClient;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vtbl[5];
        return fn(pCaptureClient, pNumFramesInNextPacket);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(Guid* rclsid, IntPtr pUnkOuter, uint dwClsContext, Guid* riid, IntPtr* ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariantBlob activationParams,
        IntPtr completionHandler,
        out IntPtr activationOperation);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, int bManualReset, int bInitialState, IntPtr lpName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int CloseHandle(IntPtr hObject);
}

/// <summary>
/// Enumerates active WASAPI audio output (loopback) and input (microphone) endpoints
/// via direct unmanaged COM vtable calls (100% Native AOT compatible).
/// </summary>
public static unsafe class AudioDeviceEnumerator
{
    private const int CoInitMultithreaded = 0x0;
    private const uint ClsctxAll = 0x17;
    private const int ERender = 0;
    private const int ECapture = 1;
    private const uint DeviceStateActive = 0x1;
    private const uint StgmRead = 0x0;
    private const ushort VtLpwstr = 31;

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly PropertyKey PkeyDeviceFriendlyName = new()
    {
        Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        Pid = 14
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantValue
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr PtrVal;
        public ulong Padding;
    }

    /// <summary>
    /// Returns the preset host audio capture modes plus every active WASAPI output (loopback)
    /// and input (microphone) hardware device on the system.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<HostAudioCaptureOption> EnumerateOptions()
    {
        var list = new System.Collections.Generic.List<HostAudioCaptureOption>
        {
            new(HostAudioCaptureMode.SystemAudio, "System Audio (Default \u00B7 No Echo)", null),
            new(HostAudioCaptureMode.Microphone, "Microphone (Default)", null),
            new(HostAudioCaptureMode.SystemAndMicrophone, "System Audio + Default Mic", null)
        };

        if (OperatingSystem.IsWindows())
        {
            EnumerateFlowDevices(ERender, HostAudioCaptureMode.SystemAudio, "Output \u00B7 ", list);
            EnumerateFlowDevices(ECapture, HostAudioCaptureMode.Microphone, "Mic \u00B7 ", list);
        }

        list.Add(new(HostAudioCaptureMode.Mute, "Mute (Silent 48 kHz Clock)", null));
        return list;
    }

    private static void EnumerateFlowDevices(
        int dataFlow,
        HostAudioCaptureMode mode,
        string prefix,
        System.Collections.Generic.List<HostAudioCaptureOption> targetList)
    {
        int coInitHr = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        bool shouldUninit = coInitHr >= 0;

        IntPtr pEnumerator = IntPtr.Zero;
        IntPtr pCollection = IntPtr.Zero;

        try
        {
            Guid clsid = ClsidMMDeviceEnumerator;
            Guid iid = IidIMMDeviceEnumerator;
            int hr = CoCreateInstance(&clsid, IntPtr.Zero, ClsctxAll, &iid, &pEnumerator);
            if (hr < 0 || pEnumerator == IntPtr.Zero) return;

            void** enumVtbl = *(void***)pEnumerator;
            var enumEndpoints = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, int>)enumVtbl[3];
            hr = enumEndpoints(pEnumerator, dataFlow, DeviceStateActive, &pCollection);
            if (hr < 0 || pCollection == IntPtr.Zero) return;

            void** collVtbl = *(void***)pCollection;
            var getCount = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)collVtbl[3];
            var getItem = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)collVtbl[4];

            uint count = 0;
            if (getCount(pCollection, &count) < 0 || count == 0) return;

            for (uint i = 0; i < count; i++)
            {
                IntPtr pDevice = IntPtr.Zero;
                try
                {
                    if (getItem(pCollection, i, &pDevice) < 0 || pDevice == IntPtr.Zero)
                        continue;

                    string? deviceId = TryGetDeviceId(pDevice);
                    if (string.IsNullOrWhiteSpace(deviceId))
                        continue;

                    string? friendlyName = TryGetDeviceFriendlyName(pDevice);
                    if (string.IsNullOrWhiteSpace(friendlyName))
                        friendlyName = $"Audio Endpoint {i + 1}";

                    targetList.Add(new HostAudioCaptureOption(mode, $"{prefix}{friendlyName}", deviceId));
                }
                finally
                {
                    ReleaseCom(pDevice);
                }
            }
        }
        catch
        {
            // Ignore device enumeration errors on headless/CI machines without audio subsystem
        }
        finally
        {
            ReleaseCom(pCollection);
            ReleaseCom(pEnumerator);
            if (shouldUninit)
            {
                CoUninitialize();
            }
        }
    }

    private static string? TryGetDeviceId(IntPtr pDevice)
    {
        void** vtbl = *(void***)pDevice;
        var getId = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[5];
        IntPtr pStr = IntPtr.Zero;
        int hr = getId(pDevice, &pStr);
        if (hr < 0 || pStr == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(pStr);
        }
        finally
        {
            CoTaskMemFree(pStr);
        }
    }

    private static string? TryGetDeviceFriendlyName(IntPtr pDevice)
    {
        void** devVtbl = *(void***)pDevice;
        var openPropStore = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)devVtbl[4];
        IntPtr pStore = IntPtr.Zero;
        if (openPropStore(pDevice, StgmRead, &pStore) < 0 || pStore == IntPtr.Zero)
            return null;

        try
        {
            void** storeVtbl = *(void***)pStore;
            var getValue = (delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, PropVariantValue*, int>)storeVtbl[5];
            PropertyKey key = PkeyDeviceFriendlyName;
            PropVariantValue pv = default;
            if (getValue(pStore, &key, &pv) < 0)
                return null;

            try
            {
                if (pv.Vt == VtLpwstr && pv.PtrVal != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(pv.PtrVal);
                }
                return null;
            }
            finally
            {
                PropVariantClear(&pv);
            }
        }
        finally
        {
            ReleaseCom(pStore);
        }
    }

    private static void ReleaseCom(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            void** vtbl = *(void***)ptr;
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
            release(ptr);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(Guid* rclsid, IntPtr pUnkOuter, uint dwClsContext, Guid* riid, IntPtr* ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(PropVariantValue* pvar);
}
