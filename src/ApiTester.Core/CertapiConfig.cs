using System.Text.Json;

namespace ApiTester.Core;

/// <summary>One named set of default option values. Every field is optional: a profile supplies
/// only what it wants to, and a command reads only the fields it understands — so a profile with a
/// proxy in it never breaks `certapi certs`.</summary>
public sealed record ConfigProfile
{
    public string? Cert { get; init; }
    public string? Store { get; init; }
    public string? CertFile { get; init; }
    public string? CertPassword { get; init; }
    public string? KeyFile { get; init; }

    public string? Proxy { get; init; }
    public string? ProxyUser { get; init; }
    public bool? NoProxy { get; init; }
    public string? NoProxyList { get; init; }

    public string? Revocation { get; init; }
    public bool? RevocationStrict { get; init; }

    public int? Retry { get; init; }
    public int? Timeout { get; init; }
    public bool? Insecure { get; init; }
    public string? Workspace { get; init; }

    /// <summary>Headers added to every request a command sends, unless the command line sets the
    /// same name — the one place a profile contributes something a flag cannot simply replace.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();
}

/// <summary>Where a configuration file was found, and by which rule — reported by
/// `certapi config path` so a surprising default can be traced to the file that set it.</summary>
public sealed record ConfigSource(string Path, string Rule);

/// <summary>A parsed configuration file: named profiles plus the one used when none is asked for.</summary>
public sealed record CertapiConfig(
    string? DefaultProfile,
    IReadOnlyDictionary<string, ConfigProfile> Profiles,
    ConfigSource? Source)
{
    public static CertapiConfig Empty { get; } =
        new(null, new Dictionary<string, ConfigProfile>(StringComparer.OrdinalIgnoreCase), null);

    /// <summary>The profile a command should use: the one named, else the file's default, else
    /// none. Throws <see cref="ConfigException"/> when a name is asked for and is not there —
    /// silently running with no profile would be worse than stopping, because the whole point is
    /// that the profile carries the identity.</summary>
    public ConfigProfile? Resolve(string? requested)
    {
        string? name = requested ?? DefaultProfile;
        if (name is null) return null;
        if (Profiles.TryGetValue(name, out var profile)) return profile;

        string known = Profiles.Count == 0 ? "(none)" : string.Join(", ", Profiles.Keys);
        throw new ConfigException(
            requested is null
                ? $"the configuration file names '{name}' as its default profile, but no such profile is defined. Profiles: {known}."
                : $"no profile named '{name}'. Profiles: {known}.");
    }
}

/// <summary>A configuration file that cannot be used as written — bad JSON, an unknown profile, or
/// an unresolvable <c>${env:…}</c>. Always names the file and what is wrong with it.</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>Finds, reads, and expands the configuration file. Pure with respect to the machine:
/// discovery and the environment are both injectable, so every rule is testable without touching
/// the real filesystem or the real environment.</summary>
public static class ConfigLoader
{
    public const string FileName = "certapi.config.json";
    public const string EnvironmentVariable = "CERTAPI_CONFIG";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        // These files are edited by people: a trailing comma and a // note are ordinary, and
        // refusing them would be pedantry rather than safety.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>The file to read, by the documented precedence: an explicit path, then
    /// <c>CERTAPI_CONFIG</c>, then <c>certapi.config.json</c> found by walking up from
    /// <paramref name="startDirectory"/>, then the per-user file. Null when nothing is found.</summary>
    public static ConfigSource? Discover(
        string? explicitPath,
        string startDirectory,
        string? userConfigPath,
        Func<string, string?>? environment = null,
        Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        var env = environment ?? Environment.GetEnvironmentVariable;

        if (explicitPath is { Length: > 0 })
        {
            // An explicit path that is not there is an error, not a silent fall-through to a
            // different file: the user named this one.
            if (!exists(explicitPath)) throw new ConfigException($"configuration file not found: {explicitPath}");
            return new ConfigSource(explicitPath, "--config");
        }

        if (env(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            if (!exists(fromEnv)) throw new ConfigException($"{EnvironmentVariable} names a file that does not exist: {fromEnv}");
            return new ConfigSource(fromEnv, EnvironmentVariable);
        }

        // Walking up finds the project's own file from anywhere inside it, which is what makes a
        // per-repository configuration work without every command being run from the root.
        var directory = startDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, FileName);
            if (exists(candidate)) return new ConfigSource(candidate, FileName + " (found by walking up)");
            var parent = Path.GetDirectoryName(directory);
            if (parent == directory) break;
            directory = parent ?? "";
        }

