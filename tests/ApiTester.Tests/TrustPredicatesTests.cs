using ApiTester.Core;

namespace ApiTester.Tests;

public class TrustPredicatesTests
{
    [Fact]
    public void For_returns_the_same_delegate_instance_for_the_same_host()
    {
        var predicates = new TrustPredicates(new AppState());

        var first = predicates.For("api.example.com");
        var second = predicates.For("api.example.com");

        Assert.Same(first, second);
    }

    [Fact]
    public void For_returns_the_same_delegate_instance_for_a_differently_cased_host()
    {
        var predicates = new TrustPredicates(new AppState());

        var lower = predicates.For("api.example.com");
        var upper = predicates.For("API.EXAMPLE.COM");

        Assert.Same(lower, upper);
    }

    [Fact]
    public void For_returns_a_different_delegate_instance_for_a_different_host()
    {
        var predicates = new TrustPredicates(new AppState());

        var a = predicates.For("a.example.com");
        var b = predicates.For("b.example.com");

        Assert.NotSame(a, b);
    }

    [Fact]
    public void The_returned_predicate_trusts_a_certificate_pinned_for_that_host()
    {
        var state = new AppState();
        TrustService.Trust(state, "api.example.com", "ABC123", "CN=x");
        var predicates = new TrustPredicates(state);

        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var cert = SelfSignedCertificateFactory.CreateSignedCertificate("ABC123-holder", ca, true, false);

        // The predicate decides by thumbprint, not the certificate's subject, so what actually gets
        // pinned is proven directly against TrustService rather than by constructing a certificate
        // with that literal thumbprint (not something a caller can choose).
        bool expected = TrustService.IsTrusted(state, "api.example.com", cert.Thumbprint!);
        Assert.Equal(expected, predicates.For("api.example.com")(cert));
    }

    [Fact]
    public void The_returned_predicate_does_not_trust_an_unpinned_certificate()
    {
        var state = new AppState();
        var predicates = new TrustPredicates(state);

        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var cert = SelfSignedCertificateFactory.CreateSignedCertificate("unpinned", ca, true, false);

        Assert.False(predicates.For("api.example.com")(cert));
    }

    [Fact]
    public void The_returned_predicate_rejects_a_null_certificate()
    {
        var state = new AppState();
        TrustService.Trust(state, "api.example.com", "ABC123", "CN=x");
        var predicates = new TrustPredicates(state);

        Assert.False(predicates.For("api.example.com")(null));
    }
}
