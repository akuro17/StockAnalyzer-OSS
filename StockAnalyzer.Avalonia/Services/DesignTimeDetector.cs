using StockAnalyzer.Core.Interfaces;

namespace StockAnalyzer.Avalonia.Services;

public class DesignTimeDetector : IDesignTimeDetector
{
    public bool IsDesignMode => global::Avalonia.Controls.Design.IsDesignMode;
}
