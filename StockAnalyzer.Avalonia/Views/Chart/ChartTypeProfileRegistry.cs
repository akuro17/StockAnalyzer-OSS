using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// 全てのチャートタイプ(ChartType)に対応する IChartTypeProfile を一元管理する静的レジストリ。
/// 新しいチャートタイプが追加された場合は、ここに1行登録するだけで
/// レンダラー・設定・レイアウト・価格範囲の全ロジックが統合的に提供される。
/// </summary>
public static class ChartTypeProfileRegistry
{
    private static readonly Dictionary<ChartType, IChartTypeProfile> _profiles = new()
    {
        [ChartType.Candlestick] = new CandlestickProfile(),
        [ChartType.HeikinAshi] = new HeikinAshiProfile(),
        [ChartType.OHLCBar] = new OhlcBarProfile(),
        [ChartType.Line] = new LineProfile(),
        [ChartType.Area] = new AreaProfile(),
        [ChartType.Renko] = new RenkoProfile(),
        [ChartType.PointAndFigure] = new PnfProfile(),
        [ChartType.Kagi] = new KagiProfile(),
        [ChartType.ThreeLineBreak] = new ThreeLineBreakProfile(),
        [ChartType.ReverseWatch] = new ReverseWatchProfile(),
        [ChartType.RelativePerformance] = new RelativePerformanceProfile(),
    };

    /// <summary>
    /// 指定されたチャートタイプに対応するプロファイルを取得する。
    /// 未知のタイプが指定された場合は安全なフォールバックとして CandlestickProfile を返す。
    /// </summary>
    public static IChartTypeProfile Get(ChartType type)
    {
        if (_profiles.TryGetValue(type, out var profile))
        {
            return profile;
        }
        
        return _profiles[ChartType.Candlestick];
    }
}
