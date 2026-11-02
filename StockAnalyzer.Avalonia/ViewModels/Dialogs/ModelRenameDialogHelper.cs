using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

internal static class ModelRenameDialogHelper
{
    public static async Task<(bool Changed, string? StatusMessage)> PromptAndRenameAsync(
        IDialogService dialogService,
        IModelMetadataAnnotationService annotationService,
        ModelMetadataKey key,
        Func<string, string>? localizer = null,
        CancellationToken ct = default)
    {
        string Localize(string k) => localizer != null ? localizer(k) : LocalizationManager.Instance[k];

        var currentName = annotationService.GetAnnotation(key)?.CustomDisplayName ?? string.Empty;
        var title = Localize("Model_Rename_Title");
        var prompt = Localize("Model_Rename_Prompt");

        var input = await dialogService.ShowInputAsync(title, prompt, currentName).ConfigureAwait(true);
        if (input is null)
        {
            return (false, null);
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            try
            {
                await annotationService.ClearCustomDisplayNameAsync(key, reason: "User cleared custom display name", cancellationToken: ct).ConfigureAwait(true);
                return (true, Localize("Model_Rename_ResetSuccess"));
            }
            catch (Exception ex)
            {
                return (false, string.Format(CultureInfo.CurrentCulture, Localize("Model_Rename_Failed"), ex.Message));
            }
        }

        var trimmed = input.Trim();
        if (trimmed.Length > ModelMetadataAnnotationService.MaxDisplayNameLength)
        {
            return (false, Localize("Model_Rename_LengthError"));
        }

        if (trimmed.Any(char.IsControl))
        {
            return (false, Localize("Model_Rename_InvalidCharError"));
        }

        try
        {
            await annotationService.SetCustomDisplayNameAsync(key, trimmed, reason: "User renamed model", cancellationToken: ct).ConfigureAwait(true);
            return (true, string.Format(CultureInfo.CurrentCulture, Localize("Model_Rename_Success"), trimmed));
        }
        catch (ArgumentException)
        {
            if (trimmed.Length > ModelMetadataAnnotationService.MaxDisplayNameLength)
                return (false, Localize("Model_Rename_LengthError"));
            if (trimmed.Any(char.IsControl))
                return (false, Localize("Model_Rename_InvalidCharError"));
            return (false, string.Format(CultureInfo.CurrentCulture, Localize("Model_Rename_Failed"), "Invalid input"));
        }
        catch (Exception ex)
        {
            return (false, string.Format(CultureInfo.CurrentCulture, Localize("Model_Rename_Failed"), ex.Message));
        }
    }
}
