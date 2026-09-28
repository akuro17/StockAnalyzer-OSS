using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    public partial class FilterSettingsViewModel : ColumnPickerViewModelBase<ColumnItemViewModel>
    {
        public static readonly HashSet<string> ExcludedFieldKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "IsChecked", "Symbol", "Name", "LongBusinessSummary"
        };

        private readonly Guid _id;
        private readonly Guid _parentId;
        private readonly FilterSettings _initialSettings;
        private readonly ITemplateService _templateService;
        private bool _hasApplied;
        private bool _isTemplateBusy;

        [ObservableProperty]
        private string _name = string.Empty;

        public ObservableCollection<ColumnItemViewModel> FilteredFields => FilteredItems;

        private readonly ILocalizationService _localizationService;

        public ObservableCollection<FilterRuleViewModel> Rules { get; } = new();

        public IReadOnlyList<ColumnItemViewModel> AvailableFields { get; }
        public IReadOnlyList<FilterColumnGroup> AvailableGroups { get; }

        public ICommand DeleteRuleCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ApplyCommand { get; }

        [ObservableProperty]
        private FilterTemplate? _selectedTemplate;

        [ObservableProperty]
        private string _newTemplateName = string.Empty;

        public ObservableCollection<FilterTemplate> Templates { get; } = new();
        public ObservableCollection<FilterRulePreviewItem> SelectedTemplateRulesPreview { get; } = new();

        public bool IsTemplatesCategory => SelectedCategory == ColumnCategory.Templates;
        public bool IsNotTemplatesCategory => SelectedCategory != ColumnCategory.Templates;

        public ICommand SaveTemplateCommand { get; }
        public ICommand LoadTemplateCommand { get; }
        public ICommand AppendTemplateCommand { get; }
        public ICommand DeleteTemplateCommand { get; }
        public ICommand AddTemplateRuleCommand { get; }

        public event EventHandler<FilterSettings>? RequestClose;
        public event EventHandler? RequestCancel;
        public event EventHandler<FilterSettings>? RequestApply;

        public Action<FilterSettings>? OnApplyCallback { get; set; }

        public FilterSettingsViewModel(FilterSettings settings, ILocalizationService localizationService, IWatchlistColumnRegistry columnRegistry, ITemplateService? templateService = null)
        {
            _localizationService = localizationService;
            _templateService = templateService ?? new StockAnalyzer.Core.Services.TemplateService();
            if (columnRegistry == null)
            {
                throw new ArgumentNullException(nameof(columnRegistry));
            }

            var allCols = columnRegistry.GetAllColumns();
            if (allCols == null)
            {
                throw new InvalidOperationException("Registry columns cannot be null.");
            }

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filtered = new List<WatchlistColumnMetadata>();
            foreach (var col in allCols)
            {
                if (col == null || string.IsNullOrWhiteSpace(col.MemberName))
                    continue;

                if (ExcludedFieldKeys.Contains(col.MemberName))
                    continue;

                if (seenKeys.Add(col.MemberName))
                {
                    filtered.Add(col);
                }
            }

            var fields = new List<ColumnItemViewModel>();
            foreach (var col in filtered)
            {
                var displayName = localizationService.GetString(col.HeaderKey);
                if (displayName == col.HeaderKey)
                {
                    displayName = null;
                }
                var itemVM = ColumnItemViewModel.Create(col, false, displayName);
                fields.Add(itemVM);
            }

            AvailableFields = fields
                .OrderBy(f => (int)f.Category)
                .ThenBy(f => {
                    var col = filtered.First(c => c.MemberName == f.MemberName);
                    return col.Priority;
                })
                .ThenBy(f => f.MemberName, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly();

            AvailableGroups = AvailableFields
                .GroupBy(f => f.Category)
                .Select(g => {
                    var categoryName = g.Key.GetLocalizedName(localizationService);
                    var sortedItems = g.OrderBy(f => f.LocalizedHeader, StringComparer.CurrentCultureIgnoreCase).ToList().AsReadOnly();
                    return new FilterColumnGroup(categoryName, sortedItems);
                })
                .ToList()
                .AsReadOnly();

            _id = settings.Id;
            _parentId = settings.ParentId;
            Name = settings.Name;
            _initialSettings = new FilterSettings
            {
                Id = settings.Id,
                ParentId = settings.ParentId,
                Name = settings.Name,
                Rules = settings.Rules.Select(r => new FilterRule
                {
                    Field = r.Field,
                    Operator = r.Operator,
                    Value = r.Value
                }).ToList()
            };

            foreach (var rule in settings.Rules)
            {
                var ruleVM = new FilterRuleViewModel(rule, AvailableFields);
                Rules.Add(ruleVM);
            }

            UpdateFilteredItems();

            DeleteRuleCommand = new RelayCommand<FilterRuleViewModel>(DeleteRule);
            SaveCommand = new RelayCommand(Save);
            CancelCommand = new RelayCommand(Cancel);
            ApplyCommand = new RelayCommand(Apply);

            SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync);
            LoadTemplateCommand = new AsyncRelayCommand<FilterTemplate?>(LoadTemplateAsync);
            AppendTemplateCommand = new AsyncRelayCommand<FilterTemplate?>(AppendTemplateAsync);
            DeleteTemplateCommand = new AsyncRelayCommand<FilterTemplate?>(DeleteTemplateAsync);
            AddTemplateRuleCommand = new RelayCommand<FilterRule>(AddTemplateRule);

            _ = LoadTemplatesAsync();
        }

        protected override void OnSelectedCategoryChangedCore(ColumnCategory value)
        {
            base.OnSelectedCategoryChangedCore(value);
            OnPropertyChanged(nameof(IsTemplatesCategory));
            OnPropertyChanged(nameof(IsNotTemplatesCategory));
        }

        partial void OnSelectedTemplateChanged(FilterTemplate? value)
        {
            SelectedTemplateRulesPreview.Clear();
            if (value?.RootSettings?.Rules == null) return;
            foreach (var rule in value.RootSettings.Rules)
            {
                SelectedTemplateRulesPreview.Add(new FilterRulePreviewItem(rule, FilterTemplateFormatting.FormatRule(rule)));
            }
        }

        /// <summary>Adds a single template rule to the currently-edited Rules, preserving existing rules.</summary>
        private void AddTemplateRule(FilterRule? rule)
        {
            if (rule == null) return;
            Rules.Add(new FilterRuleViewModel(CloneRule(rule), AvailableFields));
        }

        private async Task LoadTemplatesAsync()
        {
            if (_isTemplateBusy) return;
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
            finally
            {
                _isTemplateBusy = false;
            }
        }

        /// <summary>Saves the currently-edited Rules (this dialog has no Children to capture) as a named FilterTemplate, sharing the same store as the tree-level subtree templates.</summary>
        private async Task SaveTemplateAsync()
        {
            if (_isTemplateBusy || string.IsNullOrWhiteSpace(NewTemplateName)) return;

            _isTemplateBusy = true;
            try
            {
                var trimmedName = NewTemplateName.Trim();
                var existing = Templates.FirstOrDefault(t => string.Equals(t.Name, trimmedName, StringComparison.OrdinalIgnoreCase));

                var template = new FilterTemplate
                {
                    Name = trimmedName,
                    RootSettings = new FilterSettings { Rules = Rules.Select(r => r.ToModel()).ToList() }
                };
                if (existing != null)
                {
                    template.Id = existing.Id;
                    template.CreatedAt = existing.CreatedAt;
                }

                var validation = await _templateService.ValidateAsync(template);
                if (!validation.IsValid) return;

                await _templateService.SaveAsync(template);
                if (existing == null)
                {
                    Templates.Add(template);
                }
                else
                {
                    Templates[Templates.IndexOf(existing)] = template;
                }
            }
            finally
            {
                _isTemplateBusy = false;
            }
        }

        /// <summary>Replaces the currently-edited Rules with the template's rules (Children, if any, are ignored since this dialog only edits a single node's Rules).</summary>
        private Task LoadTemplateAsync(FilterTemplate? template)
        {
            if (template?.RootSettings == null) return Task.CompletedTask;

            Rules.Clear();
            foreach (var rule in template.RootSettings.Rules)
            {
                Rules.Add(new FilterRuleViewModel(CloneRule(rule), AvailableFields));
            }
            return Task.CompletedTask;
        }

        /// <summary>Adds the template's rules to the currently-edited Rules, preserving existing rules.</summary>
        private Task AppendTemplateAsync(FilterTemplate? template)
        {
            if (template?.RootSettings == null) return Task.CompletedTask;

            foreach (var rule in template.RootSettings.Rules)
            {
                Rules.Add(new FilterRuleViewModel(CloneRule(rule), AvailableFields));
            }
            return Task.CompletedTask;
        }

        private static FilterRule CloneRule(FilterRule rule) => new()
        {
            Field = rule.Field,
            Operator = rule.Operator,
            Value = rule.Value,
            IsCompareToField = rule.IsCompareToField,
            CompareMode = rule.CompareMode
        };

        private async Task DeleteTemplateAsync(FilterTemplate? template)
        {
            if (template == null) return;

            await _templateService.DeleteAsync(TemplateType.Filter, template.Id);
            Templates.Remove(template);
            if (SelectedTemplate == template)
            {
                SelectedTemplate = null;
            }
        }

        protected override void UpdateFilteredItems()
        {
            FilteredItems.Clear();
            if (SelectedCategory == ColumnCategory.Templates) return;

            var query = SearchQuery?.Trim() ?? string.Empty;

            IEnumerable<ColumnItemViewModel> items = AvailableFields;
            if (SelectedCategory != ColumnCategory.All)
            {
                items = items.Where(f => f.Category == SelectedCategory);
            }

            if (!string.IsNullOrEmpty(query))
            {
                items = items.Where(f => f.LocalizedHeader.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                         f.MemberName.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            // Sort alphabetically by LocalizedHeader
            items = items.OrderBy(f => f.LocalizedHeader, StringComparer.CurrentCultureIgnoreCase);

            foreach (var item in items)
            {
                FilteredItems.Add(item);
            }
        }

        private void DeleteRule(FilterRuleViewModel? rule)
        {
            if (rule != null)
            {
                Rules.Remove(rule);
            }
        }

        private void Save()
        {
            if (string.IsNullOrWhiteSpace(Name)) return;

            var result = new FilterSettings
            {
                Id = _id,
                ParentId = _parentId,
                Name = Name.Trim(),
                Rules = Rules.Select(r => r.ToModel()).ToList()
            };

            RequestClose?.Invoke(this, result);
        }

        private void Cancel()
        {
            if (_hasApplied)
            {
                RequestApply?.Invoke(this, _initialSettings);
                OnApplyCallback?.Invoke(_initialSettings);
            }
            RequestCancel?.Invoke(this, EventArgs.Empty);
        }

        private void Apply()
        {
            if (string.IsNullOrWhiteSpace(Name)) return;

            var result = new FilterSettings
            {
                Id = _id,
                ParentId = _parentId,
                Name = Name.Trim(),
                Rules = Rules.Select(r => r.ToModel()).ToList()
            };

            _hasApplied = true;
            RequestApply?.Invoke(this, result);
            OnApplyCallback?.Invoke(result);
        }
    }

    public class FilterColumnGroup
    {
        public string GroupName { get; }
        public IReadOnlyList<ColumnItemViewModel> Items { get; }

        public FilterColumnGroup(string groupName, IReadOnlyList<ColumnItemViewModel> items)
        {
            GroupName = groupName;
            Items = items;
        }
    }

    public class FilterRulePreviewItem
    {
        public FilterRule Rule { get; }
        public string DisplayText { get; }

        public FilterRulePreviewItem(FilterRule rule, string displayText)
        {
            Rule = rule;
            DisplayText = displayText;
        }
    }

    public partial class FilterRuleViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _field = "Tag";

        private readonly IReadOnlyList<ColumnItemViewModel> _availableFields;

        public ColumnItemViewModel? SelectedField
        {
            get => _availableFields.FirstOrDefault(f => f.MemberName == Field);
            set
            {
                if (value != null && value.MemberName != Field)
                {
                    Field = value.MemberName;
                    OnPropertyChanged();
                }
            }
        }

        [ObservableProperty]
        private string _operator = "*=";

        [ObservableProperty]
        private string _value = string.Empty;

        [ObservableProperty]
        private bool _isCompareToField;

        [ObservableProperty]
        private string _compareMode = "Numeric"; // "Numeric", "String", "Indicator"

        [ObservableProperty]
        private bool _isNumericMode = true;

        [ObservableProperty]
        private bool _isStringMode = false;

        [ObservableProperty]
        private bool _isIndicatorMode = false;

        private bool _isModeSetByUser = false;

        partial void OnCompareModeChanged(string value)
        {
            IsNumericMode = string.Equals(value, "Numeric", StringComparison.OrdinalIgnoreCase);
            IsStringMode = string.Equals(value, "String", StringComparison.OrdinalIgnoreCase);
            IsIndicatorMode = string.Equals(value, "Indicator", StringComparison.OrdinalIgnoreCase);
            IsCompareToField = IsIndicatorMode;

            var allowed = OperatorsForField;
            if (!allowed.Contains(Operator))
            {
                Operator = allowed.FirstOrDefault() ?? "==";
            }
            OnPropertyChanged(nameof(OperatorsForField));
        }

        public ColumnItemViewModel? SelectedTargetField
        {
            get => _availableFields.FirstOrDefault(f => f.MemberName == Value);
            set
            {
                if (value != null && value.MemberName != Value)
                {
                    Value = value.MemberName;
                    OnPropertyChanged();
                }
            }
        }

        private static readonly List<string> NumericOperators = new() { "==", "!=", ">", ">=", "<", "<=" };
        private static readonly List<string> TextOperators = new() { "*=", "!*=", "==", "!=" };

        public static readonly HashSet<string> DefaultTextColumnNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Symbol", "Name", "Sector", "Industry", "Region", "Tag", "QuoteType", "RecommendationKey"
        };

        public List<string> OperatorsForField =>
            (IsStringMode || (CompareMode != "Indicator" && DefaultTextColumnNames.Contains(Field)))
                ? TextOperators
                : NumericOperators;

        partial void OnFieldChanged(string value)
        {
            if (DefaultTextColumnNames.Contains(value) && !IsIndicatorMode && !_isModeSetByUser)
            {
                CompareMode = "String";
            }
            var allowed = OperatorsForField;
            if (!allowed.Contains(Operator))
            {
                Operator = allowed.FirstOrDefault() ?? "==";
            }
            OnPropertyChanged(nameof(SelectedField));
            OnPropertyChanged(nameof(OperatorsForField));
        }

        partial void OnValueChanged(string value)
        {
            OnPropertyChanged(nameof(SelectedTargetField));
        }

        public FilterRuleViewModel(FilterRule model, IReadOnlyList<ColumnItemViewModel> availableFields)
        {
            _availableFields = availableFields;
            Field = model.Field;
            Operator = model.Operator;
            Value = model.Value;
            IsCompareToField = model.IsCompareToField;

            if (model.IsCompareToField)
            {
                CompareMode = "Indicator";
            }
            else if (!string.IsNullOrEmpty(model.CompareMode))
            {
                CompareMode = model.CompareMode;
            }
            else if (DefaultTextColumnNames.Contains(model.Field) || model.Operator == "*=" || model.Operator == "!*=")
            {
                CompareMode = "String";
            }
            else
            {
                CompareMode = "Numeric";
            }
        }

        [RelayCommand]
        private void SelectField(ColumnItemViewModel? field)
        {
            if (field != null)
            {
                SelectedField = field;
            }
        }

        [RelayCommand]
        private void SelectTargetField(ColumnItemViewModel? field)
        {
            if (field != null)
            {
                SelectedTargetField = field;
            }
        }

        [RelayCommand]
        private void SetModeNumeric()
        {
            _isModeSetByUser = true;
            CompareMode = "Numeric";
        }

        [RelayCommand]
        private void SetModeString()
        {
            _isModeSetByUser = true;
            CompareMode = "String";
        }

        [RelayCommand]
        private void SetModeIndicator()
        {
            _isModeSetByUser = true;
            CompareMode = "Indicator";
            if (SelectedTargetField == null)
            {
                var defaultField = _availableFields.FirstOrDefault(f => f.MemberName != Field) ?? _availableFields.FirstOrDefault();
                if (defaultField != null)
                {
                    Value = defaultField.MemberName;
                }
            }
        }

        [RelayCommand]
        private void SetCompareToField(bool isCompareToField)
        {
            if (isCompareToField)
                SetModeIndicator();
            else
                SetModeNumeric();
        }

        public FilterRule ToModel()
        {
            return new FilterRule
            {
                Field = Field,
                Operator = Operator,
                Value = Value,
                IsCompareToField = IsIndicatorMode,
                CompareMode = CompareMode
            };
        }
    }
}
