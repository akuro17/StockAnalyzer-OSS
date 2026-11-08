using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.McpServer.Protocol;

namespace StockAnalyzer.McpServer.Tools.Discussion;

/// <summary>
/// sa_discussion_attach_analysis: the server runs one read-only analysis tool and stores its exact result in the discussion as a numbered context (spec 5.10). Agents cannot
/// paste data as an "analysis": every number a participant sees here was produced by this server's own code, so all participants discuss identical, hashed data.
/// <para>
/// The order is fixed so that no board lock is held while the (possibly slow) inner tool runs and the other participants can read and post meanwhile:
/// P1 the shape of the arguments (the executor), P2 a precheck at the board (nothing is reserved), P3 whitelist, inner schema, canonical arguments, the inner tool, and
/// P4 the commit at the board, which checks everything of P2 again because the discussion may have closed meanwhile.
/// </para>
/// <para>
/// The inner tool is called directly, not through the executor: this call already holds the single tool turn, and going through the executor would wait for it for ever.
/// A consequence for the command log: one entry is recorded for this call (the executor's), none for the inner tool.
/// </para>
/// </summary>
internal sealed class DiscussionAttachAnalysisTool : IMcpTool
{
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement;

    private readonly IDiscussionGateway _gateway;
    private readonly ClientIdentity? _identity;
    private readonly Func<McpToolRegistry> _registry;

    /// <param name="registry">Resolved at call time: this tool is itself a member of the registry it looks up.</param>
    public DiscussionAttachAnalysisTool(IDiscussionGateway gateway, ClientIdentity? identity, Func<McpToolRegistry> registry)
    {
        _gateway = gateway;
        _identity = identity;
        _registry = registry;
    }

    public string Name => "sa_discussion_attach_analysis";

    public string Description =>
        "Runs one read-only analysis tool on this server and stores its exact result in the discussion as a numbered context, so every participant discusses identical, hashed data. " +
        "Use it for numbers instead of typing them from memory; other participants fetch the context with sa_discussion_get_analysis, and a post can refer to it with contextIds. " +
        "The same tool, arguments and result are stored once (reused true). Execution tools, image tools and discussion tools are refused, and a result larger than maxContextBytes is refused, never cut. " +
        "The call takes the single tool turn like any analysis and may take as long as a heavy tool; other participants can still read and post meanwhile.";

    public bool IsHeavy => true;

    public bool IsReadOnly => false;

    public string? ExampleArgumentsJson =>
        "{\"sessionId\":\"Sabcdef\",\"participantToken\":\"abcdef.AAAAAAAAAAAAAAAAAAAAAA\",\"tool\":\"sa_calculate_indicators\",\"arguments\":{\"symbol\":\"7203.T\"},\"analysisIntent\":\"TrendDetection\",\"note\":\"daily RSI\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String("sessionId", "The discussion.", required: true)
        .String("participantToken", "Your token from sa_discussion_join.", required: true)
        .String("tool", "The analysis tool to run, for example sa_calculate_indicators.", required: true)
        .FreeObject("arguments", "The arguments of that tool, as described by the tool itself (default: none).")
        .String("analysisIntent", "[Recommended] Purpose or hypothesis of the analysis (e.g. TrendDetection, ReversalDetection, OverboughtOversold, MomentumAnalysis, DivergenceDetection, VolatilityAnalysis, SupportResistance, VolumeStructure, RelativeStrength, RegimeClassification, RiskAssessment).")
        .String("note", $"A short remark shown with the context (at most {DiscussionLimits.NoteMaxChars} characters).")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string sessionId = ToolArgs.String(arguments, "sessionId")!;
        string token = ToolArgs.String(arguments, "participantToken")!;
        string toolName = ToolArgs.String(arguments, "tool")!;
        string? note = ToolArgs.String(arguments, "note");
        string? analysisIntent = ToolArgs.String(arguments, "analysisIntent");
        JsonElement innerArguments = arguments.TryGetProperty("arguments", out JsonElement given) && given.ValueKind == JsonValueKind.Object ? given : EmptyArguments;

        string? normalizedIntent = null;
        if (analysisIntent is not null)
        {
            if (!DiscussionEvidenceValidator.TryNormalizeAnalysisIntent(analysisIntent, out normalizedIntent))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"analysisIntent must be at most {DiscussionEvidenceLimits.MaxAnalysisIntentChars} characters without control characters.",
                    "analysisIntent");
            }
        }

        // P2: stop before the expensive part when the attach could not succeed anyway.
        await DiscussionToolCalls.ExecuteAsync(
            _gateway, DiscussionOperations.AttachPrecheck, _identity, Build(writer => WriteIdentity(writer, sessionId, token)), cancellationToken).ConfigureAwait(false);

        // P3: nothing below holds a board lock or has a board call in flight.
        RegisteredTool inner = Resolve(toolName);
        try
        {
            ToolArgumentValidator.Validate(inner.Schema, innerArguments, inner.Tool.MisnamedArgumentHints);
        }
        catch (ToolException error)
        {
            throw new ToolException(error.Code, error.Message, error.Parameter is null ? "arguments" : "arguments." + error.Parameter, error.Hint);
        }

        string canonical;
        try
        {
            canonical = DiscussionCanonicalJson.Canonicalize(innerArguments);
        }
        catch (DiscussionException error)
        {
            throw DiscussionToolCalls.ToToolException(error);
        }

        string result = await inner.Tool.InvokeAsync(innerArguments, cancellationToken).ConfigureAwait(false);

        // P4: the board checks token, state and room again, then the size, the reuse and the memory budget, and stores the text as it is.
        string commit = Build(writer =>
        {
            WriteIdentity(writer, sessionId, token);
            writer.WriteString("tool", toolName);
            writer.WritePropertyName("arguments");
            writer.WriteRawValue(canonical);
            writer.WriteString("result", result);
            if (note is not null)
            {
                writer.WriteString("note", note);
            }

            if (normalizedIntent is not null)
            {
                writer.WriteString("analysisIntent", normalizedIntent);
            }
        });
        return await DiscussionToolCalls.ExecuteAsync(_gateway, DiscussionOperations.AttachCommit, _identity, commit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The registered, read-only, non-image, non-discussion tool of that name; anything else lists the tools that are allowed.</summary>
    private RegisteredTool Resolve(string toolName)
    {
        McpToolRegistry registry = _registry();
        if (registry.TryGet(toolName, out RegisteredTool? registered) && IsAllowed(registered))
        {
            return registered;
        }

        string allowed = string.Join(", ", registry.Tools.Where(IsAllowed).Select(t => t.Tool.Name).OrderBy(name => name, StringComparer.Ordinal));
        throw new ToolException(ToolErrorCodes.InvalidArgument, "tool is not an analysis tool that can be attached.", "tool", $"Allowed tools: {allowed}.");
    }

    private static bool IsAllowed(RegisteredTool registered)
        => !registered.Tool.IsExecution && registered.Tool is not IMcpImageTool && registered.Tool is not DiscussionToolBase and not DiscussionAttachAnalysisTool;

    private static void WriteIdentity(Utf8JsonWriter writer, string sessionId, string token)
    {
        writer.WriteString("sessionId", sessionId);
        writer.WriteString("participantToken", token);
    }

    /// <summary>Builds a JSON object: <paramref name="write"/> adds the members; the braces are written here.</summary>
    private static string Build(Action<Utf8JsonWriter> write)
        => ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        });
}
