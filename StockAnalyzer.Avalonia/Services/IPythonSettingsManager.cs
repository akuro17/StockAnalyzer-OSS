using System.ComponentModel;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services
{
    public interface IPythonSettingsManager : INotifyPropertyChanged
    {
        bool ShowUpdateConfirmationDialog { get; }

        void SetShowUpdateConfirmationDialog(bool value);

        Task SaveAsync();
        Task LoadAsync();
    }
}
