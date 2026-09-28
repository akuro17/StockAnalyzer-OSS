using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Interfaces; // Namespace matches ITimeSeriesEngine

public class DataUpdateEventArgs : EventArgs
{
    public string Symbol { get; }
    public TimeFrame TimeFrame { get; }
    public CandleData NewData { get; }
    public bool IsRealTime { get; }

    public DataUpdateEventArgs(string symbol, TimeFrame tf, CandleData newData, bool isRealTime)
    {
        Symbol = symbol;
        TimeFrame = tf;
        NewData = newData;
        IsRealTime = isRealTime;
    }
}
