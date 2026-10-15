using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OmtCaptureStudio.Models;
using OmtCaptureStudio.Services.Audio;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Captures pro-audio input from a user-selected Steinberg ASIO driver and delivers 48 kHz
/// stereo planar 32-bit float frames (the same contract as <see cref="WasapiHostAudioSource"/>),
/// implemented entirely by direct unmanaged COM vtable interop: 100% Native AOT compatible,
/// zero <c>[ComImport]</c> wrappers, zero <c>Marshal.GetFunctionPointerForDelegate</c> closures,
/// zero reflection and zero per-frame allocations.
///
/// ASIO is exclusive — only one client may hold a driver open at a time — so the driver lifecycle
/// (<c>init</c> → <c>createBuffers</c> → <c>start</c> → <c>stop</c> → <c>disposeBuffers</c> →
/// <c>Release</c>) is owned by one dedicated STA worker thread and torn down there, because that
/// apartment must stay alive for the whole session and only it may legally talk to the driver.
///
/// Two important ASIO SDK facts shape this implementation:
///
/// 1. The instance pointer is obtained from <c>CoCreateInstance</c> asking for <b>IID_IUnknown</b>
///    rather than a hard-coded IID_IASIO, then the <b>IASIO face is resolved with a QueryInterface
///    against the driver's own class id</b> — validated empirically against Realtek ASIO and
///    VB-Matrix VASIO: the bare IUnknown face is a dead end (Realtek's holds only 4 pointers and
///    hands you adjacent string data afterwards, so calling "slot 4" faults), whereas every driver
///    answers QueryInterface with its class id and returns a pointer whose vtable is exactly the
///    24 entries of IUnknown + IASIO. Nothing is ever executed through a candidate vtable until it
///    has passed both a structural check (<see cref="LooksLikeAsioVtable"/>) and the functional
///    getDriverName comparison against the registry display name.
///
/// 2. <c>ASIOCallbacks</c> carries <b>no context/closure pointer</b> — <c>bufferSwitch</c> receives
///    only (index, directProcess). A static callback together with a single static active-instance
///    slot is therefore the only correct way to reach back into managed state (see <c>s_active</c>).
///
/// Sample rate policy (transparency first): before <c>createBuffers</c> we call
/// <c>canSampleRate(48000)</c> and, when the driver answers OK, <c>setSampleRate(48000)</c>. When that
/// path succeeds there is no resampling at all — the ring holds native 48 kHz samples and
/// <see cref="FillPlanarAudio"/> copies them straight out. Only when the driver refuses 48 kHz (and
/// reports a different native rate) do we fall back to an internal fixed-ratio windowed-sinc
/// (Blackman-Harris, 96 taps / 64 phases) fractional resampler, mirroring the kernel quality of
/// <see cref="AudioClockResampler"/> but with a plain fixed ratio instead of a video-coupled PLL.
///
/// Threading: the ASIO driver raises its callbacks on threads it owns, not on any thread we created.
/// The callbacks are <c>[UnmanagedCallersOnly]</c> statics, so each invocation attaches that foreign
/// thread to the runtime automatically; they then only append into the locked dual-channel float ring
/// and never block. <see cref="FillPlanarAudio"/> drains the ring and must never block either.
/// </summary>
public sealed unsafe class AsioHostAudioSource : IHostAudioSource
{
    // Shared audio-layer vocabulary (ADR-0003): the ring policy itself now lives in StereoCaptureRing.
    private const int TargetSampleRate = HostAudioFormat.TargetSampleRate; // 48000 Hz
    private const int TargetChannels = HostAudioFormat.TargetChannels;     // 2 (stereo)

    private const int CoInitApartmentThreaded = 0x2;
    private const uint ClsCtxInprocServer = 0x1;
    private const int CoInitSFalse = 1;          // CoInitializeEx: already initialized on this thread
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    private const int MaxDriverNameBytes = 256;  // ASIO hosts pass ~124; over-allocate defensively
    private const int MaxStopWaitMs = 2000;
    private const int CallbackDrainMs = 200;     // grace period for an in-flight driver callback

    // ---- ASIOError (int per the C enum) -------------------------------------------------------
    private const int AseOk = 0;
    private const int AseSuccess = 0x3f4847a0;   // ASE_SUCCESS magic returned by handled asioMessage selectors

    // ---- ASIOSampleType -----------------------------------------------------------------------
    private const int AsioStInt16Msb = 0;
    private const int AsioStInt24Msb = 1;
    private const int AsioStInt32Msb = 2;
    private const int AsioStFloat32Msb = 3;
    private const int AsioStFloat64Msb = 4;
    private const int AsioStInt32Msb16 = 8;
    private const int AsioStInt32Msb18 = 9;
    private const int AsioStInt32Msb20 = 10;
    private const int AsioStInt32Msb24 = 11;
    private const int AsioStInt16Lsb = 16;
    private const int AsioStInt24Lsb = 17;
    private const int AsioStInt32Lsb = 18;
    private const int AsioStFloat32Lsb = 19;
    private const int AsioStFloat64Lsb = 20;
    private const int AsioStInt32Lsb16 = 24;
    private const int AsioStInt32Lsb18 = 25;
    private const int AsioStInt32Lsb20 = 26;
    private const int AsioStInt32Lsb24 = 27;

    // ---- asioMessage selectors ----------------------------------------------------------------
    private const int KAsioSelectorSupported = 1;
    private const int KAsioEngineVersion = 2;
    private const int KAsioResetRequest = 3;
    private const int KAsioBufferSizeChange = 4;
    private const int KAsioResyncRequest = 5;
    private const int KAsioLatenciesChanged = 6;
    private const int KAsioSupportsTimeInfo = 7;
    private const int KAsioSupportsTimeCode = 8;
    private const int KAsioSupportsInputMonitor = 13;
    private const int KAsioOverload = 16;

    private readonly AsioCaptureOption _option;

    /// <summary>The IUnknown identity face handed back by <c>CoCreateInstance</c>; kept so both faces are released.</summary>
    private IntPtr _driverUnknown;
    private int _callbackCount;
    private string? _negotiationNote;

    /// <summary>How the driver responded when we asked it to run at 48 kHz (<c>null</c> when it complied).</summary>
    public string? SampleRateNegotiationNote => _negotiationNote;

    /// <summary>
    /// Number of buffer-switch callbacks the driver has delivered. Non-zero growth proves the
    /// <c>createBuffers</c> → <c>start</c> → callback path is live even while every sample is silence.
    /// </summary>
    public long CallbacksInvoked => Volatile.Read(ref _callbackCount);

