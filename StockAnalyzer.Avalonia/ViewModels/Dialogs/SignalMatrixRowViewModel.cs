using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class SignalMatrixRowViewModel : ObservableObject
{
    public SignalTargetType TargetType { get; }
    public string TargetName { get; }

    [ObservableProperty]
    private bool? _flagState;

    public ObservableCollection<string> BundledStatusList { get; } = new();

    public SignalMatrixRowViewModel(SignalTargetType targetType, string targetName, bool? flagState = null)
    {
        TargetType = targetType;
        TargetName = targetName;
        FlagState = flagState;
    }
}
