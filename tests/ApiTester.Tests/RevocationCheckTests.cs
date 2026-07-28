using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Protects the one pure decision table the whole revocation feature rests on. A genuinely
/// revoked certificate needs a real public-key infrastructure and cannot be produced here, so every
/// interesting case — revoked beating a pin, an unknown status being swallowed by default but fatal
/// under --revocation-strict, --insecure overriding but still being reported honestly — is exercised
/// as plain data through <see cref="RevocationCheck.Decide"/> with no network and no certificates.</summary>
public class RevocationCheckTests
{
    // --- mode None: today's behavior, byte for byte -----------------------------------------

    [Fact]
    public void None_mode_with_a_clean_chain_is_accepted_and_reports_not_checked()
    {
        var decision = RevocationCheck.Decide(
            RevocationMode.None, strict: false, insecure: false,
            SslPolicyErrors.None, X509ChainStatusFlags.NoError, pinnedThumbprintMatches: null);

        Assert.True(decision.Accepted);
        Assert.Equal(RevocationStatus.NotChecked, decision.Status);
        Assert.Null(decision.RefusedByRevocation);
    }

    [Fact]
    public void None_mode_with_an_untrusted_root_and_no_pin_is_refused()
    {
        var decision = RevocationCheck.Decide(
            RevocationMode.None, strict: false, insecure: false,
            SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.UntrustedRoot,
            pinnedThumbprintMatches: null);

        Assert.False(decision.Accepted);
        Assert.Equal(RevocationStatus.NotChecked, decision.Status);
        Assert.Null(decision.RefusedByRevocation);
    }

    [Fact]
    public void None_mode_with_an_untrusted_root_lets_a_matching_pin_through()
    {
        var decision = RevocationCheck.Decide(
            RevocationMode.None, strict: false, insecure: false,
            SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.UntrustedRoot,
            pinnedThumbprintMatches: () => true);

        Assert.True(decision.Accepted);
        Assert.Equal(RevocationStatus.NotChecked, decision.Status);
        Assert.Null(decision.RefusedByRevocation);
    }

    [Fact]
    public void None_mode_ignores_revoked_chain_flags_entirely_because_nothing_was_checked()
    {
        // Even a chain that says Revoked must be decided by the ordinary trust rules under mode
        // None: nothing was asked for, so nothing about revocation may be claimed. A matching pin
        // still rescues it, exactly as it would for any other chain error.
        var decision = RevocationCheck.Decide(
            RevocationMode.None, strict: false, insecure: false,
            SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.Revoked,
            pinnedThumbprintMatches: () => true);

        Assert.True(decision.Accepted);
        Assert.Equal(RevocationStatus.NotChecked, decision.Status);
        Assert.Null(decision.RefusedByRevocation);
    }

    // --- --insecure: keeps overriding everything, but must say so honestly -----------------

    [Fact]
    public void Insecure_accepts_a_revoked_chain_but_reports_that_revocation_was_not_enforced()
    {
        var decision = RevocationCheck.Decide(
            RevocationMode.Online, strict: false, insecure: true,
            SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.Revoked,
            pinnedThumbprintMatches: null);

        Assert.True(decision.Accepted);
        Assert.Equal(RevocationStatus.NotEnforced, decision.Status);
        Assert.Null(decision.RefusedByRevocation);
    }

    [Fact]
    public void Insecure_without_a_revocation_mode_reports_not_checked_rather_than_not_enforced()
    {
        // Nothing was asked for, so there is nothing to say was left unenforced.
        var decision = RevocationCheck.Decide(
            RevocationMode.None, strict: false, insecure: true,
            SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.Revoked,
            pinnedThumbprintMatches: null);

        Assert.True(decision.Accepted);
        Assert.Equal(RevocationStatus.NotChecked, decision.Status);
        Assert.Null(decision.RefusedByRevocation);
    }

    // --- checking enabled: the table that matters most --------------------------------------

