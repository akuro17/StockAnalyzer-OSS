using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>
/// Incremental Kagi (see <c>KagiCalculator</c> in ZeroAllocationChartCore.cs, which it mirrors bar by bar): closes only, decimal only.
/// <list type="bullet">
/// <item>The first bar only fixes the starting price. No direction yet: it is set by the first close that moves at least S from the current price (inclusive); until then nothing is confirmed
/// (the calculator draws a flat anchor segment there, which has no direction and no reversal price).</item>
/// <item>With a direction: a close beyond the current price (strictly) continues the line; only when it does not, a close at or beyond <c>S</c> against the line (inclusive) reverses it, and the new
/// line starts at the old extreme. So continuation is decided before reversal.</item>
/// <item>The Yang/Yin state of the calculator (shoulder and waist prices) only decides how a segment is split and coloured; it never changes the price or the direction, so it is not part of this
/// engine. It is also NOT the Reversal price (which is the turning price below).</item>
/// <item>Outputs (once a direction exists): Close = the current price (end of the current segment); Reversal = the price a close must reach to reverse the line, <see cref="KagiLevels.ReversalPrice"/>
/// (current price - S for an up line, + S for a down line); no Update series.</item>
/// </list>
/// </summary>
public sealed class KagiBarEngine : IBlockChartBarEngine
{
    private readonly decimal _reversalAmount;

    private bool _seeded;
    private int _direction; // BlockChartDirection: none before the first line, then up or down
    private decimal _currentPrice;

    /// <param name="reversalAmount">The reversal amount (price); must be positive (the overlay mapper only passes resolved, positive sizes).</param>
    public KagiBarEngine(decimal reversalAmount)
    {
        if (reversalAmount <= 0m) throw new ArgumentOutOfRangeException(nameof(reversalAmount), "Reversal amount must be positive.");

        _reversalAmount = reversalAmount;
    }

    public BlockChartBarState State { get; private set; } = BlockChartBarState.Undefined;

    public void Feed(CoreCandleData bar)
    {
        decimal price = bar.Close;

        if (!_seeded)
        {
            _currentPrice = price;
            _direction = BlockChartDirection.None;
            _seeded = true;
            return;
        }

        Step(price);
        State = Describe();
    }

    private void Step(decimal price)
    {
        if (_direction == BlockChartDirection.None)
        {
            if (Math.Abs(price - _currentPrice) >= _reversalAmount)
            {
                _direction = price > _currentPrice ? BlockChartDirection.Up : BlockChartDirection.Down;
                _currentPrice = price;
            }

            return;
        }

        if ((_direction == BlockChartDirection.Up && price > _currentPrice) || (_direction == BlockChartDirection.Down && price < _currentPrice))
        {
            _currentPrice = price;
        }
        else if ((_direction == BlockChartDirection.Up && price <= _currentPrice - _reversalAmount) ||
                 (_direction == BlockChartDirection.Down && price >= _currentPrice + _reversalAmount))
        {
            // The line turns at its extreme and the new line runs from there to this close.
            _direction = -_direction;
            _currentPrice = price;
        }
    }

    private BlockChartBarState Describe()
    {
        if (_direction == BlockChartDirection.None) return BlockChartBarState.Undefined;

        bool isUp = _direction > 0;
        return new BlockChartBarState(true, _currentPrice, KagiLevels.ReversalPrice(isUp, _currentPrice, _reversalAmount), null, isUp);
    }
}
