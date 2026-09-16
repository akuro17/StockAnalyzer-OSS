namespace StockAnalyzer.Core.Models;

using StockAnalyzer.Core.Constants;

/// <summary>
/// チャートタイプごとの価格範囲計算を委譲するインターフェース。
/// </summary>
/// <remarks>
/// <para>
/// ChartDataSnapshot のコンストラクタ内にあった Kagi 固有の if 分岐を排除するために導入。
/// Kagi チャートでは High/Low フィールドが色フラグとして転用されているため、
/// Y 軸スケーリングでは Open/Close の max/min を使う必要がある。
/// この特殊処理を Snapshot 外部に委譲し、OCP を実現する。
/// </para>
/// </remarks>
public interface IPriceRangeCalculator
{
    /// <summary>
    /// ロウソク足データから Y 軸スケーリング用の High/Low 値を取得する。
    /// </summary>
    /// <param name="candle">対象のロウソク足データ。</param>
    /// <returns>Y 軸スケーリングに使用する (High, Low) のタプル。</returns>
    (decimal High, decimal Low) GetPriceRange(CoreCandleData candle);
}

/// <summary>
/// 標準的な価格範囲計算 (Candlestick, HeikinAshi, OHLCBar, Line, Area, Renko, P&amp;F, TLB)。
/// High/Low フィールドをそのまま使用する。
/// </summary>
public sealed class StandardPriceRangeCalculator : IPriceRangeCalculator
{
    /// <summary>シングルトンインスタンス。</summary>
    public static readonly StandardPriceRangeCalculator Instance = new();

    public (decimal High, decimal Low) GetPriceRange(CoreCandleData candle)
        => (candle.High, candle.Low);
}

/// <summary>
/// Kagi チャート用の価格範囲計算。
/// </summary>
/// <remarks>
/// <para>
/// Kagi チャートの変換ロジック (ZeroAllocation.KagiConverter) は
/// CandleData の High フィールドに「陽線フラグ (1)」、Low フィールドに
/// 「陰線フラグ (0 または -1)」を格納する仕様。
/// そのため Y 軸スケーリングに High/Low を直接使うと 0 ~ 1 の範囲と認識されてしまう。
/// Open/Close から実際の価格範囲を算出する。
/// </para>
/// </remarks>
public sealed class KagiPriceRangeCalculator : IPriceRangeCalculator
{
    /// <summary>シングルトンインスタンス。</summary>
    public static readonly KagiPriceRangeCalculator Instance = new();

    public (decimal High, decimal Low) GetPriceRange(CoreCandleData candle)
        => (Math.Max(candle.Open, candle.Close), Math.Min(candle.Open, candle.Close));
}

/// <summary>
/// Point &amp; Figure (P&amp;F) チャート用の価格範囲計算。
/// </summary>
/// <remarks>
/// <para>
/// P&amp;F のレンダラー (PointAndFigureRenderer) は、各カラム of High/Low をボックスサイズ単位に
/// スナップ（Math.Round(value / boxSize) * boxSize）した上で、最上段ボックスは
/// snapHigh + boxSize の高さまで描画する。
/// StandardPriceRangeCalculator が返す生の High/Low をそのまま Y 軸スケーリングに使うと、
/// このスナップ後の端数ボックス分（最大で boxSize 弱）がスケーリング範囲外となり、
/// チャート上端・下端のボックスが見切れる（クリッピングされる）。
/// 本クラスは、レンダラーと同一のスナップ・マージン規則を適用した (High, Low) を返すことで、
/// 上下の見切れを解消する。
/// </para>
/// <para>
/// 上限: Math.Round(High / boxSize) * boxSize + boxSize （次段のボックス天井まで確保）
/// 下限: Math.Round(Low / boxSize) * boxSize （ボックス底辺）
/// </para>
/// </remarks>
public sealed class PnfPriceRangeCalculator : IPriceRangeCalculator
{
    private readonly decimal _boxSize;

