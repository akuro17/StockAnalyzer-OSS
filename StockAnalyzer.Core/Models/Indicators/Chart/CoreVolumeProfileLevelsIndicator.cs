using System.Buffers;
using System.Runtime.InteropServices;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>
/// Causal volume-profile levels: for every bar t with t &gt;= Period - 1 the volume profile of the window of the last <see cref="Period"/> bars (t - Period + 1 .. t) is built by the
/// rules of <see cref="VolumeAnalysis.CalculateProfile"/> and <c>Main</c> (POC) = the price (centre) of the bin with the greatest volume (first one on a tie), <c>VAH</c> / <c>VAL</c> =
/// the upper / lower bound of the value area that covers <see cref="ValueAreaPercentage"/> of the profile volume (<see cref="VolumeAnalysis.CalculateValueArea"/>).
/// Unit: price-native. Bars before Period - 1, and a window whose bars all have the same price, are null. Bar t uses bars &lt;= t only, so unlike the Volume Profile
/// (one POC copied to every bar of the visible range, listed as non-causal) it can be used by the Screener and the Backtest. No <c>CustomData</c> is returned: the histogram is drawn by Volume Profile.
/// The bins are filled by the allocation-free <see cref="VolumeAnalysis.FillProfile"/>: one bin array per Calculate (stack up to
/// <see cref="IndicatorDefaultConstants.VolumeProfileLevelsMaxStackRows"/> rows, otherwise rented), no allocation inside the bar loop.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.VolumeProfileLevels)]
public class CoreVolumeProfileLevelsIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.VolumeProfilePeriod;
    public int RowCount { get; set; } = IndicatorDefaultConstants.VolumeProfileRowCount;
    public VolumeDistributionMode Mode { get; set; } = VolumeDistributionMode.Proportional;
    public decimal ValueAreaPercentage { get; set; } = IndicatorDefaultConstants.VolumeProfileLevelsDefaultValueAreaPercentage;

    public override string Name => $"Volume Profile Levels ({Period}, {RowCount})";

    /// <summary>
    /// The Main series under the output name "POC". Output-name discovery lists the public series properties, so without this alias the VAH and VAL
    /// properties would hide the main output.
    /// </summary>
    public IReadOnlyList<decimal?> POC => _values;

    /// <summary>Upper bound of the value area of the window ending at each bar.</summary>
    public List<decimal?> VAH { get; } = new();

    /// <summary>Lower bound of the value area of the window ending at each bar.</summary>
    public List<decimal?> VAL { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreVolumeProfileLevelsParameter p)
        {
            p.Validate();
            Period = p.Period;
            RowCount = p.RowCount;
            Mode = p.Mode;
            ValueAreaPercentage = p.ValueAreaPercentage;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        VAH.Clear();
        VAL.Clear();
        int count = candles.Count;
        _values.Capacity = count;
        VAH.Capacity = count;
        VAL.Capacity = count;

        int period = Period;
        int rowCount = RowCount;
        double valueAreaPercent = (double)ValueAreaPercentage;

        // One bin array for the whole calculation.
        decimal[]? rentedBins = null;
        Span<decimal> bins = rowCount <= IndicatorDefaultConstants.VolumeProfileLevelsMaxStackRows
            ? stackalloc decimal[rowCount]
            : (rentedBins = ArrayPool<decimal>.Shared.Rent(rowCount)).AsSpan(0, rowCount);

        // The window is a slice of the candles when they are contiguous; otherwise one buffer of Period candles is overwritten per bar.
        ReadOnlySpan<CoreCandleData> all = default;
        if (candles is CoreCandleData[] array) all = array;
        else if (candles is List<CoreCandleData> list) all = CollectionsMarshal.AsSpan(list);
        CoreCandleData[]? buffer = all.IsEmpty && count > 0 ? new CoreCandleData[period] : null;

        try
        {
            for (int i = 0; i < count; i++)
            {
                if (i < period - 1)
                {
                    _values.Add(null);
                    VAH.Add(null);
                    VAL.Add(null);
                    continue;
                }

                ReadOnlySpan<CoreCandleData> window;
                if (buffer is null)
                {
                    window = all.Slice(i - period + 1, period);
                }
                else
                {
                    for (int k = 0; k < period; k++) buffer[k] = candles[i - period + 1 + k];
                    window = buffer;
                }

                if (!VolumeAnalysis.FillProfile(window, bins, Mode, out decimal minPrice, out decimal binSize))
                {
                    _values.Add(null);
                    VAH.Add(null);
                    VAL.Add(null);
                    continue;
                }

                VolumeAnalysis.ComputeValueArea(bins, minPrice, binSize, valueAreaPercent, out decimal vah, out decimal val);
                _values.Add(VolumeAnalysis.BinPrice(minPrice, binSize, VolumeAnalysis.FindPointOfControlIndexFromVolumes(bins)));
                VAH.Add(vah);
                VAL.Add(val);
            }
        }
        finally
        {
            if (rentedBins is not null) ArrayPool<decimal>.Shared.Return(rentedBins);
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { nameof(VAH), VAH },
            { nameof(VAL), VAL },
        });
    }
}
