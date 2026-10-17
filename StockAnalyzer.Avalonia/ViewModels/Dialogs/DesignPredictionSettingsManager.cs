using System.ComponentModel;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>Design-time stand-in of <see cref="IPredictionSettingsManager"/> for the parameterless (designer) constructors of the AI Predictions view models.</summary>
internal sealed class DesignPredictionSettingsManager : IPredictionSettingsManager
{
    public int WindowSize => PredictionSettingsManager.DefaultWindowSize;
    public void SetWindowSize(int value) { }
    public Task SaveAsync() => Task.CompletedTask;
    public Task LoadAsync() => Task.CompletedTask;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}
