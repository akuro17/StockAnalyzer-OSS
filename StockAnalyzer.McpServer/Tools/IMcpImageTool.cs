using System.Text.Json;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>The result of an image tool: the structured JSON (also sent as text and structuredContent) and the PNG bytes of the image block.</summary>
internal sealed record ToolImageResult(string StructuredJson, byte[] Png);

/// <summary>
/// A tool whose result carries one PNG image next to its structured JSON. Only these tools reach the protocol's <c>image</c> content block;
/// every other tool keeps returning one JSON text through <see cref="IMcpTool.InvokeAsync"/> and is not affected.
/// </summary>
internal interface IMcpImageTool : IMcpTool
{
    /// <summary>The MIME type of the image block.</summary>
    const string PngMimeType = "image/png";

    /// <summary>Runs the tool. Failures are <see cref="ToolException"/>s, as for any tool.</summary>
    Task<ToolImageResult> InvokeImageAsync(JsonElement arguments, CancellationToken cancellationToken);

    /// <summary>
    /// The structured JSON only, for a caller that cannot carry an image (the executor calls <see cref="InvokeImageAsync"/>). Implemented here so a tool writes one method.
    /// </summary>
    async Task<string> IMcpTool.InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        => (await InvokeImageAsync(arguments, cancellationToken).ConfigureAwait(false)).StructuredJson;
}
