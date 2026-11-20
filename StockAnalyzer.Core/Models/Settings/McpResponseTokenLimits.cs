namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Single source of truth for <c>AiIntegration:MaxResponseTokens</c> hard band and recommended budget values.
/// Shared across Core settings, Avalonia UI validation/presets, and StockAnalyzer.McpServer startup validation.
/// </summary>
public static class McpResponseTokenLimits
{
    public const int Kibibyte = 1024;
    public const int Mebibyte = 1024 * Kibibyte;

    /// <summary>Hard minimum for <c>MaxResponseTokens</c>.</summary>
    public const int Minimum = 1024;

    /// <summary>Hard maximum for <c>MaxResponseTokens</c> (8 MiB / 4 bytes per token).</summary>
    public const int Maximum = (8 * Mebibyte) / 4;

    /// <summary>Default <c>MaxResponseTokens</c> (4 MiB / 4 bytes per token).</summary>
    public const int Default = (4 * Mebibyte) / 4;

    /// <summary>Recommended strict per-response token budget (within 24,000 - 32,000).</summary>
    public const int RecommendedStrict = 24_576;

    /// <summary>Returns true when <paramref name="value"/> lies inside [<see cref="Minimum"/>, <see cref="Maximum"/>].</summary>
    public static bool Contains(int value) => value >= Minimum && value <= Maximum;
}
