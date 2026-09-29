using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public sealed class SsaDynamicEnvelopesObject : SsaSupportResistanceObject
{
    public override ChartObjectType Type => ChartObjectType.SsaDynamicEnvelopes;

    public SsaDynamicEnvelopesObject()
    {
        Mode = SsaSupportResistanceMode.DynamicEnvelopes;
    }
}
