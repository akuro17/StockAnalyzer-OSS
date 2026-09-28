using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.VIDYA)]
public class CoreVidyaIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => true;

    public int SmoothPeriod { get; set; } = IndicatorDefaultConstants.VidyaSmoothPeriod;
    public int CmoPeriod { get; set; } = IndicatorDefaultConstants.VidyaCmoPeriod;

    public override string Name => $"VIDYA ({SmoothPeriod}, {CmoPeriod})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreVidyaParameter p)
        {
            SmoothPeriod = p.SmoothPeriod;
            CmoPeriod = p.CmoPeriod;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

        var vidyaValues = IndicatorCalculationHelper.CalculateVidya(series, SmoothPeriod, CmoPeriod);

        _values.Clear();
        _values.AddRange(vidyaValues);
        return IndicatorResult.Success(_values);
    }
}
