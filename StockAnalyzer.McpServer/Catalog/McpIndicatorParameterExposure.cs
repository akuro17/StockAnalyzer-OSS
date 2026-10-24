namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// Which indicator parameter types a client can set through the tools, and which types are deliberately unsettable (with the reason).
/// <c>IndicatorSettingsBinder</c> converts exactly <see cref="SettableTypes"/>; <c>McpParityGuardTests</c> fails when a registered indicator
/// has a parameter of a type that is in neither list, so a new parameter type is decided on purpose instead of surfacing as a runtime refusal.
/// </summary>
internal static class McpIndicatorParameterExposure
{
    public static IReadOnlyList<string> SettableTypes { get; } = new[]
    {
        IndicatorParameterReader.IntegerType,
        IndicatorParameterReader.NumberType,
        IndicatorParameterReader.BooleanType,
        IndicatorParameterReader.StringType,
        IndicatorParameterReader.EnumType,
    };

    /// <summary>Parameter types the tools refuse on purpose, by the type name the catalogue reports.</summary>
    public static IReadOnlyDictionary<string, string> UnsettableOnPurpose { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [IndicatorParameterReader.ColorType] = "Display only: a colour does not change any value.",
    };
}
