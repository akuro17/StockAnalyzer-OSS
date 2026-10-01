using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Tickers;

namespace StockAnalyzer.Avalonia.Services
{
    /// <summary>
    /// Settings &gt; Tickers store (Y:\Temp\sa_implementation_plan_TickersSettingsCategory.md). Defaults and the
    /// interval upper bound come from <see cref="TickersSettings"/> (appsettings.json "Tickers"); the values the
    /// user changed persist in Data/Config/user_tickers_settings.json. State is mutated on the UI thread only.
    /// </summary>
    public class TickersSettingsManager : ITickersSettingsManager
    {
        private const string SettingsFileName = "user_tickers_settings.json";

        private readonly string _filePath;
        private readonly ILogger _logger;
        private readonly TickersSettings _config;

        // AtomicJsonFile writes through one shared "<file>.tmp" opened with FileShare.None, so two overlapping saves
        // (e.g. a double click on Save) would make the second fail with an IOException; saves run one at a time.
        private readonly SemaphoreSlim _saveGate = new(1, 1);

        private bool _autoPlayStopAtListEnd;
        private bool _autoPlayStopOnListChange;
        private int _autoPlayIntervalSeconds;
        private TickerColumnSelectionScope _columnSelectionScope;

        public TickersSettingsManager(
            IOptions<TickersSettings> options,
            ILogger<TickersSettingsManager>? logger = null,
            string? filePath = null)
        {
            ArgumentNullException.ThrowIfNull(options);

            _config = options.Value;
            _config.Validate();
            _logger = (ILogger?)logger ?? NullLogger.Instance;
            _filePath = filePath ?? StockAnalyzer.Core.Common.PathDiscovery.ResolveConfigPath(SettingsFileName);

            _autoPlayStopAtListEnd = _config.AutoPlayDefaultStopAtListEnd;
            _autoPlayStopOnListChange = _config.AutoPlayDefaultStopOnListChange;
            _autoPlayIntervalSeconds = _config.AutoPlayDefaultIntervalSeconds;
            _columnSelectionScope = default;
        }

        public bool AutoPlayStopAtListEnd => _autoPlayStopAtListEnd;
        public bool AutoPlayStopOnListChange => _autoPlayStopOnListChange;
        public int AutoPlayIntervalSeconds => _autoPlayIntervalSeconds;
        public int AutoPlayMinIntervalSeconds => TickersSettings.MinAutoPlayIntervalSeconds;
        public int AutoPlayMaxIntervalSeconds => _config.AutoPlayMaxIntervalSeconds;
        public TickerColumnSelectionScope ColumnSelectionScope => _columnSelectionScope;
        public bool AutoPlayDefaultStopAtListEnd => _config.AutoPlayDefaultStopAtListEnd;
        public bool AutoPlayDefaultStopOnListChange => _config.AutoPlayDefaultStopOnListChange;
        public int AutoPlayDefaultIntervalSeconds => _config.AutoPlayDefaultIntervalSeconds;

        public bool SetAutoPlayStopAtListEnd(bool value)
        {
            if (_autoPlayStopAtListEnd != value)
            {
                _autoPlayStopAtListEnd = value;
                OnPropertyChanged(nameof(AutoPlayStopAtListEnd));
            }
            return true;
        }

        public bool SetAutoPlayStopOnListChange(bool value)
        {
            if (_autoPlayStopOnListChange != value)
            {
                _autoPlayStopOnListChange = value;
                OnPropertyChanged(nameof(AutoPlayStopOnListChange));
            }
            return true;
        }

        public bool SetAutoPlayIntervalSeconds(int value)
        {
            if (!IsIntervalInRange(value))
                return false;

            if (_autoPlayIntervalSeconds != value)
            {
                _autoPlayIntervalSeconds = value;
                OnPropertyChanged(nameof(AutoPlayIntervalSeconds));
            }
            return true;
        }

