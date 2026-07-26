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

    public ProxyMode Proxy { get => _proxy; set { _proxy = value; Raise(nameof(Proxy)); } }
    public string? ProxyUrl { get => _proxyUrl; set { _proxyUrl = value; Raise(nameof(ProxyUrl)); } }
    public string? ProxyUser { get => _proxyUser; set { _proxyUser = value; Raise(nameof(ProxyUser)); } }
    public string? ProxyPassword { get => _proxyPassword; set { _proxyPassword = value; Raise(nameof(ProxyPassword)); } }
    public bool FollowRedirects { get => _followRedirects; set { _followRedirects = value; Raise(nameof(FollowRedirects)); } }
    public int MaxRedirects { get => _maxRedirects; set { _maxRedirects = value; Raise(nameof(MaxRedirects)); } }
    public bool Decompress { get => _decompress; set { _decompress = value; Raise(nameof(Decompress)); } }
    public HttpVersionMode Version { get => _version; set { _version = value; Raise(nameof(Version)); } }

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
        IgnoreServerCertificateErrors = ignoreServerCertificateErrors
    };

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
        Version = options.Version
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
