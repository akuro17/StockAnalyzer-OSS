using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
namespace StockAnalyzer.Avalonia.ViewModels;
public partial class MainWindowViewModel
{
    [RelayCommand] private Task OpenPredictionModelsAsync() => _dialogService.ShowPredictionModelsWindowAsync();
}
