using System;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>In-memory <see cref="IChartSettingsManager"/> that exposes how many listeners are attached and how often it was written.</summary>
internal sealed class FakeChartSettingsManager : IChartSettingsManager
{
    private Action? _settingsChanged;

    public GlobalChartSettings Current { get; private set; } = new GlobalChartSettings().Validate();

    public int SubscriberCount => _settingsChanged?.GetInvocationList().Length ?? 0;

    public int UpdateCount { get; private set; }

    public event Action? SettingsChanged
    {
        add => _settingsChanged += value;
        remove => _settingsChanged -= value;
    }

    public Task UpdateAsync(GlobalChartSettings settings)
    {
        UpdateCount++;
        Current = settings.Validate();
        _settingsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public void UpdatePreview(GlobalChartSettings settings)
    {
        Current = settings.Validate();
        _settingsChanged?.Invoke();
    }

    /// <summary>Changes the current settings and raises <see cref="SettingsChanged"/> the way an unrelated settings write would.</summary>
    public void Publish(GlobalChartSettings settings) => UpdatePreview(settings);

    public Task LoadAsync() => Task.CompletedTask;
}
