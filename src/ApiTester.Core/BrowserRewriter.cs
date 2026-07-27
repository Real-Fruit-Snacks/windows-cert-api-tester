namespace ApiTester.Core;

/// <summary>A preflight answered by the gateway itself — never forwarded upstream.</summary>
public sealed record PreflightResponse(
    int StatusCode,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>Which browser accommodations the gateway applies. The default relay is byte-faithful,
/// so every switch here is off unless the caller asked for it.</summary>
public sealed record BrowserOptions(
    bool Cors,
    IReadOnlyList<string>? AllowedOrigins,   // null = echo the request Origin
    bool RewriteCookies,
    bool RewriteLocation,
    bool AllowUpgrade,
    Uri LocalOrigin)
{
    /// <summary>The gateway's own origin is https (serve --tls), so a cookie's Secure attribute and
    /// SameSite=None are valid as the upstream sent them and a __Host-/__Secure- prefix works.
    /// False — today's plaintext loopback behavior — unless the caller asks for it.</summary>
    public bool SecureOrigin { get; init; }

    /// <summary>How long a browser may cache a preflight answer, in seconds — `serve --cors-max-age`.
    /// 600 unless the caller asked otherwise, which is what Access-Control-Max-Age has always been, so
    /// an existing user's gateway answers exactly as it did before.</summary>
    public int CorsMaxAgeSeconds { get; init; } = 600;
}

/// <summary>The policy seam that makes an upstream usable from a browser through the gateway:
/// CORS, cookie attributes and redirect targets. Pure functions over header lists with no sockets,
/// so the fiddly parts are testable on their own and <see cref="MtlsGateway"/> stays a faithful
/// byte relay.</summary>
public static class BrowserRewriter
{
    /// <summary>The CORS-safelisted response headers, which a script may already read.</summary>
    private static readonly HashSet<string> Safelisted = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache-Control", "Content-Language", "Content-Length", "Content-Type",
        "Expires", "Last-Modified", "Pragma"
    };

    /// <summary>The preflight answer, or null when this request is not a CORS preflight and must be
    /// forwarded upstream as usual.</summary>
    public static PreflightResponse? TryPreflight(GatewayRequest request, BrowserOptions options)
    {
        if (!options.Cors) return null;
        // A preflight is an OPTIONS that carries both CORS request headers. A plain OPTIONS — an
        // API advertising its allowed methods, say — is a real request and must reach the upstream.
        if (!request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase)) return null;
        if (Header(request.Headers, "Origin") is not { } origin) return null;
        if (Header(request.Headers, "Access-Control-Request-Method") is not { } method) return null;

        // Refused outright rather than forwarded: an origin the operator did not list has no
        // business reaching an upstream through someone else's client certificate, and a silent
        // forward would leave the browser to discover that from a missing response header.
        if (!IsAllowed(origin, options))
            return new PreflightResponse(403, Array.Empty<KeyValuePair<string, string>>());

        var headers = new List<KeyValuePair<string, string>>
        {
            // The exact origin, never "*": a wildcard is not valid alongside
            // Access-Control-Allow-Credentials: true, which the gateway always sends because the
            // whole point of the relay is carrying the caller's credentials.
            new("Access-Control-Allow-Origin", origin),
            new("Access-Control-Allow-Methods", method)
        };
        // Echoed only when asked for: an empty Access-Control-Allow-Headers is not the same as no
        // header at all, and the browser only needs an answer to a question it posed.
        if (Header(request.Headers, "Access-Control-Request-Headers") is { } requested)
            headers.Add(new("Access-Control-Allow-Headers", requested));
        headers.Add(new("Access-Control-Allow-Credentials", "true"));
        // Only ever the answer to a question an allowed origin actually asked: the allowlist check
        // above (or echo mode's implicit "any origin is allowed") has already run by this point, so
        // an origin the operator did not permit can never reach this line and receive it. Private
        // Network Access exists precisely to stop an arbitrary public page reaching a private or
        // loopback service; without that ordering this header would hand back the very permission
        // the feature is meant to withhold.
        if (Header(request.Headers, "Access-Control-Request-Private-Network") is { } pna &&
            pna.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            headers.Add(new("Access-Control-Allow-Private-Network", "true"));
        headers.Add(new("Access-Control-Max-Age",
            options.CorsMaxAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        headers.Add(new("Vary", "Origin"));
        return new PreflightResponse(204, headers);
    }

    /// <summary>The upstream response headers as the browser should see them, with any
    /// <paramref name="warnings"/> the caller ought to log. Never mutates the input list.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> RewriteResponseHeaders(
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string? requestOrigin,
        GatewayRoute route,
        BrowserOptions options,
        out IReadOnlyList<string> warnings)
    {
        var notes = new List<string>();
        var result = new List<KeyValuePair<string, string>>(headers.Count);
        foreach (var h in headers)
        {
            // Every upstream Access-Control-* header goes, whatever it said: an upstream that
            // already answers CORS for its own origin would otherwise leave the browser holding two
            // Access-Control-Allow-Origin values, which it rejects outright.
            if (options.Cors && h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
                continue;
            if (options.RewriteCookies && h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                // Rewritten in place, so several Set-Cookie headers stay several headers in the
                // order the upstream set them.
                result.Add(new(h.Key, RewriteSetCookie(h.Value, options, notes)));
                continue;
            }
            if (options.RewriteLocation && h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new(h.Key, RewriteLocation(h.Value, route, options, notes)));
                continue;
            }
            result.Add(h);
        }

        if (options.Cors && requestOrigin is not null)
        {
            if (!IsAllowed(requestOrigin, options))
                notes.Add($"Origin '{requestOrigin}' is not in the allowed list; no CORS headers " +
                          "were added and the browser will refuse to read this response.");
            else
            {
                // Computed over the upstream's own headers, before the gateway adds its own — the
                // browser needs to be told about the response it is reading, not about this policy.
                string exposed = ExposedNames(result);
                // The exact origin rather than "*", for the same reason as on a preflight: a
                // wildcard is not valid alongside Allow-Credentials, and a relay carries credentials.
                result.Add(new("Access-Control-Allow-Origin", requestOrigin));
                result.Add(new("Access-Control-Allow-Credentials", "true"));
                result.Add(new("Vary", "Origin"));
                if (exposed.Length > 0) result.Add(new("Access-Control-Expose-Headers", exposed));
            }
        }

        warnings = notes;
        return result;
    }

    /// <summary>A Location that keeps the browser on the gateway. Applied to any Location handed
    /// over: the status code is not part of this seam, and a Location outside a 3xx is not something
    /// to second-guess.</summary>
    private static string RewriteLocation(
        string location, GatewayRoute route, BrowserOptions options, List<string> notes)
    {
        // A protocol-relative Location — "//host/path" — borrows only the scheme and brings an
        // authority of its own, so the browser resolves it into a destination off the gateway just
        // as an absolute URI would. Giving it the upstream's scheme lets it be judged as one:
        // written that way, this route's own upstream is the same destination and deserves the same
        // rewrite rather than a warning about a departure that is not happening. It is spelled back
        // out untouched in either case, so the browser sees exactly what the upstream wrote.
        string absolute = location.StartsWith("//", StringComparison.Ordinal)
            ? route.Upstream.Scheme + ":" + location
            : location;

        // A path-relative Location — "/next", "next", "../up" — is the only form that resolves
        // against the gateway's own origin, so it is the only one with nothing to rewrite and
        // nothing to warn about.
        if (!Uri.TryCreate(absolute, UriKind.Absolute, out var target)) return location;

        // Anywhere but this route's upstream is left as the upstream wrote it, and said out loud:
        // the browser is about to leave the gateway, and the client certificate with it.
        if (!target.GetLeftPart(UriPartial.Authority).Equals(
                route.Upstream.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"Redirect to '{location}' leaves the gateway; the browser will follow it " +
                      "directly and without the client certificate.");
            return location;
        }

        // Back through the mount point the upstream is reached by, so the rewritten target routes
        // to the same upstream on the next request. The root prefix mounts everything, and adding
        // it would only double the path's leading slash.
        string prefix = route.PathPrefix == "/" ? "" : route.PathPrefix;
        return options.LocalOrigin.GetLeftPart(UriPartial.Authority) + prefix + target.PathAndQuery;
    }

    /// <summary>One Set-Cookie value as the browser can actually store it. Over the default
    /// plaintext loopback origin, Secure and SameSite=None cannot survive as the upstream wrote
    /// them; over the gateway's own https origin (<see cref="BrowserOptions.SecureOrigin"/>, set by
    /// `serve --tls`) they are exactly what the upstream sent, so they are kept.</summary>
    private static string RewriteSetCookie(string setCookie, BrowserOptions options, List<string> notes)
    {
        // Attributes are separated by ';' and no attribute value may contain one — an Expires date
        // carries a comma, not a semicolon — so this split is safe.
        var parts = setCookie.Split(';');
        // The cookie's value may itself contain '=', so the name/value pair is passed through whole
        // rather than re-joined from pieces.
        string pair = parts[0].Trim();

        var kept = new List<string>(parts.Length) { pair };
        for (int i = 1; i < parts.Length; i++)
        {
            string attribute = parts[i].Trim();
            if (attribute.Length == 0) continue;
            // An attribute is either a bare flag (Secure, HttpOnly) or name=value; the name is
            // matched case-insensitively, as the cookie specification requires.
            int eq = attribute.IndexOf('=');
            string attributeName = (eq < 0 ? attribute : attribute[..eq]).Trim();
            string attributeValue = eq < 0 ? "" : attribute[(eq + 1)..].Trim();

            // Domain= scopes the cookie to the upstream host, which is not the host the browser is
            // talking to; without it the cookie scopes to the gateway itself. True regardless of
            // scheme — the browser is still talking to a different host than the upstream.
            if (attributeName.Equals("Domain", StringComparison.OrdinalIgnoreCase)) continue;
            if (attributeName.Equals("Secure", StringComparison.OrdinalIgnoreCase))
            {
                // Secure over http://127.0.0.1 means the browser stores nothing at all, so it is
                // dropped there; over the gateway's own https origin (serve --tls) it is exactly
                // what the upstream sent, so it survives.
                if (options.SecureOrigin) kept.Add(attribute);
                continue;
            }
            if (attributeName.Equals("SameSite", StringComparison.OrdinalIgnoreCase) &&
                attributeValue.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                // SameSite=None is only legal alongside Secure. Over a plaintext origin Secure has
                // just gone above, so the browser would reject the cookie entirely and Lax is the
                // nearest thing that still works; over an https origin Secure survived above, so
                // None is kept exactly as the upstream wrote it.
                kept.Add(options.SecureOrigin ? "SameSite=None" : "SameSite=Lax");
                continue;
            }
            kept.Add(attribute);
        }

        // A __Host-/__Secure- cookie requires the Secure attribute by specification, so over a
        // plaintext loopback origin it cannot work however it is rewritten — it is still emitted and
        // said out loud rather than dropped behind the operator's back, and `serve --tls` is the
        // honest fix. Over the gateway's own https origin Secure survived above, so the prefix works
        // and there is nothing to warn about. The prefixes are case-sensitive, per the cookie prefix
        // specification.
        string name = NameOf(pair);
        if (!options.SecureOrigin &&
            (name.StartsWith("__Host-", StringComparison.Ordinal) ||
             name.StartsWith("__Secure-", StringComparison.Ordinal)))
            notes.Add($"Cookie '{name}' uses a prefix that requires the Secure attribute, so the " +
                      "browser will reject it over the gateway's plaintext loopback origin.");

        return string.Join("; ", kept);
    }

    /// <summary>The cookie's name: everything before the first '=', because the value after it may
    /// contain more of them.</summary>
    private static string NameOf(string pair)
    {
        int eq = pair.IndexOf('=');
        return (eq < 0 ? pair : pair[..eq]).Trim();
    }

    /// <summary>The response header names a script cannot read without being told about them:
    /// everything outside the CORS-safelisted set, in the order the upstream sent them and one
    /// entry per name however often it repeats. Empty when there is nothing to expose.</summary>
    private static string ExposedNames(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
            if (!Safelisted.Contains(h.Key) && seen.Add(h.Key))
                names.Add(h.Key);
        return string.Join(", ", names);
    }

    /// <summary>Whether the gateway will speak CORS for this origin. A null allowlist means "echo
    /// whatever asked", which is the loopback-only default; an explicit list is an exact,
    /// case-insensitive match on the whole origin string.</summary>
    private static bool IsAllowed(string origin, BrowserOptions options) =>
        options.AllowedOrigins is null ||
        options.AllowedOrigins.Any(o => o.Equals(origin, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first value of a header, or null when the request did not send it. Header names
    /// are case-insensitive, and the request list may repeat a key.</summary>
    private static string? Header(IReadOnlyList<KeyValuePair<string, string>> headers, string name)
    {
        foreach (var h in headers)
            if (h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return h.Value;
        return null;
    }
}
