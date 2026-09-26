using Avalonia.Controls;
using Avalonia.Input;

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
}
