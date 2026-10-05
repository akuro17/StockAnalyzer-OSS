using System;
using System.IO;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public readonly record struct RlDecimalInterval(decimal Lower, decimal Upper)
{
    public static RlDecimalInterval Exact(decimal value) => new(value, value);
    public bool Contains(decimal value) => Lower <= value && value <= Upper;
    public decimal MaximumErrorFrom(decimal reference) => Math.Max(Math.Abs(Lower - reference), Math.Abs(Upper - reference));
}

public sealed record RlFinancialErrorRow(int Decision, decimal CashError, decimal CashErrorBound, decimal EquityError,
    decimal EquityErrorBound, decimal RealizedGrossError, decimal RealizedGrossBound, decimal RealizedNetError,
    decimal RealizedNetBound, decimal FillPriceError, decimal FillPriceBound, decimal CommissionError,
    decimal SlippageError, decimal SlippageBound, decimal NativePeak, decimal ExportedPeak,
    decimal NativeDrawdown, decimal ExportedDrawdown, decimal NativeMaximumDrawdown, decimal ExportedMaximumDrawdown);

/// <summary>Decimal endpoint proof for a paired lifecycle. It never produces trades or feeds an account.</summary>
public sealed class RlFinancialEnvelope
{
    private readonly BacktestConfiguration _costs;
    private RlDecimalInterval _cash, _gross, _net, _slippage;
    private decimal _entryFee;
    private decimal? _stopLower, _stopUpper;
    public RlFinancialEnvelope(BacktestConfiguration costs)
    { _costs = costs; _cash = RlDecimalInterval.Exact(costs.InitialCapital); }

    public void RetainStopInterval(decimal entryPrice, decimal lowerDistance, decimal upperDistance)
    {
        if (lowerDistance > upperDistance) throw new InvalidDataException("Invalid retained stop distance interval.");
        _stopLower = entryPrice * (1m - upperDistance);
        _stopUpper = entryPrice * (1m - lowerDistance);
    }

    public RlDecimalInterval StopFillInterval(CandleData bar)
    {
        if (_stopLower is null || _stopUpper is null) throw new InvalidDataException("A stop fill has no captured entry interval.");
        // Use the same directional pricing and decimal rounding as the Core fill owner, not an ML atol.
        return new(FillPricing.ApplySlippage(Math.Min(bar.Open, _stopLower.Value), OrderSide.Sell, _costs.SlippageRatio),
            FillPricing.ApplySlippage(Math.Min(bar.Open, _stopUpper.Value), OrderSide.Sell, _costs.SlippageRatio));
    }

    public decimal ApplyFill(BacktestFill reference, BacktestFill actual, OrderType type, CandleData bar,
        decimal quantityBefore, decimal entryPrice, decimal marginBefore, bool forcedLiquidation)
    {
        if (reference.FillId != actual.FillId || reference.OrderId != actual.OrderId || reference.BarIndex != actual.BarIndex
            || reference.FillTime != actual.FillTime || reference.Side != actual.Side || reference.Quantity != actual.Quantity
            || reference.Commission != actual.Commission)
            throw new InvalidDataException("Actor changed fill identity, time, side, quantity or commission.");
        bool stop = type == OrderType.Stop && !forcedLiquidation;
        var price = stop ? StopFillInterval(bar) : RlDecimalInterval.Exact(reference.Price);
        if (!price.Contains(reference.Price) || !price.Contains(actual.Price)) throw new InvalidDataException("Fill price escaped its decimal pricing interval.");
        // A slipped Sell reference amount is monotone in raw price; compute its actual endpoints as Core does.
        RlDecimalInterval slip;
        if (stop)
        {
            decimal rawLower = Math.Min(bar.Open, _stopLower!.Value), rawUpper = Math.Min(bar.Open, _stopUpper!.Value);
            // Subtracting rounded decimal prices need not be monotone. Cross endpoints enclose that subtraction too.
            slip = _costs.SlippageRatio == 0m ? RlDecimalInterval.Exact(0m)
                : new(FillPricing.ComputeSlippageAmount(Math.Min(price.Upper, rawLower), rawLower, reference.Quantity),
                    FillPricing.ComputeSlippageAmount(Math.Min(price.Lower, rawUpper), rawUpper, reference.Quantity));
        }
        else slip = RlDecimalInterval.Exact(reference.SlippageAmount);
        if (!slip.Contains(reference.SlippageAmount) || !slip.Contains(actual.SlippageAmount))
            throw new InvalidDataException("Fill slippage amount escaped its decimal interval.");
        _slippage = new(_slippage.Lower + slip.Lower, _slippage.Upper + slip.Upper);
        if (reference.Side == OrderSide.Buy)
        {
            if (quantityBefore != 0m || stop) throw new InvalidDataException("Unexpected entry lifecycle.");
            decimal margin = reference.Quantity * reference.Price * _costs.InitialMarginRatio;
            _cash = new(_cash.Lower - (margin + reference.Commission), _cash.Upper - (margin + reference.Commission));
            _entryFee = reference.Commission;
        }
        else
        {
            if (quantityBefore != reference.Quantity || quantityBefore <= 0m) throw new InvalidDataException("Exit does not close the exact paired quantity.");
            decimal lowerGross = quantityBefore * (price.Lower - entryPrice), upperGross = quantityBefore * (price.Upper - entryPrice);
            // Match ClosePosition's expression order at each endpoint. A q*real-number price bound can understate decimal rounding.
            _cash = new(_cash.Lower + marginBefore + lowerGross - reference.Commission,
                _cash.Upper + marginBefore + upperGross - reference.Commission);
            _gross = new(_gross.Lower + lowerGross, _gross.Upper + upperGross);
            _net = new(_net.Lower + (lowerGross - _entryFee - reference.Commission),
                _net.Upper + (upperGross - _entryFee - reference.Commission));
            _entryFee = 0m;
        }
        return price.MaximumErrorFrom(reference.Price);
    }

