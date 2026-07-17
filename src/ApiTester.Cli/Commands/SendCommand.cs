using System.Net.Http;
using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class SendCommand
{
    public const string Help = """
        Usage: certapi send <url> [options]

        Request:
          -X, --method <m>        HTTP method (default GET)
          -H, --header "k: v"     Add a header (repeatable)
          -d, --data <body>       Request body ( --data-file <file> reads it from disk )
          --content-type <ct>     Body content type (default application/json)
          --bearer <token>        Authorization: Bearer …
          --basic <user:pass>     Authorization: Basic …
          --timeout <seconds>     Default 100

        TLS / certificates:
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --insecure              Ignore server certificate errors

        Variables:
          --env <name>            Environment ({{var}} values) from your workspace
          --var k=v               Override/add a variable (repeatable)
          --workspace <file>      Load environments from a workspace file instead of the live state
          --strict-vars           Unresolved {{tokens}} become an error instead of a warning

        Output:
          -o, --output <file>     Write the body to a file instead of stdout
          --include               Print status line and headers before the body
          --pretty                Pretty-print the body (JSON/XML; hex for binary)
          --json                  Print a JSON result envelope instead of the raw body
          --fail                  Exit 1 when the HTTP status is 400 or higher
          -q, --quiet             No metadata line on stderr

        The body goes to stdout; everything else goes to stderr. Exit 0 on a delivered
        response (any status unless --fail), 1 on transport errors, 2/3 on usage/data errors.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, Stream bodyOut, CliServices services)
    {
        // ---- bind options ----
        string method = args.Value("-X", "--method") ?? "GET";
        var headers = args.Values("-H", "--header");
        string? data = args.Value("-d", "--data");
        string? dataFile = args.Value("--data-file");
        string? contentType = args.Value("--content-type");
        string? bearer = args.Value("--bearer");
        string? basic = args.Value("--basic");
        string? certQuery = args.Value("--cert");
        string store = args.Value("--store") ?? "CurrentUser";
        bool insecure = args.Flag("--insecure");
        string? timeoutRaw = args.Value("--timeout");
        int timeout = 100;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeout) || timeout <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");
        string? envName = args.Value("--env");
        var varOverrides = args.Values("--var");
        string? workspace = args.Value("--workspace");
        bool strictVars = args.Flag("--strict-vars");
        string? outFile = args.Value("-o", "--output");
        bool include = args.Flag("--include");
        bool pretty = args.Flag("--pretty");
        bool json = args.Flag("--json");
        bool fail = args.Flag("--fail");
        bool quiet = args.Flag("-q", "--quiet");

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        string url = positionals[0];
        if (data is not null && dataFile is not null)
            throw new CliUsageException("-d/--data and --data-file are mutually exclusive.");
        if (bearer is not null && basic is not null)
            throw new CliUsageException("--bearer and --basic are mutually exclusive.");
        string? body = data ?? (dataFile is not null
            ? File.Exists(dataFile) ? File.ReadAllText(dataFile) : throw new CliDataException($"Body file not found: {dataFile}")
            : null);

        // ---- variables ----
        var state = (envName is not null || workspace is not null)
            ? CliWorkspace.Load(workspace, services.LiveStatePath) : new AppState();
        var vars = CliWorkspace.BuildVars(state, envName, varOverrides);
        var unresolved = new List<string>();
        string R(string s)
        {
            var (resolved, missing) = VariableResolver.Resolve(s ?? "", vars);
            foreach (var m in missing) if (!unresolved.Contains(m)) unresolved.Add(m);
            return resolved;
        }

        url = R(url);
        var headerPairs = new List<KeyValuePair<string, string>>();
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            headerPairs.Add(new(R(raw[..colon].Trim()), R(raw[(colon + 1)..].Trim())));
        }
        if (bearer is not null) headerPairs.Add(new("Authorization", "Bearer " + R(bearer)));
        if (basic is not null) headerPairs.Add(new("Authorization",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(R(basic)))));
        if (body is not null) body = R(body);

        if (unresolved.Count > 0)
        {
            var tokens = string.Join(", ", unresolved.Select(u => "{{" + u + "}}"));
            if (strictVars) throw new CliDataException($"Unresolved variables: {tokens}");
            stderr.WriteLine($"warning: unresolved variables: {tokens}");
        }

        // ---- certificate ----
        bool localMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        if (!localMachine && !store.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("--store must be CurrentUser or LocalMachine.");
        var cert = certQuery is null ? null
            : CertPicker.Resolve(services.ListCertificates(localMachine), certQuery, stderr).Certificate;

        // ---- send ----
        var request = new ApiRequest
        {
            Method = new HttpMethod(method.ToUpperInvariant()),
            Url = url,
            Headers = headerPairs,
            Body = body,
            ContentType = body is not null ? (contentType ?? "application/json") : null,
            Timeout = TimeSpan.FromSeconds(timeout)
        };
        var response = services.Client.SendAsync(request, cert, insecure,
            cancellationToken: services.Cancel).GetAwaiter().GetResult();

        if (!quiet) stderr.WriteLine(OutputText.MetaLine(response));

        // ---- output ----
        if (json)
        {
            if (outFile is not null && response.Error is null) File.WriteAllBytes(outFile, response.Body);
            stdout.WriteLine(BuildEnvelope(response, includeBody: outFile is null));
        }
        else if (response.Error is null)
        {
            if (include)
            {
                stdout.WriteLine($"{response.StatusCode} {response.ReasonPhrase}".Trim());
                foreach (var h in response.Headers) stdout.WriteLine($"{h.Key}: {h.Value}");
                stdout.WriteLine();
            }
            if (outFile is not null) File.WriteAllBytes(outFile, response.Body);
            else if (pretty) stdout.WriteLine(new ResponseFormatter().Format(response).Text);
            else { stdout.Flush(); bodyOut.Write(response.Body); bodyOut.Flush(); }
        }

        if (response.Error is not null) return ExitCodes.Failure;
        if (fail && response.StatusCode is >= 400) return ExitCodes.Failure;
        return ExitCodes.Ok;
    }

    internal static string BuildEnvelope(ApiResponse r, bool includeBody)
    {
        bool binary = false;
        string? text = null;
        if (includeBody && r.Error is null)
        {
            // UTF8.GetString never throws — invalid bytes decode to U+FFFD, which marks the body binary.
            text = Encoding.UTF8.GetString(r.Body);
            binary = text.Contains('�');
        }
        var obj = new Dictionary<string, object?>
        {
            ["status"] = r.StatusCode,
            ["reasonPhrase"] = r.ReasonPhrase,
            ["elapsedMs"] = Math.Round(r.Elapsed.TotalMilliseconds),
            ["contentType"] = r.ContentType,
            ["sizeBytes"] = r.Body.LongLength,
            ["headers"] = r.Headers
                .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Count() == 1 ? (object)g.First().Value : g.Select(h => h.Value).ToArray()),
            ["tlsProtocol"] = r.Connection?.TlsProtocol,
            ["clientCertPresented"] = r.Connection?.ClientCertificateSent ?? false,
            ["error"] = r.Error is null ? null : new { kind = r.Error.Kind.ToString(), message = r.Error.Message }
        };
        if (includeBody && r.Error is null)
        {
            if (binary) obj["bodyBase64"] = Convert.ToBase64String(r.Body);
            else obj["body"] = text;
        }
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }
}
