using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiTester.Core;

/// <summary>Persisted window/request state, stored under %AppData%\CertApiTester\state.json.</summary>
public sealed class AppState
{
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    public string? LastCertThumbprint { get; set; }
    public bool IgnoreServerCertErrors { get; set; }
    public int TimeoutSeconds { get; set; } = 100;

    public string? LastBaseUrl { get; set; }
    public List<string> SavedBaseUrls { get; set; } = new();

    public List<HistoryEntry> History { get; set; } = new();

    public List<RequestModel> Tabs { get; set; } = new();
    public int ActiveTabIndex { get; set; }

    public List<CollectionNode> Collections { get; set; } = new();
    public List<ApiEnvironment> Environments { get; set; } = new();
    public string? ActiveEnvironmentId { get; set; }

    public List<SessionToken> SessionTokens { get; set; } = new();

    /// <summary>Master switch for automatic token capture/attach. Detection results are still
    /// stored while off, so turning it back on works immediately.</summary>
    public bool AutoTokens { get; set; } = true;

    /// <summary>File-format version. 0 = files from before the Auto/None auth split.</summary>
    public int SchemaVersion { get; set; }

    public const int CurrentSchemaVersion = 1;

    /// <summary>The live GUI state file under %AppData%. Computing the path has no side
    /// effects; <see cref="SaveTo"/> creates the directory when it first writes.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "CertApiTester", "state.json");

    public static AppState Load()
    {
        try { return File.Exists(DefaultPath) ? LoadFrom(DefaultPath) : new AppState(); }
        catch { return new AppState(); } // corrupt or unreadable — start fresh
    }

    /// <summary>Load from an explicit file. Throws on missing/corrupt files — callers decide.</summary>
    public static AppState LoadFrom(string path)
    {
        var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path)) ?? new AppState();
        state.Migrate();
        return state;
    }

    /// <summary>Upgrade older states in place. Version 0 → 1: auth "None" predates the
    /// Auto/None split and meant "nothing configured", so it becomes "Auto".</summary>
    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion) return;
        foreach (var t in Tabs) MigrateAuth(t);
        foreach (var h in History) if (h.AuthType == "None") h.AuthType = "Auto";
        foreach (var c in Collections) MigrateNode(c);
        SchemaVersion = CurrentSchemaVersion;

        static void MigrateAuth(RequestModel m) { if (m.AuthType == "None") m.AuthType = "Auto"; }
        static void MigrateNode(CollectionNode n)
        {
            if (n.Request is { } r) MigrateAuth(r);
            foreach (var child in n.Children) MigrateNode(child);
        }
    }

    public void Save()
    {
        try { SaveTo(DefaultPath); }
        catch { /* best effort for the GUI */ }
    }

    /// <summary>Write atomically: serialize to a temp file, then replace the target.</summary>
    public void SaveTo(string path)
    {
        SchemaVersion = CurrentSchemaVersion;   // a written file is by definition current
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Combines a base URL with a path, honoring absolute URLs typed in the path box.</summary>
public static class UrlHelper
{
    public static string Combine(string? baseUrl, string? path)
    {
        path = (path ?? "").Trim();
        baseUrl = (baseUrl ?? "").Trim();
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;
        if (string.IsNullOrEmpty(baseUrl)) return path;
        if (string.IsNullOrEmpty(path)) return baseUrl;
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }
}

public sealed class HistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Method { get; set; } = "GET";
    public string? BaseUrl { get; set; }
    public string Url { get; set; } = "";           // the path/rest typed in the URL box
    public List<ParamRow> Params { get; set; } = new();
    public List<HeaderRow> Headers { get; set; } = new();
    public string? Body { get; set; }
    public string ContentType { get; set; } = "application/json";
    public string AuthType { get; set; } = "None";
    public string? AuthUser { get; set; }
    public string? AuthSecret { get; set; }
    public string? CertThumbprint { get; set; }
    public bool IgnoreServerCert { get; set; }
    public int TimeoutSeconds { get; set; } = 100;
    public int? StatusCode { get; set; }
    public ResponseSnapshot? Response { get; set; }
    public List<CaptureRule> Captures { get; set; } = new();

    [JsonIgnore] public string EffectiveUrl => UrlHelper.Combine(BaseUrl, Url);

    [JsonIgnore]
    public string DisplayPath
    {
        get
        {
            if (Uri.TryCreate(EffectiveUrl, UriKind.Absolute, out var u))
                return string.IsNullOrEmpty(u.PathAndQuery) ? "/" : u.PathAndQuery;
            return string.IsNullOrEmpty(Url) ? EffectiveUrl : Url;
        }
    }

    [JsonIgnore]
    public string DisplayHost =>
        Uri.TryCreate(EffectiveUrl, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}" : "";
}

