using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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

    public List<HistoryEntry> History { get; set; } = new();

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

public sealed class HistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public List<HeaderRow> Headers { get; set; } = new();
    public string? Body { get; set; }
    public string ContentType { get; set; } = "application/json";
    public string AuthType { get; set; } = "None";
    public string? AuthUser { get; set; }
    public string? AuthSecret { get; set; }
    public string? CertThumbprint { get; set; }
    public int? StatusCode { get; set; }

    public string Display => $"{Method}  {Url}";
}

public sealed class HeaderRow
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}
