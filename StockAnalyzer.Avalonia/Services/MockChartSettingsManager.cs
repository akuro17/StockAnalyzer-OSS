using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using System;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services;

public class MockChartSettingsManager : IChartSettingsManager
{
    public GlobalChartSettings Current { get; private set; } = new();

    public event Action? SettingsChanged;

    public Task UpdateAsync(GlobalChartSettings settings)
    {
        Current = settings;
        SettingsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public void UpdatePreview(GlobalChartSettings settings)
    {
        Current = settings;
        SettingsChanged?.Invoke();
    }

    public Task LoadAsync() => Task.CompletedTask;
}
