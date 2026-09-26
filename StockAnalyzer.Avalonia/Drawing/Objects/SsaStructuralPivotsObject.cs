using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public sealed class SsaStructuralPivotsObject : SsaSupportResistanceObject
{
    public override ChartObjectType Type => ChartObjectType.SsaStructuralPivots;

    public SsaStructuralPivotsObject()
    {
        Mode = SsaSupportResistanceMode.StructuralPivots;
    }
}
