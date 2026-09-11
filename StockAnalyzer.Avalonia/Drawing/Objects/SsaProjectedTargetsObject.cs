using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public sealed class SsaProjectedTargetsObject : SsaSupportResistanceObject
{
    public override ChartObjectType Type => ChartObjectType.SsaProjectedTargets;

    public SsaProjectedTargetsObject()
    {
        Mode = SsaSupportResistanceMode.ProjectedTargets;
    }
}
