namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Specifies the type category of a stored template.
/// </summary>
public enum TemplateType
{
    Indicator = 0,
    Column = 1,
    Chart = 2,
    Drawing = 3,
    Filter = 4,
    RelativePerformance = 5,
    Theme = 6,
    Feature = 7,
    SourceIndicator = 8,
    DynamicPeriodDriver = 9
}