    /// <summary>
    /// 指定されたボックスサイズに基づき、P&amp;F 専用の価格範囲計算機を生成する。
    /// </summary>
    /// <param name="boxSize">P&amp;F チャートの実効ボックスサイズ（EffectivePnfBoxSize）。0 以下の場合は 1 にフォールバックする。</param>
    public PnfPriceRangeCalculator(decimal boxSize)
    {
        _boxSize = boxSize > 0 ? boxSize : 1m;
    }

    public (decimal High, decimal Low) GetPriceRange(CoreCandleData candle)
    {
        // レンダラー (PointAndFigureRenderer) と同一のスナップ規則:
        // snapHigh/snapLow = Math.Round(value / boxSize) * boxSize
        decimal snapHigh = Math.Round(candle.High / _boxSize) * _boxSize;
        decimal snapLow = Math.Round(candle.Low / _boxSize) * _boxSize;

        // レンダラーは最上段ボックスを snapHigh + boxSize の高さまで描画するため、
        // Y軸範囲にもその余白を含める。下限はボックス底辺 (snapLow) のまま。
        return (snapHigh + _boxSize, snapLow);
    }
}

/// <summary>
/// 銘柄比較チャート（相対パフォーマンス）用の価格範囲計算。
/// </summary>
/// <remarks>
/// 表示開始点（startIndex）の終値を 0% とした際の、表示全銘柄の騰落率範囲を算出する。
/// </remarks>
public sealed class ComparisonPriceRangeCalculator : IPriceRangeCalculator
{
    private readonly ComparisonAlignedData _data;
    private readonly int _startIndex;
    private readonly ComparisonMode _mode;
    private readonly int _zScorePeriod;
    private readonly Dictionary<string, decimal> _basePrices;

    public ComparisonPriceRangeCalculator(ComparisonAlignedData data, int startIndex, ComparisonMode mode = ComparisonMode.Performance, int zScorePeriod = 20)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _startIndex = startIndex;
        _mode = mode;
        _zScorePeriod = zScorePeriod;
        _basePrices = new Dictionary<string, decimal>();

