namespace ApiTester.Cli;

/// <summary>Diagnostic sink for the global --debug / --log-file options. Debug lines go to
/// stderr when debug is on and to the log file always; the log file also receives every line
/// the command writes to stderr (via <see cref="WrapStderr"/>). Logging never throws — a
/// broken log file must not break the command.</summary>
public sealed class CliLog : IDisposable
{
    public static CliLog None { get; } = new(debug: false, file: null, stderr: TextWriter.Null);

    private readonly TextWriter _stderr;
    private readonly StreamWriter? _file;
    private readonly object _lock = new();

    public bool DebugEnabled { get; }

    private CliLog(bool debug, StreamWriter? file, TextWriter stderr)
    {
        DebugEnabled = debug;
        _file = file;
        _stderr = stderr;
    }

    /// <summary>Open the sink. A log file that cannot be opened is a one-line warning, not an error.</summary>
    public static CliLog Create(bool debug, string? logFilePath, TextWriter stderr)
    {
        StreamWriter? file = null;
        if (logFilePath is not null)
        {
            try
            {
                if (Path.GetDirectoryName(logFilePath) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                file = new StreamWriter(logFilePath, append: true) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"warning: could not open log file '{logFilePath}': {ex.Message}");
            }
        }
        return new CliLog(debug, file, stderr);
    }

    /// <summary>A debug diagnostic: stderr under --debug, log file always.</summary>
    public void Debug(string message)
    {
        if (DebugEnabled) _stderr.WriteLine("debug: " + message);
        ToFile("debug", message);
    }

    /// <summary>Record a line the command wrote to stderr (notes, warnings, errors).</summary>
    public void Note(string line) => ToFile("stderr", line);

    /// <summary>The error text for an exception: full chain and stack under --debug.</summary>
    public string Describe(Exception ex) => DebugEnabled ? ex.ToString() : ex.Message;

    /// <summary>Wrap stderr so every completed line is also recorded in the log file.</summary>
    public TextWriter WrapStderr(TextWriter stderr) => _file is null ? stderr : new TeeWriter(stderr, this);

    private void ToFile(string level, string message)
    {
        if (_file is null) return;
        lock (_lock)
        {
            try { _file.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} [{level}] {message}"); }
            catch { /* never break the command over logging */ }
        }
    }

    public void Dispose() => _file?.Dispose();

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly CliLog _log;
        private readonly System.Text.StringBuilder _line = new();

        public TeeWriter(TextWriter inner, CliLog log) { _inner = inner; _log = log; }

        public override System.Text.Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            if (value == '\n')
            {
                _log.Note(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
            else _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        public override void Flush() => _inner.Flush();
    }
}

/// <summary>Extracts the global --debug / --log-file options, which are valid anywhere on any
/// command line, before command dispatch.</summary>
public static class GlobalOptions
{
    public static (string[] Remaining, bool Debug, string? LogFile) Extract(string[] args)
    {
        var rest = new List<string>(args.Length);
        bool debug = false;
        string? logFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--debug", StringComparison.OrdinalIgnoreCase)) { debug = true; continue; }
            if (args[i].Equals("--log-file", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) throw new CliUsageException("Option --log-file needs a value.");
                logFile = args[++i];
                continue;
            }
            rest.Add(args[i]);
        }
        return (rest.ToArray(), debug, logFile);
    }
}
