using System.Runtime.InteropServices;
using OmtCaptureStudio.Interop;

namespace OmtCaptureStudio.Services;

/// <summary>What WinVerifyTrust (and the signer certificate, when one could be read) said about a file.</summary>
public readonly record struct AuthenticodeVerdict(
    bool HasSignature,
    bool SignatureValid,
    string? Publisher,
    string? Thumbprint,
    string Detail);

/// <summary>
/// When an installer must be Authenticode-signed. The default does not enforce: current releases are
/// unsigned, and refusing them would brick the update channel. Enforcement turns on when the owner
/// sets <c>OMT_UPDATE_REQUIRE_SIGNATURE=1</c>, or supplies an expected publisher or thumbprint.
/// See ADR-0013.
/// </summary>
public sealed class UpdateSignaturePolicy
{
    /// <summary>Unsigned installers are accepted. A signature is not consulted.</summary>
    public static UpdateSignaturePolicy AllowUnsigned { get; } = new();

    /// <summary>When false, <see cref="RejectionReason"/> is always null and the native verifier is not called.</summary>
    public bool Enforce { get; init; }

    /// <summary>
    /// Case-insensitive substring of the signer's simple display name (or the full subject).
    /// Null means any trusted signer is acceptable once <see cref="Enforce"/> is on.
    /// </summary>
    public string? ExpectedPublisher { get; init; }

    /// <summary>SHA-1 thumbprint, hex, no spaces. Null means the thumbprint is not pinned.</summary>
    public string? ExpectedThumbprint { get; init; }

    public static UpdateSignaturePolicy FromEnvironment(Func<string, string?> environment)
    {
        string? enforce = environment("OMT_UPDATE_REQUIRE_SIGNATURE");
        string? publisher = environment("OMT_UPDATE_SIGN_PUBLISHER");
        string? thumbprint = NormalizeThumbprint(environment("OMT_UPDATE_SIGN_THUMBPRINT"));
        bool flag = string.Equals(enforce, "1", StringComparison.Ordinal)
                    || string.Equals(enforce, "true", StringComparison.OrdinalIgnoreCase);
        bool on = flag
                  || !string.IsNullOrWhiteSpace(publisher)
                  || !string.IsNullOrWhiteSpace(thumbprint);
        return new UpdateSignaturePolicy
        {
            Enforce = on,
            ExpectedPublisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
            ExpectedThumbprint = string.IsNullOrWhiteSpace(thumbprint) ? null : thumbprint
        };
    }

    /// <summary>Null when the verdict satisfies the policy. A reason string when the install must be refused.</summary>
    public string? RejectionReason(AuthenticodeVerdict verdict)
    {
        if (!Enforce) return null;
        if (!verdict.HasSignature || !verdict.SignatureValid)
        {
            return string.IsNullOrWhiteSpace(verdict.Detail)
                ? "the installer is not Authenticode-signed by a trusted publisher."
                : verdict.Detail;
        }

        if (!string.IsNullOrWhiteSpace(ExpectedPublisher))
        {
            string got = verdict.Publisher ?? string.Empty;
            if (got.IndexOf(ExpectedPublisher, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return $"signer '{got}' does not match the expected publisher '{ExpectedPublisher}'.";
            }
        }

        if (!string.IsNullOrWhiteSpace(ExpectedThumbprint))
        {
            string got = NormalizeThumbprint(verdict.Thumbprint) ?? string.Empty;
            if (!string.Equals(got, ExpectedThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return $"signer thumbprint '{got}' does not match the configured certificate.";
            }
        }

        return null;
    }

    public static string? NormalizeThumbprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is ' ' or ':' or '-') continue;
            if (!Uri.IsHexDigit(c)) return null;
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.Length == 0 ? null : sb.ToString();
    }
}

/// <summary>
/// WinVerifyTrust plus a best-effort read of the embedded signer. Native AOT safe: plain P/Invoke,
/// no X509Certificate.CreateFromSignedFile. Used only when signature enforcement is opted in.
/// </summary>
public static class AuthenticodeVerifier
{
    /// <summary>x64 size of <c>WINTRUST_DATA</c> including <c>pSignatureSettings</c>. Locked by the suite.</summary>
    public static int WinTrustDataBytes => Marshal.SizeOf<NativeMethods.WinTrustData>();

