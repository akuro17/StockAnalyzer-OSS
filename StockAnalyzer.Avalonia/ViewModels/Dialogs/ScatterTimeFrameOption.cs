using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>One entry of the time frame combo box of the scatter metric registration window: the time frame and its localized name.</summary>
public sealed record ScatterTimeFrameOption(TimeFrame Value, string DisplayName);
