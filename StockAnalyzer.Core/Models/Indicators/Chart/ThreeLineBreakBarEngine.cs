using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>
/// Incremental Three Line Break (see <see cref="ThreeLineBreakConverter"/> and <see cref="StandardThreeLineBreakStrategy"/>, which it mirrors bar by bar and whose rules it calls instead of copying): closes only,
/// decimal only, with the absolute tolerance <see cref="ChartConstants.ThreeLineBreakTrendThreshold"/>.
/// <list type="bullet">
/// <item>The first bar only fixes the starting close. The first block appears with the first close that differs from it by MORE than the tolerance (strict); its direction is the sign of the difference.</item>
/// <item>Afterwards a bar is ignored when <c>minMove &gt; 0</c> and its close is less than <c>minMove</c> away from the last block's close (equal passes). Otherwise: a close beyond the last block's close by more than
/// the tolerance continues the trend (strict); only when it does not, a close beyond the extreme of the last <c>min(count, lineCount)</c> blocks (lowest low for an up trend, highest high for a down trend) by
/// more than the tolerance reverses it (strict). Continuation is tested before reversal.</item>
/// <item>Only the last <c>lineCount</c> blocks matter for the reversal level, so only they are kept (a bounded window; fewer blocks at the start are handled by <c>min(count, lineCount)</c>, no index can fall outside).</item>
/// <item>Outputs (once a block exists): Close = the close of the last block; Reversal = the price whose break (by more than the tolerance) reverses the trend, i.e. the threshold of
/// <see cref="IThreeLineBreakStrategy.ShouldReverse"/> for the last block (it includes the last block itself); no Update series.</item>
/// </list>
/// </summary>
public sealed class ThreeLineBreakBarEngine : IBlockChartBarEngine
{
    private readonly int _lineCount;
    private readonly decimal _minimumMove;
    private readonly StandardThreeLineBreakStrategy _strategy = new();
    private readonly List<ThreeLineBreakBlock> _window;

    private bool _seeded;
    private decimal _seedClose;
    private DateTime _seedTime;
    private ThreeLineBreakBlock _last;
    private bool _hasBlock;

    /// <param name="lineCount">Blocks that must be broken for a reversal (1 or more).</param>
    /// <param name="minimumMove">Smaller moves than this from the last block's close are ignored; 0 or less disables the filter.</param>
    public ThreeLineBreakBarEngine(int lineCount, decimal minimumMove)
    {
        if (lineCount < 1) throw new ArgumentOutOfRangeException(nameof(lineCount), "At least one line is required.");

        _lineCount = lineCount;
        _minimumMove = minimumMove;
        _window = new List<ThreeLineBreakBlock>(lineCount + 1);
    }

    public BlockChartBarState State { get; private set; } = BlockChartBarState.Undefined;

    public void Feed(CoreCandleData bar)
    {
        decimal close = bar.Close;

        if (!_seeded)
        {
            _seedClose = close;
            _seedTime = bar.Timestamp;
            _seeded = true;
            return;
        }

        if (!_hasBlock)
        {
            decimal difference = close - _seedClose;
            if (Math.Abs(difference) > ChartConstants.ThreeLineBreakTrendThreshold)
            {
                AddBlock(new ThreeLineBreakBlock(_seedTime, bar.Timestamp, _seedClose, close, difference > 0m));
            }
        }
        else if (!(_minimumMove > 0m && Math.Abs(close - _last.ClosePrice) < _minimumMove))
        {
            bool trendIsUp = _last.IsUp;
            if (_strategy.ShouldContinue(close, _last, ChartConstants.ThreeLineBreakTrendThreshold))
            {
                AddBlock(new ThreeLineBreakBlock(_last.EndDate, bar.Timestamp, _last.ClosePrice, close, trendIsUp));
            }
            else if (_strategy.ShouldReverse(close, _window, trendIsUp, _lineCount, ChartConstants.ThreeLineBreakTrendThreshold, out _))
            {
                AddBlock(new ThreeLineBreakBlock(_last.EndDate, bar.Timestamp, _last.ClosePrice, close, !trendIsUp));
            }
        }

        State = Describe();
    }

    private void AddBlock(ThreeLineBreakBlock block)
    {
        _window.Add(block);
        if (_window.Count > _lineCount) _window.RemoveAt(0);

        _last = block;
        _hasBlock = true;
    }

    private BlockChartBarState Describe()
    {
        if (!_hasBlock) return BlockChartBarState.Undefined;

        // The threshold does not depend on the close that is passed; the last close is used only to call the strategy.
        _strategy.ShouldReverse(_last.ClosePrice, _window, _last.IsUp, _lineCount, ChartConstants.ThreeLineBreakTrendThreshold, out decimal threshold);
        return new BlockChartBarState(true, _last.ClosePrice, threshold, null, _last.IsUp);
    }
}
