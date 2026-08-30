using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
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
    private readonly ITemplateService _templateService;
    private readonly ILogger<FilterTemplatePickerDialogViewModel> _logger;
    private bool _isDisposed;
    private bool _isTemplateBusy;

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
        _templateService = templateService;
        ToastService = toastService;
        _logger = logger ?? NullLogger<FilterTemplatePickerDialogViewModel>.Instance;

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
        if (_isDisposed || _isTemplateBusy || _targetNode == null) return;
        if (string.IsNullOrWhiteSpace(NewTemplateName)) return;

        _isTemplateBusy = true;
        try
        {
            var trimmedName = NewTemplateName.Trim();
            var existing = Templates.FirstOrDefault(t => string.Equals(t.Name, trimmedName, StringComparison.OrdinalIgnoreCase));

            var template = _owner.BuildFilterTemplateFromNode(_targetNode, trimmedName);
            if (existing != null)
            {
                template.Id = existing.Id;
                template.CreatedAt = existing.CreatedAt;
            }

            var validation = await _templateService.ValidateAsync(template);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Cannot save invalid filter template '{Name}': {Errors}", trimmedName, string.Join(", ", validation.Errors));
                ToastService.ShowNotification("Template is invalid and cannot be saved");
                return;
            }

            await _templateService.SaveAsync(template);
            if (existing == null)
            {
                Templates.Add(template);
            }
            else
            {
                var index = Templates.IndexOf(existing);
                Templates[index] = template;
            }
            ToastService.ShowNotification("Template saved successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save filter template '{Name}'", NewTemplateName);
            ToastService.ShowNotification("Failed to save template");
        }
        finally
        {
            _isTemplateBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadTemplateAsync(FilterTemplate? template)
    {
        if (_isDisposed || _isTemplateBusy || template == null) return;

        _isTemplateBusy = true;
        try
        {
            var validation = await _templateService.ValidateAsync(template);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Cannot apply invalid filter template '{Name}': {Errors}", template.Name, string.Join(", ", validation.Errors));
                ToastService.ShowNotification("Template is invalid and cannot be applied");
                return;
            }

            if (_targetNode != null)
            {
                _owner.ApplyFilterTemplateAsReplace(_targetNode, template);
            }
            else if (_parentNode != null)
            {
                _owner.CreateFilterNodeFromTemplate(_parentNode, template);
            }
            ToastService.ShowNotification($"Template {template.Name} loaded");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load filter template '{Name}'", template.Name);
            ToastService.ShowNotification("Error loading template");
        }
        finally
        {
            _isTemplateBusy = false;
        }
    }

    [RelayCommand]
    private async Task AppendTemplateAsync(FilterTemplate? template)
    {
        if (_isDisposed || _isTemplateBusy || template == null || _targetNode == null) return;

        _isTemplateBusy = true;
        try
        {
            var validation = await _templateService.ValidateAsync(template);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Cannot apply invalid filter template '{Name}': {Errors}", template.Name, string.Join(", ", validation.Errors));
                ToastService.ShowNotification("Template is invalid and cannot be applied");
                return;
            }

            _owner.ApplyFilterTemplateAsAppend(_targetNode, template);
            ToastService.ShowNotification($"Template {template.Name} appended");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append filter template '{Name}'", template.Name);
            ToastService.ShowNotification("Error appending template");
        }
        finally
        {
            _isTemplateBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(FilterTemplate? template)
    {
        if (_isDisposed || _isTemplateBusy || template == null) return;

        _isTemplateBusy = true;
        try
        {
            await _templateService.DeleteAsync(TemplateType.Filter, template.Id);
            Templates.Remove(template);
            if (SelectedTemplate == template)
            {
                SelectedTemplate = null;
            }
            ToastService.ShowNotification("Template deleted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete filter template '{Name}' ({Id})", template.Name, template.Id);
            ToastService.ShowNotification("Failed to delete template");
        }
        finally
        {
            _isTemplateBusy = false;
        }
    }

    private async Task LoadTemplatesAsync()
    {
        if (_isDisposed || _isTemplateBusy) return;
        _isTemplateBusy = true;
        try
        {
            var list = await _templateService.GetAllAsync<FilterTemplate>(TemplateType.Filter);
            Templates.Clear();
            foreach (var t in list)
            {
                Templates.Add(t);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load filter templates.");
        }
        finally
        {
            _isTemplateBusy = false;
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
    }
}
