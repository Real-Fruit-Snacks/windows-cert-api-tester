using System.IO;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Protects the persisted transport mirror: its defaults must reproduce the behavior the
/// client had before transport control existed, and a workspace written before the feature must
/// still load.</summary>
public class TransportSettingsTests
{
    [Fact]
    public void Default_settings_produce_the_pre_feature_transport_behavior()
    {
        var options = new TransportSettings().ToOptions();

        Assert.Equal(ProxyMode.System, options.Proxy);
        Assert.Null(options.ProxyUrl);
        Assert.Null(options.ProxyUser);
        Assert.Null(options.ProxyPassword);
        Assert.True(options.FollowRedirects);
        Assert.Equal(20, options.MaxRedirects);
        Assert.True(options.Decompress);
        Assert.Equal(HttpVersionMode.Auto, options.Version);
        Assert.False(options.IgnoreServerCertificateErrors);
        Assert.Empty(options.Resolve);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToOptions_carries_the_requests_ignore_certificate_errors_switch(bool ignore)
    {
        var options = new TransportSettings().ToOptions(ignore);

        Assert.Equal(ignore, options.IgnoreServerCertificateErrors);
    }

    [Fact]
    public void From_seeds_settings_that_reproduce_the_options_they_came_from()
    {
        var back = TransportSettings.From(new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = "http://proxy.corp:8080",
            ProxyUser = "alice",
            ProxyPassword = "s3cret",
            FollowRedirects = false,
            MaxRedirects = 3,
            Decompress = false,
            Version = HttpVersionMode.Http2
        }).ToOptions();

        Assert.Equal(ProxyMode.Explicit, back.Proxy);
        Assert.Equal("http://proxy.corp:8080", back.ProxyUrl);
        Assert.Equal("alice", back.ProxyUser);
        Assert.Equal("s3cret", back.ProxyPassword);
        Assert.False(back.FollowRedirects);
        Assert.Equal(3, back.MaxRedirects);
        Assert.False(back.Decompress);
        Assert.Equal(HttpVersionMode.Http2, back.Version);
    }

    [Fact]
    public void A_requests_transport_choices_survive_a_history_entry_round_trip()
    {
        var original = new RequestModel { Method = "GET", Path = "/health" };
        original.Transport.Proxy = ProxyMode.None;
        original.Transport.ProxyUrl = "http://ignored:3128";
        original.Transport.ProxyUser = "bob";
        original.Transport.ProxyPassword = "pw";
        original.Transport.FollowRedirects = false;
        original.Transport.MaxRedirects = 5;
        original.Transport.Decompress = false;
        original.Transport.Version = HttpVersionMode.Http11;

        var restored = RequestModel.FromHistoryEntry(original.ToHistoryEntry(200, null));

        Assert.Equal(ProxyMode.None, restored.Transport.Proxy);
        Assert.Equal("http://ignored:3128", restored.Transport.ProxyUrl);
        Assert.Equal("bob", restored.Transport.ProxyUser);
        Assert.Equal("pw", restored.Transport.ProxyPassword);
        Assert.False(restored.Transport.FollowRedirects);
        Assert.Equal(5, restored.Transport.MaxRedirects);
        Assert.False(restored.Transport.Decompress);
        Assert.Equal(HttpVersionMode.Http11, restored.Transport.Version);
    }

    [Fact]
    public void A_stored_history_entry_is_unaffected_by_later_editing_of_the_request()
    {
        var request = new RequestModel { Method = "GET", Path = "/health" };
        request.Transport.MaxRedirects = 5;

        var entry = request.ToHistoryEntry(200, null);
        request.Transport.MaxRedirects = 99;
        request.Transport.Proxy = ProxyMode.Explicit;

        var restored = RequestModel.FromHistoryEntry(entry);
        Assert.Equal(5, restored.Transport.MaxRedirects);
        Assert.Equal(ProxyMode.System, restored.Transport.Proxy);
    }

