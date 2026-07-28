using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The advance-warning window: a certificate that still works but stops working soon is
/// the outage this warns about before it happens, and every boundary of that window is pinned
/// here against an injected clock rather than the machine's. The already-expired case keeps its
/// own louder wording — the two must never blur into each other.</summary>
public class CertificateExpiryTests
{
    private static readonly DateTime Now = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Local);

    private static CertificateInfo Cert(DateTime notAfter, DateTime? notBefore = null) => new()
    {
        Subject = "CN=Renew Me",
        Issuer = "CN=CA",
        Thumbprint = "AABBCC",
        NotBefore = notBefore ?? Now.AddDays(-365),
        NotAfter = notAfter,
        HasClientAuthEku = true,
        Certificate = null!   // never touched by the date logic under test
    };

    [Theory]
    [InlineData(0.5, 0)]      // half a day left is 0 days, never rounded up to 1
    [InlineData(1.5, 1)]
    [InlineData(14, 14)]
    [InlineData(30, 30)]
    public void DaysUntilExpiry_floors_rather_than_rounds(double daysAway, int expected)
    {
        Assert.Equal(expected, Cert(Now.AddDays(daysAway)).DaysUntilExpiry(Now));
    }

    [Theory]
    [InlineData(1, true)]     // inside the window
    [InlineData(13.9, true)]
    [InlineData(14, true)]    // the boundary itself warns
    [InlineData(14.1, true)]  // 14 days and change still floors to 14
    [InlineData(15, false)]   // outside it
    [InlineData(365, false)]
    public void IsExpiringSoon_covers_the_fourteen_day_window(double daysAway, bool expected)
    {
        Assert.Equal(expected, Cert(Now.AddDays(daysAway)).IsExpiringSoon(Now));
    }

    [Fact]
    public void An_expired_certificate_is_not_reported_as_expiring_soon()
    {
        // The louder message owns this case; softening it to "expires soon" would be a lie about a
        // certificate that has already stopped working.
        var expired = Cert(Now.AddDays(-1));

        Assert.True(expired.IsExpired(Now));
        Assert.False(expired.IsExpiringSoon(Now));
        Assert.Contains("is expired", expired.ExpiryWarning(Now));
    }

    [Fact]
    public void A_certificate_that_is_not_valid_yet_says_so_rather_than_saying_expired()
    {
        var future = Cert(Now.AddDays(400), notBefore: Now.AddDays(5));

        Assert.True(future.IsExpired(Now));   // "not usable now" — the existing predicate's meaning
        Assert.Contains("not valid yet", future.ExpiryWarning(Now));
    }

    [Theory]
    [InlineData(0.2, "today")]
    [InlineData(1.2, "tomorrow")]
    [InlineData(9, "in 9 days")]
    public void The_warning_says_when_in_words_a_person_can_act_on(double daysAway, string expected)
    {
        string? warning = Cert(Now.AddDays(daysAway)).ExpiryWarning(Now);

        Assert.NotNull(warning);
        Assert.Contains(expected, warning);
        Assert.Contains("CN=Renew Me", warning);      // which certificate
        Assert.Contains("not after", warning);        // and the date itself, for a ticket
    }

    [Fact]
    public void A_healthy_certificate_produces_no_warning_at_all()
    {
        Assert.Null(Cert(Now.AddDays(90)).ExpiryWarning(Now));
        Assert.False(Cert(Now.AddDays(90)).IsExpiringSoon(Now));
    }

    [Fact]
    public void The_warning_window_is_a_named_constant_rather_than_a_scattered_literal()
    {
        // If this is ever retuned, it must be retuned in ONE place — the constant every caller and
        // every message derives from.
        Assert.Equal(14, CertificateInfo.ExpiryWarningDays);
        Assert.True(Cert(Now.AddDays(CertificateInfo.ExpiryWarningDays)).IsExpiringSoon(Now));
        Assert.False(Cert(Now.AddDays(CertificateInfo.ExpiryWarningDays + 1)).IsExpiringSoon(Now));
    }
}
