using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace StockAnalyzer.Avalonia.Views.Spikes;

public class PrototypeCandle
{
    public DateTime Time { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
}

public partial class SkiaSharpPrototypeViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<PrototypeCandle> _candles = new();

    public SkiaSharpPrototypeViewModel()
    {
        GenerateDummyData();
    }

    private void GenerateDummyData()
    {
        var random = new Random(42);
        decimal price = 100m;
        var start = DateTime.Now.AddDays(-100);

        for (int i = 0; i < 100; i++)
        {
            decimal change = (decimal)(random.NextDouble() - 0.5) * 2;
            decimal open = price;
            decimal close = price + change;
            decimal high = Math.Max(open, close) + (decimal)random.NextDouble();
            decimal low = Math.Min(open, close) - (decimal)random.NextDouble();

            Candles.Add(new PrototypeCandle
            {
                Time = start.AddDays(i),
                Open = open,
                High = high,
                Low = low,
                Close = close
            });

            price = close;
        }
    }
}