    // ---- capture ring: ONE shared implementation (ADR-0003) -------------------------------------
    private readonly StereoCaptureRing _ring = new();

    private float[] _scratchLeft = Array.Empty<float>();   // per-callback conversion target
    private float[] _scratchRight = Array.Empty<float>();
    private float[] _pullLeft = Array.Empty<float>();      // resampler feed buffers
    private float[] _pullRight = Array.Empty<float>();

    // ---- driver state written on the driver thread, read by the callbacks ----------------------
    private IntPtr[] _inputBufferA = Array.Empty<IntPtr>();
    private IntPtr[] _inputBufferB = Array.Empty<IntPtr>();
    private IntPtr[] _outputBufferA = Array.Empty<IntPtr>();
    private IntPtr[] _outputBufferB = Array.Empty<IntPtr>();
    private int[] _inputSampleTypes = Array.Empty<int>();
    private int[] _outputSampleTypes = Array.Empty<int>();
    private int _leftPhysicalChannel;
    private int _rightPhysicalChannel;
    private int _bufferSizeFrames;

    private readonly ManualResetEventSlim _stopSignal = new(false);
    private Thread? _driverThread;
    private volatile bool _isRunning;
    private int _stopping;
    private int _disposed;
    private int _callbacksInFlight;
    private string? _lastError;

    private int _driverActualSampleRate;
    private bool _resamplingRequired;
    private int _inputLatencySamples;
    private int _outputLatencySamples;
    private string _driverReportedName = string.Empty;

    /// <summary>The single instance whose driver is currently delivering callbacks.</summary>
    /// <remarks>
    /// Required because <c>ASIOCallbacks</c> has no context pointer. Starting a second
    /// <see cref="AsioHostAudioSource"/> takes ownership of the slot; the previous instance then
    /// simply underruns to silence until it is disposed. In practice only one is ever live because
    /// ASIO grants exclusive access per driver.
    /// </remarks>
    private static volatile AsioHostAudioSource? s_active;

    public int SampleRate => TargetSampleRate;
    public int Channels => TargetChannels;

    /// <summary>True once the driver has been initialised, buffers created and the stream started.</summary>
    public bool IsHardwareActive => _isRunning;

    /// <summary>Rate the driver actually runs at (differs from <see cref="SampleRate"/> only when resampling).</summary>
    public int DriverActualSampleRate => _driverActualSampleRate;

    /// <summary>True when the driver refused 48 kHz and the fallback resampler is engaged.</summary>
    public bool IsResamplingRequired => _resamplingRequired;

    /// <summary>Driver-reported input latency in frames (informational).</summary>
    public int InputLatencySamples => _inputLatencySamples;

    // ---- IHostAudioSource (ADR-0003 interface widening; kills the caller downcast) --------------
    public int DeviceSampleRate => _driverActualSampleRate > 0 ? _driverActualSampleRate : TargetSampleRate;
    public int InputLatencyFrames => _inputLatencySamples;
    public string? RateNegotiationNote => _negotiationNote;

    /// <summary>Driver-reported output latency in frames (informational).</summary>
    public int OutputLatencySamples => _outputLatencySamples;

    /// <summary>Driver-reported name returned by the driver's own <c>getDriverName</c> (vtable sanity check).</summary>
    public string DriverReportedName => _driverReportedName;

    /// <summary>Last setup/runtime failure, or <c>null</c> when everything succeeded.</summary>
    public string? LastError => _lastError;

    public AsioHostAudioSource(AsioCaptureOption option)
    {
        _option = option ?? throw new ArgumentNullException(nameof(option));
        if (option.Driver == null) throw new ArgumentException("AsioCaptureOption.Driver is required.", nameof(option));

        _driverThread = new Thread(DriverThreadMain)
        {
            Name = "ASIO_Capture",
            IsBackground = true
        };

        // ASIO drivers are apartment-agnostic in theory but many are apartment-bound in practice
        // (and several pop up a control-panel/DCOM dialog), so pin this thread to a real STA *before*
        // it starts — after COM has initialised the thread it can never change.
        try { _driverThread.SetApartmentState(ApartmentState.STA); } catch { }

        _driverThread.Start();
    }

