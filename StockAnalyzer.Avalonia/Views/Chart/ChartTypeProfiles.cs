using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// 各チャートタイプに特化した設定プロファイルの抽象基底クラス。
/// 共通のIChartRenderConfig生成ロジックを提供する。
/// </summary>
public abstract class ChartTypeProfileBase : IChartTypeProfile
{
    public abstract ChartType Type { get; }
    
    public ChartTypeCapabilities Capabilities => ChartTypeCapabilitiesRegistry.Get(Type);
    
    public abstract IChartRenderer CreateRenderer();
    
    public abstract IChartLayoutProvider LayoutProvider { get; }
    
    public abstract IPriceRangeCalculator PriceRangeCalculator { get; }

    public abstract IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null);
}

// ---------------------------------------------------------
// 個別チャートタイプのプロファイル実装群
// ---------------------------------------------------------

public sealed class CandlestickProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.Candlestick;
    public override IChartRenderer CreateRenderer() => new CandleStickRenderer();
    public override IChartLayoutProvider LayoutProvider => new StandardChartLayoutProvider(Capabilities.SupportsVolume, Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => StandardPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return CandlestickRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling);
    }
}

public sealed class HeikinAshiProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.HeikinAshi;
    public override IChartRenderer CreateRenderer() => new CandleStickRenderer(); // HeikinAshi は内部データ変換で対応
    public override IChartLayoutProvider LayoutProvider => new StandardChartLayoutProvider(Capabilities.SupportsVolume, Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => StandardPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return HeikinAshiRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling);
    }
}

public sealed class OhlcBarProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.OHLCBar;
    public override IChartRenderer CreateRenderer() => new OHLCBarRenderer();
    public override IChartLayoutProvider LayoutProvider => new StandardChartLayoutProvider(Capabilities.SupportsVolume, Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => StandardPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return OhlcBarRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling);
    }
}

public sealed class LineProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.Line;
    public override IChartRenderer CreateRenderer() => new LineChartRenderer();
    public override IChartLayoutProvider LayoutProvider => new StandardChartLayoutProvider(Capabilities.SupportsVolume, Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => StandardPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return LineChartRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling);
    }
}

public sealed class AreaProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.Area;
    public override IChartRenderer CreateRenderer() => new AreaChartRenderer();
    public override IChartLayoutProvider LayoutProvider => new StandardChartLayoutProvider(Capabilities.SupportsVolume, Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => StandardPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return AreaChartRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling);
    }
}

public sealed class RenkoProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.Renko;
    public override IChartRenderer CreateRenderer() => new RenkoRenderer();
    public override IChartLayoutProvider LayoutProvider => new CompactChartLayoutProvider(Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => StandardPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return RenkoRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling);
    }
}

public sealed class PnfProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.PointAndFigure;
    public override IChartRenderer CreateRenderer() => new PointAndFigureRenderer();
    public override IChartLayoutProvider LayoutProvider => new CompactChartLayoutProvider(Capabilities.SupportsIndicators);

    /// <summary>
    /// FR-PNF-01: P&amp;F 専用の <see cref="PnfPriceRangeCalculator"/> を返す。
    /// </summary>
    /// <remarks>
    /// このプロパティはボックスサイズ情報を持たないコンテキスト向けのデフォルト
    /// （boxSize = 1）を返す。ChartDataCoordinator.CreatePointAndFigureSnapshot は、
    /// 実際の EffectivePnfBoxSize を用いて <see cref="GetPriceRangeCalculator(decimal)"/>
    /// から都度インスタンスを生成し、Snapshot に渡す。
    /// </remarks>
    public override IPriceRangeCalculator PriceRangeCalculator => new PnfPriceRangeCalculator(1m);

    /// <summary>
    /// 指定されたボックスサイズに基づく P&amp;F 専用価格範囲計算機を生成する。
    /// </summary>
    public static IPriceRangeCalculator GetPriceRangeCalculator(decimal boxSize) => new PnfPriceRangeCalculator(boxSize);

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return PnfRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling);
    }
}

public sealed class KagiProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.Kagi;
    public override IChartRenderer CreateRenderer() => new KagiRenderer();
    public override IChartLayoutProvider LayoutProvider => new CompactChartLayoutProvider(Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => KagiPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return KagiRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling);
    }
}

public sealed class ThreeLineBreakProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.ThreeLineBreak;
    public override IChartRenderer CreateRenderer() => new ThreeLineBreakRenderer();
    public override IChartLayoutProvider LayoutProvider => new StandardChartLayoutProvider(Capabilities.SupportsVolume, Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => StandardPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return ThreeLineBreakRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling);
    }
}

public sealed class ReverseWatchProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.ReverseWatch;
    public override IChartRenderer CreateRenderer() => new ReverseWatchRenderer();
    public override IChartLayoutProvider LayoutProvider => new StandardChartLayoutProvider(false, Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => StandardPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return ReverseWatchRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling, reverseWatchData);
    }
}

public sealed class RelativePerformanceProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.RelativePerformance;
    public override IChartRenderer CreateRenderer() => new ComparisonChartRenderer();
    public override IChartLayoutProvider LayoutProvider => new StandardChartLayoutProvider(false, Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => StandardPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return ComparisonRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling, comparisonData);
    }
}

/// <summary>
/// Profile for Invisible / Price-Hidden ChartType that omits price rendering while preserving indicators and layouts.
/// </summary>
public sealed class InvisibleProfile : ChartTypeProfileBase
{
    public override ChartType Type => ChartType.Invisible;
    public override IChartRenderer CreateRenderer() => new ChartRendererRegistry.NoOpRenderer();
    public override IChartLayoutProvider LayoutProvider => new StandardChartLayoutProvider(Capabilities.SupportsVolume, Capabilities.SupportsIndicators);
    public override IPriceRangeCalculator PriceRangeCalculator => StandardPriceRangeCalculator.Instance;

    public override IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        ReverseWatchCurveData? reverseWatchData = null,
        ComparisonAlignedData? comparisonData = null)
    {
        return CandlestickRenderConfig.Create(settings, currentPrice, visibleStartIndex, visibleCandleCount, mousePosition, transform, renderScaling);
    }
}
