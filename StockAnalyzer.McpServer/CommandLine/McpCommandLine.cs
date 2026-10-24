namespace StockAnalyzer.McpServer.CommandLine;

/// <summary>What the process was asked to do.</summary>
internal abstract record McpCommand
{
    private McpCommand()
    {
    }

    /// <summary>The stdio session (no arguments, or only <see cref="McpCommandLine.EnableExecutionToken"/>).</summary>
    public sealed record Run : McpCommand;

    /// <summary>Print the help and exit; <paramref name="ToolName"/> is null for the overview.</summary>
    public sealed record Help(string? ToolName) : McpCommand;

    public sealed record Version : McpCommand;

    /// <summary>The arguments are wrong; <paramref name="Message"/> is the one line for standard error.</summary>
    public sealed record Invalid(string Message) : McpCommand;
}

internal sealed record McpCommandLineResult(McpCommand Command, bool EnableExecution);

/// <summary>
/// Reads the command-line arguments. A pure function: it touches no stream and starts nothing, so it can run before the
/// process redirects standard output for the protocol.
/// </summary>
internal static class McpCommandLine
{
    public const string HelpToken = "--help";
    public const string VersionToken = "--version";
    public const string EnableExecutionToken = "--enable-execution";

    /// <summary>Exit code of a normal end.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code of a configured limit outside its hard band.</summary>
    public const int ExitInvalidConfiguration = 2;

    /// <summary>Exit code of wrong command-line arguments (EX_USAGE).</summary>
    public const int ExitInvalidUsage = 64;

    /// <summary>The tokens that ask for the help; the first is the canonical one.</summary>
    public static IReadOnlyList<string> HelpTokens { get; } = new[] { HelpToken, "-h", "-?", "/?" };

    private const int MaxEchoedTokenChars = 100;
    private const string EchoEllipsis = "...";

    /// <summary>Tokens are case-sensitive and processed left to right; a token after a help token that starts with neither '-' nor '/' is a tool name.</summary>
    public static McpCommandLineResult Parse(string[] args)
    {
        bool help = false;
        bool version = false;
        bool enableExecution = false;
        string? toolName = null;
        int processed = 0;

        for (int index = 0; index < args.Length; index++)
        {
            string token = args[index];
            if (version)
            {
                return Invalid(token);
            }

            if (HelpTokens.Contains(token, StringComparer.Ordinal))
            {
                if (help)
                {
                    return Invalid(token);
                }

                help = true;
                if (index + 1 < args.Length && !StartsLikeOption(args[index + 1]))
                {
                    toolName = args[++index];
                    processed++;
                }
            }
            else if (string.Equals(token, VersionToken, StringComparison.Ordinal))
            {
                if (processed > 0)
                {
                    return Invalid(token);
                }

                version = true;
            }
            else if (string.Equals(token, EnableExecutionToken, StringComparison.Ordinal))
            {
                enableExecution = true;
            }
            else
            {
                return Invalid(token);
            }

            processed++;
        }

        McpCommand command = version ? new McpCommand.Version() : help ? new McpCommand.Help(toolName) : new McpCommand.Run();
        return new McpCommandLineResult(command, enableExecution);
    }

    /// <summary>The one-line message for an argument that is not accepted; an over-long token is cut so the line stays short.</summary>
    public static string UnknownArgumentMessage(string token)
    {
        string echoed = token.Length > MaxEchoedTokenChars ? token[..MaxEchoedTokenChars] + EchoEllipsis : token;
        return $"Unknown argument '{echoed}'. Run with {HelpToken}.";
    }

    public static string UnknownToolMessage(string name)
    {
        string echoed = name.Length > MaxEchoedTokenChars ? name[..MaxEchoedTokenChars] + EchoEllipsis : name;
        return $"Unknown tool '{echoed}'. Run with {HelpToken}.";
    }

    private static bool StartsLikeOption(string token) => token.StartsWith('-') || token.StartsWith('/');

    private static McpCommandLineResult Invalid(string token)
        => new(new McpCommand.Invalid(UnknownArgumentMessage(token)), EnableExecution: false);
}