/// <summary>A node in the collections tree: either a folder (with children) or a saved request.</summary>
public sealed class CollectionNode : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    private int? _lastStatusCode;
    private DateTime? _lastCheckedUtc;

    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public bool IsFolder { get; set; }
    public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
    public System.Collections.ObjectModel.ObservableCollection<CollectionNode> Children { get; set; } = new();
    public RequestModel? Request { get; set; }   // populated when this is a saved request (not a folder)

    /// <summary>Folder-level defaults: the website and client certificate a request opened from
    /// this folder inherits when it doesn't carry its own. The nearest ancestor with a value wins.</summary>
    public string? DefaultBaseUrl { get; set; }
    public string? DefaultCertThumbprint { get; set; }

    /// <summary>The status code the last send of this saved request returned (null if it failed
    /// without a response, or if it has never been sent — see <see cref="LastCheckedUtc"/>).</summary>
    public int? LastStatusCode
    {
        get => _lastStatusCode;
        set { _lastStatusCode = value; RaiseStatus(); }
    }

    /// <summary>When this saved request was last sent (UTC); null if never.</summary>
    public DateTime? LastCheckedUtc
    {
        get => _lastCheckedUtc;
        set { _lastCheckedUtc = value; RaiseStatus(); }
    }

    /// <summary>Record the outcome of sending this saved request.</summary>
    public void RecordResult(int? statusCode, DateTime utcNow)
    {
        _lastStatusCode = statusCode;
        _lastCheckedUtc = utcNow;
        RaiseStatus();
    }

    /// <summary>Known good: the last send returned a 2xx.</summary>
    [JsonIgnore] public bool IsKnownGood => LastStatusCode is >= 200 and < 300;

    /// <summary>Whether this saved request has ever been sent.</summary>
    [JsonIgnore] public bool HasResult => LastCheckedUtc is not null;

    /// <summary>Tooltip for the status dot, e.g. "Last checked 2026-07-16 14:02 — 200 (known good)".</summary>
    [JsonIgnore]
    public string? StatusSummary
    {
        get
        {
            if (IsFolder) return null;
            if (LastCheckedUtc is not { } utc) return "Never sent";
            string when = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            string outcome = LastStatusCode is { } c
                ? $"{c}{(IsKnownGood ? " (known good)" : "")}"
                : "failed (no response)";
            return $"Last checked {when} — {outcome}";
        }
    }

    private void RaiseStatus()
    {
        Raise(nameof(LastStatusCode));
        Raise(nameof(LastCheckedUtc));
        Raise(nameof(IsKnownGood));
        Raise(nameof(HasResult));
        Raise(nameof(StatusSummary));
    }

    /// <summary>Method badge shown before a saved request's name; a folder mark for folders.</summary>
    [JsonIgnore] public string MethodBadge => IsFolder ? "▸" : Request?.Method ?? "";

    /// <summary>Build the UI-free collection DTO from this node (the reverse of
    /// <see cref="FromParsed"/>), e.g. to export as an OpenAPI document. Auth values ride along
    /// in memory, but the OpenAPI exporter only writes the security scheme — never the secrets.</summary>
    public ParsedCollection ToParsed()
    {
        var pc = new ParsedCollection { Name = Name };
        if (!IsFolder)
        {
            if (ToParsedRequest() is { } self) pc.Requests.Add(self);
        }
        else
        {
            foreach (var child in Children)
            {
                if (child.IsFolder) pc.Folders.Add(child.ToParsed());
                else if (child.ToParsedRequest() is { } r) pc.Requests.Add(r);
            }
        }
        pc.BaseUrl = FirstBase(pc);
        return pc;
    }

    private ParsedRequest? ToParsedRequest()
    {
        if (Request is not { } r) return null;

        // Merge any query still in the path with the enabled rows of the Params grid.
        var (path, rawQuery) = QueryString.Split((r.Path ?? "").Trim());
        var pairs = QueryString.Parse(rawQuery);
        pairs.AddRange(r.EnabledParams());

        var req = new ParsedRequest
        {
            Method = r.Method,
            BaseUrl = string.IsNullOrWhiteSpace(r.BaseUrl) ? null : r.BaseUrl!.Trim(),
            Url = QueryString.Compose(path, pairs),
            Name = Name,
            Description = HasResult ? StatusSummary : null,
            Body = string.IsNullOrEmpty(r.Body) ? null : r.Body,
            ContentType = r.ContentType == "(none)" ? null : r.ContentType,
            InsecureSkipVerify = r.IgnoreServerCert
        };
        foreach (var h in r.Headers)
            if (h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                req.Headers.Add(new(h.Name.Trim(), h.Value ?? ""));
        switch (r.AuthType)
        {
            case "Bearer": req.BearerToken = r.AuthSecret ?? ""; break;
            case "Basic": req.BasicUser = r.AuthUser ?? ""; req.BasicPassword = r.AuthSecret; break;
        }
        return req;
    }

    private static string? FirstBase(ParsedCollection pc)
    {
        foreach (var r in pc.Requests)
            if (!string.IsNullOrWhiteSpace(r.BaseUrl)) return r.BaseUrl;
        foreach (var f in pc.Folders)
            if (FirstBase(f) is { } b) return b;
        return null;
    }

    /// <summary>Build a collections folder (and its requests) from an imported OpenAPI collection.</summary>
    public static CollectionNode FromParsed(ParsedCollection pc)
    {
        var folder = new CollectionNode { Name = pc.Name, IsFolder = true };
        foreach (var sub in pc.Folders) folder.Children.Add(FromParsed(sub));
        foreach (var req in pc.Requests)
            folder.Children.Add(new CollectionNode
            {
                Name = string.IsNullOrWhiteSpace(req.Name) ? $"{req.Method} {req.Url}" : req.Name!,
                IsFolder = false,
                Request = RequestModel.FromParsed(req)
            });
        return folder;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>A named environment holding <c>{{variable}}</c> values.</summary>
public sealed class ApiEnvironment : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
    public System.Collections.ObjectModel.ObservableCollection<Variable> Variables { get; set; } = new();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public sealed class Variable : System.ComponentModel.INotifyPropertyChanged
{
    private string _key = "";
    private string _value = "";
    public string Key { get => _key; set { _key = value; Raise(nameof(Key)); } }
    public string Value { get => _value; set { _value = value; Raise(nameof(Value)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public sealed class HeaderRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _name = "";
    private string _value = "";

    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }
    public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
    public string Value { get => _value; set { _value = value; Raise(nameof(Value)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public sealed class ParamRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _key = "";
    private string _value = "";

    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }
    public string Key { get => _key; set { _key = value; Raise(nameof(Key)); } }
    public string Value { get => _value; set { _value = value; Raise(nameof(Value)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public enum CaptureSource { Body, Header }

/// <summary>A rule that saves a value from a response into an environment variable after a send.</summary>
public sealed class CaptureRule : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _variable = "";
    private CaptureSource _source = CaptureSource.Body;
    private string _path = "";

    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }
    public string Variable { get => _variable; set { _variable = value; Raise(nameof(Variable)); } }
    public CaptureSource Source { get => _source; set { _source = value; Raise(nameof(Source)); } }
    public string Path { get => _path; set { _path = value; Raise(nameof(Path)); } }

    public CaptureSpec ToSpec() => new(Variable.Trim(), Source == CaptureSource.Header, Path.Trim());

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>A stored snapshot of the response for a history entry.</summary>
public sealed class ResponseSnapshot
{
    public int? StatusCode { get; set; }
    public string? ReasonPhrase { get; set; }
    public double ElapsedMs { get; set; }
    public string? ContentType { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public bool BodyTruncated { get; set; }
    public List<HeaderRow> Headers { get; set; } = new();
    public string? Diagnostics { get; set; }
    public string? ErrorKind { get; set; }
    public string? ErrorMessage { get; set; }
}