        if (_mode == ComparisonMode.Performance)
        {
            foreach (var kvp in data.Series)
            {
                var series = kvp.Value;
                if (series == null || series.Length == 0) continue;
                
                // 表示開始点（startIndex）を基準点とする。欠損している場合は後方、次に前方を探索して補完する。
                int start = Math.Clamp(startIndex, 0, series.Length - 1);
                decimal? foundBase = null;
                
                if (series[start] is { } exact) 
                {
                    foundBase = exact.Close;
                }
                else 
                {
                    // 後方探索（過去の終値を使用）
                    for (int i = start - 1; i >= 0; i--) 
                    { 
                        if (series[i] is { } prev) { foundBase = prev.Close; break; } 
                     }
                    // それでも見つからない場合のみ前方探索
                    if (foundBase == null) 
                    {
                        for (int i = start + 1; i < series.Length; i++) 
                        { 
                            if (series[i] is { } next) { foundBase = next.Close; break; } 
                        }
                    }
                }

                if (foundBase != null)
                {
                    _basePrices[kvp.Key] = foundBase.Value;
                }
            }
        }
    }

    public (decimal High, decimal Low) GetPriceRange(CoreCandleData candle)
    {
        // タイムスタンプからインデックスを特定
        int index = Array.BinarySearch(_data.Timestamps, candle.Timestamp);
        if (index < 0) return (0, 0);

        decimal max = decimal.MinValue;
        decimal min = decimal.MaxValue;
        bool found = false;

        foreach (var kvp in _data.Series)
        {
            var symbol = kvp.Key;
            var series = kvp.Value;
            if (series == null || index >= series.Length || !series[index].HasValue) continue;

            decimal val = 0;
            switch (_mode)
            {
                case ComparisonMode.Performance:
                    if (_basePrices.TryGetValue(symbol, out var basePrice) && basePrice != 0)
                    {
                        val = (series[index]!.Value.Close - basePrice) / basePrice * 100m;
                    }
                    else continue;
                    break;

                case ComparisonMode.Ratio:
                    if (_data.Series.TryGetValue(_data.PrimarySymbol, out var primarySeries) &&
                        primarySeries != null && index < primarySeries.Length && primarySeries[index] is { } primaryCandle)
                    {
                        var primaryClose = primaryCandle.Close;
                        if (primaryClose != 0)
                        {
                            val = series[index]!.Value.Close / primaryClose;
                        }
                        else continue;
                    }
                    else continue;
                    break;

                case ComparisonMode.ZScore:
                    if (ComputeZScore(series, index, _zScorePeriod, out var z))
                    {
                        val = (decimal)z;
                    }
                    else continue;
                    break;

                case ComparisonMode.Spread:
                    if (_data.Series.TryGetValue(_data.PrimarySymbol, out var spreadPrimarySeries) &&
                        spreadPrimarySeries != null && index < spreadPrimarySeries.Length && spreadPrimarySeries[index] is { } spreadPrimaryCandle)
                    {
                        val = series[index]!.Value.Close - spreadPrimaryCandle.Close;
                    }
                    else continue;
                    break;
            }

            if (val > max)
            {
                // RatioモードやSpreadモードかつ基軸銘柄（PrimarySymbol）の場合は、スケーリング計算（max/min）から除外する。
                // これらのモードでは基軸銘柄は常に定数（1.0 または 0.0）であり、他銘柄の変動を優先して表示すべきため。
                bool isStaticBase = (symbol == _data.PrimarySymbol) && (_mode == ComparisonMode.Ratio || _mode == ComparisonMode.Spread);
                if (!isStaticBase)
                {
                    max = val;
                }
            }
            if (val < min)
            {
                bool isStaticBase = (symbol == _data.PrimarySymbol) && (_mode == ComparisonMode.Ratio || _mode == ComparisonMode.Spread);
                if (!isStaticBase)
                {
                    min = val;
                }
            }
            found = true;
        }

        if (!found) return (0, 0);

        // 最小描画レンジ制約（過剰ズームによるノイズ強調を防ぐ）
        // Performance(%)モードでは計2.0%(±1.0%)、Ratioモードでは計0.02(±0.01)を確保する。
        decimal minRange = _mode switch
        {
            ComparisonMode.Ratio => 0.02m,
            ComparisonMode.ZScore => LayoutConstants.ZScoreMinRange, // Ensure ±4.0σ range for clipping visibility
            _ => 2.0m
        };
        if (max - min < minRange)
        {
            decimal mid = (max + min) / 2m;
            max = mid + (minRange / 2m);
            min = mid - (minRange / 2m);
        }

        return (max, min);
    }

    public static bool ComputeZScore(CandleData?[] series, int index, int period, out double zScore)
    {
        zScore = 0;
        if (series == null || period <= 1 || index < 0 || index >= series.Length || index < period - 1) return false;

        double sum = 0;
        int count = 0;
        for (int i = 0; i < period; i++)
        {
            var data = series[index - i];
            if (data.HasValue)
            {
                sum += (double)data.Value.Close;
                count++;
            }
        }
        
        if (count < period) return false; 

        double mean = sum / period;
        double sumSqDiff = 0;
        for (int i = 0; i < period; i++)
        {
            if (series[index - i] is { } data)
            {
                double diff = (double)data.Close - mean;
                sumSqDiff += diff * diff;
            }
        }

        double stdDev = Math.Sqrt(sumSqDiff / period);
        if (stdDev < 1e-10) return false; // Avoid division by zero

        zScore = ((double)series[index]!.Value.Close - mean) / stdDev;
        return true;
    }
}
