using System.Globalization;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Settings of the Kagi overlay: the size settings are the reversal amount of the Kagi chart type (no drawing options of that chart, no Breakout Patterns).
/// The class name is persisted (WorkspacePolymorphicResolver uses type.Name). NEVER rename after release.
/// </summary>
public class CoreKagiChartParameter : CoreBlockChartSizingParameterBase
{
    public override string GetDisplayName(string type) => string.Create(CultureInfo.InvariantCulture, $"{type} ({DescribeSizing()})");

    public override void Validate() => ValidateSizing();
}