    public static IEnumerable<object?[]> CheckingEnabledCases => new List<object?[]>
    {
        // name, mode, strict, policyErrors, chainFlags, pinReturns (null = no pin),
        // expectedAccepted, expectedStatus, expectedRefusedByRevocation

        // Revocation wins over a pin: a pin is an operator's earlier statement, revocation is the
        // issuer's later withdrawal, and the later statement wins.
        new object?[]
        {
            "revoked chain refuses even when a pin would otherwise accept it",
            RevocationMode.Online, false, SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.Revoked, true,
            false, RevocationStatus.Revoked, RevocationStatus.Revoked
        },
        // Revoked also wins over untrusted-root in the reported status when both are present.
        new object?[]
        {
            "revoked wins the reported status over an untrusted root also present in the chain",
            RevocationMode.Online, false, SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.Revoked | X509ChainStatusFlags.UntrustedRoot, (bool?)null,
            false, RevocationStatus.Revoked, RevocationStatus.Revoked
        },
        // An unreachable revocation endpoint on an otherwise clean chain must not fail the
        // connection by default -- this is the common corporate case the whole feature exists for.
        new object?[]
        {
            "an unreachable revocation endpoint on an otherwise clean chain is accepted by default",
            RevocationMode.Online, false, SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.RevocationStatusUnknown, (bool?)null,
            true, RevocationStatus.Unknown, null
        },
        // Offline mode reports the same swallowed-unknown outcome for a cached-list miss.
        new object?[]
        {
            "an offline revocation list miss on an otherwise clean chain is accepted by default",
            RevocationMode.Offline, false, SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.OfflineRevocation, (bool?)null,
            true, RevocationStatus.Unknown, null
        },
        // --revocation-strict makes an unknown status fatal, and a pin does not rescue it: the
        // user explicitly asked that an indeterminate answer be fatal, which is the stricter of
        // the two statements.
        new object?[]
        {
            "strict mode fails a chain with unknown revocation status even when a pin would accept it",
            RevocationMode.Online, true, SslPolicyErrors.None,
            X509ChainStatusFlags.RevocationStatusUnknown, true,
            false, RevocationStatus.Unknown, RevocationStatus.Unknown
        },
        // An unknown status alongside a real chain problem is not revocation's business: the
        // ordinary rules (here, no pin) decide it, so RefusedByRevocation must be null.
        new object?[]
        {
            "unknown status alongside an untrusted root is refused by the ordinary rules, not revocation",
            RevocationMode.Online, false, SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.UntrustedRoot, (bool?)null,
            false, RevocationStatus.Unknown, null
        },
        // ...but a matching pin still rescues that same combination, exactly as it would for any
        // other ordinary chain error.
        new object?[]
        {
            "unknown status alongside an untrusted root is accepted when a pin matches",
            RevocationMode.Online, false, SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.UntrustedRoot, true,
            true, RevocationStatus.Unknown, null
        },
        // A policy-level problem outside the chain itself (name mismatch) is also not revocation's
        // business to swallow.
        new object?[]
        {
            "unknown status alongside a name mismatch outside the chain is refused, not by revocation",
            RevocationMode.Online, false, SslPolicyErrors.RemoteCertificateNameMismatch,
            X509ChainStatusFlags.RevocationStatusUnknown, (bool?)null,
            false, RevocationStatus.Unknown, null
        },
        // An ordinary chain problem with no revocation flags at all is decided by the ordinary
        // rules exactly as before, now reported as Checked because checking did run.
        new object?[]
        {
            "an expired certificate with no revocation flags is refused and reported as checked",
            RevocationMode.Online, false, SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.NotTimeValid, (bool?)null,
            false, RevocationStatus.Checked, null
        },
        new object?[]
        {
            "a clean chain with checking enabled is accepted and reported as checked",
            RevocationMode.Online, false, SslPolicyErrors.None,
            X509ChainStatusFlags.NoError, (bool?)null,
            true, RevocationStatus.Checked, null
        },
        // The pin path under checking: an ordinary chain problem with no revocation flags is
        // still rescued by a matching pin — the same rescue mode None gives, now reported as
        // Checked because checking ran and found no revocation problem.
        new object?[]
        {
            "an untrusted root with no revocation flags is accepted when a pin matches",
            RevocationMode.Online, false, SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.UntrustedRoot, true,
            true, RevocationStatus.Checked, null
        },
    };

