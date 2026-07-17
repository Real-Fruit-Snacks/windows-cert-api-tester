using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace ApiTester.Core;

/// <summary>A single request's editable state — the value a tab, history entry, or saved
/// request is built from. Bindable so the editor reflects changes both ways.</summary>
public sealed class RequestModel : INotifyPropertyChanged
{
    private string _method = "GET";
    private string? _baseUrl;
    private string _path = "";
    private string? _body;
    private string _contentType = "application/json";
    private string _authType = "None";
    private string? _authUser;
    private string? _authSecret;
    private string? _certThumbprint;
    private bool _ignoreServerCert;
    private int _timeoutSeconds = 100;
    private string? _sourceCollectionId;

    public string Method { get => _method; set { _method = value; Raise(nameof(Method)); } }
    public string? BaseUrl { get => _baseUrl; set { _baseUrl = value; Raise(nameof(BaseUrl)); } }
    public string Path { get => _path; set { _path = value; Raise(nameof(Path)); } }
    public string? Body { get => _body; set { _body = value; Raise(nameof(Body)); } }
    public string ContentType { get => _contentType; set { _contentType = value; Raise(nameof(ContentType)); } }
    public string AuthType { get => _authType; set { _authType = value; Raise(nameof(AuthType)); } }
    public string? AuthUser { get => _authUser; set { _authUser = value; Raise(nameof(AuthUser)); } }
    public string? AuthSecret { get => _authSecret; set { _authSecret = value; Raise(nameof(AuthSecret)); } }
    public string? CertThumbprint { get => _certThumbprint; set { _certThumbprint = value; Raise(nameof(CertThumbprint)); } }
    public bool IgnoreServerCert { get => _ignoreServerCert; set { _ignoreServerCert = value; Raise(nameof(IgnoreServerCert)); } }
    public int TimeoutSeconds { get => _timeoutSeconds; set { _timeoutSeconds = value; Raise(nameof(TimeoutSeconds)); } }

    /// <summary>The collection entry this request was opened from (or saved to), so a send can
    /// record the endpoint's last result back on that entry. Null for unlinked requests.</summary>
    public string? SourceCollectionId
    {
        get => _sourceCollectionId;
        set { _sourceCollectionId = value; Raise(nameof(SourceCollectionId)); }
    }

    public ObservableCollection<HeaderRow> Headers { get; set; } = new();
    public ObservableCollection<ParamRow> QueryParams { get; set; } = new();
    public ObservableCollection<CaptureRule> Captures { get; set; } = new();

    /// <summary>The enabled, non-empty query parameters as key/value pairs.</summary>
    public IEnumerable<KeyValuePair<string, string>> EnabledParams() =>
        QueryParams.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Key))
                   .Select(p => new KeyValuePair<string, string>(p.Key.Trim(), p.Value ?? ""));

    /// <summary>Base URL + path + enabled query parameters, composed into the URL to send.</summary>
    public string EffectiveUrl() => RequestUrl.Effective(BaseUrl, Path, EnabledParams());

    /// <summary>Build a history entry from this request and the response it produced.</summary>
    public HistoryEntry ToHistoryEntry(int? statusCode, ResponseSnapshot? snapshot)
    {
        var entry = new HistoryEntry
        {
            Method = Method,
            BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl!.Trim(),
            Url = Path.Trim(),
            Params = QueryParams.Select(p => new ParamRow { Enabled = p.Enabled, Key = p.Key, Value = p.Value }).ToList(),
            Headers = Headers.Select(h => new HeaderRow { Enabled = h.Enabled, Name = h.Name, Value = h.Value }).ToList(),
            Captures = Captures.Select(c => new CaptureRule { Enabled = c.Enabled, Variable = c.Variable, Source = c.Source, Path = c.Path }).ToList(),
            Body = string.IsNullOrEmpty(Body) ? null : Body,
            ContentType = ContentType,
            AuthType = AuthType,
            AuthUser = AuthUser,
            AuthSecret = AuthSecret,
            CertThumbprint = CertThumbprint,
            IgnoreServerCert = IgnoreServerCert,
            TimeoutSeconds = TimeoutSeconds,
            StatusCode = statusCode,
            Response = snapshot
        };
        return entry;
    }

    /// <summary>Build a request model from an imported (cURL or OpenAPI) request.</summary>
    public static RequestModel FromParsed(ParsedRequest p)
    {
        var m = new RequestModel { Method = p.Method, BaseUrl = p.BaseUrl ?? "" };
        var (path, query) = RequestUrl.SplitForEditing(p.Url);
        m.Path = path;
        foreach (var kv in query) m.QueryParams.Add(new ParamRow { Key = kv.Key, Value = kv.Value });
        foreach (var h in p.Headers) m.Headers.Add(new HeaderRow { Name = h.Key, Value = h.Value });
        m.Body = string.IsNullOrEmpty(p.Body) ? null : p.Body;
        if (!string.IsNullOrEmpty(p.ContentType)) m.ContentType = p.ContentType;
        if (p.BearerToken is not null) { m.AuthType = "Bearer"; m.AuthSecret = p.BearerToken; }
        else if (p.BasicUser is not null || p.BasicPassword is not null)
        {
            m.AuthType = "Basic"; m.AuthUser = p.BasicUser; m.AuthSecret = p.BasicPassword;
        }
        m.IgnoreServerCert = p.InsecureSkipVerify;
        return m;
    }

    /// <summary>Rebuild a request model from a stored history entry.</summary>
    public static RequestModel FromHistoryEntry(HistoryEntry e)
    {
        var m = new RequestModel();
        m.LoadFrom(e);
        return m;
    }

    /// <summary>Copy a stored history entry's fields into this model in place (keeps the
    /// same instance so open tabs/bindings update rather than being replaced).</summary>
    public void LoadFrom(HistoryEntry e)
    {
        Method = e.Method;
        BaseUrl = e.BaseUrl ?? "";
        Path = e.Url;
        Body = e.Body ?? "";
        ContentType = e.ContentType;
        AuthType = e.AuthType switch { "Bearer" => "Bearer", "Basic" => "Basic", _ => "None" };
        AuthUser = e.AuthUser;
        AuthSecret = e.AuthSecret;
        CertThumbprint = e.CertThumbprint;
        IgnoreServerCert = e.IgnoreServerCert;
        TimeoutSeconds = e.TimeoutSeconds;
        Headers.Clear();
        foreach (var h in e.Headers)
            Headers.Add(new HeaderRow { Enabled = h.Enabled, Name = h.Name, Value = h.Value });
        QueryParams.Clear();
        foreach (var p in e.Params)
            QueryParams.Add(new ParamRow { Enabled = p.Enabled, Key = p.Key, Value = p.Value });
        Captures.Clear();
        foreach (var c in e.Captures)
            Captures.Add(new CaptureRule { Enabled = c.Enabled, Variable = c.Variable, Source = c.Source, Path = c.Path });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}
