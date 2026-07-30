using System.ComponentModel;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services
{
    public interface IFontSettingsManager : INotifyPropertyChanged
    {
        double BaseFontSize { get; }
        double TitleFontSize { get; }
        double DetailFontSize { get; }
        double HelperFontSize { get; }
        void SetBaseFontSize(double size);
        void SetTitleFontSize(double size);
        void SetDetailFontSize(double size);
        void SetHelperFontSize(double size);
        Task SaveAsync();
        Task LoadAsync();
    }
}