    [Theory]
    [MemberData(nameof(CheckingEnabledCases))]
    public void Decide_resolves_the_revocation_table_when_checking_is_enabled(
        string _,
        RevocationMode mode,
        bool strict,
        SslPolicyErrors policyErrors,
        X509ChainStatusFlags chainFlags,
        bool? pinReturns,
        bool expectedAccepted,
        RevocationStatus expectedStatus,
        RevocationStatus? expectedRefusedByRevocation)
    {
        Func<bool>? pin = pinReturns is null ? null : () => pinReturns.Value;

        var decision = RevocationCheck.Decide(mode, strict, insecure: false, policyErrors, chainFlags, pin);

        Assert.Equal(expectedAccepted, decision.Accepted);
        Assert.Equal(expectedStatus, decision.Status);
        Assert.Equal(expectedRefusedByRevocation, decision.RefusedByRevocation);
    }

    // --- ToX509Mode / Apply / ChainFlagsOf --------------------------------------------------

    [Theory]
    [InlineData(RevocationMode.None, X509RevocationMode.NoCheck)]
    [InlineData(RevocationMode.Offline, X509RevocationMode.Offline)]
    [InlineData(RevocationMode.Online, X509RevocationMode.Online)]
    public void ToX509Mode_maps_each_revocation_mode_to_its_x509_equivalent(
        RevocationMode mode, X509RevocationMode expected)
    {
        Assert.Equal(expected, RevocationCheck.ToX509Mode(mode));
    }

    [Theory]
    [InlineData(RevocationMode.None, X509RevocationMode.NoCheck)]
    [InlineData(RevocationMode.Offline, X509RevocationMode.Offline)]
    [InlineData(RevocationMode.Online, X509RevocationMode.Online)]
    public void Apply_sets_the_certificate_revocation_check_mode_on_ssl_options(
        RevocationMode mode, X509RevocationMode expected)
    {
        var options = new SslClientAuthenticationOptions();

        RevocationCheck.Apply(options, mode);

        Assert.Equal(expected, options.CertificateRevocationCheckMode);
    }

    [Fact]
    public void ChainFlagsOf_a_null_chain_reports_no_error()
    {
        Assert.Equal(X509ChainStatusFlags.NoError, RevocationCheck.ChainFlagsOf(null));
    }

    [Fact]
    public void ChainFlagsOf_a_selfsigned_chain_keeps_the_untrusted_root_flag()
    {
        using var cert = SelfSignedCertificateFactory.CreateCertificateAuthority("ChainFlags");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(cert);

        var flags = RevocationCheck.ChainFlagsOf(chain);

        // The same flag arrives from both the aggregate status and the per-element scan, so the
        // accumulation must keep it present rather than cancelling it out on the second sighting.
        Assert.True(flags.HasFlag(X509ChainStatusFlags.UntrustedRoot));
    }

    [Fact]
    public void ChainFlagsOf_keeps_a_chain_level_flag_no_element_carries()
    {
        // A leaf whose issuer is nowhere to be found: the chain cannot reach a root, and
        // PartialChain is recorded on the chain's aggregate status only — no element carries it.
        // That makes this the one case where the aggregate scan is load-bearing rather than
        // redundant with the per-element scan, so it is the case that proves the aggregate scan
        // actually accumulates. (A flag both scans see — UntrustedRoot on a self-signed chain —
        // cannot prove that: either scan alone reproduces it.)
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("AbsentIssuer");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate("leaf", ca, true, false, new[] { "leaf" });
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(leaf); // the CA is deliberately not in ExtraStore

        var flags = RevocationCheck.ChainFlagsOf(chain);

        Assert.True(flags.HasFlag(X509ChainStatusFlags.PartialChain));
    }

    [Fact]
    public void ChainFlagsOf_a_disposed_chain_reports_no_error_rather_than_throwing()
    {
        // The documented promise: a diagnostics helper must never be what breaks a handshake,
        // even when the callback's chain has already been torn down under it.
        var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using (var cert = SelfSignedCertificateFactory.CreateCertificateAuthority("Disposed"))
            chain.Build(cert);
        chain.Dispose();

        Assert.Equal(X509ChainStatusFlags.NoError, RevocationCheck.ChainFlagsOf(chain));
    }
}