    /// <summary>x64 size of <c>WINTRUST_FILE_INFO</c>. Locked by the suite.</summary>
    public static int WinTrustFileInfoBytes => Marshal.SizeOf<NativeMethods.WinTrustFileInfo>();

    public static AuthenticodeVerdict VerifyFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AuthenticodeVerdict(false, false, null, null, "Authenticode verification is only available on Windows.");
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new AuthenticodeVerdict(false, false, null, null, "the installer path does not exist.");
        }

        int hr = VerifyTrust(path);
        bool unsigned = hr == NativeMethods.TrustENoSignature;
        bool trusted = hr == 0;
        string? publisher = null;
        string? thumbprint = null;
        if (!unsigned)
        {
            TryReadSigner(path, out publisher, out thumbprint);
        }

        string detail = trusted
            ? $"Authenticode signature is trusted{(publisher is null ? "" : $" ({publisher})")}."
            : unsigned
                ? "the installer has no Authenticode signature."
                : $"WinVerifyTrust returned 0x{hr:X8}.";

        return new AuthenticodeVerdict(!unsigned, trusted, publisher, thumbprint, detail);
    }

    private static unsafe int VerifyTrust(string path)
    {
        IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
        try
        {
            var file = new NativeMethods.WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<NativeMethods.WinTrustFileInfo>(),
                pcwszFilePath = pathPtr,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };

            var data = new NativeMethods.WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<NativeMethods.WinTrustData>(),
                dwUIChoice = NativeMethods.WtdUiNone,
                fdwRevocationChecks = NativeMethods.WtdRevokeNone,
                dwUnionChoice = NativeMethods.WtdChoiceFile,
                pFile = (IntPtr)(&file),
                dwStateAction = NativeMethods.WtdStateActionVerify,
                dwProvFlags = NativeMethods.WtdCacheOnlyUrlRetrieval
            };

            Guid action = NativeMethods.WinTrustActionGenericVerifyV2;
            int hr = NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = NativeMethods.WtdStateActionClose;
            NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return hr;
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    private static void TryReadSigner(string path, out string? publisher, out string? thumbprint)
    {
        publisher = null;
        thumbprint = null;
        if (!NativeMethods.CryptQueryObject(
                NativeMethods.CertQueryObjectFile,
                path,
                NativeMethods.CertQueryContentFlagPkcs7SignedEmbed,
                NativeMethods.CertQueryFormatFlagBinary,
                0,
                out _,
                out _,
                out _,
                out IntPtr store,
                out IntPtr msg,
                out _))
        {
            return;
        }

        try
        {
            IntPtr cert = NativeMethods.CertEnumCertificatesInStore(store, IntPtr.Zero);
            if (cert == IntPtr.Zero) return;

            try
            {
            var name = new char[512];
            int written = NativeMethods.CertGetNameString(
                cert, NativeMethods.CertNameSimpleDisplayType, 0, IntPtr.Zero, name, name.Length);
            if (written > 1)
            {
                publisher = new string(name, 0, written - 1);
            }

            int hashLen = 0;
            if (NativeMethods.CertGetCertificateContextProperty(cert, NativeMethods.CertHashPropId, null, ref hashLen)
                && hashLen > 0 && hashLen <= 64)
            {
                var hash = new byte[hashLen];
                if (NativeMethods.CertGetCertificateContextProperty(cert, NativeMethods.CertHashPropId, hash, ref hashLen))
                {
                    thumbprint = Convert.ToHexString(hash, 0, hashLen).ToLowerInvariant();
                }
            }
            }
            finally
            {
                NativeMethods.CertFreeCertificateContext(cert);
            }
        }
        finally
        {
            if (msg != IntPtr.Zero) NativeMethods.CryptMsgClose(msg);
            if (store != IntPtr.Zero) NativeMethods.CertCloseStore(store, 0);
        }
    }
}
