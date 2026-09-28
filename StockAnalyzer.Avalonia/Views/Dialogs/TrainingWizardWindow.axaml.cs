using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Code-behind for the ONNX training wizard window. Shown modelessly (see
/// <see cref="StockAnalyzer.Avalonia.Services.DialogService.ShowTrainingWizardDialogAsync"/>)
/// so a long-running training job stays visible while the user keeps working elsewhere.
/// </summary>
public partial class TrainingWizardWindow : Window
{
    public TrainingWizardWindow()
    {
        InitializeComponent();
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private async void OnBrowseManifestClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(LocalizationManager.Instance["TrainingWizard_ManifestFileType"]) { Patterns = new[] { "*.json" } } },
        });
        if (files.Count > 0 && DataContext is TrainingWizardViewModel viewModel)
            viewModel.SourceManifestPath = files[0].TryGetLocalPath();
    }
}
