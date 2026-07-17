namespace ApiTester.Cli;

/// <summary>Tiny argument reader: commands consume the options they know, then call
/// <see cref="Positionals"/>, which rejects anything option-shaped that is left over.</summary>
public sealed class Args
{
    private readonly string?[] _tokens;   // consumed slots become null

    public Args(IReadOnlyList<string> tokens) => _tokens = tokens.ToArray();

    public bool Flag(params string[] names)
    {
        for (int i = 0; i < _tokens.Length; i++)
            if (_tokens[i] is { } t && names.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                _tokens[i] = null;
                return true;
            }
        return false;
    }

    public string? Value(params string[] names)
    {
        for (int i = 0; i < _tokens.Length; i++)
        {
            if (_tokens[i] is not { } t || !names.Contains(t, StringComparer.OrdinalIgnoreCase)) continue;
            if (i + 1 >= _tokens.Length || _tokens[i + 1] is null)
                throw new CliUsageException($"Option {t} needs a value.");
            var value = _tokens[i + 1]!;
            _tokens[i] = _tokens[i + 1] = null;
            return value;
        }
        return null;
    }

    public List<string> Values(params string[] names)
    {
        var all = new List<string>();
        while (Value(names) is { } v) all.Add(v);
        return all;
    }

    /// <summary>The remaining non-option tokens; call last. Unknown options are usage errors.</summary>
    public List<string> Positionals()
    {
        var positionals = new List<string>();
        foreach (var t in _tokens)
        {
            if (t is null) continue;
            if (t.StartsWith('-') && t.Length > 1 && !char.IsDigit(t[1]))
                throw new CliUsageException($"Unknown option '{t}'.");
            positionals.Add(t);
        }
        return positionals;
    }
}