        public bool SetColumnSelectionScope(TickerColumnSelectionScope value)
        {
            if (!Enum.IsDefined(value))
                return false;

            if (_columnSelectionScope != value)
            {
                _columnSelectionScope = value;
                OnPropertyChanged(nameof(ColumnSelectionScope));
            }
            return true;
        }

        private bool IsIntervalInRange(int value) =>
            value >= TickersSettings.MinAutoPlayIntervalSeconds && value <= _config.AutoPlayMaxIntervalSeconds;

        /// <summary>
        /// Saves the current values. The snapshot is taken when the call is made (before any await) and saves then run
        /// one at a time. Waiters of the gate are released in arrival order in practice, but <see cref="SemaphoreSlim"/>
        /// does not document that; the values are read on the UI thread and every save writes the state of its own call,
        /// so a save that runs later can only write an equal or newer state unless the runtime ever reorders waiters.
        /// </summary>
        public async Task SaveAsync()
        {
            // Owned snapshot, taken before the first await (also before waiting for the save gate).
            var data = new TickersPersistenceData
            {
                AutoPlayStopAtListEnd = _autoPlayStopAtListEnd,
                AutoPlayStopOnListChange = _autoPlayStopOnListChange,
                AutoPlayIntervalSeconds = _autoPlayIntervalSeconds,
                ColumnSelectionScope = _columnSelectionScope
            };

            await _saveGate.WaitAsync();
            try
            {
                await StockAnalyzer.Core.Common.AtomicJsonFile.SaveAsync(_filePath, data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save tickers settings to {Path}.", _filePath);
            }
            finally
            {
                _saveGate.Release();
            }
        }

        /// <summary>
        /// A missing file keeps the defaults and writes nothing. A present field that is out of range or an
        /// undefined enum value makes the whole load an error: all in-memory values stay at their defaults, the
        /// error is logged and the file is left untouched (a value is never clamped).
        /// </summary>
        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return;

                var data = await StockAnalyzer.Core.Common.AtomicJsonFile.LoadAsync<TickersPersistenceData?>(_filePath);
                if (!data.HasValue)
                    return;

                var loaded = data.Value;
                if (loaded.AutoPlayIntervalSeconds is { } interval && !IsIntervalInRange(interval))
                {
                    _logger.LogError("Tickers settings file {Path} rejected: AutoPlayIntervalSeconds {Value} is outside [{Min}, {Max}].",
                        _filePath, interval, TickersSettings.MinAutoPlayIntervalSeconds, _config.AutoPlayMaxIntervalSeconds);
                    return;
                }
                if (loaded.ColumnSelectionScope is { } scope && !Enum.IsDefined(scope))
                {
                    _logger.LogError("Tickers settings file {Path} rejected: ColumnSelectionScope {Value} is not defined.", _filePath, (int)scope);
                    return;
                }

                if (loaded.AutoPlayStopAtListEnd is { } stopAtListEnd)
                    SetAutoPlayStopAtListEnd(stopAtListEnd);
                if (loaded.AutoPlayStopOnListChange is { } stopOnListChange)
                    SetAutoPlayStopOnListChange(stopOnListChange);
                if (loaded.AutoPlayIntervalSeconds is { } validInterval)
                    SetAutoPlayIntervalSeconds(validInterval);
                if (loaded.ColumnSelectionScope is { } validScope)
                    SetColumnSelectionScope(validScope);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load tickers settings from {Path}; defaults are kept.", _filePath);
            }
        }

        // Nullable so an absent key (file written before a field existed) keeps the default instead of
        // being misread as an explicit false/0.
        private readonly record struct TickersPersistenceData
        {
            public bool? AutoPlayStopAtListEnd { get; init; }
            public bool? AutoPlayStopOnListChange { get; init; }
            public int? AutoPlayIntervalSeconds { get; init; }
            public TickerColumnSelectionScope? ColumnSelectionScope { get; init; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
