namespace StockAnalyzer.Core.Models;

/// <summary>
/// Represents an indicator option in dropdowns for chaining (SourceIndicatorId) or dynamic modulation (DynamicPeriodIndicatorId).
/// </summary>
public class IndicatorReferenceOption
{
    public string? Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    public override string ToString() => DisplayName;
}
