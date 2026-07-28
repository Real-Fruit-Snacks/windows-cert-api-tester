using System.Diagnostics.Tracing;
using System.Text;

namespace ApiTester.Core;

/// <summary>One connection this process opened, and what has been sent over it.</summary>
/// <param name="Id">The runtime's own connection identifier, unique within the process — verified
/// by observing two origins receive 0 and 1 rather than 0 and 0.</param>
public sealed record ConnectionRecord(long Id, string Scheme, string Host, int Port,
                                      TimeSpan EstablishedAt, string Version, string RemoteAddress)
{
    /// <summary>Requests whose headers went out on this connection. One is the first request; more
    /// than one is proof of reuse, which is the question this whole report exists to answer.</summary>
    public int Requests { get; internal set; }

    public TimeSpan LastUsedAt { get; internal set; }

    public string Origin => $"{Scheme}://{Host}:{Port}";
}

/// <summary>Answers "which connection served this request, and was it reused?" — the question this
/// product's own test suite has had to guess at more than once.
///
/// <para>It reads the runtime's own <c>System.Net.Http</c> event source rather than reaching into
/// the connection pool, so it needs no driver, no administrator rights and no private API. Two
/// events carry the whole answer, both confirmed by probe rather than assumed:
/// <c>ConnectionEstablished</c> (identifier, origin, protocol version, remote address) and
/// <c>RequestHeadersStart</c>, which carries the <c>connectionId</c> its request went out on. A
/// request that reuses a pooled connection emits the second without the first — that absence is
/// the reuse signal.</para>
///
/// <para><b>Two limits, stated because the report would otherwise imply otherwise.</b> First, the
/// runtime emits no connection-closed event that this listener can see — a server dropping the
/// connection produces nothing — so what is reported is every connection *observed since this
/// inspector started*, not a live count of open sockets. Second, it is process-wide: while it is
/// running it sees every connection this process makes, including a `mock` or `serve` server's own
/// client connections if one is running in the same process.</para></summary>
public sealed class ConnectionInspector : EventListener
{
    private const string Source = "System.Net.Http";

    private readonly object _gate = new();
    private readonly Dictionary<long, ConnectionRecord> _connections = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private bool _ready;

    public ConnectionInspector()
    {
        // As in NetworkTrace: the base constructor's OnEventSourceCreated callbacks run before this
        // body, so they can decide nothing. Sweeping here is what actually subscribes.
        _ready = true;
        foreach (var source in EventSource.GetSources()) EnableIfWanted(source);
    }

    /// <summary>Every connection observed since this inspector started, oldest first.</summary>
    public IReadOnlyList<ConnectionRecord> Connections
    {
        get { lock (_gate) return _connections.Values.OrderBy(c => c.EstablishedAt).ToArray(); }
    }

    /// <summary>Whether the given connection was opened while this inspector was running. False
    /// means the request reused a connection that already existed — which is the good outcome, and
    /// is why this is reported rather than inferred from timing.</summary>
    public bool WasEstablishedHere(long connectionId)
    {
        lock (_gate) return _connections.ContainsKey(connectionId);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (!_ready) return;      // still inside the base constructor; the sweep above covers these
        EnableIfWanted(eventSource);
    }

    private void EnableIfWanted(EventSource source)
    {
        if (source.Name == Source) EnableEvents(source, EventLevel.Informational, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource?.Name != Source || eventData.Payload is null) return;

        switch (eventData.EventName)
        {
            case "ConnectionEstablished":
            {
                long id = Payload<long>(eventData, "connectionId", -1);
                if (id < 0) return;
                var record = new ConnectionRecord(
                    id,
                    Payload(eventData, "scheme", "?"),
                    Payload(eventData, "host", "?"),
                    Payload<int>(eventData, "port", 0),
                    _clock.Elapsed,
                    $"{Payload<int>(eventData, "versionMajor", 0)}.{Payload<int>(eventData, "versionMinor", 0)}",
                    Payload(eventData, "remoteAddress", ""));
                lock (_gate) _connections[id] = record;
                break;
            }

            case "RequestHeadersStart":
            {
                long id = Payload<long>(eventData, "connectionId", -1);
                if (id < 0) return;
                lock (_gate)
                {
                    if (_connections.TryGetValue(id, out var record))
                    {
                        record.Requests++;
                        record.LastUsedAt = _clock.Elapsed;
                    }
                    // A connection established before this inspector started has no record here.
                    // Counting it under a fabricated record would misreport its age and origin, so
                    // it is deliberately left out and reported as "not opened during this run".
                }
                break;
            }
        }
    }