    public void FillPlanarAudio(Span<float> planarDestination, int samplesPerChannel)
    {
        if (samplesPerChannel <= 0 || planarDestination.Length < samplesPerChannel * TargetChannels)
        {
            planarDestination.Clear();
            return;
        }

        Span<float> left = planarDestination.Slice(0, samplesPerChannel);
        Span<float> right = planarDestination.Slice(samplesPerChannel, samplesPerChannel);

        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _stopping) != 0 || !_isRunning)
        {
            left.Clear();
            right.Clear();
            return;
        }

        if (_resamplingRequired)
        {
            FillResampled(left, right, samplesPerChannel);
        }
        else
        {
            _ring.Read(left, right, samplesPerChannel);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            Volatile.Write(ref _stopping, 1);

            // Detach first: the callbacks may still be firing from the driver's own threads, and once
            // the slot no longer points at us those invocations become harmless no-ops.
            if (s_active == this) s_active = null;

            Thread? t = _driverThread;
            if (t != null)
            {
                _stopSignal.Set();
                if (!t.Join(MaxStopWaitMs))
                {
                    // The driver thread wedged inside a driver call. Nothing further is safe to touch,
                    // but we must not throw out of Dispose, and we must not free driver buffers here
                    // because an in-flight callback could still be reading them.
                    SetError("Driver thread did not exit within " + MaxStopWaitMs + " ms during Dispose.");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            try { _stopSignal.Dispose(); } catch { }
        }
    }

    // ============================================================================================
    // Driver thread
    // ============================================================================================

    private void DriverThreadMain()
    {
        // Only uninitialize when *we* were the one to bring up this apartment; if the runtime had
        // already done it at thread start, CoInitializeEx returns S_FALSE and we must not balance it.
        int coHr = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
        bool shouldUninit = coHr == 0;

        IntPtr pAsio = IntPtr.Zero;
        try
        {
            Guid clsid = _option.Driver.Clsid;
            Guid riid = IidIUnknown;
            // EMPERICALLY DETERMINED (Realtek ASIO + VB-Matrix VASIO on this machine) — read this
            // before "simplifying" any of it away:
            // CoCreateInstance asking for IID_IUnknown returns the driver's IUnknown *identity* face.
            // For Realtek ASIO that vtable holds only 4 pointers; everything past slot 3 is the
            // driver's adjacent string data (we watched "\Kernel\...ase.dll" there), so treating it as
            // the IASIO vtable raises an instant AccessViolationException. IASIO is a SEPARATE face of
            // the same object and must be reached with QueryInterface.
            //
            // We still never hardcode IID_IASIO: the identifier we already have — the driver's own
            // class id — is the one every tested driver answers QueryInterface with, and we refuse to
            // execute anything until the resulting vtable has passed a structural *and* a functional
            // check (below).
            //
            // NOTE this also means the pointer is NOT valid unless this really is a single-threaded
            // apartment: from an MTA, COM hands back a cross-apartment proxy whose vtable lives in
            // combase.dll, which is why this whole lifecycle runs on a dedicated STA thread.
            IntPtr pUnknown = IntPtr.Zero;
            int hr = CoCreateInstance(&clsid, IntPtr.Zero, ClsCtxInprocServer, &riid, &pUnknown);
            if (hr < 0 || pUnknown == IntPtr.Zero)
            {
                SetError($"CoCreateInstance failed for ASIO driver '{_option.Driver.DisplayName}' (0x{hr:X8}). " +
                         "Check that a 64-bit build of the driver is registered.");
                return;
            }

            IntPtr pFace = ResolveAsioFace(pUnknown, clsid, out string faceDiagnostic);
            if (pFace == IntPtr.Zero)
            {
                SetError($"Could not resolve the IASIO interface of '{_option.Driver.DisplayName}': {faceDiagnostic}");
                ReleaseComObject(pUnknown);
                return;
            }
            _driverUnknown = pUnknown;
            pAsio = pFace;

            // --- empirical vtable validation ----------------------------------------------------
            // Now that slot 3..23 have been shown to be distinct, executable code pointers, it is safe
            // to exercise slot 4. It must be getDriverName(char*): if it isn't, every later slot we
            // call would be wrong too, so we refuse to continue rather than silently playing garbage.
            string? reported = TryReadDriverName(pAsio, out string rawDiagnostic);
            _driverReportedName = rawDiagnostic;
            if (reported == null)
            {
                SetError($"ASIO vtable slot 4 did not behave like getDriverName(char*) for '{_option.Driver.DisplayName}' " +
                         $"({rawDiagnostic}); refusing to proceed with an unverified interface layout.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(_option.Driver.DisplayName) &&
                !DriverNameMatches(reported, _option.Driver.DisplayName))
            {
                SetError($"ASIO vtable validation failed: expected driver name '{_option.Driver.DisplayName}' " +
                         $"but getDriverName returned '{reported}'. The IASIO vtable slot layout is likely wrong.");
                return;
            }

            SetupAndRunDriver(pAsio);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            _isRunning = false;
            try { ShutdownDriver(pAsio); } catch { }
            if (shouldUninit) CoUninitialize();
        }
    }

    private void SetupAndRunDriver(IntPtr pAsio)
    {
        // 1. init: sysHandle is nominally the "window with focus"; 0 is accepted by every driver we
        //    exercised (a real HWND changes nothing for either Realtek ASIO or VB-Matrix).
        //    Per the SDK, init may also return 1 meaning "this instance is already initialised" —
        //    VB-Matrix VASIO does exactly that while still being ready to answer every query, so only
        //    negative values are errors here.
        int err = AsioInit(pAsio, IntPtr.Zero);
        if (err < 0)
        {
            SetError($"ASIO init failed (0x{err:X8}).");
            return;
        }

        // 2. Channel counts.
        int numInput = 0, numOutput = 0;
        err = AsioGetChannels(pAsio, &numInput, &numOutput);
        if (err != AseOk)
        {
            SetError($"ASIO getChannels failed (0x{err:X8}).");
            return;
        }
        if (numInput <= 0)
        {
            SetError("ASIO driver exposes no input channels; it cannot be used as a capture source.");
            return;
        }

        // 3. User-selected input pair (L = FirstInputChannelIndex, R = the next input channel).
        int first = _option.FirstInputChannelIndex;
        if (first < 0 || first >= numInput)
        {
            SetError($"Requested input channel index {first} is out of range: the driver exposes {numInput} input channel(s).");
            return;
        }
        _leftPhysicalChannel = first;
        _rightPhysicalChannel = first + 1 < numInput ? first + 1 : first; // mono driver => dual mono

        // 4. Sample rate negotiation: ask for 48 kHz so no resampling is ever needed.
        //
        // verified: VB-Matrix VASIO advertises canSampleRate(48000) == ASE_OK and then fails the
        // actual setSampleRate with ASE_NoClock (-995) because the Matrix application owns the clock,
        // so canSampleRate must never be trusted alone — we always re-read getSampleRate afterwards
        // and let <see cref="DecideResampling"/> fall back from whatever rate is genuinely running.
        // The setup order below (channels -> rate -> buffer size -> channel info -> createBuffers)
        // is the one laid out by the ASIO SDK host sample; Realtek ASIO answers ASE_NotPresent for
        // getChannels when another client already owns the device, which we report rather than retry.
        if (AsioCanSampleRate(pAsio, TargetSampleRate) == AseOk)
        {
            int setResult = AsioSetSampleRate(pAsio, TargetSampleRate);
            if (setResult != AseOk)
            {
                // Non-fatal: DecideResampling() below falls back from whatever rate is truly running.
                _negotiationNote = $"canSampleRate({TargetSampleRate}) answered OK but setSampleRate " +
                                   $"returned 0x{setResult:X8}; resampling from the driver's native rate.";
            }
        }

        double rateAfterRequest = 0;
        AsioGetSampleRate(pAsio, &rateAfterRequest);

        // 5. Preferred buffer size.
        int minSize = 0, maxSize = 0, preferredSize = 0, granularity = 0;
        err = AsioGetBufferSize(pAsio, &minSize, &maxSize, &preferredSize, &granularity);
        if (err != AseOk)
        {
            SetError($"ASIO getBufferSize failed (0x{err:X8}).");
            return;
        }
        if (preferredSize <= 0) preferredSize = 512;

        int inputLatency = 0, outputLatency = 0;
        AsioGetLatencies(pAsio, &inputLatency, &outputLatency);
        _inputLatencySamples = inputLatency;
        _outputLatencySamples = outputLatency;
        if (inputLatency > 0 && preferredSize < inputLatency) preferredSize = inputLatency;

        // 6. Per-channel sample types (both directions: outputs must be created too, and we silence
        //    them every callback so a curious driver never plays back stale garbage).
        int totalChannels = numInput + numOutput;
        _inputSampleTypes = new int[numInput];
        _outputSampleTypes = new int[numOutput];
        for (int i = 0; i < numInput; i++)
        {
            AsioChannelInfo info = default;
            info.Channel = i;
            info.IsInput = 1;
            if (AsioGetChannelInfo(pAsio, &info) == AseOk)
            {
                _inputSampleTypes[i] = info.Type;
            }
            else
            {
                _inputSampleTypes[i] = AsioStInt32Lsb;
            }
        }
        for (int i = 0; i < numOutput; i++)
        {
            AsioChannelInfo info = default;
            info.Channel = i;
            info.IsInput = 0;
            if (AsioGetChannelInfo(pAsio, &info) == AseOk)
            {
                _outputSampleTypes[i] = info.Type;
            }
            else
            {
                _outputSampleTypes[i] = AsioStInt32Lsb;
            }
        }

        // 7. createBuffers over every channel — ASIO expects inputs and outputs in one call.
        AsioBufferInfo* infos = (AsioBufferInfo*)NativeMemory.Alloc((nuint)(totalChannels * sizeof(AsioBufferInfo)));
        if (infos == null)
        {
            SetError("Out of memory allocating ASIOBufferInfo array.");
            return;
        }

        _inputBufferA = new IntPtr[numInput];
        _inputBufferB = new IntPtr[numInput];
        _outputBufferA = new IntPtr[numOutput];
        _outputBufferB = new IntPtr[numOutput];

        err = AseOk;
        try
        {
            for (int i = 0; i < numInput; i++)
            {
                infos[i].IsInput = 1;
                infos[i].ChannelNum = i;
                infos[i].Buffer0 = IntPtr.Zero;
                infos[i].Buffer1 = IntPtr.Zero;
            }
            for (int i = 0; i < numOutput; i++)
            {
                infos[numInput + i].IsInput = 0;
                infos[numInput + i].ChannelNum = i;
                infos[numInput + i].Buffer0 = IntPtr.Zero;
                infos[numInput + i].Buffer1 = IntPtr.Zero;
            }

            err = AsioCreateBuffers(pAsio, infos, totalChannels, preferredSize, GetStaticCallbacks());
            if (err == AseOk)
            {
                for (int i = 0; i < numInput; i++)
                {
                    _inputBufferA[i] = infos[i].Buffer0;
                    _inputBufferB[i] = infos[i].Buffer1;
                }
                for (int i = 0; i < numOutput; i++)
                {
                    _outputBufferA[i] = infos[numInput + i].Buffer0;
                    _outputBufferB[i] = infos[numInput + i].Buffer1;
                }
                _bufferSizeFrames = preferredSize;
            }
        }
        finally
        {
            NativeMemory.Free(infos);
        }

        if (err != AseOk)
        {
            SetError($"ASIO createBuffers failed (0x{err:X8}).");
            return;
        }

        // createBuffers may finalise a rate the driver is willing to honour, so trust whatever is
        // reported *now* rather than what we asked for earlier.
        double finalRate = 0;
        if (AsioGetSampleRate(pAsio, &finalRate) != AseOk || finalRate <= 0)
        {
            finalRate = rateAfterRequest;
        }

        DecideResampling(finalRate);

        // 8. Publish ourselves before start() so no callback can be missed, then go live.
        _scratchLeft = new float[_bufferSizeFrames];
        _scratchRight = new float[_bufferSizeFrames];
        s_active = this;

        err = AsioStart(pAsio);
        if (err != AseOk)
        {
            SetError($"ASIO start failed (0x{err:X8}).");
            s_active = null;
            return;
        }

        _isRunning = true;

        // Park this apartment until Dispose. All teardown below happens on *this* thread, which is
        // the only thread allowed to issue driver calls.
        if (Volatile.Read(ref _stopping) != 0) return;
        try { _stopSignal.Wait(); } catch (ObjectDisposedException) { }
    }

    private void DecideResampling(double finalRate)
    {
        _driverActualSampleRate = 0;

        if (finalRate > 0 && Math.Abs(finalRate - TargetSampleRate) <= 1.0)
        {
            // The happy path: the driver genuinely runs at 48 kHz, so publishes spelling
            // (float/bit-depth) conversions only — no resampling, bit-transparent audio.
            _resamplingRequired = false;
            _driverActualSampleRate = TargetSampleRate;
            _resampler = null;
            return;
        }

        if (finalRate >= 8000 && finalRate <= 384000)
        {
            _resamplingRequired = true;
            _driverActualSampleRate = (int)Math.Round(finalRate);
            _resampler = new FixedRatioStereoResampler(finalRate, TargetSampleRate);
            return;
        }

        // Unusable/absent rate report: assume 48 kHz rather than resample from a fabricated figure.
        _resamplingRequired = false;
        _driverActualSampleRate = finalRate > 0 ? (int)Math.Round(finalRate) : TargetSampleRate;
        _resampler = null;
    }

    /// <summary>Tears the driver down on the owning STA thread.</summary>
    private void ShutdownDriver(IntPtr pAsio)
    {
        if (Volatile.Read(ref _stopping) == 0)
        {
            Volatile.Write(ref _stopping, 1);
        }

        if (pAsio != IntPtr.Zero)
        {
            try { AsioStop(pAsio); } catch { }

            // Give any callback that is mid-flight a brief grace period to leave before the driver
            // frees the sample buffers we are reading from.
            long deadlineMs = Environment.TickCount64 + CallbackDrainMs;
            while (Volatile.Read(ref _callbacksInFlight) > 0 && Environment.TickCount64 < deadlineMs)
            {
                Thread.Sleep(1);
            }

            try { AsioDisposeBuffers(pAsio); } catch { }
        }

        _ring.Reset();

        if (pAsio != IntPtr.Zero)
        {
            ReleaseComObject(pAsio);
            pAsio = IntPtr.Zero;
        }
        if (_driverUnknown != IntPtr.Zero)
        {
            ReleaseComObject(_driverUnknown);
            _driverUnknown = IntPtr.Zero;
        }
    }

    private void SetError(string message)
    {
        _lastError = message;
        _isRunning = false;
    }

    // ============================================================================================
    // Callbacks — [UnmanagedCallersOnly] statics reached from the driver's own threads
    // ============================================================================================

    private static AsioCallbacks* GetStaticCallbacks()
    {
        // Process-lifetime, intentionally never freed: the driver stores the ASIOCallbacks pointer
        // and may invoke it until disposeBuffers returns, and the function pointers are identical
        // for every instance. Mirrors the house static-vtable pattern in WasapiHostAudioSource.
        if (s_asioCallbacks != null) return s_asioCallbacks;
        lock (s_callbacksLock)
        {
            if (s_asioCallbacks == null)
            {
                AsioCallbacks* cb = (AsioCallbacks*)NativeMemory.Alloc((nuint)sizeof(AsioCallbacks));
                cb->BufferSwitch = (IntPtr)(delegate* unmanaged[Stdcall]<int, int, void>)&StaticBufferSwitch;
                cb->SampleRateDidChange = (IntPtr)(delegate* unmanaged[Stdcall]<double, void>)&StaticSampleRateDidChange;
                cb->AsioMessage = (IntPtr)(delegate* unmanaged[Stdcall]<int, int, IntPtr, double*, long>)&StaticAsioMessage;
                cb->BufferSwitchTimeInfo = (IntPtr)(delegate* unmanaged[Stdcall]<AsioTime*, int, int, AsioTime*>)&StaticBufferSwitchTimeInfo;
                s_asioCallbacks = cb;
            }
            return s_asioCallbacks;
        }
    }

    private static AsioCallbacks* s_asioCallbacks;
    private static readonly object s_callbacksLock = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void StaticBufferSwitch(int doubleBufferIndex, int directProcess)
    {
        try
        {
            AsioHostAudioSource? self = s_active;
            self?.OnBufferSwitch(doubleBufferIndex);
        }
        catch
        {
            // Never let an exception cross back into the driver's callback thread.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static AsioTime* StaticBufferSwitchTimeInfo(AsioTime* timeInfo, int doubleBufferIndex, int directProcess)
    {
        try
        {
            AsioHostAudioSource? self = s_active;
            self?.OnBufferSwitch(doubleBufferIndex);
        }
        catch
        {
            // Swallowed: unwinding into driver code would abort the process under Native AOT.
        }
        // The SDK contract requires handing the same ASIOTime pointer back.
        return timeInfo;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void StaticSampleRateDidChange(double sampleRate)
    {
        try
        {
            AsioHostAudioSource? self = s_active;
            if (self == null) return;
            // The driver changed rate behind our back: re-evaluate whether we must resample now.
            self.DecideResampling(sampleRate);
        }
        catch
        {
            // Ignored by design.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static long StaticAsioMessage(int selector, int value, IntPtr message, double* opt)
    {
        try
        {
            switch (selector)
            {
                case KAsioSelectorSupported:
                    switch (value)
                    {
                        case KAsioEngineVersion:
                        case KAsioResetRequest:
                        case KAsioBufferSizeChange:
                        case KAsioResyncRequest:
                        case KAsioLatenciesChanged:
                        case KAsioSupportsTimeInfo:
                        case KAsioSupportsTimeCode:
                        case KAsioOverload:
                            return 1; // supported
                        default:
                            return 0;
                    }
                case KAsioEngineVersion:
                    return 2; // ASIO 2.x host
                case KAsioSupportsInputMonitor:
                    return 0; // we never request monitor mixes
                case KAsioResetRequest:
                case KAsioBufferSizeChange:
                case KAsioResyncRequest:
                case KAsioLatenciesChanged:
                case KAsioOverload:
                    return 1; // acknowledged; nothing we can reconfigure mid-stream
                default:
                    return 0;
            }
        }
        catch
        {
            return 0;
        }
    }

    private void OnBufferSwitch(int doubleBufferIndex)
    {
        // Guard against a late callback issued between stop() and disposeBuffers().
        if (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _disposed) != 0) return;
        if (s_active != this) return;
        if (doubleBufferIndex != 0 && doubleBufferIndex != 1) return;

        int frames = _bufferSizeFrames;
        if (frames <= 0 || frames > _scratchLeft.Length) return;

        Interlocked.Increment(ref _callbacksInFlight);
        Interlocked.Increment(ref _callbackCount);
        try
        {
            SilenceOutputBuffers(doubleBufferIndex, frames);

            IntPtr pLeft = doubleBufferIndex == 0 ? Buffer0Of(_inputBufferA) : Buffer0Of(_inputBufferB);
            IntPtr pRight = doubleBufferIndex == 0 ? Buffer1Of(_inputBufferA) : Buffer1Of(_inputBufferB);
            if (pLeft == IntPtr.Zero) return;

            ConvertToFloat(pLeft, _scratchLeft, frames, TypeOf(_inputSampleTypes, _leftPhysicalChannel));
            if (pRight != IntPtr.Zero)
            {
                ConvertToFloat(pRight, _scratchRight, frames, TypeOf(_inputSampleTypes, _rightPhysicalChannel));
            }
            else
            {
                Array.Clear(_scratchRight, 0, frames);
            }

            _ring.Write(_scratchLeft, _scratchRight, frames);
        }
        finally
        {
            Interlocked.Decrement(ref _callbacksInFlight);
        }
    }

    private IntPtr Buffer0Of(IntPtr[] table) => ChannelBuffer(table, _leftPhysicalChannel);

    private IntPtr Buffer1Of(IntPtr[] table) => ChannelBuffer(table, _rightPhysicalChannel);

    private static IntPtr ChannelBuffer(IntPtr[] table, int physicalChannel)
    {
        return table != null && physicalChannel >= 0 && physicalChannel < table.Length ? table[physicalChannel] : IntPtr.Zero;
    }

    private static int TypeOf(int[] types, int physicalChannel)
    {
        return types != null && physicalChannel >= 0 && physicalChannel < types.Length ? types[physicalChannel] : 0;
    }

    private void SilenceOutputBuffers(int doubleBufferIndex, int frames)
    {
        IntPtr[] table = doubleBufferIndex == 0 ? _outputBufferA : _outputBufferB;
        for (int c = 0; c < table.Length; c++)
        {
            IntPtr b = table[c];
            if (b == IntPtr.Zero) continue;
            int bytesPerFrame = SampleTypeSizeBytes(TypeOf(_outputSampleTypes, c));
            new Span<byte>((void*)b, frames * bytesPerFrame).Clear();
        }
    }

    // ============================================================================================
    // Sample-type conversion
    // ============================================================================================

    private static int SampleTypeSizeBytes(int sampleType)
    {
        return sampleType switch
        {
            AsioStInt16Lsb or AsioStInt16Msb => 2,
            AsioStInt24Lsb or AsioStInt24Msb => 3,
            AsioStFloat64Lsb or AsioStFloat64Msb => 8,
            _ => 4
        };
    }

    /// <summary>
    /// Converts one ASIO channel's buffer into normalised floats in <c>[-1, 1)</c>.
    /// Unknown or unsupported sample types deliberately become silence rather than noise or an
    /// exception — a misread container must never be amplified into the listener's ears.
    /// </summary>
    private static void ConvertToFloat(IntPtr source, float[] destination, int frames, int sampleType)
    {
        byte* p = (byte*)source;
        Span<float> dst = destination.AsSpan(0, frames);

        switch (sampleType)
        {
            case AsioStFloat32Lsb:
            {
                for (int i = 0; i < frames; i++) dst[i] = *(float*)(p + (i * 4));
                return;
            }
            case AsioStFloat32Msb:
            {
                for (int i = 0; i < frames; i++)
                {
                    uint bits = Swap32(*(uint*)(p + (i * 4)));
                    dst[i] = *(float*)&bits;
                }
                return;
            }
            case AsioStFloat64Lsb:
            {
                for (int i = 0; i < frames; i++) dst[i] = (float)*(double*)(p + (i * 8));
                return;
            }
            case AsioStFloat64Msb:
            {
                for (int i = 0; i < frames; i++)
                {
                    ulong bits = Swap64(*(ulong*)(p + (i * 8)));
                    dst[i] = (float)*(double*)&bits;
                }
                return;
            }
            case AsioStInt32Lsb:
            case AsioStInt32Lsb16:
            case AsioStInt32Lsb18:
            case AsioStInt32Lsb20:
            case AsioStInt32Lsb24:
            {
                // The padded LSB variants store the sample MSB-aligned inside a 32-bit word, so
                // dividing the whole container by 2^31 already yields the correct full-scale value.
                for (int i = 0; i < frames; i++) dst[i] = *(int*)(p + (i * 4)) * (1.0f / 2147483648.0f);
                return;
            }
            case AsioStInt32Msb:
            case AsioStInt32Msb16:
            case AsioStInt32Msb18:
            case AsioStInt32Msb20:
            case AsioStInt32Msb24:
            {
                for (int i = 0; i < frames; i++)
                {
                    uint bits = Swap32(*(uint*)(p + (i * 4)));
                    dst[i] = *(int*)&bits * (1.0f / 2147483648.0f);
                }
                return;
            }
            case AsioStInt24Lsb:
            {
                for (int i = 0; i < frames; i++)
                {
                    byte* q = p + (i * 3);
                    int v = ((int)q[0] << 8) | ((int)q[1] << 16) | ((int)q[2] << 24);
                    dst[i] = v * (1.0f / 2147483648.0f);
                }
                return;
            }
            case AsioStInt16Lsb:
            {
                for (int i = 0; i < frames; i++) dst[i] = *(short*)(p + (i * 2)) * (1.0f / 32768.0f);
                return;
            }
            case AsioStInt16Msb:
            {
                // Read byte-wise: swapping through a wider load would over-read the last frame.
                for (int i = 0; i < frames; i++)
                {
                    byte* q = p + (i * 2);
                    short v = (short)((q[0] << 8) | q[1]);
                    dst[i] = v * (1.0f / 32768.0f);
                }
                return;
            }
            default:
            {
                dst.Clear();
                return;
            }
        }
    }

    private static uint Swap32(uint v) =>
        ((v & 0x000000FFu) << 24) | ((v & 0x0000FF00u) << 8) | ((v & 0x00FF0000u) >> 8) | ((v & 0xFF000000u) >> 24);

    private static ulong Swap64(ulong v) =>
        ((v & 0x00000000000000FFUL) << 56) |
        ((v & 0x000000000000FF00UL) << 40) |
        ((v & 0x0000000000FF0000UL) << 24) |
        ((v & 0x00000000FF000000UL) << 8) |
        ((v & 0x000000FF00000000UL) >> 8) |
        ((v & 0x0000FF0000000000UL) >> 24) |
        ((v & 0x00FF000000000000UL) >> 40) |
        ((v & 0xFF00000000000000UL) >> 56);

    // ============================================================================================
    // Fallback: fixed-ratio windowed-sinc resampler
    // ============================================================================================

    private FixedRatioStereoResampler? _resampler;

    private void FillResampled(Span<float> leftDest, Span<float> rightDest, int wanted)
    {
        FixedRatioStereoResampler? resampler = _resampler;
        if (resampler == null)
        {
            leftDest.Clear();
            rightDest.Clear();
            return;
        }

        // Feed the resampler whatever the ring currently holds (capped) so its latency stays bounded.
        // ReadUpTo reports the count in one atomic step, so the pull never has to be sized racily.
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

    /// <summary>
    /// Stereo fixed-ratio polyphase windowed-sinc (Blackman-Harris, 96 taps / 64 phases) resampler,
    /// used only when an ASIO driver refuses to run at 48 kHz. Same kernel ideals as
    /// <see cref="AudioClockResampler"/> (~92 dB stopband, DC-normalised phases, unit impulse at
    /// phase 0) but driven by a plain constant input/output rate pair rather than a video-coupled PLL.
    /// Confined to whichever thread calls it (the <see cref="FillPlanarAudio"/> consumer).
    /// </summary>
    private sealed class FixedRatioStereoResampler
    {
        private const int HalfWidth = 48;
        private const int TapCount = HalfWidth * 2; // 96 taps
        private const int PhaseCount = 64;
        private const int MaxFifoFrames = 65536;

        private static readonly float[] s_filter = BuildFilter();

        private readonly double _step;       // input frames consumed per output frame
        private float[] _fifoLeft;
        private float[] _fifoRight;
        private int _fifoCount;
        private double _readPos;

        public FixedRatioStereoResampler(double sourceRate, double targetRate)
        {
            if (sourceRate <= 0) sourceRate = TargetSampleRate;
            if (targetRate <= 0) targetRate = TargetSampleRate;
            _step = sourceRate / targetRate;

            // Prime with HalfWidth frames of silence so the FIR has left-edge history for sample 0.
            _fifoLeft = new float[HalfWidth * 2 + 4096];
            _fifoRight = new float[HalfWidth * 2 + 4096];
            _fifoCount = HalfWidth;
            _readPos = HalfWidth;
        }

        public int MaxFeedFrames(int wanted) => (int)(wanted * 8) + 4096;

        public void Append(float[] left, float[] right, int frames)
        {
            if (frames <= 0) return;

            EnsureCapacity(_fifoCount + frames);
            Array.Copy(left, 0, _fifoLeft, _fifoCount, frames);
            Array.Copy(right, 0, _fifoRight, _fifoCount, frames);
            _fifoCount += frames;

            // Safety valve: if no output is ever requested, never grow without bound.
            if (_fifoCount > HalfWidth + MaxFifoFrames) Compact();
        }

        /// <summary>Produces up to <paramref name="wanted"/> frames per channel; returns how many were produced.</summary>
        public int Process(Span<float> leftDest, Span<float> rightDest, int wanted)
        {
            if (wanted <= 0) return 0;
            int limit = Math.Min(wanted, Math.Min(leftDest.Length, rightDest.Length));
            float[] filter = s_filter;
            int produced = 0;

            while (produced < limit)
            {
                int i = (int)_readPos;
                // Need HalfWidth-1 frames of history before i and HalfWidth frames of lookahead after.
                if (i - HalfWidth + 1 < 0 || i + HalfWidth > _fifoCount) break;

                int phase = (int)((_readPos - i) * PhaseCount + 0.5);
                if (phase >= PhaseCount) phase = PhaseCount - 1;
                int p = phase * TapCount;
                int baseIdx = i - HalfWidth + 1;

                double accL = 0.0;
                double accR = 0.0;
                for (int t = 0; t < TapCount; t++)
                {
                    float k = filter[p + t];
                    accL += k * _fifoLeft[baseIdx + t];
                    accR += k * _fifoRight[baseIdx + t];
                }
                leftDest[produced] = (float)accL;
                rightDest[produced] = (float)accR;
                produced++;
                _readPos += _step;
            }

            Compact();
            return produced;
        }

        private void Compact()
        {
            int dropCount = (int)_readPos - (HalfWidth - 1);
            int maxDrop = _fifoCount - (HalfWidth + 1); // always retain HalfWidth+1 frames
            if (dropCount < 0) dropCount = 0;
            if (dropCount > maxDrop) dropCount = maxDrop;
            if (dropCount > 0)
            {
                int remain = _fifoCount - dropCount;
                Array.Copy(_fifoLeft, dropCount, _fifoLeft, 0, remain);
                Array.Copy(_fifoRight, dropCount, _fifoRight, 0, remain);
                _fifoCount = remain;
                _readPos -= dropCount;
            }
        }

        private void EnsureCapacity(int frames)
        {
            if (_fifoLeft.Length < frames)
            {
                int size = frames + 4096;
                Array.Resize(ref _fifoLeft, size);
                Array.Resize(ref _fifoRight, size);
            }
        }

        private static float[] BuildFilter()
        {
            var table = new float[PhaseCount * TapCount];
            for (int q = 0; q < PhaseCount; q++)
            {
                double frac = (double)q / PhaseCount;
                double sum = 0.0;
                for (int j = 0; j < TapCount; j++)
                {
                    double m = j - HalfWidth + 1;   // tap offset from the read index
                    double t = m - frac;            // signed distance from the ideal sample position
                    double v = Sinc(t) * BlackmanHarris(t);
                    table[q * TapCount + j] = (float)v;
                    sum += v;
                }
                if (sum > 1e-9)
                {
                    for (int j = 0; j < TapCount; j++)
                    {
                        table[q * TapCount + j] = (float)(table[q * TapCount + j] / sum);
                    }
                }
            }
            return table;
        }

        private static double Sinc(double t)
        {
            if (Math.Abs(t) < 1e-12) return 1.0;
            double x = Math.PI * t;
            return Math.Sin(x) / x;
        }

        /// <summary>Symmetrical 4-term Blackman-Harris window, normalised so w(0) == 1.</summary>
        private static double BlackmanHarris(double t)
        {
            double arg = Math.PI * t / HalfWidth;
            return 0.35875
                 + 0.48829 * Math.Cos(arg)
                 + 0.14128 * Math.Cos(2.0 * arg)
                 + 0.01168 * Math.Cos(3.0 * arg);
        }
    }

    // ============================================================================================
    // IASIO vtable call sites (slot numbers include IUnknown's three)
    // ============================================================================================

    private static void** VtblOf(IntPtr p) => *(void***)p;

    private static void ReleaseComObject(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        void** vtbl = VtblOf(ptr);
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
        release(ptr);
    }

    private static string? TryReadDriverName(IntPtr pAsio, out string raw)
    {
        raw = string.Empty;
        byte* buf = stackalloc byte[MaxDriverNameBytes];
        new Span<byte>(buf, MaxDriverNameBytes).Clear();

        AsioGetDriverName(pAsio, buf);

        int len = 0;
        while (len < MaxDriverNameBytes && buf[len] != 0) len++;
        bool terminated = len < MaxDriverNameBytes;
        if (!terminated) len = MaxDriverNameBytes;

        // Walk the bytes first: a wrong slot/prototype is indistinguishable from a real name unless
        // we check that every byte is ordinary printable text.
        for (int i = 0; i < len; i++)
        {
            byte b = buf[i];
            bool ok = b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b < 0x7F) || b >= 0x80;
            if (!ok)
            {
                raw = "<non-text bytes at offset " + i.ToString() + ">";
                return null;
            }
        }

        string name = Encoding.UTF8.GetString(buf, len).Trim();

        raw = name;
        if (name.Length == 0)
        {
            raw = "<empty>";
            return null; // a real driver always reports a name
        }
        return name;
    }

    /// <summary>
    /// Resolves the IASIO face of the freshly created driver object without ever hardcoding IID_IASIO.
    ///
    /// Tested behaviour: the driver accepts QueryInterface for (a) its own class id — returning a
    /// pointer with the 24-entry IASIO vtable — and (b) IID_IUnknown, returning the identity face
    /// (whose vtable is often far too short). We therefore try each candidate, keep only ones whose
    /// vtable is structurally plausible, and hand the winner to the name check in the caller.
    /// </summary>
    private static IntPtr ResolveAsioFace(IntPtr pUnknown, Guid clsid, out string diagnostic)
    {
        diagnostic = string.Empty;

        // Candidate 1: QueryInterface with the driver's own class id.
        IntPtr pViaClsid = IntPtr.Zero;
        int qiHr = QueryInterface(pUnknown, &clsid, &pViaClsid);
        if (qiHr >= 0 && pViaClsid != IntPtr.Zero)
        {
            if (LooksLikeAsioVtable(pViaClsid)) return pViaClsid;
            diagnostic = "the pointer returned by QueryInterface(driver CLSID) has no 24-slot IASIO vtable; ";
            ReleaseComObject(pViaClsid);
        }
        else
        {
            diagnostic = $"QueryInterface(driver CLSID) failed (0x{qiHr:X8}); ";
        }

        // Candidate 2: some drivers hand back IASIO directly from CoCreateInstance.
        if (LooksLikeAsioVtable(pUnknown)) return pUnknown;

        diagnostic += "the IUnknown face returned by CoCreateInstance is not the IASIO interface. " +
                      "Refusing to call into an unverified vtable (doing so faults the process).";
        return IntPtr.Zero;
    }

    /// <summary>
    /// Structural sanity check performed <i>before</i> executing anything through the vtable.
    /// IASIO needs 24 in-line slots (IUnknown's 3 + the 21 methods), so every one of those slots has
    /// to hold a pointer into committed, executable image memory. A short identity face reads past its
    /// own vtable into the driver's string heap — candidate pointers there land in MEM_FREE or
    /// non-executable pages, which fails this check instead of faulting the process later.
    ///
    /// Deliberately NOT checked: that the slots are distinct. Real drivers alias implementations
    /// (VB-Matrix VASIO uses one stub for both <c>controlPanel</c> and <c>outputReady</c>).
    /// </summary>
    private static bool LooksLikeAsioVtable(IntPtr p)
    {
        if (p == IntPtr.Zero) return false;

        void** vtbl = VtblOf(p);
        MemoryBasicInformation mbi;
        for (int i = 0; i < AsioVtableSlots; i++)
        {
            IntPtr slot = (IntPtr)vtbl[i];
            if (slot == IntPtr.Zero) return false;

            if (VirtualQuery(slot, &mbi, (nuint)sizeof(MemoryBasicInformation)) == 0) return false;
            bool committed = mbi.State == MemCommit;
            bool image = mbi.Type == MemImage;
            bool executable = (mbi.Protect & 0xF0) != 0; // PAGE_EXECUTE / _READ / _READWRITE / _WRITECOPY
            if (!committed || !image || !executable) return false;
        }
        return true;
    }

    private const int AsioVtableSlots = 24; // IUnknown (3) + IASIO (21)
    private const uint MemCommit = 0x1000;
    private const uint MemImage = 0x1000000;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint VirtualQuery(IntPtr lpAddress, MemoryBasicInformation* lpBuffer, nuint dwLength);

    private static int QueryInterface(IntPtr p, Guid* riid, IntPtr* ppv)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
        return fn(p, riid, ppv);
    }

    private static bool DriverNameMatches(string reported, string expected)
    {
        // Registry display names occasionally differ in trailing punctuation or case, so compare on
        // a normalised form: same characters, ignoring case, spaces and separators.
        static string Norm(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        string a = Norm(reported);
        string b = Norm(expected);
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b || a.Contains(b) || b.Contains(a);
    }

    private static int AsioInit(IntPtr p, IntPtr sysHandle)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtbl[3];
        return fn(p, sysHandle);
    }

    private static void AsioGetDriverName(IntPtr p, byte* name)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)vtbl[4];
        fn(p, name);
    }

    private static int AsioGetChannels(IntPtr p, int* numInputChannels, int* numOutputChannels)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int>)vtbl[9];
        return fn(p, numInputChannels, numOutputChannels);
    }

    private static int AsioGetLatencies(IntPtr p, int* inputLatency, int* outputLatency)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int>)vtbl[10];
        return fn(p, inputLatency, outputLatency);
    }

    private static int AsioGetBufferSize(IntPtr p, int* minSize, int* maxSize, int* preferredSize, int* granularity)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int*, int*, int>)vtbl[11];
        return fn(p, minSize, maxSize, preferredSize, granularity);
    }

    private static int AsioCanSampleRate(IntPtr p, double sampleRate)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, double, int>)vtbl[12];
        return fn(p, sampleRate);
    }

    private static int AsioGetSampleRate(IntPtr p, double* sampleRate)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, double*, int>)vtbl[13];
        return fn(p, sampleRate);
    }

    private static int AsioSetSampleRate(IntPtr p, double sampleRate)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, double, int>)vtbl[14];
        return fn(p, sampleRate);
    }

    private static int AsioGetChannelInfo(IntPtr p, AsioChannelInfo* info)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, AsioChannelInfo*, int>)vtbl[18];
        return fn(p, info);
    }

    private static int AsioCreateBuffers(IntPtr p, AsioBufferInfo* bufferInfos, int numChannels, int bufferSize, AsioCallbacks* callbacks)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, AsioBufferInfo*, int, int, AsioCallbacks*, int>)vtbl[19];
        return fn(p, bufferInfos, numChannels, bufferSize, callbacks);
    }

    private static int AsioDisposeBuffers(IntPtr p)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[20];
        return fn(p);
    }

    private static int AsioStart(IntPtr p)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[7];
        return fn(p);
    }

    private static int AsioStop(IntPtr p)
    {
        void** vtbl = VtblOf(p);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[8];
        return fn(p);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(Guid* rclsid, IntPtr pUnkOuter, uint dwClsContext, Guid* riid, IntPtr* ppv);

    // ============================================================================================
    // Unmanaged structs — hand-rolled, sequential, matching the ASIO SDK layout on Win64
    // (note <c>long</c> is 32-bit there, so every SDK <c>long</c> maps to <c>int</c>)
    // ============================================================================================

    /// <summary>typedef struct ASIOBufferInfo { ASIOBool isInput; long channelNum; void* buffers[2]; }</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AsioBufferInfo
    {
        public int IsInput;
        public int ChannelNum;
        public IntPtr Buffer0;
        public IntPtr Buffer1;
    }

    /// <summary>typedef struct ASIOChannelInfo { long channel; ASIOBool isInput; ASIOBool isActive; long channelGroup; ASIOSampleType type; char name[32]; }</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AsioChannelInfo
    {
        public int Channel;
        public int IsInput;
        public int IsActive;
        public int ChannelGroup;
        public int Type;
        public fixed byte Name[32];
    }

    /// <summary>
    /// typedef struct ASIOTime { long reserved[4]; &lt;sample position&gt;; &lt;system timestamp&gt;; } — 32 bytes total.
    /// We never interpret its fields (we use the doubleBufferIndex argument instead), so only its
    /// size and alignment need to match; the members below document that shape.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AsioTime
    {
        public int Reserved0;
        public int Reserved1;
        public int Reserved2;
        public int Reserved3;
        public long TimeInfoSamplePosition;
        public long SystemTimeStamp;
    }

    /// <summary>
    /// typedef struct ASIOCallbacks { bufferSwitch; sampleRateHasChanged; asioMessage; bufferSwitchTimeInfo; }
    /// Note the absence of any user/context pointer — see <c>s_active</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AsioCallbacks
    {
        public IntPtr BufferSwitch;
        public IntPtr SampleRateDidChange;
        public IntPtr AsioMessage;
        public IntPtr BufferSwitchTimeInfo;
    }
}