    public RlFinancialErrorRow Verify(RlSessionSnapshot reference, RlSessionSnapshot actual, decimal referenceGross,
        decimal actualGross, decimal referenceNet, decimal actualNet, decimal referenceCommission, decimal actualCommission,
        decimal referenceSlippage, decimal actualSlippage, decimal fillError, decimal fillBound)
    {
        if (reference.Quantity != actual.Quantity || reference.EntryPrice != actual.EntryPrice
            || reference.EquityPoint.HeldMargin != actual.EquityPoint.HeldMargin || reference.UnrealizedPnL != actual.UnrealizedPnL
            || referenceCommission != actualCommission)
            throw new InvalidDataException("Paired quantity, entry, margin, unrealized value or fee changed.");
        var equity = new RlDecimalInterval(_cash.Lower + reference.EquityPoint.HeldMargin + reference.UnrealizedPnL,
            _cash.Upper + reference.EquityPoint.HeldMargin + reference.UnrealizedPnL);
        if (!_cash.Contains(reference.EquityPoint.Cash) || !_cash.Contains(actual.EquityPoint.Cash)
            || !equity.Contains(reference.EquityPoint.Equity) || !equity.Contains(actual.EquityPoint.Equity)
            || !_gross.Contains(referenceGross) || !_gross.Contains(actualGross) || !_net.Contains(referenceNet) || !_net.Contains(actualNet)
            || !_slippage.Contains(referenceSlippage) || !_slippage.Contains(actualSlippage))
            throw new InvalidDataException("Paired financial ledger escaped its decimal endpoint proof.");
        if (reference.ActiveStopPrice.HasValue != actual.ActiveStopPrice.HasValue
            || (actual.ActiveStopPrice is { } stop && (_stopLower is null || stop < _stopLower || stop > _stopUpper)))
            throw new InvalidDataException("Protective stop is outside the retained mapped interval.");
        return new(reference.CompletedDecisions, Math.Abs(reference.EquityPoint.Cash - actual.EquityPoint.Cash), _cash.MaximumErrorFrom(reference.EquityPoint.Cash),
            Math.Abs(reference.EquityPoint.Equity - actual.EquityPoint.Equity), equity.MaximumErrorFrom(reference.EquityPoint.Equity),
            Math.Abs(referenceGross - actualGross), _gross.MaximumErrorFrom(referenceGross), Math.Abs(referenceNet - actualNet), _net.MaximumErrorFrom(referenceNet),
            fillError, fillBound, Math.Abs(referenceCommission - actualCommission), Math.Abs(referenceSlippage - actualSlippage), _slippage.MaximumErrorFrom(referenceSlippage),
            reference.PeakEquity, actual.PeakEquity, reference.Drawdown, actual.Drawdown, reference.MaximumDrawdown, actual.MaximumDrawdown);
    }
}
