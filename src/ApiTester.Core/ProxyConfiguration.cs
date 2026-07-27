using System.Net;
using System.Net.Http;

namespace ApiTester.Core;

/// <summary>The one place a <see cref="SocketsHttpHandler"/> learns how to reach a server: the
/// three-way <see cref="ProxyMode"/>, narrowed by <see cref="TransportOptions.NoProxy"/> when there
/// is a bypass list. <see cref="ApiClient"/>'s direct and proxied paths both call this, and a later
/// slice points <c>MtlsGateway</c> and <c>GrpcChannelFactory</c> at it too, so the bypass cannot end
/// up applying on one path and not another.</summary>
public static class ProxyConfiguration
{
    /// <summary>Configure how this handler reaches a server: the three-way proxy mode, narrowed by
    /// the bypass list when there is one.</summary>
    public static void Apply(SocketsHttpHandler handler, TransportOptions transport)
    {
        switch (transport.Proxy)
        {
            case ProxyMode.None:
                // Bypassing the proxy also restores the ConnectCallback path, which is the only
                // place the TLS details can be read. A bypass list is meaningless here — that
                // combination is refused by ValidateTransport before a send ever gets this far —
                // so nothing about NoProxy is consulted.
                handler.UseProxy = false;
                break;

            case ProxyMode.Explicit:
            {
                var explicitProxy = new WebProxy(transport.ProxyUrl)
                {
                    Credentials = transport.ProxyUser is null
                        ? CredentialCache.DefaultCredentials
                        : new NetworkCredential(transport.ProxyUser, transport.ProxyPassword)
                };
                // Only wrapped when there is something to narrow: an empty list has nothing to add
                // over the plain WebProxy this handler has always been given.
                handler.Proxy = transport.NoProxy.Count > 0
                    ? new BypassingProxy(explicitProxy, transport.NoProxy)
                    : explicitProxy;
                handler.UseProxy = true;
                break;
            }

            case ProxyMode.System:
                // With an empty bypass list this must change nothing at all: handler.Proxy stays
                // unset, exactly as before a bypass list existed, so the handler keeps consulting
                // HttpClient.DefaultProxy — and DefaultProxyCredentials above — entirely on its own.
                // Only a non-empty list is worth installing a decorator for.
                if (transport.NoProxy.Count > 0)
                {
                    handler.Proxy = new BypassingProxy(HttpClient.DefaultProxy, transport.NoProxy);
                    handler.UseProxy = true;
                }
                break;
        }
    }

    /// <summary>Narrows an inner <see cref="IWebProxy"/> by a NO_PROXY-style bypass list: a
    /// destination the list matches is answered "go direct" (null), and everything else is forwarded
    /// to the inner proxy unchanged. The inner proxy may be null — <see cref="HttpClient.DefaultProxy"/>
    /// is not documented to be non-null in every hosting configuration — in which case this decorator
    /// behaves as "no proxy, narrowed by the list", which for every destination not in the list is
    /// simply "no proxy".</summary>
    internal sealed class BypassingProxy : IWebProxy
    {
        private readonly IWebProxy? _inner;
        private readonly IReadOnlyList<ProxyBypassRule> _rules;

        public BypassingProxy(IWebProxy? inner, IReadOnlyList<ProxyBypassRule> rules)
        {
            _inner = inner;
            _rules = rules;
        }

        public ICredentials? Credentials
        {
            get => _inner?.Credentials;
            set
            {
                if (_inner is not null) _inner.Credentials = value;
            }
        }

        public Uri? GetProxy(Uri destination) =>
            ProxyBypass.IsBypassed(_rules, destination) ? null : _inner?.GetProxy(destination);

        public bool IsBypassed(Uri host) =>
            ProxyBypass.IsBypassed(_rules, host) || (_inner?.IsBypassed(host) ?? false);
    }
}
