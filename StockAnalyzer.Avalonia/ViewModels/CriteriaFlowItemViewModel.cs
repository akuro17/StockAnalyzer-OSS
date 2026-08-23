using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class CriteriaFlowItemViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _label = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private int? _matchedCount;

    [ObservableProperty]
    private string _operatorSymbol = "∩";

    [ObservableProperty]
    private bool _hasNext = true;

    [ObservableProperty]
    private string _prefixBracket = string.Empty;

    [ObservableProperty]
    private string _suffixBracket = string.Empty;

    public string DisplayText
    {
        get
        {
            if (!MatchedCount.HasValue)
                return $"[{Label}: --]";

            var format = LocalizationManager.Instance["Screener_MatchCountFormat"];
            if (string.IsNullOrEmpty(format) || format == "[Screener_MatchCountFormat]")
            {
                format = "[{0}: {1}]";
            }
            return string.Format(format, Label, MatchedCount.Value);
        }
    }
}
