using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OmtCaptureStudio.Models;

namespace OmtCaptureStudio.Services;

/// <summary>
/// Reads just the <b>input channel inventory</b> of a registered ASIO driver so the UI can offer a
/// real choice of input pairs (1/2, 3/4, 5/6, …) instead of hard-coding pair 1/2.
///
/// Why a separate type instead of reusing <see cref="AsioHostAudioSource"/>:
/// <list type="bullet">
///   <item>Everything that talks to IASIO lives as <c>private static</c> members inside
///         <see cref="AsioHostAudioSource"/>, and reaching into 1500 lines of working COM interop is
///         far riskier than repeating the handful of primitives a read-only probe needs.</item>
///   <item>Capture opens the driver for the whole broadcast; this probe is a <em>transient</em> open
///         that never allocates buffers and never starts the engine.</item>
/// </list>
/// The vtable primitives below (slots 0/2 from IUnknown, plus init 3, getDriverName 4, getChannels 9
/// and getChannelInfo 18) are therefore <b>deliberately duplicated</b> from <see cref="AsioHostAudioSource"/>
/// and must be kept in step with it if IASIO ever changes.
///
/// Contract: <c>Probe</c> <b>never throws</b>. Every failure — missing driver, another application
/// holding the exclusive lock, an unusable vtable, a hanging <c>init</c> — comes back as an empty
/// list plus a human-readable reason, so callers can degrade to the old "In 1/2 only" behaviour.
/// </summary>
public static unsafe class AsioInputChannelProbe
{
    /// <summary>Hard ceiling on how long we let a driver's <c>init</c> block before giving up.</summary>
    private const int ProbeTimeoutMs = 4000;

    private const int CoInitApartmentThreaded = 0x2;
    private const uint ClsCtxInprocServer = 0x1;
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    private const int AseOk = 0;

    private const int AsioVtableSlots = 24; // IUnknown (3) + IASIO (21)
    private const uint MemCommit = 0x1000;
    private const uint MemImage = 0x1000000;

    private const int MaxDriverNameBytes = 256;
    private const int MaxChannelNameBytes = 32; // ASIOChannelInfo.name[32]

    /// <summary>
    /// Lists the driver's input channels (index + driver-reported name).
    /// Returns an empty list on any failure; <paramref name="reason"/> then carries why.
    /// </summary>
    public static IReadOnlyList<AsioInputChannel> Probe(AsioDriverInfo driver) => Probe(driver, out _);

    /// <inheritdoc cref="Probe(AsioDriverInfo)"/>
    /// <param name="driver">The driver to interrogate.</param>
    /// <param name="reason">Failure detail, or <c>null</c> when the probe succeeded.</param>
    public static IReadOnlyList<AsioInputChannel> Probe(AsioDriverInfo driver, out string? reason)
    {
        reason = null;
        IReadOnlyList<AsioInputChannel> none = Array.Empty<AsioInputChannel>();

        if (driver is null || driver.Clsid == Guid.Empty)
        {
            reason = "no ASIO driver identity to probe.";
            return none;
        }

        // COM demands an STA for these drivers (see the note in AsioHostAudioSource: from an MTA we
        // would get a proxy whose vtable is not IASIO at all), so the whole probe runs on a dedicated
        // STA thread and is bounded by Join below. A hanging driver can never block the caller.
        var channels = new List<AsioInputChannel>();
        string? failure = null;

        var thread = new Thread(() => failure = ProbeCore(driver, channels))
        {
            IsBackground = true,
            Name = "ASIO input probe"
        };

        try
        {
            thread.SetApartmentState(ApartmentState.STA);
        }
        catch (Exception ex)
        {
            // Non-Windows (or a runtime that refuses apartment changes): refuse rather than risk a
            // cross-apartment proxy vtable.
            reason = $"could not start a single-threaded apartment ({ex.Message}).";
            return none;
        }

        try
        {
            thread.Start();
            if (!thread.Join(ProbeTimeoutMs))
            {
                reason = $"the driver did not respond within {ProbeTimeoutMs} ms (it may be held by another application).";
                return none;
            }
        }
        catch (Exception ex)
        {
            reason = $"the probe thread failed to start ({ex.Message}).";
            return none;
        }

        if (failure != null)
        {
            reason = failure;
            return none;
        }

        if (channels.Count == 0)
        {
            reason = "the driver exposes no input channels.";
            return none;
        }

        return channels;
    }

