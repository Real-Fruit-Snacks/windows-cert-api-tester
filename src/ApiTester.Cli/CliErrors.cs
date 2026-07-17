namespace ApiTester.Cli;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int Data = 3;
}

/// <summary>Bad command line – exits 2 with the message (and usage where helpful).</summary>
public sealed class CliUsageException(string message) : Exception(message);

/// <summary>Missing/ambiguous data (workspace, path, env, cert) – exits 3 with the message.</summary>
public sealed class CliDataException(string message) : Exception(message);
