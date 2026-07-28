using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>A saved request's proxy password is a credential, and it was being treated as though it
/// were not.
///
/// <para>It lives on the request's own <c>Transport</c> rather than beside <c>AuthSecret</c>, which
/// is exactly how it escaped both halves of the secret handling: it was written to
/// <c>state.json</c> in the clear while every other credential was encrypted, and
/// <c>certapi export workspace</c> stripped the auth secrets, reported what it had stripped, and
/// left the proxy password in the exported file. An export that says it was sanitised has to have
/// been.</para></summary>
public class ProxyPasswordSecretTests
{
    private const string Plain = "proxy-password-plaintext-42";

    private static RequestModel WithProxyPassword(string password = Plain)
    {
        var request = new RequestModel { Method = "GET", BaseUrl = "https://api.internal", Path = "/orders" };
        request.Transport.Proxy = ProxyMode.Explicit;
        request.Transport.ProxyUrl = "http://proxy.corp:8080";
        request.Transport.ProxyUser = "svc-account";
        request.Transport.ProxyPassword = password;
        return request;
    }

    private static AppState StateWithProxyPasswordsEverywhere()
    {
        var state = new AppState();

        state.Tabs.Add(WithProxyPassword());

        var history = new HistoryEntry { Method = "GET", BaseUrl = "https://api.internal", Url = "/orders" };
        history.Transport.ProxyPassword = Plain;
        state.History.Add(history);

        var folder = new CollectionNode { Name = "Orders", IsFolder = true };
        folder.Children.Add(new CollectionNode
        { Name = "Get orders", IsFolder = false, Request = WithProxyPassword() });
        state.Collections.Add(folder);

        return state;
    }

    // ---------------------------------------------------------------- the export leak

    [Fact]
    public void Stripping_for_export_removes_the_proxy_password_everywhere_it_can_live()
    {
        var state = StateWithProxyPasswordsEverywhere();

        var summary = StateSecrets.Strip(state);

        Assert.DoesNotContain(Plain, JsonSerializer.Serialize(state));
        Assert.Equal(3, summary.ProxyPasswords);      // tab, history entry, saved request
        Assert.Contains("proxy password", summary.Describe());
    }

    [Fact]
    public void A_workspace_whose_only_secret_is_a_proxy_password_is_not_reported_as_clean()
    {
        // The dangerous shape of the bug: `Any` was false, so the export said "it contained no
        // secrets to strip" while writing the password out.
        var state = new AppState();
        state.Tabs.Add(WithProxyPassword());

        var summary = StateSecrets.Strip(state);

        Assert.True(summary.Any);
        Assert.NotEmpty(summary.Describe());
    }

    [Fact]
    public void The_rest_of_the_proxy_settings_survive_stripping()
    {
        // Only the password is a secret. Dropping the proxy URL or user would break the request for
        // whoever imports the workspace, which is not what stripping is for.
        var state = StateWithProxyPasswordsEverywhere();

        StateSecrets.Strip(state);

        var request = state.Collections[0].Children[0].Request!;
        Assert.Equal("http://proxy.corp:8080", request.Transport.ProxyUrl);
        Assert.Equal("svc-account", request.Transport.ProxyUser);
        Assert.Equal(ProxyMode.Explicit, request.Transport.Proxy);
        Assert.True(string.IsNullOrEmpty(request.Transport.ProxyPassword));
    }

    // ---------------------------------------------------------------- encryption at rest

    [Fact]
    public void The_proxy_password_is_encrypted_at_rest_like_every_other_credential()
    {
        var state = StateWithProxyPasswordsEverywhere();

        Assert.True(StateSecrets.Protect(state, SecretProtection.Default));

        string json = JsonSerializer.Serialize(state);
        Assert.DoesNotContain(Plain, json);
        Assert.True(SecretProtection.LooksProtected(state.Tabs[0].Transport.ProxyPassword));
        Assert.True(SecretProtection.LooksProtected(state.History[0].Transport.ProxyPassword));
        Assert.True(SecretProtection.LooksProtected(
            state.Collections[0].Children[0].Request!.Transport.ProxyPassword));
    }

    [Fact]
    public void It_comes_back_intact()
    {
        var state = StateWithProxyPasswordsEverywhere();
        StateSecrets.Protect(state, SecretProtection.Default);

        var warnings = new List<string>();
        StateSecrets.Unprotect(state, SecretProtection.Default, warnings);

        Assert.Empty(warnings);
        Assert.Equal(Plain, state.Tabs[0].Transport.ProxyPassword);
        Assert.Equal(Plain, state.History[0].Transport.ProxyPassword);
        Assert.Equal(Plain, state.Collections[0].Children[0].Request!.Transport.ProxyPassword);
    }

    [Fact]
    public void Protecting_twice_does_not_double_encrypt_it()
    {
        // Every save runs Protect over state that may already be protected.
        var state = StateWithProxyPasswordsEverywhere();
        StateSecrets.Protect(state, SecretProtection.Default);
        string once = state.Tabs[0].Transport.ProxyPassword!;

        StateSecrets.Protect(state, SecretProtection.Default);

        Assert.Equal(once, state.Tabs[0].Transport.ProxyPassword);
    }

    [Fact]
    public void A_workspace_written_before_this_existed_still_loads()
    {
        // Its proxy password is plaintext and does not look protected, so Unprotect must leave it
        // exactly as it is rather than dropping it — losing a user's setting to a migration would
        // be a worse bug than the one being fixed.
        var state = StateWithProxyPasswordsEverywhere();

        var warnings = new List<string>();
        StateSecrets.Unprotect(state, SecretProtection.Default, warnings);

        Assert.Empty(warnings);
        Assert.Equal(Plain, state.Tabs[0].Transport.ProxyPassword);
        Assert.Equal(Plain, state.Collections[0].Children[0].Request!.Transport.ProxyPassword);
    }

    [Fact]
    public void A_request_with_no_proxy_password_is_untouched_and_uncounted()
    {
        var state = new AppState();
        state.Tabs.Add(new RequestModel { Method = "GET", BaseUrl = "https://api.internal", Path = "/x" });

        var summary = StateSecrets.Strip(state);

        Assert.Equal(0, summary.ProxyPasswords);
        Assert.False(summary.Any);
    }
}
