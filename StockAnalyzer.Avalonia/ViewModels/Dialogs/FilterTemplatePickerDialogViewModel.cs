using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs.Common;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the Filter Template picker dialog: saves the target filter node's subtree
/// (rules + nested children) as a named template, and loads (replace) or appends saved templates
/// back onto the target node.
/// </summary>
public partial class FilterTemplatePickerDialogViewModel : ViewModelBase, IDisposable
{
    private readonly TickerListViewModel _owner;
    private readonly FilterNode? _targetNode;
    private readonly TickerGroupNode? _parentNode;
    private readonly ILogger<FilterTemplatePickerDialogViewModel> _logger;
    private readonly TemplateCrudHelper<FilterTemplate> _templateCrud;
    private bool _isDisposed;

    public IToastNotificationService ToastService { get; }

    [ObservableProperty]
    private FilterTemplate? _selectedTemplate;

    [ObservableProperty]
    private string _newTemplateName = string.Empty;

    public ObservableCollection<FilterTemplate> Templates { get; } = new();

    public string TargetNodeName => _targetNode?.DisplayName ?? _parentNode?.DisplayName ?? string.Empty;

    /// <summary>True when the dialog was opened from an existing FilterNode (Save/Load=Replace/Append available).</summary>
    public bool IsExistingNodeMode => _targetNode != null;

    /// <summary>
    /// Single constructor covering both workflows, distinguished by the runtime type of <paramref name="node"/>:
    /// when it is an existing <see cref="FilterNode"/>, Save/Load(Replace)/Append operate on its subtree; otherwise
    /// (e.g. AllTickers/Watchlist/Portfolio) Load instantiates a brand-new FilterNode as its child, and Save/Append
    /// are unavailable since there is no existing filter subtree. A second constructor overload distinguished only
    /// by static parameter type (FilterNode vs. TickerGroupNode) would be ambiguous for ActivatorUtilities, since a
    /// FilterNode instance satisfies both parameter types.
    /// </summary>
    public FilterTemplatePickerDialogViewModel(
        TickerListViewModel owner,
        TickerGroupNode node,
        ITemplateService templateService,
        IToastNotificationService toastService,
        ILogger<FilterTemplatePickerDialogViewModel>? logger = null)
    {
        _owner = owner;
        ToastService = toastService;
        _logger = logger ?? NullLogger<FilterTemplatePickerDialogViewModel>.Instance;
        _templateCrud = new TemplateCrudHelper<FilterTemplate>(templateService, toastService, TemplateType.Filter);

        if (node is FilterNode filterNode)
        {
            _targetNode = filterNode;
            NewTemplateName = filterNode.DisplayName;
        }
        else
        {
            _parentNode = node;
        }

        _ = LoadTemplatesAsync();
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        if (_isDisposed || _templateCrud.IsBusy || _targetNode == null) return;
        if (string.IsNullOrWhiteSpace(NewTemplateName)) return;

        var trimmedName = NewTemplateName.Trim();
        await _templateCrud.SaveAsync(
            trimmedName,
            Templates,
            build: existing =>
            {
                var template = _owner.BuildFilterTemplateFromNode(_targetNode, trimmedName);
                if (existing != null)
                {
                    template.Id = existing.Id;
                    template.CreatedAt = existing.CreatedAt;
                }

                return template;
            },
            commit: (saved, existing) =>
            {
                if (existing == null)
                {
                    Templates.Add(saved);
                }
                else
                {
                    Templates[Templates.IndexOf(existing)] = saved;
                }
            },
            onInvalid: validation => _logger.LogWarning(
                "Cannot save invalid filter template '{Name}': {Errors}", trimmedName, string.Join(", ", validation.Errors)),
            onError: ex => _logger.LogError(ex, "Failed to save filter template '{Name}'", NewTemplateName));
    }

    [RelayCommand]
    private async Task LoadTemplateAsync(FilterTemplate? template)
    {
        if (_isDisposed || _templateCrud.IsBusy || template == null) return;

        await _templateCrud.ApplyAsync(
            template,
            append: false,
            apply: t =>
            {
                if (_targetNode != null)
                {
                    _owner.ApplyFilterTemplateAsReplace(_targetNode, t);
                }
                else if (_parentNode != null)
                {
                    _owner.CreateFilterNodeFromTemplate(_parentNode, t);
                }

                return Task.CompletedTask;
            },
            onInvalid: validation => _logger.LogWarning(
                "Cannot apply invalid filter template '{Name}': {Errors}", template.Name, string.Join(", ", validation.Errors)),
            onError: ex => _logger.LogError(ex, "Failed to load filter template '{Name}'", template.Name));
    }

    [RelayCommand]
    private async Task AppendTemplateAsync(FilterTemplate? template)
    {
        if (_isDisposed || _templateCrud.IsBusy || template == null || _targetNode == null) return;

        await _templateCrud.ApplyAsync(
            template,
            append: true,
            apply: t =>
            {
                _owner.ApplyFilterTemplateAsAppend(_targetNode, t);
                return Task.CompletedTask;
            },
            onInvalid: validation => _logger.LogWarning(
                "Cannot apply invalid filter template '{Name}': {Errors}", template.Name, string.Join(", ", validation.Errors)),
            onError: ex => _logger.LogError(ex, "Failed to append filter template '{Name}'", template.Name));
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(FilterTemplate? template)
    {
        if (_isDisposed || _templateCrud.IsBusy || template == null) return;

        await _templateCrud.DeleteAsync(
            template,
            Templates,
            afterRemove: removed =>
            {
                if (SelectedTemplate == removed)
                {
                    SelectedTemplate = null;
                }
            },
            onError: ex => _logger.LogError(
                ex, "Failed to delete filter template '{Name}' ({Id})", template.Name, template.Id));
    }

    private async Task LoadTemplatesAsync()
    {
        if (_isDisposed) return;

        await _templateCrud.LoadAllAsync(
            Templates,
            onError: ex => _logger.LogError(ex, "Failed to load filter templates."));
    }

    public void Dispose()
    {
        _isDisposed = true;
    }
}
