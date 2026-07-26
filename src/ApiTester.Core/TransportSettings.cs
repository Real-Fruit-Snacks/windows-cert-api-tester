namespace ApiTester.Core;

/// <summary>The serializable, bindable mirror of <see cref="TransportOptions"/> that persists with a
/// saved request. Two types exist because they answer to different owners: <see cref="TransportOptions"/>
/// is an immutable record built fresh for one send, while this is the mutable,
/// <see cref="System.ComponentModel.INotifyPropertyChanged"/>-backed value the request editor binds to
/// and the workspace file stores. Every default reproduces the behavior this client had before
/// transport control existed, which is why adding it to an existing state.json changes nothing.</summary>
/// <remarks>Deliberately absent: <c>Resolve</c>, because host-to-address pinning is a command-line-only
/// diagnostic that describes one invocation rather than a saved request; and
/// <c>IgnoreServerCertificateErrors</c>, which already lives on <see cref="RequestModel.IgnoreServerCert"/>
/// and is folded in by <see cref="ToOptions"/> rather than stored twice.</remarks>
public sealed class TransportSettings : System.ComponentModel.INotifyPropertyChanged
{
    private ProxyMode _proxy = ProxyMode.System;
    private string? _proxyUrl;
    private string? _proxyUser;
    private string? _proxyPassword;
    private bool _followRedirects = true;
    private int _maxRedirects = 20;
    private bool _decompress = true;
    private HttpVersionMode _version = HttpVersionMode.Auto;
    private int _retries;
    private string _retryOn = "429,502,503,504";
    private int _retryDelayMs = 500;
    private bool _retryOnTransportError = true;
    private bool _honorRetryAfter = true;
    private bool _retryUnsafeMethods;

    public ProxyMode Proxy { get => _proxy; set { _proxy = value; Raise(nameof(Proxy)); } }
    public string? ProxyUrl { get => _proxyUrl; set { _proxyUrl = value; Raise(nameof(ProxyUrl)); } }
    public string? ProxyUser { get => _proxyUser; set { _proxyUser = value; Raise(nameof(ProxyUser)); } }
    public string? ProxyPassword { get => _proxyPassword; set { _proxyPassword = value; Raise(nameof(ProxyPassword)); } }
    public bool FollowRedirects { get => _followRedirects; set { _followRedirects = value; Raise(nameof(FollowRedirects)); } }
    public int MaxRedirects { get => _maxRedirects; set { _maxRedirects = value; Raise(nameof(MaxRedirects)); } }
    public bool Decompress { get => _decompress; set { _decompress = value; Raise(nameof(Decompress)); } }
    public HttpVersionMode Version { get => _version; set { _version = value; Raise(nameof(Version)); } }

    /// <summary>How many times to retry a failed request. 0 = off, which is the behavior this client
    /// had before retry existed.</summary>
    public int Retries { get => _retries; set { _retries = value; Raise(nameof(Retries)); } }

    /// <summary>The statuses that earn a retry, comma-separated. A string rather than a list because
    /// this is the value the request editor binds a text box to and the workspace file stores, and a
    /// text box is the honest control for "429,502,503,504".</summary>
    public string RetryOn { get => _retryOn; set { _retryOn = value; Raise(nameof(RetryOn)); } }

    /// <summary>The first backoff delay in milliseconds; each further attempt doubles it.</summary>
    public int RetryDelayMs { get => _retryDelayMs; set { _retryDelayMs = value; Raise(nameof(RetryDelayMs)); } }

    /// <summary>Retry connection refused/reset, DNS failures, and timeouts.</summary>
    public bool RetryOnTransportError { get => _retryOnTransportError; set { _retryOnTransportError = value; Raise(nameof(RetryOnTransportError)); } }

    /// <summary>Let a Retry-After header override the computed backoff.</summary>
    public bool HonorRetryAfter { get => _honorRetryAfter; set { _honorRetryAfter = value; Raise(nameof(HonorRetryAfter)); } }

