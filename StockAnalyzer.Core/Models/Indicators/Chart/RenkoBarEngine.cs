using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>
/// Incremental Renko (see <c>RenkoCalculator</c> in ZeroAllocationChartCore.cs, which it mirrors bar by bar): closes only, decimal only.
/// State: the direction (0 = no brick yet, +1 up, -1 down) and the top <c>H</c> / bottom <c>L</c> of the last brick (before the first brick: the top and bottom of the grid cell of the first close).
/// <list type="bullet">
/// <item>Continuation (inclusive): up <c>close &gt;= H + S</c>, down <c>close &lt;= L - S</c>. Reversal (inclusive): from up <c>close &lt;= L - R x S</c>, from down <c>close &gt;= H + R x S</c>;
/// the reversal brick is one box beyond the edge of the last brick. Continuation is tested before reversal; after a reversal brick the same close may continue in the new direction.</item>
/// <item>A close that moves several boxes: the number of bricks is COMPUTED (not found by testing brick after brick) and no brick list is kept, so there is no memory growth and no unbounded
/// loop. Up to <see cref="ChartConstants.MaxIteratedRenkoBricksPerBar"/> bricks per bar the top and bottom are accumulated by the same decimal additions as the calculator (identical digits, also for a
/// non-terminating size); a larger gap is added in closed form (O(1)), where the last digits may differ from the calculator's accumulation.</item>
/// <item>Outputs (once a brick exists): Close = top of an up brick / bottom of a down brick; Reversal and Update from <see cref="RenkoLevels"/>.</item>
/// </list>
/// </summary>
public sealed class RenkoBarEngine : IBlockChartBarEngine
{
    private readonly decimal _size;
    private readonly int _reversalBricks;
    private readonly ChartRoundingMode _rounding;

    private bool _seeded;
    private int _direction; // BlockChartDirection: none before the first brick, then up or down
    private decimal _top;
    private decimal _bottom;

    /// <param name="size">Brick size; must be positive (the overlay mapper only passes resolved, positive sizes).</param>
    /// <param name="reversalBricks">Bricks of a reversal; a smaller number than <see cref="ChartConstants.MinRenkoReversalBricks"/> is treated as that (as the calculator does).</param>
    /// <param name="rounding">Grid alignment of the first brick, as in the calculator.</param>
    public RenkoBarEngine(decimal size, int reversalBricks, ChartRoundingMode rounding)
    {
        if (size <= 0m) throw new ArgumentOutOfRangeException(nameof(size), "Brick size must be positive.");

        _size = size;
        _reversalBricks = Math.Max(reversalBricks, ChartConstants.MinRenkoReversalBricks);
        _rounding = rounding;
    }

    public BlockChartBarState State { get; private set; } = BlockChartBarState.Undefined;

    public void Feed(CoreCandleData bar)
    {
        decimal close = bar.Close;

        if (!_seeded)
        {
            // Align the first cell to the grid exactly as RenkoCalculator does; the seed bar itself can create no brick.
            _bottom = _rounding == ChartRoundingMode.None
                ? Math.Floor(close / _size) * _size
                : ChartMath.Quantize(close, _size, _rounding);
            _top = _bottom + _size;
            _direction = BlockChartDirection.None;
            _seeded = true;
        }

        Step(close);
        State = Describe();
    }

    private void Step(decimal close)
    {
        decimal reversalDistance = _size * _reversalBricks;

        if (_direction == BlockChartDirection.None)
        {
            if (close >= _top + _size)
            {
                _direction = BlockChartDirection.Up;
                ContinueUp(close);
            }
            else if (close <= _bottom - _size)
            {
                _direction = BlockChartDirection.Down;
                ContinueDown(close);
            }
        }
        else if (_direction == BlockChartDirection.Up)
        {
            if (close >= _top + _size)
            {
                ContinueUp(close);
            }
            else if (close <= _bottom - reversalDistance)
            {
                // Reversal brick: from the bottom of the last up brick down one box.
                _direction = BlockChartDirection.Down;
                _top = _bottom;
                _bottom -= _size;
                if (close <= _bottom - _size) ContinueDown(close);
            }
        }
        else
        {
            if (close <= _bottom - _size)
            {
                ContinueDown(close);
            }
            else if (close >= _top + reversalDistance)
            {
                // Reversal brick: from the top of the last down brick up one box.
                _direction = BlockChartDirection.Up;
                _bottom = _top;
                _top += _size;
                if (close >= _top + _size) ContinueUp(close);
            }
        }
    }

    /// <summary>
    /// Adds every up brick the close reaches (at least one by precondition). Up to <see cref="ChartConstants.MaxIteratedRenkoBricksPerBar"/> bricks are added exactly as the calculator
    /// does (top += size, the old top becomes the bottom), so the digits are identical; a larger count is added in closed form (count x size) without iterating.
    /// </summary>
    private void ContinueUp(decimal close)
    {
        decimal count = CountBoxes(close - _top, _size);
        if (count > ChartConstants.MaxIteratedRenkoBricksPerBar)
        {
            _top += count * _size;
            _bottom = _top - _size;
            return;
        }

        for (decimal added = 0m; added < count; added += 1m)
        {
            _bottom = _top;
            _top += _size;
        }
    }

    /// <summary>Adds every down brick the close reaches (at least one by precondition); same exactness rule as <see cref="ContinueUp"/>.</summary>
    private void ContinueDown(decimal close)
    {
        decimal count = CountBoxes(_bottom - close, _size);
        if (count > ChartConstants.MaxIteratedRenkoBricksPerBar)
        {
            _bottom -= count * _size;
            _top = _bottom + _size;
            return;
        }

        for (decimal added = 0m; added < count; added += 1m)
        {
            _top = _bottom;
            _bottom -= _size;
        }
    }

    /// <summary>
    /// The largest whole number n with <c>n x size &lt;= distance</c>, i.e. the number of iterations of the calculator's "while close reaches the next brick" loop.
    /// The division gives the candidate; the two correction loops make the result exact against the same inclusive comparison the calculator uses (each runs at most once or twice).
    /// </summary>
    private static decimal CountBoxes(decimal distance, decimal size)
    {
        decimal count = Math.Floor(distance / size);
        while (count > 0m && distance < count * size) count -= 1m;
        while (distance >= (count + 1m) * size) count += 1m;
        return count;
    }

    private BlockChartBarState Describe()
    {
        if (_direction == BlockChartDirection.None) return BlockChartBarState.Undefined;

        bool isUp = _direction > 0;
        RenkoLevels.TryDescribe(isUp, _top, _bottom, _size, _reversalBricks, out decimal reversal, out decimal update);
        return new BlockChartBarState(true, isUp ? _top : _bottom, reversal, update, isUp);
    }
}
