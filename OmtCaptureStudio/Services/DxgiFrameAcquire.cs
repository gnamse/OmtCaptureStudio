namespace OmtCaptureStudio.Services;

/// <summary>
/// What <c>IDXGIOutputDuplication.AcquireNextFrame</c> returned. Split out of the capturer so the
/// access-lost decision is testable with no GPU: a timeout means the desktop is unchanged and the
/// last buffer is still valid; access-lost and device-lost mean the duplication must be recreated.
/// </summary>
public enum DxgiAcquireKind
{
    /// <summary>A frame was acquired, or the HRESULT was a success code.</summary>
    Success,

    /// <summary><c>DXGI_ERROR_WAIT_TIMEOUT</c> — the screen did not change. Not a failure.</summary>
    Timeout,

    /// <summary><c>DXGI_ERROR_ACCESS_LOST</c> — the duplication is dead and must be recreated.</summary>
    AccessLost,

    /// <summary><c>DXGI_ERROR_DEVICE_REMOVED</c> or <c>DEVICE_RESET</c> — the device itself is dead.</summary>
    DeviceLost,

    /// <summary>Any other failure HRESULT. Treated as a dead duplication so a stuck session cannot freeze silently.</summary>
    OtherFailure
}

/// <summary>
/// HRESULT classification for desktop-duplication acquire. The numeric values are the documented
/// DXGI error codes; they do not come from a runtime lookup, so this stays Native AOT safe.
/// </summary>
public static class DxgiFrameAcquire
{
    public const int Ok = 0;

    /// <summary><c>DXGI_ERROR_WAIT_TIMEOUT</c> (0x887A0027).</summary>
    public const int WaitTimeout = unchecked((int)0x887A0027);

    /// <summary><c>DXGI_ERROR_ACCESS_LOST</c> (0x887A0026).</summary>
    public const int AccessLost = unchecked((int)0x887A0026);

    /// <summary><c>DXGI_ERROR_DEVICE_REMOVED</c> (0x887A0005).</summary>
    public const int DeviceRemoved = unchecked((int)0x887A0005);

    /// <summary><c>DXGI_ERROR_DEVICE_RESET</c> (0x887A0007).</summary>
    public const int DeviceReset = unchecked((int)0x887A0007);

    public static DxgiAcquireKind Classify(int hresult)
    {
        if (hresult == WaitTimeout) return DxgiAcquireKind.Timeout;
        if (hresult == AccessLost) return DxgiAcquireKind.AccessLost;
        if (hresult == DeviceRemoved || hresult == DeviceReset) return DxgiAcquireKind.DeviceLost;
        if (hresult >= 0) return DxgiAcquireKind.Success;
        return DxgiAcquireKind.OtherFailure;
    }

    /// <summary>True when the duplication (or the device behind it) has to be rebuilt before another frame can arrive.</summary>
    public static bool NeedsDuplicationReset(DxgiAcquireKind kind)
        => kind is DxgiAcquireKind.AccessLost or DxgiAcquireKind.DeviceLost or DxgiAcquireKind.OtherFailure;
}