    [Fact]
    public void LoadFrom_updates_transport_in_place_keeping_the_same_instance()
    {
        var model = new RequestModel { Method = "GET", Path = "/old" };
        var transportInstance = model.Transport;

        var entry = new HistoryEntry { Method = "GET", Url = "/new" };
        entry.Transport.Proxy = ProxyMode.Explicit;
        entry.Transport.ProxyUrl = "http://proxy.corp:8080";
        entry.Transport.MaxRedirects = 2;
        entry.Transport.Decompress = false;

        model.LoadFrom(entry);

        Assert.Same(transportInstance, model.Transport);   // same instance -> data bindings survive
        Assert.Equal(ProxyMode.Explicit, model.Transport.Proxy);
        Assert.Equal("http://proxy.corp:8080", model.Transport.ProxyUrl);
        Assert.Equal(2, model.Transport.MaxRedirects);
        Assert.False(model.Transport.Decompress);
    }

    [Fact]
    public void Transport_settings_survive_a_workspace_file_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"certapi-transport-{Guid.NewGuid():N}.json");
        try
        {
            var state = new AppState
            {
                Tabs = { new RequestModel { Method = "GET", Path = "/tab" } },
                History = { new HistoryEntry { Method = "POST", Url = "/history" } }
            };
            state.Tabs[0].Transport.Proxy = ProxyMode.Explicit;
            state.Tabs[0].Transport.ProxyUrl = "http://proxy.corp:8080";
            state.Tabs[0].Transport.ProxyUser = "alice";
            state.Tabs[0].Transport.ProxyPassword = "s3cret";
            state.Tabs[0].Transport.Version = HttpVersionMode.Http2;
            state.Tabs[0].Transport.MaxRedirects = 7;
            state.History[0].Transport.FollowRedirects = false;
            state.History[0].Transport.Decompress = false;
            state.History[0].Transport.Version = HttpVersionMode.Http11;
            state.SaveTo(path);

            var back = AppState.LoadFrom(path);

            var tab = Assert.Single(back.Tabs);
            Assert.Equal(ProxyMode.Explicit, tab.Transport.Proxy);
            Assert.Equal("http://proxy.corp:8080", tab.Transport.ProxyUrl);
            Assert.Equal("alice", tab.Transport.ProxyUser);
            Assert.Equal("s3cret", tab.Transport.ProxyPassword);
            Assert.Equal(HttpVersionMode.Http2, tab.Transport.Version);
            Assert.Equal(7, tab.Transport.MaxRedirects);

            var entry = Assert.Single(back.History);
            Assert.False(entry.Transport.FollowRedirects);
            Assert.False(entry.Transport.Decompress);
            Assert.Equal(HttpVersionMode.Http11, entry.Transport.Version);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_workspace_written_before_transport_settings_existed_loads_with_the_defaults()
    {
        // Hand-written to match a state.json from before this feature: no Transport anywhere.
        const string legacy = """
            {
              "SchemaVersion": 1,
              "TimeoutSeconds": 100,
              "Tabs": [
                {
                  "Method": "POST",
                  "BaseUrl": "https://api.example",
                  "Path": "/users",
                  "ContentType": "application/json",
                  "AuthType": "Bearer",
                  "AuthSecret": "tok",
                  "TimeoutSeconds": 100
                }
              ],
              "History": [
                {
                  "Method": "GET",
                  "Url": "/health",
                  "ContentType": "application/json",
                  "AuthType": "Auto",
                  "TimeoutSeconds": 100,
                  "StatusCode": 200
                }
              ]
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"certapi-legacy-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, legacy);

            var state = AppState.LoadFrom(path);

            var tab = Assert.Single(state.Tabs);
            Assert.Equal("POST", tab.Method);            // the file really did load
            AssertPreFeatureDefaults(tab.Transport.ToOptions());

            var entry = Assert.Single(state.History);
            Assert.Equal("/health", entry.Url);
            AssertPreFeatureDefaults(entry.Transport.ToOptions());
        }
        finally { File.Delete(path); }
    }

    private static void AssertPreFeatureDefaults(TransportOptions options)
    {
        Assert.Equal(ProxyMode.System, options.Proxy);
        Assert.Null(options.ProxyUrl);
        Assert.Null(options.ProxyUser);
        Assert.Null(options.ProxyPassword);
        Assert.True(options.FollowRedirects);
        Assert.Equal(20, options.MaxRedirects);
        Assert.True(options.Decompress);
        Assert.Equal(HttpVersionMode.Auto, options.Version);
        Assert.False(options.IgnoreServerCertificateErrors);
        Assert.Empty(options.Resolve);
    }
}
