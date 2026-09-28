using System.ComponentModel;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services
{
    public interface IPredictionSettingsManager : INotifyPropertyChanged
    {
        int WindowSize { get; }

        void SetWindowSize(int value);

        Task SaveAsync();
        Task LoadAsync();
    }
}
