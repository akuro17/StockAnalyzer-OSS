namespace StockAnalyzer.McpServer.Protocol;

/// <summary>
/// Protocol revisions this server speaks, newest first. Only revisions that define <c>structuredContent</c>
/// are listed because every tool result carries both <c>content</c> and <c>structuredContent</c>.
/// All of them use the <c>initialize</c> handshake; the stateless revision 2026-07-28 (no <c>initialize</c>) is not implemented.
/// </summary>
internal static class McpProtocolVersions
{
    public static IReadOnlyList<string> Supported { get; } = new[] { "2025-11-25", "2025-06-18" };

    /// <summary>The requested revision when supported, otherwise the newest supported one (client then decides whether to disconnect).</summary>
    public static string Negotiate(string requested)
        => Supported.Contains(requested, StringComparer.Ordinal) ? requested : Supported[0];
}
