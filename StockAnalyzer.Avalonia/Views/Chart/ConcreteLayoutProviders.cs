namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// マージンやボリューム領域を持つ一般的な時系列チャート用のレイアウトプロバイダー。
/// (Candlestick, Line, Area 等)
/// </summary>
public sealed class StandardChartLayoutProvider : ChartLayoutProviderBase
{
    private readonly bool _supportsVolume;
    private readonly bool _supportsIndicators;

    public StandardChartLayoutProvider(bool supportsVolume, bool supportsIndicators)
    {
        _supportsVolume = supportsVolume;
        _supportsIndicators = supportsIndicators;
    }

    protected override float GetMarginTop(float? customMarginTop) => customMarginTop ?? ChartTheme.MarginTop;
    protected override float GetMarginBottom(float? customMarginBottom) => customMarginBottom ?? ChartTheme.MarginBottom;
    protected override bool SupportsVolume => _supportsVolume;
    protected override bool SupportsIndicators => _supportsIndicators;
}

/// <summary>
/// マージンを持たない非時系列コンパクトチャート用のレイアウトプロバイダー。
/// (Renko, PointAndFigure 等)
/// </summary>
public sealed class CompactChartLayoutProvider : ChartLayoutProviderBase
{
    private readonly bool _supportsIndicators;

    public CompactChartLayoutProvider(bool supportsIndicators)
    {
        // Renko等の非時系列はデフォルトではVolume非表示（本来は表示可否をCapabilitiesから取る）
        _supportsIndicators = supportsIndicators;
    }

    protected override float GetMarginTop(float? customMarginTop) => 0f;
    protected override float GetMarginBottom(float? customMarginBottom) => 0f;
    protected override bool SupportsVolume => false; // Compactタイプは基本的にボリュームなし
    protected override bool SupportsIndicators => _supportsIndicators;
}
