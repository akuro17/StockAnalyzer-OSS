using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Interfaces;

public class BarClosedEventArgs : EventArgs
{
    public string Symbol { get; }
    public TimeFrame TimeFrame { get; }
    public CandleData ClosedBar { get; }

    public BarClosedEventArgs(string symbol, TimeFrame tf, CandleData closedBar)
    {
        Symbol = symbol;
        TimeFrame = tf;
        ClosedBar = closedBar;
    }
}
