using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs.Common;

/// <summary>
/// The shared Save / Load(Replace) / Append / Delete / LoadAll skeleton for the template-picker
/// dialog ViewModels (<c>IndicatorSettingsDialogViewModel</c>,
/// <c>FilterTemplatePickerDialogViewModel</c>, <c>FeatureChannelPickerViewModel</c>). It owns the
/// single mutual-exclusion busy flag, the <see cref="ITemplateService.ValidateAsync{T}"/> gate, the
/// try/catch reporting, and the localized success/failure toast dispatch that were previously
/// duplicated almost line-for-line across all three (CLAUDE.md: shared definitions must be
/// consolidated into a single source of truth).
///
/// <para>
/// The operation-specific pieces - how a template is built from the current editor state, how a
/// loaded template is applied, and how the owning collection is mutated - stay with the caller and
/// are supplied as delegates, so this helper adds no behavioral opinion of its own.
/// </para>
/// </summary>
public sealed class TemplateCrudHelper<TTemplate>
    where TTemplate : TemplateBase
{
    /// <summary>Localization key: template failed validation and was not saved.</summary>
    public const string MsgInvalidForSave = "Msg_Template_InvalidForSave";

    /// <summary>Localization key: template failed validation and was not applied.</summary>
    public const string MsgInvalidForApply = "Msg_Template_InvalidForApply";

    /// <summary>Localization key: template saved.</summary>
    public const string MsgSaved = "Msg_Template_SavedSuccessfully";

    /// <summary>Localization key: an exception was thrown while saving.</summary>
    public const string MsgSaveFailed = "Msg_Template_SaveFailed";

    /// <summary>Localization format key: template loaded (replace); takes the template name.</summary>
    public const string MsgLoaded = "Msg_Template_Loaded";

    /// <summary>Localization key: an exception was thrown while loading (replace).</summary>
    public const string MsgLoadError = "Msg_Template_LoadError";

    /// <summary>Localization format key: template appended; takes the template name.</summary>
    public const string MsgAppended = "Msg_Template_Appended";

    /// <summary>Localization key: an exception was thrown while appending.</summary>
    public const string MsgAppendError = "Msg_Template_AppendError";

    /// <summary>Localization key: template deleted.</summary>
    public const string MsgDeleted = "Msg_Template_Deleted";

    /// <summary>Localization key: an exception was thrown while deleting.</summary>
    public const string MsgDeleteFailed = "Msg_Template_DeleteFailed";

    private readonly ITemplateService _templateService;
    private readonly IToastNotificationService? _toastService;
    private readonly TemplateType _templateType;
    private bool _busy;

    /// <param name="templateService">Persistence/validation backend.</param>
    /// <param name="toastService">
    /// User feedback sink for the localized result toasts. May be <see langword="null"/>:
    /// <c>FeatureChannelPickerViewModel</c> is constructible without one (design-time DataContext,
    /// tests), and its pre-consolidation code guarded every toast call with <c>?.</c> - so a null
    /// sink simply drops the result toasts here too, rather than throwing.
    /// </param>
    /// <param name="templateType">The template category this helper instance manages.</param>
    public TemplateCrudHelper(
        ITemplateService templateService,
        IToastNotificationService? toastService,
        TemplateType templateType)
    {
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _toastService = toastService;
        _templateType = templateType;
    }

    /// <summary>
    /// <see langword="true"/> while any of the operations below is in flight. This is a
    /// non-observable re-entrancy guard by design (it raises no change notification), matching the
    /// private <c>_isTemplateBusy</c> field it replaced in the caller ViewModels.
    /// </summary>
    public bool IsBusy => _busy;

    /// <summary>
    /// Replaces <paramref name="destination"/> with every stored template of this helper's type.
    /// Silently no-ops while another operation is running. When <paramref name="reportErrors"/> is
    /// <see langword="true"/> (the default) exceptions are routed to <paramref name="onError"/> (no
    /// toast, matching the pre-consolidation behavior of the initial load); when it is
    /// <see langword="false"/> they propagate unchanged - <c>FeatureChannelPickerViewModel</c>'s
    /// initial load has no try/catch at all and must keep letting a load fault surface.
    /// </summary>
    public async Task LoadAllAsync(
        ObservableCollection<TTemplate> destination,
        Action<Exception>? onError = null,
        bool reportErrors = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var list = await _templateService.GetAllAsync<TTemplate>(_templateType).ConfigureAwait(true);
            destination.Clear();
            foreach (var template in list)
            {
                destination.Add(template);
            }
        }
        catch (Exception ex) when (reportErrors)
        {
            onError?.Invoke(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Validates and persists a template, then reports the outcome. <paramref name="build"/> receives
    /// the existing same-named template (matched case-insensitively in <paramref name="collection"/>)
    /// or <see langword="null"/>, and returns the instance to persist; <paramref name="commit"/> then
    /// mutates <paramref name="collection"/> for that (saved, existing) pair. Validation failure and
    /// caught exceptions surface through <paramref name="onInvalid"/> / <paramref name="onError"/> for
    /// caller-side logging.
    /// </summary>
    public async Task SaveAsync(
        string trimmedName,
        ObservableCollection<TTemplate> collection,
        Func<TTemplate?, TTemplate> build,
        Action<TTemplate, TTemplate?> commit,
        Action<TemplateValidationResult>? onInvalid = null,
        Action<Exception>? onError = null,
        bool reportErrors = true)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(commit);
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var existing = collection.FirstOrDefault(
                t => string.Equals(t.Name, trimmedName, StringComparison.OrdinalIgnoreCase));

            var template = build(existing);
            var validation = await _templateService.ValidateAsync(template).ConfigureAwait(true);
            if (!validation.IsValid)
            {
                onInvalid?.Invoke(validation);
                _toastService?.ShowNotification(LocalizationManager.Instance[MsgInvalidForSave]);
                return;
            }

            await _templateService.SaveAsync(template).ConfigureAwait(true);
            commit(template, existing);
            _toastService?.ShowNotification(LocalizationManager.Instance[MsgSaved]);
        }
        catch (Exception ex) when (reportErrors)
        {
            onError?.Invoke(ex);
            _toastService?.ShowNotification(LocalizationManager.Instance[MsgSaveFailed]);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Validates <paramref name="template"/> and, if valid, runs <paramref name="apply"/> (the
    /// replace or append mutation), then runs <paramref name="onApplied"/> (e.g. a chart re-apply),
    /// then shows the success toast. Both <paramref name="apply"/> and <paramref name="onApplied"/>
    /// run inside the try, so a throw in either yields only the error toast rather than a
    /// success toast followed by an error toast. <paramref name="append"/> only selects which
    /// localized success/error key is used. Invalid/exception paths route to
    /// <paramref name="onInvalid"/> / <paramref name="onError"/>.
    /// </summary>
    public async Task ApplyAsync(
        TTemplate template,
        bool append,
        Func<TTemplate, Task> apply,
        Action<TemplateValidationResult>? onInvalid = null,
        Action<Exception>? onError = null,
        Action? onApplied = null,
        bool reportErrors = true)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(apply);
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var validation = await _templateService.ValidateAsync(template).ConfigureAwait(true);
            if (!validation.IsValid)
            {
                onInvalid?.Invoke(validation);
                _toastService?.ShowNotification(LocalizationManager.Instance[MsgInvalidForApply]);
                return;
            }

            await apply(template).ConfigureAwait(true);
            onApplied?.Invoke();
            _toastService?.ShowNotification(
                string.Format(LocalizationManager.Instance[append ? MsgAppended : MsgLoaded], template.Name));
        }
        catch (Exception ex) when (reportErrors)
        {
            onError?.Invoke(ex);
            _toastService?.ShowNotification(LocalizationManager.Instance[append ? MsgAppendError : MsgLoadError]);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Deletes <paramref name="template"/> from storage, removes it from <paramref name="collection"/>,
    /// runs <paramref name="afterRemove"/> (e.g. clearing a selection), and reports the outcome.
    /// </summary>
    public async Task DeleteAsync(
        TTemplate template,
        ObservableCollection<TTemplate> collection,
        Action<TTemplate>? afterRemove = null,
        Action<Exception>? onError = null,
        bool reportErrors = true)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(collection);
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await _templateService.DeleteAsync(_templateType, template.Id).ConfigureAwait(true);
            collection.Remove(template);
            afterRemove?.Invoke(template);
            _toastService?.ShowNotification(LocalizationManager.Instance[MsgDeleted]);
        }
        catch (Exception ex) when (reportErrors)
        {
            onError?.Invoke(ex);
            _toastService?.ShowNotification(LocalizationManager.Instance[MsgDeleteFailed]);
        }
        finally
        {
            _busy = false;
        }
    }
}