    /// <summary>Body of the STA probe thread. Never throws; returns a failure string instead.</summary>
    private static string? ProbeCore(AsioDriverInfo driver, List<AsioInputChannel> channels)
    {
        bool apartmentOwned = false;
        IntPtr pUnknown = IntPtr.Zero;
        IntPtr pAsio = IntPtr.Zero;

        try
        {
            int coHr = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            apartmentOwned = coHr == 0; // S_FALSE => the runtime already owns it here; do not balance it.

            Guid clsid = driver.Clsid;
            Guid riid = IidIUnknown;
            int hr = CoCreateInstance(&clsid, IntPtr.Zero, ClsCtxInprocServer, &riid, &pUnknown);
            if (hr < 0 || pUnknown == IntPtr.Zero)
            {
                return $"the driver could not be instantiated (0x{hr:X8}); check that a 64-bit build is registered.";
            }

            // Resolve the IASIO face exactly as the capture source does: never hardcode IID_IASIO,
            // ask for the driver's own class id, and verify the resulting vtable structurally first.
            IntPtr pCandidate = IntPtr.Zero;
            int qiHr = QueryInterface(pUnknown, &clsid, &pCandidate);
            if (qiHr >= 0 && pCandidate != IntPtr.Zero && LooksLikeAsioVtable(pCandidate))
            {
                pAsio = pCandidate;
            }
            else
            {
                if (pCandidate != IntPtr.Zero) Release(pCandidate);
                if (LooksLikeAsioVtable(pUnknown))
                {
                    pAsio = pUnknown;
                }
                else
                {
                    return "the driver did not expose a 24-slot IASIO interface; refusing to call into it.";
                }
            }

            // Functional check before touching any deeper slot: getDriverName must answer with text.
            if (!ValidateDriverName(pAsio, driver.DisplayName, out string nameFailure))
            {
                return nameFailure;
            }

            // init(0) is the SDK's entry point — getChannels is only meaningful after it — but we go
            // no further: createBuffers/start are never called, so no audio engine is ever spun up.
            int err = AsioInit(pAsio, IntPtr.Zero);
            if (err < 0)
            {
                return $"init failed (0x{err:X8}); another application probably owns the driver exclusively.";
            }

            int numInput = 0, numOutput = 0;
            err = AsioGetChannels(pAsio, &numInput, &numOutput);
            if (err != 0)
            {
                return $"getChannels failed (0x{err:X8}).";
            }
            if (numInput <= 0)
            {
                return "the driver reports no input channels.";
            }

            for (int i = 0; i < numInput; i++)
            {
                AsioChannelInfo info = default;
                info.Channel = i;
                info.IsInput = 1;
                string name = AsioGetChannelInfo(pAsio, &info) == AseOk ? ReadChannelName(&info) : string.Empty;
                channels.Add(new AsioInputChannel(i, name.Length > 0 ? name : $"Input {i + 1}"));
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"the probe faulted ({ex.Message}).";
        }
        finally
        {
            // Release both faces exactly once (they may be the same pointer) and never touch the
            // driver again. No buffers were ever created, so there is nothing to dispose.
            try
            {
                IntPtr first = pUnknown != IntPtr.Zero ? pUnknown : pAsio;
                if (first != IntPtr.Zero) Release(first);
                if (pAsio != IntPtr.Zero && pAsio != first) Release(pAsio);
            }
            catch
            {
                // Releasing during teardown must not mask the probe result.
            }

            if (apartmentOwned)
            {
                try { CoUninitialize(); } catch { }
            }
        }
    }

    /// <summary>
    /// Exercises vtable slot 4 (getDriverName). Its answer must be printable text, and it should
    /// resemble the registry display name — otherwise every later slot is suspect and we bail out.
    /// </summary>
    private static bool ValidateDriverName(IntPtr pAsio, string expectedName, out string failure)
    {
        failure = string.Empty;
        byte* buf = stackalloc byte[MaxDriverNameBytes];
        new Span<byte>(buf, MaxDriverNameBytes).Clear();

        string? reported = TryReadDriverName(pAsio, buf);
        if (reported == null)
        {
            failure = "slot 4 did not behave like getDriverName(char*); refusing to trust the vtable.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedName) && !DriverNameMatches(reported, expectedName))
        {
            failure = $"getDriverName returned '{reported}' instead of '{expectedName}'.";
            return false;
        }

        return true;
    }

    private static string? TryReadDriverName(IntPtr pAsio, byte* buf)
    {
        AsioGetDriverName(pAsio, buf);

        int len = 0;
        while (len < MaxDriverNameBytes && buf[len] != 0) len++;
        if (len == 0) return null;

        for (int i = 0; i < len; i++)
        {
            byte b = buf[i];
            bool ok = b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b < 0x7F) || b >= 0x80;
            if (!ok) return null; // not text => wrong slot/prototype
        }

        return Encoding.UTF8.GetString(buf, len).Trim();
    }

    private static bool DriverNameMatches(string reported, string expected)
    {
        // Same lenient normalisation as the capture source: registry names drift in case, spacing
        // and trailing punctuation.
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

    /// <summary>
    /// ASIOChannelInfo.name is a fixed char[32] that drivers often leave unterminated, so read it as
    /// a bounded byte span and tolerate a missing NUL.
    /// </summary>
    private static string ReadChannelName(AsioChannelInfo* info)
    {
        var raw = new ReadOnlySpan<byte>(info->Name, MaxChannelNameBytes);
        int nul = raw.IndexOf((byte)0);
        ReadOnlySpan<byte> text = nul >= 0 ? raw[..nul] : raw;

        bool printable = true;
        foreach (byte b in text)
        {
            bool ok = b >= 0x20 && b < 0x7F;
            if (!ok) { printable = false; break; }
        }

        if (!printable) return string.Empty;
        string name = Encoding.UTF8.GetString(text).Trim();
        return name;
    }

    /// <summary>
    /// Structural gate applied before a single call goes through the pointer: all 24 slots must be
    /// non-null pointers into committed, executable image memory. A short identity face (or garbage
    /// from the driver's string heap) fails here instead of faulting the process later.
    /// </summary>
    private static bool LooksLikeAsioVtable(IntPtr p)
    {
        if (p == IntPtr.Zero) return false;

        void** vtbl = *(void***)p;
        for (int i = 0; i < AsioVtableSlots; i++)
        {
            IntPtr slot = (IntPtr)vtbl[i];
            if (slot == IntPtr.Zero) return false;

            MemoryBasicInformation mbi;
            if (VirtualQuery(slot, &mbi, (nuint)sizeof(MemoryBasicInformation)) == 0) return false;

            bool committed = mbi.State == MemCommit;
            bool image = mbi.Type == MemImage;
            bool executable = (mbi.Protect & 0xF0) != 0; // PAGE_EXECUTE / _READ / _READWRITE / _WRITECOPY
            if (!committed || !image || !executable) return false;
        }

        return true;
    }

    // ---- IASIO vtable slots (slot numbers include IUnknown's three) -----------------------------

    private static void Release(IntPtr p)
    {
        void** vtbl = *(void***)p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
        fn(p);
    }

    private static int QueryInterface(IntPtr p, Guid* riid, IntPtr* ppv)
    {
        void** vtbl = *(void***)p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
        return fn(p, riid, ppv);
    }

    private static int AsioInit(IntPtr p, IntPtr sysHandle)
    {
        void** vtbl = *(void***)p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtbl[3];
        return fn(p, sysHandle);
    }

    private static void AsioGetDriverName(IntPtr p, byte* name)
    {
        void** vtbl = *(void***)p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)vtbl[4];
        fn(p, name);
    }

    private static int AsioGetChannels(IntPtr p, int* numInputChannels, int* numOutputChannels)
    {
        void** vtbl = *(void***)p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int>)vtbl[9];
        return fn(p, numInputChannels, numOutputChannels);
    }

    private static int AsioGetChannelInfo(IntPtr p, AsioChannelInfo* info)
    {
        void** vtbl = *(void***)p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, AsioChannelInfo*, int>)vtbl[18];
        return fn(p, info);
    }

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

    /// <summary>typedef struct ASIOChannelInfo { long channel; ASIOBool isInput; ASIOBool isActive; long channelGroup; ASIOSampleType type; char name[32]; }</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AsioChannelInfo
    {
        public int Channel;
        public int IsInput;
        public int IsActive;
        public int ChannelGroup;
        public int Type;
        public fixed byte Name[MaxChannelNameBytes];
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint VirtualQuery(IntPtr lpAddress, MemoryBasicInformation* lpBuffer, nuint dwLength);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(Guid* rclsid, IntPtr pUnkOuter, uint dwClsContext, Guid* riid, IntPtr* ppv);
}
