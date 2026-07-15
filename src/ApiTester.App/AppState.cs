using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiTester.App;

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

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CertApiTester");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "state.json");
        }
    }

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) ?? new AppState();
        }
        catch { /* corrupt or unreadable — start fresh */ }
        return new AppState();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
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
