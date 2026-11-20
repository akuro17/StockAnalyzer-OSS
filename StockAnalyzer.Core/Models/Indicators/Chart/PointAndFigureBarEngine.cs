using StockAnalyzer.Core.Utilities;
using StockAnalyzer.ZeroAllocation;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>
/// Incremental Point and Figure (see <c>PointAndFigureCalculator</c> in ZeroAllocationChartCore.cs, which it mirrors bar by bar): uses High and Low, decimal only, and the calculator's own
/// quantizers (<c>QuantizeDown</c> for tops, <c>QuantizeUp</c> for bottoms; none of the rounding logic is copied).
/// <list type="bullet">
/// <item>The first bar only fixes the baseline (<c>QuantizeDown(close)</c>); it starts no column, as in the calculator (its loop starts at the second bar).</item>
/// <item>No column yet: an X column starts when <c>high &gt;= baseline + S</c>, otherwise an O column when <c>low &lt;= baseline - S</c> (the up test comes first).</item>
/// <item>X column: it extends while the QUANTIZED high is strictly above the top; only when it does not, a reversal is tested on the RAW low: <c>low &lt;= top - R x S</c> (inclusive). The O column is symmetric
/// (quantized low strictly below the bottom, then raw <c>high &gt;= bottom + R x S</c>). So a bar that reaches both conditions extends first, exactly as the calculator decides.</item>
/// <item>A reversal starts the opposite column one box beyond the old end (top - S, bottom + S) and takes the bar's own extreme as its far end.</item>
/// <item>The state is reported after every bar, so the column in progress counts (the calculator commits it at the end of its run). Close = the end of the column (top of X, bottom of O);
/// Reversal and Update from <see cref="PointAndFigureLevels.TryDescribeColumn"/>. The direction is the engine's own, never derived from Open/Close (a one-box column has Open == Close).</item>
/// </list>
/// </summary>
public sealed class PointAndFigureBarEngine : IBlockChartBarEngine
{
    private readonly decimal _boxSize;
    private readonly int _reversalAmount;
    private readonly decimal _reversalValue;
    private readonly ChartRoundingMode _rounding;

    private bool _seeded;
    private int _direction; // BlockChartDirection: none before the first column, then up (X) or down (O)
    private decimal _baseline;
    private decimal _columnHigh;
    private decimal _columnLow;

    /// <param name="boxSize">Box size; must be positive (the overlay mapper only passes resolved, positive sizes).</param>
    /// <param name="reversalAmount">Boxes of a reversal; a smaller number than <see cref="ChartConstants.MinPnfReversalAmount"/> is treated as that (as the calculator does).</param>
    public PointAndFigureBarEngine(decimal boxSize, int reversalAmount, ChartRoundingMode rounding)
    {
        if (boxSize <= 0m) throw new ArgumentOutOfRangeException(nameof(boxSize), "Box size must be positive.");

        _boxSize = boxSize;
        _reversalAmount = Math.Max(reversalAmount, ChartConstants.MinPnfReversalAmount);
        _reversalValue = boxSize * _reversalAmount;
        _rounding = rounding;
    }

    public BlockChartBarState State { get; private set; } = BlockChartBarState.Undefined;

    public void Feed(CoreCandleData bar)
    {
        if (!_seeded)
        {
            _baseline = PointAndFigureCalculator.QuantizeDown(bar.Close, _boxSize, _rounding);
            _columnHigh = _baseline;
            _columnLow = _baseline;
            _direction = BlockChartDirection.None;
            _seeded = true;
            return;
        }

        Step(bar.High, bar.Low);
        State = Describe();
    }

    private void Step(decimal high, decimal low)
    {
        if (_direction == BlockChartDirection.None)
        {
            if (high >= _baseline + _boxSize)
            {
                _direction = BlockChartDirection.Up;
                _columnLow = _baseline;
                _columnHigh = PointAndFigureCalculator.QuantizeDown(high, _boxSize, _rounding);
            }
            else if (low <= _baseline - _boxSize)
            {
                _direction = BlockChartDirection.Down;
                _columnHigh = _baseline;
                _columnLow = PointAndFigureCalculator.QuantizeUp(low, _boxSize, _rounding);
            }

            return;
        }

        if (_direction == BlockChartDirection.Up)
        {
            decimal quantizedHigh = PointAndFigureCalculator.QuantizeDown(high, _boxSize, _rounding);
            if (quantizedHigh > _columnHigh)
            {
                _columnHigh = quantizedHigh;
            }
            else if (low <= _columnHigh - _reversalValue)
            {
                _direction = BlockChartDirection.Down;
                _columnHigh -= _boxSize;
                _columnLow = PointAndFigureCalculator.QuantizeUp(low, _boxSize, _rounding);
            }
        }
        else
        {
            decimal quantizedLow = PointAndFigureCalculator.QuantizeUp(low, _boxSize, _rounding);
            if (quantizedLow < _columnLow)
            {
                _columnLow = quantizedLow;
            }
            else if (high >= _columnLow + _reversalValue)
            {
                _direction = BlockChartDirection.Up;
                _columnLow += _boxSize;
                _columnHigh = PointAndFigureCalculator.QuantizeDown(high, _boxSize, _rounding);
            }
        }
    }

    private BlockChartBarState Describe()
    {
        if (_direction == BlockChartDirection.None) return BlockChartBarState.Undefined;

        bool isUp = _direction > 0;
        decimal columnEnd = isUp ? _columnHigh : _columnLow;
        PointAndFigureLevels.TryDescribeColumn(isUp, columnEnd, _boxSize, _reversalAmount, out decimal reversal, out decimal update);
        return new BlockChartBarState(true, columnEnd, reversal, update, isUp);
    }
}
