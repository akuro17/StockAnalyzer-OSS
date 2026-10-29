using System.Text.Json;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// A tool handler. It receives the raw arguments (already checked against <see cref="InputSchemaJson"/>) and returns the
/// structured result as one JSON text; it depends on no protocol type. Failures are <see cref="ToolException"/>s.
/// </summary>
internal interface IMcpTool
{
    string Name { get; }

    /// <summary>English text for the AI client.</summary>
    string Description { get; }

    /// <summary>JSON Schema of the arguments: an object with <c>properties</c>, optional <c>required</c> and <c>additionalProperties: false</c>.</summary>
    string InputSchemaJson { get; }

    /// <summary>True for tools that use the longer time limit.</summary>
    bool IsHeavy { get; }

    /// <summary>True for a tool that starts work or writes (offered only with <c>--enable-execution</c>); the read-only tools keep the default.</summary>
    bool IsExecution => false;

    /// <summary>
    /// An optional compact JSON object string illustrating valid, copy-pasteable arguments for this tool. Default is null.
    /// When non-null, must be a single-line minified JSON object containing all required properties and valid ranges.
    /// </summary>
    string? ExampleArgumentsJson => null;

    /// <summary>
    /// Top-level argument names that a sibling tool accepts for the same idea but this tool does not (for example the as-of date is <c>endDate</c> in one tool and
    /// <c>asOfDate</c> in another), each with the sentence that names the right argument. An unknown argument found here is rejected with that sentence as its hint.
    /// Not part of the input schema, and no alias is accepted. Default is none.
    /// </summary>
    IReadOnlyDictionary<string, string>? MisnamedArgumentHints => null;

    Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken);
}