    /// <summary>Also retry POST and PATCH. Off by default: re-sending a POST nobody confirmed can
    /// charge a card twice.</summary>
    public bool RetryUnsafeMethods { get => _retryUnsafeMethods; set { _retryUnsafeMethods = value; Raise(nameof(RetryUnsafeMethods)); } }

    /// <summary>Build the immutable per-send options, folding in the request's own
    /// ignore-certificate-errors switch (which lives on the request, not here).</summary>
    public TransportOptions ToOptions(bool ignoreServerCertificateErrors = false) => new()
    {
        Proxy = Proxy,
        ProxyUrl = ProxyUrl,
        ProxyUser = ProxyUser,
        ProxyPassword = ProxyPassword,
        FollowRedirects = FollowRedirects,
        MaxRedirects = MaxRedirects,
        Decompress = Decompress,
        Version = Version,
        IgnoreServerCertificateErrors = ignoreServerCertificateErrors,
        Retries = Retries,
        RetryOn = ParseRetryOn(RetryOn),
        RetryDelay = TimeSpan.FromMilliseconds(RetryDelayMs),
        RetryOnTransportError = RetryOnTransportError,
        HonorRetryAfter = HonorRetryAfter,
        RetryUnsafeMethods = RetryUnsafeMethods
    };

    /// <summary>The typed status list behind the text box. Anything that is not a number is skipped
    /// rather than fatal, but a list with nothing usable in it falls back to the defaults: an empty
    /// list would switch retry-on-status off while the user believes they configured it.</summary>
    private static IReadOnlyList<int> ParseRetryOn(string? text)
    {
        var codes = (text ?? "")
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, out var code) ? code : (int?)null)
            .Where(code => code is not null)
            .Select(code => code!.Value)
            .ToArray();
        return codes.Length > 0 ? codes : new TransportOptions().RetryOn;
    }

    /// <summary>The reverse of <see cref="ToOptions"/>, for tests and for seeding the editor from a
    /// set of options. The options' certificate and resolve fields have no home here by design.</summary>
    public static TransportSettings From(TransportOptions options) => new()
    {
        Proxy = options.Proxy,
        ProxyUrl = options.ProxyUrl,
        ProxyUser = options.ProxyUser,
        ProxyPassword = options.ProxyPassword,
        FollowRedirects = options.FollowRedirects,
        MaxRedirects = options.MaxRedirects,
        Decompress = options.Decompress,
        Version = options.Version,
        Retries = options.Retries,
        RetryOn = string.Join(",", options.RetryOn),
        RetryDelayMs = (int)options.RetryDelay.TotalMilliseconds,
        RetryOnTransportError = options.RetryOnTransportError,
        HonorRetryAfter = options.HonorRetryAfter,
        RetryUnsafeMethods = options.RetryUnsafeMethods
    };

    /// <summary>Copy another instance's values into this one — used when a history entry is loaded
    /// into an open request so existing bindings keep pointing at the same object.</summary>
    public void CopyFrom(TransportSettings other)
    {
        Proxy = other.Proxy;
        ProxyUrl = other.ProxyUrl;
        ProxyUser = other.ProxyUser;
        ProxyPassword = other.ProxyPassword;
        FollowRedirects = other.FollowRedirects;
        MaxRedirects = other.MaxRedirects;
        Decompress = other.Decompress;
        Version = other.Version;
        Retries = other.Retries;
        RetryOn = other.RetryOn;
        RetryDelayMs = other.RetryDelayMs;
        RetryOnTransportError = other.RetryOnTransportError;
        HonorRetryAfter = other.HonorRetryAfter;
        RetryUnsafeMethods = other.RetryUnsafeMethods;
    }

    /// <summary>An independent copy, so a stored history entry cannot be mutated by later editing.</summary>
    public TransportSettings Clone()
    {
        var copy = new TransportSettings();
        copy.CopyFrom(this);
        return copy;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}
