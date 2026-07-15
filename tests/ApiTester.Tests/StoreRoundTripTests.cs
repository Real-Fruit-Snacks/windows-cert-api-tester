using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

public class StoreRoundTripTests
{
    [Trait("Category", "StoreRoundTrip")]
    [Fact]
    public void Finds_installed_cert_in_current_user_store()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("RoundTrip CA");
        // Must be persistable to the store, so re-import without EphemeralKeySet.
        using var ephemeral = SelfSignedCertificateFactory.CreateSignedCertificate("RoundTrip Client", ca, false, true);
        using var client = X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(client);
        try
        {
            var found = new CertificateStoreService().FindByThumbprint(client.Thumbprint!);
            Assert.NotNull(found);
            Assert.Equal(client.Thumbprint, found!.Thumbprint);
        }
        finally
        {
            store.Remove(client);
        }
    }
}
