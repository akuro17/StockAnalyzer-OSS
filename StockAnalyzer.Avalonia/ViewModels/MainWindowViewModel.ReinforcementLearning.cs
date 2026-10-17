using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
namespace StockAnalyzer.Avalonia.ViewModels;
public partial class MainWindowViewModel
{
    [RelayCommand] private Task OpenRlTrainingAsync() => _dialogService.ShowRlTrainingDialogAsync(ChartViewModel);
    [RelayCommand] private Task OpenRlPlaybackAsync() => _dialogService.ShowRlPlaybackWindowAsync();
    [RelayCommand] private Task OpenPortfolioRlAsync() => _dialogService.ShowPortfolioRlDialogAsync();
}