    private static string Payload(EventWrittenEventArgs eventData, string name, string fallback)
    {
        int index = eventData.PayloadNames?.IndexOf(name) ?? -1;
        return index >= 0 ? eventData.Payload![index]?.ToString() ?? fallback : fallback;
    }

    private static T Payload<T>(EventWrittenEventArgs eventData, string name, T fallback)
        where T : struct
    {
        int index = eventData.PayloadNames?.IndexOf(name) ?? -1;
        if (index < 0 || eventData.Payload![index] is not { } value) return fallback;
        try { return (T)Convert.ChangeType(value, typeof(T)); }
        catch { return fallback; }
    }

    /// <summary>The report: one line per connection, then the verdict that people actually came
    /// for. Pure formatting over the records, so it is testable without a socket.</summary>
    public string Render() => Render(Connections);

    /// <summary>The report for one origin only — <c>scheme://host:port</c>, matching
    /// <see cref="ConnectionRecord.Origin"/>.
    /// <para>This is what a command that was pointed at a URL should print. The listener is
    /// process-wide by nature, so anything else the process happened to connect to would otherwise
    /// land in the middle of the answer; narrowing to the origin under test removes that noise
    /// without hiding it, since connections to other origins are still counted in a closing
    /// line.</para></summary>
    public string Render(string origin)
    {
        var all = Connections;
        var mine = all.Where(c => string.Equals(c.Origin, origin, StringComparison.OrdinalIgnoreCase)).ToArray();
        string report = Render(mine);

        int others = all.Count - mine.Length;
        if (others > 0)
            report += $"({others} connection(s) to other origins were open in this process and are "
                    + "not counted above.)\n";
        return report;
    }

    /// <summary>The origin key for a URL, in the same shape <see cref="ConnectionRecord.Origin"/>
    /// uses. <see cref="Uri.Port"/> supplies the scheme's default when none was typed, so
    /// <c>https://x/</c> and <c>https://x:443/</c> agree — as they must, since the runtime always
    /// reports the real port.</summary>
    public static string OriginOf(Uri url) => $"{url.Scheme}://{url.Host}:{url.Port}";

    /// <summary>The same report over a caller-chosen set of records — for a command that has
    /// already narrowed them, so the narrowing happens once rather than twice.</summary>
    public static string Render(IReadOnlyList<ConnectionRecord> connections)
    {
        if (connections.Count == 0)
            return "No connections were opened during this run — every request reused one that "
                 + "already existed, or none was sent.\n";

        var sb = new StringBuilder();
        foreach (var c in connections)
        {
            sb.Append($"connection {c.Id}  {c.Origin}  HTTP/{c.Version}")
              .Append($"  opened at {c.EstablishedAt.TotalMilliseconds:F1} ms")
              .Append($"  requests {c.Requests}");
            if (c.Requests > 0)
                sb.Append($"  last used at {c.LastUsedAt.TotalMilliseconds:F1} ms");
            if (c.RemoteAddress.Length > 0) sb.Append($"  peer {c.RemoteAddress}");
            sb.AppendLine();
        }

        int requests = connections.Sum(c => c.Requests);
        var origins = connections.Select(c => c.Origin).Distinct().Count();
        sb.AppendLine();
        sb.AppendLine($"{requests} request(s) over {connections.Count} connection(s) to {origins} origin(s).");

        // The verdict, because the number above is only useful if you know what it should be.
        if (connections.Count == 0 || requests == 0) { }
        else if (requests > connections.Count)
            sb.AppendLine("Connections are being reused, which is what you want.");
        else if (requests == connections.Count && requests > 1)
            sb.AppendLine("Every request opened its own connection — nothing is being reused. "
                        + "A server sending 'Connection: close', a proxy in the way, or a new "
                        + "client per request will each do that.");
        return sb.ToString();
    }
}
