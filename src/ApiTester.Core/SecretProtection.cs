using System.Runtime.InteropServices;
using System.Text;

namespace ApiTester.Core;

/// <summary>Encrypts an individual secret value for storage in the workspace file. Implementations
/// are per-user: what one Windows user protects, another cannot read.</summary>
public interface ISecretProtector
{
    /// <summary>True when this protector can actually encrypt on this system. When false, callers
    /// store secrets in the clear rather than losing them.</summary>
    bool IsAvailable { get; }

    /// <summary>Encrypt <paramref name="plaintext"/> into its stored form (see
    /// <see cref="SecretProtection.Prefix"/>). Returns null when it cannot — the caller then stores
    /// the value unencrypted rather than losing it.</summary>
    string? Protect(string plaintext);

    /// <summary>Decrypt a value produced by <see cref="Protect"/>. Returns null when it cannot be
    /// decrypted: a different Windows user, a different machine, or a corrupt blob.</summary>
    string? Unprotect(string stored);
}

/// <summary>Per-user encryption through the Windows Data Protection API (DPAPI), called directly
/// via crypt32.dll so ApiTester.Core keeps zero package references. Every operation is total: it
/// returns null rather than throwing, on any failure and on any non-Windows platform.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // CRYPTPROTECT_UI_FORBIDDEN: the call can never raise an interactive prompt — the CLI runs
    // headless. Deliberately NOT CRYPTPROTECT_LOCAL_MACHINE (0x4): scope is the current user, not
    // machine-wide — that distinction matters for this release.
    private const int CryptProtectUiForbidden = 0x1;

    // Fixed application entropy: binds a blob to this application's format. It ships in the
    // binary, so it is not a secret, but it must never change for v1 or every value already
    // written to a workspace file becomes undecryptable.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ApiTester.Core/secret/v1");

    private bool? _isAvailable;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            // DPAPI genuinely fails for accounts with no loaded user profile; round-tripping a
            // probe value through the real Protect/Unprotect path is the only honest way to know.
            return _isAvailable ??= ProbeRoundTrips();
        }
    }

    private bool ProbeRoundTrips()
    {
        const string probe = "afk-secret-protection-availability-probe";
        return ProtectCore(probe) is { } stored && UnprotectCore(stored) == probe;
    }

    /// <inheritdoc />
    public string? Protect(string plaintext) => IsAvailable ? ProtectCore(plaintext) : null;

    /// <inheritdoc />
    public string? Unprotect(string stored) =>
        IsAvailable && SecretProtection.LooksProtected(stored) ? UnprotectCore(stored) : null;

    // Never throws: any P/Invoke failure, bad Base64, or marshaling error returns null.
    private static string? ProtectCore(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        IntPtr inPtr = IntPtr.Zero, entropyPtr = IntPtr.Zero, outPtr = IntPtr.Zero;
        try
        {
            inPtr = AllocCopy(plainBytes);
            entropyPtr = AllocCopy(Entropy);
            var inBlob = new DataBlob { cbData = plainBytes.Length, pbData = inPtr };
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyPtr };

            bool ok = CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, out var outBlob);
            if (!ok) return null;
            outPtr = outBlob.pbData;

            var cipherBytes = new byte[outBlob.cbData];
            if (outBlob.cbData > 0) Marshal.Copy(outBlob.pbData, cipherBytes, 0, outBlob.cbData);
            return SecretProtection.Prefix + Convert.ToBase64String(cipherBytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (entropyPtr != IntPtr.Zero) Marshal.FreeHGlobal(entropyPtr);
            if (outPtr != IntPtr.Zero) LocalFree(outPtr);
        }
    }

    // Never throws: assumes SecretProtection.LooksProtected(stored) already passed.
    private static string? UnprotectCore(string stored)
    {
        IntPtr inPtr = IntPtr.Zero, entropyPtr = IntPtr.Zero, outPtr = IntPtr.Zero;
        int outLength = 0;
        try
        {
            var cipherBytes = Convert.FromBase64String(stored[SecretProtection.Prefix.Length..]);
            inPtr = AllocCopy(cipherBytes);
            entropyPtr = AllocCopy(Entropy);
            var inBlob = new DataBlob { cbData = cipherBytes.Length, pbData = inPtr };
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyPtr };

            bool ok = CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, out var outBlob);
            if (!ok) return null;
            outPtr = outBlob.pbData;
            outLength = outBlob.cbData;

            if (outBlob.cbData == 0) return "";
            var plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (entropyPtr != IntPtr.Zero) Marshal.FreeHGlobal(entropyPtr);
            if (outPtr != IntPtr.Zero)
            {
                // Zero the decrypted plaintext in native memory before releasing the buffer, so
                // it does not linger in the process's address space.
                if (outLength > 0) Marshal.Copy(new byte[outLength], 0, outPtr, outLength);
                LocalFree(outPtr);
            }
        }
    }

    private static IntPtr AllocCopy(byte[] bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length == 0 ? 1 : bytes.Length);
        if (bytes.Length > 0) Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }
}

/// <summary>The protector for a system that cannot encrypt: everything returns null, so callers
/// fall back to storing plaintext instead of losing the value.</summary>
public sealed class UnavailableSecretProtector : ISecretProtector
{
    public bool IsAvailable => false;
    public string? Protect(string plaintext) => null;
    public string? Unprotect(string stored) => null;
}

/// <summary>Marker and default-protector helpers shared by every caller that stores a secret.</summary>
public static class SecretProtection
{
    /// <summary>The marker a stored secret carries. Everything after it is the protector's business;
    /// a value without it is legacy plaintext.</summary>
    public const string Prefix = "enc:v1:";

    /// <summary>The protector this machine uses: DPAPI on Windows, none anywhere else.</summary>
    public static ISecretProtector Default { get; } =
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new UnavailableSecretProtector();

    /// <summary>True when <paramref name="value"/> is in the stored (encrypted) form: the marker
    /// followed by valid Base64 decoding to at least 32 bytes.</summary>
    public static bool LooksProtected(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        // The marker is the only thing that distinguishes ciphertext from a legacy plaintext
        // value, so requiring real Base64 of a plausible length keeps a user's literal password
        // that happens to begin with "enc:v1:" from being mistaken for ciphertext.
        var remainder = value[Prefix.Length..];
        var buffer = new byte[remainder.Length]; // decoded length is always <= encoded length
        return Convert.TryFromBase64String(remainder, buffer, out int bytesWritten) && bytesWritten >= 32;
    }
}
