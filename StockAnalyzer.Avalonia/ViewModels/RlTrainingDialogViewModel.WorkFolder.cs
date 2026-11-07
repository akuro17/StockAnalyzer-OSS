using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Avalonia.ViewModels;

public sealed partial class RlTrainingDialogViewModel
{
    /// <summary>
    /// Registered models whose work folder (the folder they were trained in) no longer exists. They stay usable (a registered model is a self-contained copy) and are
    /// only labelled in the lists; the owner can remove such a model with <see cref="RemoveSelectedModelCommand"/>.
    /// </summary>
    private IReadOnlySet<string> _modelsWithoutWorkFolder = new HashSet<string>();

    public bool SelectedModelHasNoWorkFolder => SelectedGeneration is { } generation && _modelsWithoutWorkFolder.Contains(generation.ModelId);
    public bool CanRemoveSelectedModel => Editable && IsStorageWritable && _dialogService is not null && SelectedModelHasNoWorkFolder;
    public bool CanRenameSelectedModel => Editable && IsStorageWritable && HasSelectedModel && _dialogService is not null && _modelMetadataAnnotationService is not null;

    private void NotifyRemoveModel()
    {
        OnPropertyChanged(nameof(SelectedModelHasNoWorkFolder)); OnPropertyChanged(nameof(CanRemoveSelectedModel));
        OnPropertyChanged(nameof(CanRenameSelectedModel));
        RemoveSelectedModelCommand.NotifyCanExecuteChanged();
        RenameSelectedModelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedModel))]
    private async Task RemoveSelectedModelAsync()
    {
        if (!CanRemoveSelectedModel || SelectedGeneration is not { } generation || _dialogService is null || _disposed) return;
        string modelId = generation.ModelId;
        bool confirmed = await _dialogService.ShowConfirmationAsync(_localization.GetString("Rl.Ui.RemoveModel.Title"),
            RlUiDisplay.RemoveModelQuestion(modelId, ActiveId == modelId, _localization));
        if (!confirmed || _disposed) return;
        BeginOperation(RlDialogOperation.RemovingModel);
        try
        {
            var result = await Task.Run(() => _registry.RemoveRlGenerationAsync(modelId));
            if (_disposed) return;
            Message = RlUiDisplay.RemoveModelResult(result, modelId, _localization);
        }
        catch (Exception error) { if (!_disposed) SetErrorMessage(error); }
        finally { EndOperation(); }
        await RefreshGenerationsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRenameSelectedModel))]
    private async Task RenameSelectedModelAsync()
    {
        if (!CanRenameSelectedModel || SelectedGeneration is not { } generation || _dialogService is null || _modelMetadataAnnotationService is null || _disposed) return;
        var key = new ModelMetadataKey(ModelArtifactKind.ReinforcementLearning, generation.ModelId);
        var (changed, statusMessage) = await ModelRenameDialogHelper.PromptAndRenameAsync(
            _dialogService,
            _modelMetadataAnnotationService,
            key,
            _localization.GetString).ConfigureAwait(true);

        if (_disposed) return;

        if (statusMessage is not null)
        {
            Message = statusMessage;
        }

        if (changed)
        {
            RebuildChoices();
            OnPropertyChanged(nameof(SelectedModelDisplay));
            NotifyUi();
        }
    }
}