        if (userConfigPath is { Length: > 0 } && exists(userConfigPath))
            return new ConfigSource(userConfigPath, "per-user configuration");

        return null;
    }

    /// <summary>The conventional per-user location, or null on a platform without one.</summary>
    public static string? DefaultUserConfigPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "certapi", "config.json");
    }

    /// <summary>Parse a configuration document. <paramref name="environment"/> resolves
    /// <c>${env:NAME}</c> inside values — injectable so a test never mutates the process's own.</summary>
    public static CertapiConfig Parse(string json, ConfigSource? source = null, Func<string, string?>? environment = null)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }); }
        catch (JsonException ex)
        {
            throw new ConfigException($"{source?.Path ?? "the configuration"} is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ConfigException($"{source?.Path ?? "the configuration"} must contain a JSON object.");

            string? defaultProfile = root.TryGetProperty("defaultProfile", out var dp) && dp.ValueKind == JsonValueKind.String
                ? dp.GetString() : null;

            var profiles = new Dictionary<string, ConfigProfile>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("profiles", out var profilesElement) &&
                profilesElement.ValueKind == JsonValueKind.Object)
                foreach (var property in profilesElement.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.Object)
                        profiles[property.Name] = ReadProfile(property.Value, property.Name, source, environment);

            return new CertapiConfig(defaultProfile, profiles, source);
        }
    }

    private static ConfigProfile ReadProfile(
        JsonElement element, string profileName, ConfigSource? source, Func<string, string?>? environment)
    {
        string? Str(string name) =>
            element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? Expand(v.GetString(), name, profileName, source, environment) : null;

        bool? Bool(string name) =>
            element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;

        int? Int(string name) =>
            element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                ? n : null;

        var headers = new List<KeyValuePair<string, string>>();
        if (element.TryGetProperty("headers", out var headersElement) &&
            headersElement.ValueKind == JsonValueKind.Object)
            foreach (var header in headersElement.EnumerateObject())
                if (header.Value.ValueKind == JsonValueKind.String)
                    headers.Add(new(header.Name,
                        Expand(header.Value.GetString(), "headers." + header.Name, profileName, source, environment) ?? ""));

        return new ConfigProfile
        {
            Cert = Str("cert"),
            Store = Str("store"),
            CertFile = Str("certFile"),
            CertPassword = Str("certPassword"),
            KeyFile = Str("keyFile"),
            Proxy = Str("proxy"),
            ProxyUser = Str("proxyUser"),
            NoProxy = Bool("noProxy"),
            NoProxyList = Str("noProxyList"),
            Revocation = Str("revocation"),
            RevocationStrict = Bool("revocationStrict"),
            Retry = Int("retry"),
            Timeout = Int("timeout"),
            Insecure = Bool("insecure"),
            Workspace = Str("workspace"),
            Headers = headers
        };
    }

    /// <summary>Expand <c>${env:NAME}</c> references so a secret can live in the environment rather
    /// than in a file that gets committed. A reference to a variable that is not set is an error
    /// naming both the field and the variable — silently substituting nothing would send an empty
    /// credential, which fails later and further away.</summary>
    internal static string? Expand(
        string? value, string field, string profileName, ConfigSource? source, Func<string, string?>? environment)
    {
        if (value is null || !value.Contains("${env:", StringComparison.Ordinal)) return value;
        var lookup = environment ?? Environment.GetEnvironmentVariable;

        var result = new System.Text.StringBuilder(value.Length);
        int index = 0;
        while (index < value.Length)
        {
            int start = value.IndexOf("${env:", index, StringComparison.Ordinal);
            if (start < 0) { result.Append(value, index, value.Length - index); break; }
            int end = value.IndexOf('}', start);
            if (end < 0) { result.Append(value, index, value.Length - index); break; }

            result.Append(value, index, start - index);
            string name = value[(start + 6)..end].Trim();
            string? resolved = name.Length == 0 ? null : lookup(name);
            if (resolved is null)
                throw new ConfigException(
                    $"profile '{profileName}' in {source?.Path ?? "the configuration"} needs the environment variable " +
                    $"'{name}' for '{field}', and it is not set.");
            result.Append(resolved);
            index = end + 1;
        }
        return result.ToString();
    }

    /// <summary>Whether a value carries an <c>${env:…}</c> reference — used by
    /// `certapi config show` to report a reference as resolved-or-missing without ever printing
    /// the secret it resolves to.</summary>
    public static bool HasEnvironmentReference(string? value) =>
        value is not null && value.Contains("${env:", StringComparison.Ordinal);
}
