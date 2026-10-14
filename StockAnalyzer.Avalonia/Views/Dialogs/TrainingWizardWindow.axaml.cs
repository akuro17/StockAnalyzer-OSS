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
        SubWindowKeys.CloseOnEscape(this);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    // async void: an exception leaving this handler would be raised on the UI synchronization context and end the process,
    // so the whole body is guarded (AsyncVoidHandlerGuardTests enforces this for every async void method).
    private async void OnBrowseManifestClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(LocalizationManager.Instance["TrainingWizard_ManifestFileType"]) { Patterns = new[] { "*.json" } } },
            });
            if (files.Count > 0 && DataContext is TrainingWizardViewModel viewModel)
                viewModel.SourceManifestPath = files[0].TryGetLocalPath();
        }
        catch (System.OperationCanceledException)
        {
            // The user cancelled the picker.
        }
        catch (System.Exception ex)
        {
            Serilog.Log.Error(ex, "{Handler} failed", nameof(OnBrowseManifestClick));
        }
    }
}
