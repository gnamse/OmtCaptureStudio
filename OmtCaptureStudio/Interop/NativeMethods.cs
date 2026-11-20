using System.Runtime.InteropServices;
using System.Text;

// The attribute is legal on the assembly and on each method, not on the class (CS0592).
// One assembly application covers every DllImport in this binary. A future import of a DLL that
// does not live in System32 must set its own DefaultDllImportSearchPaths on that method.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace OmtCaptureStudio.Interop;

/// <summary>
/// Every system-DLL P/Invoke the app makes, in one place.
///
/// <para>The assembly-level <see cref="DefaultDllImportSearchPathsAttribute"/> restricts the loader
/// to System32 for these modules (user32, gdi32, kernel32, ole32, crypt32, wintrust, dbghelp,
/// mmdevapi). That is the search path for the system DLL itself, not an argument the function
/// receives.</para>
///
/// <para>Declarations stay <c>DllImport</c> rather than source-generated <c>LibraryImport</c>.
/// Several signatures are not a clean LibraryImport fit without changing marshalling: a managed
/// <c>EnumWindows</c> callback, <c>StringBuilder</c> window titles, and the default 4-byte Win32
/// <c>BOOL</c> the existing call sites already rely on. The shipping Native AOT build already
/// accepts these imports. Call shapes are unchanged.</para>
/// </summary>
internal static class NativeMethods
{
    internal const uint SrcCopy = 0x00CC0020;
    internal const uint CaptureBlt = 0x40000000;
    internal const uint DibRgbColors = 0;
    internal const uint BiRgb = 0;
    internal const uint PwRenderFullContent = 0x00000002;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    // ---------------------------------------------------------------- user32

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    // ---------------------------------------------------------------- gdi32

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, IntPtr bits, ref BitmapInfo bmi, uint usage);

    // ---------------------------------------------------------------- kernel32

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern unsafe nuint VirtualQuery(IntPtr lpAddress, void* lpBuffer, nuint dwLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern IntPtr CreateEventW(IntPtr lpEventAttributes, int bManualReset, int bInitialState, IntPtr lpName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern int SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern int CloseHandle(IntPtr hObject);

    /// <summary><c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>. Falls back to a normal timer when the OS rejects it.</summary>
    internal const uint CreateWaitableTimerHighResolution = 0x00000002;

    /// <summary><c>TIMER_MODIFY_STATE</c>, the right needed to arm a waitable timer.</summary>
    internal const uint TimerModifyState = 0x00000002;

    /// <summary><c>SYNCHRONIZE</c>, the right needed to wait on the timer.</summary>
    internal const uint Synchronize = 0x00100000;

    internal const uint InfiniteWait = 0xFFFFFFFFu;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        IntPtr lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern int SetWaitableTimer(
        IntPtr hTimer,
        ref long lpDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        int fResume);

    // ---------------------------------------------------------------- ole32

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern unsafe int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern unsafe int CoCreateInstance(Guid* rclsid, IntPtr pUnkOuter, uint dwClsContext, Guid* riid, IntPtr* ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int PropVariantClear(IntPtr pvar);

    // ---------------------------------------------------------------- mmdevapi / dbghelp

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IntPtr completionHandler,
        out IntPtr activationOperation);

    [DllImport("Dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    // ---------------------------------------------------------------- wintrust / crypt32 (Authenticode, opt-in)

    internal const uint WtdUiNone = 2;
    internal const uint WtdRevokeNone = 0;
    internal const uint WtdChoiceFile = 1;
    internal const uint WtdStateActionVerify = 1;
    internal const uint WtdStateActionClose = 2;

    /// <summary>Do not block a broadcast machine on a live revocation fetch.</summary>
    internal const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    internal static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal const int TrustENoSignature = unchecked((int)0x800B0100);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    /// <summary>
    /// <c>WINTRUST_DATA</c> including the Windows 8 <c>pSignatureSettings</c> pointer.
    /// Field order matches the SDK. The x64 size is asserted by the headless suite.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionId, ref WinTrustData pWvtData);

    internal const int CertQueryObjectFile = 1;
    internal const int CertQueryContentFlagPkcs7SignedEmbed = 0x00000400;
    internal const int CertQueryFormatFlagBinary = 0x00000002;
    internal const int CertNameSimpleDisplayType = 4;
    internal const int CertHashPropId = 3;

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CryptQueryObject(
        int dwObjectType,
        string pvObject,
        int dwExpectedContentTypeFlags,
        int dwExpectedFormatTypeFlags,
        int dwFlags,
        out int pdwMsgAndCertEncodingType,
        out int pdwContentType,
        out int pdwFormatType,
        out IntPtr phCertStore,
        out IntPtr phMsg,
        out IntPtr ppvContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    internal static extern IntPtr CertEnumCertificatesInStore(IntPtr hCertStore, IntPtr pPrevCertContext);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int CertGetNameString(
        IntPtr pCertContext,
        int dwType,
        int dwFlags,
        IntPtr pvTypePara,
        char[] pszNameString,
        int cchNameString);

    [DllImport("crypt32.dll", SetLastError = true)]
    internal static extern bool CertGetCertificateContextProperty(
        IntPtr pCertContext,
        int dwPropId,
        byte[]? pvData,
        ref int pcbData);

    [DllImport("crypt32.dll", SetLastError = true)]
    internal static extern bool CertFreeCertificateContext(IntPtr pCertContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    internal static extern bool CertCloseStore(IntPtr hCertStore, int dwFlags);

    [DllImport("crypt32.dll")]
    internal static extern bool CryptMsgClose(IntPtr hCryptMsg);
}
