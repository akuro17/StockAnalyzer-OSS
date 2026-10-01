using System.ComponentModel;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services.Tickers
{
    /// <summary>Settings &gt; Tickers: auto play behavior of the Tickers tab and the scope of its Column
    /// Customization selection. Every <c>Set*</c> returns <c>true</c> when the value is applied (or already
    /// equal) and <c>false</c> when it is rejected; a rejected value never changes state.</summary>
    public interface ITickersSettingsManager : INotifyPropertyChanged
    {
        /// <summary>When the interval elapses while the last row of the list is the current ticker, auto play
        /// stops instead of returning to the first row.</summary>
        bool AutoPlayStopAtListEnd { get; }

        /// <summary>Auto play stops when a different ticker list is selected.</summary>
        bool AutoPlayStopOnListChange { get; }

        /// <summary>Whole seconds between two automatic ticker changes, within
        /// [<see cref="AutoPlayMinIntervalSeconds"/>, <see cref="AutoPlayMaxIntervalSeconds"/>].</summary>
        int AutoPlayIntervalSeconds { get; }

        /// <summary>Smallest accepted <see cref="AutoPlayIntervalSeconds"/> (seconds).</summary>
        int AutoPlayMinIntervalSeconds { get; }

        /// <summary>Largest accepted <see cref="AutoPlayIntervalSeconds"/> (seconds), from configuration.</summary>
        int AutoPlayMaxIntervalSeconds { get; }

        /// <summary>Whether the Column Customization selection is kept per ticker list or shared by all lists.</summary>
        TickerColumnSelectionScope ColumnSelectionScope { get; }

        /// <summary>Default of <see cref="AutoPlayStopAtListEnd"/> (used by Reset).</summary>
        bool AutoPlayDefaultStopAtListEnd { get; }

        /// <summary>Default of <see cref="AutoPlayStopOnListChange"/> (used by Reset).</summary>
        bool AutoPlayDefaultStopOnListChange { get; }

        /// <summary>Default of <see cref="AutoPlayIntervalSeconds"/> (used by Reset).</summary>
        int AutoPlayDefaultIntervalSeconds { get; }

        bool SetAutoPlayStopAtListEnd(bool value);
        bool SetAutoPlayStopOnListChange(bool value);
        bool SetAutoPlayIntervalSeconds(int value);
        bool SetColumnSelectionScope(TickerColumnSelectionScope value);

        Task SaveAsync();
        Task LoadAsync();
    }
}
