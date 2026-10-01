using System.ComponentModel;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Tickers;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>Minimal in-memory <see cref="ITickersSettingsManager"/> for ViewModel tests. Defaults come from
/// <see cref="TickersSettings"/> (production defaults); range rules mirror the real manager.</summary>
public sealed class FakeTickersSettingsManager : ITickersSettingsManager
{
    private readonly TickersSettings _config = new();

    public bool AutoPlayStopAtListEnd { get; private set; }
    public bool AutoPlayStopOnListChange { get; private set; }
    public int AutoPlayIntervalSeconds { get; private set; }
    public TickerColumnSelectionScope ColumnSelectionScope { get; private set; }
    public int AutoPlayMinIntervalSeconds => TickersSettings.MinAutoPlayIntervalSeconds;
    public int AutoPlayMaxIntervalSeconds => _config.AutoPlayMaxIntervalSeconds;
    public bool AutoPlayDefaultStopAtListEnd => _config.AutoPlayDefaultStopAtListEnd;
    public bool AutoPlayDefaultStopOnListChange => _config.AutoPlayDefaultStopOnListChange;
    public int AutoPlayDefaultIntervalSeconds => _config.AutoPlayDefaultIntervalSeconds;

    public int SaveCount { get; private set; }

    public FakeTickersSettingsManager()
    {
        AutoPlayStopAtListEnd = _config.AutoPlayDefaultStopAtListEnd;
        AutoPlayStopOnListChange = _config.AutoPlayDefaultStopOnListChange;
        AutoPlayIntervalSeconds = _config.AutoPlayDefaultIntervalSeconds;
    }

    public bool SetAutoPlayStopAtListEnd(bool value)
    {
        if (AutoPlayStopAtListEnd != value)
        {
            AutoPlayStopAtListEnd = value;
            OnPropertyChanged(nameof(AutoPlayStopAtListEnd));
        }
        return true;
    }

    public bool SetAutoPlayStopOnListChange(bool value)
    {
        if (AutoPlayStopOnListChange != value)
        {
            AutoPlayStopOnListChange = value;
            OnPropertyChanged(nameof(AutoPlayStopOnListChange));
        }
        return true;
    }

    public bool SetAutoPlayIntervalSeconds(int value)
    {
        if (value < AutoPlayMinIntervalSeconds || value > AutoPlayMaxIntervalSeconds)
            return false;
        if (AutoPlayIntervalSeconds != value)
        {
            AutoPlayIntervalSeconds = value;
            OnPropertyChanged(nameof(AutoPlayIntervalSeconds));
        }
        return true;
    }

    public bool SetColumnSelectionScope(TickerColumnSelectionScope value)
    {
        if (!System.Enum.IsDefined(value))
            return false;
        if (ColumnSelectionScope != value)
        {
            ColumnSelectionScope = value;
            OnPropertyChanged(nameof(ColumnSelectionScope));
        }
        return true;
    }

    public Task SaveAsync()
    {
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task LoadAsync() => Task.CompletedTask;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
