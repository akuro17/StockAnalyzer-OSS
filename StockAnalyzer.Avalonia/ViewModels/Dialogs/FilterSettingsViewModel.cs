using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    public partial class FilterSettingsViewModel : ViewModelBase
    {
        private readonly Guid _id;
        private readonly Guid _parentId;
        private readonly FilterSettings _initialSettings;
        private bool _hasApplied;

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private ColumnCategory _selectedCategory = ColumnCategory.All;

        [ObservableProperty]
        private string _searchQuery = string.Empty;



        public ObservableCollection<FilterFieldItem> FilteredFields { get; } = new();

        private readonly ILocalizationService _localizationService;

        public ObservableCollection<FilterRuleViewModel> Rules { get; } = new();

        public IReadOnlyList<FilterFieldItem> AvailableFields { get; }
        public IReadOnlyList<FilterColumnGroup> AvailableGroups { get; }

        public ICommand DeleteRuleCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ApplyCommand { get; }

        public event EventHandler<FilterSettings>? RequestClose;
        public event EventHandler? RequestCancel;
        public event EventHandler<FilterSettings>? RequestApply;

        public Action<FilterSettings>? OnApplyCallback { get; set; }

        public FilterSettingsViewModel(FilterSettings settings, ILocalizationService localizationService, IWatchlistColumnRegistry columnRegistry)
        {
            _localizationService = localizationService;
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

                if (FilterFieldItem.ExcludedFieldKeys.Contains(col.MemberName))
                    continue;

                if (seenKeys.Add(col.MemberName))
                {
                    filtered.Add(col);
                }
            }

            var fields = new List<FilterFieldItem>();
            foreach (var col in filtered)
            {
                var displayName = localizationService.GetString(col.HeaderKey);
                if (displayName == col.HeaderKey)
                {
                    displayName = null;
                }
                var itemVM = ColumnItemViewModel.Create(col, false, displayName);
                fields.Add(new FilterFieldItem(col.MemberName, itemVM.LocalizedHeader, itemVM.Category));
            }

            AvailableFields = fields
                .OrderBy(f => (int)f.Category)
                .ThenBy(f => {
                    var col = filtered.First(c => c.MemberName == f.PropertyName);
                    return col.Priority;
                })
                .ThenBy(f => f.PropertyName, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly();

            AvailableGroups = AvailableFields
                .GroupBy(f => f.Category)
                .Select(g => {
                    var resourceKey = g.Key switch
                    {
                        ColumnCategory.Ratio => "ColumnChooser_Category_Profitability",
                        ColumnCategory.Financial => "ColumnChooser_Category_FinancialHealth",
                        _ => $"ColumnChooser_Category_{g.Key}"
                    };
                    var categoryName = localizationService.GetString(resourceKey) ?? g.Key.ToString();
                    var sortedItems = g.OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList().AsReadOnly();
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

            UpdateFilteredFields();

            DeleteRuleCommand = new RelayCommand<FilterRuleViewModel>(DeleteRule);
            SaveCommand = new RelayCommand(Save);
            CancelCommand = new RelayCommand(Cancel);
            ApplyCommand = new RelayCommand(Apply);
        }

        partial void OnSearchQueryChanged(string value) => UpdateFilteredFields();
        partial void OnSelectedCategoryChanged(ColumnCategory value) => UpdateFilteredFields();

        private void UpdateFilteredFields()
        {
            FilteredFields.Clear();
            var query = SearchQuery?.Trim() ?? string.Empty;

            IEnumerable<FilterFieldItem> items = AvailableFields;
            if (SelectedCategory != ColumnCategory.All)
            {
                items = items.Where(f => f.Category == SelectedCategory);
            }

            if (!string.IsNullOrEmpty(query))
            {
                items = items.Where(f => f.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                         f.PropertyName.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            // Sort alphabetically by DisplayName
            items = items.OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase);

            foreach (var item in items)
            {
                FilteredFields.Add(item);
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
        public IReadOnlyList<FilterFieldItem> Items { get; }

        public FilterColumnGroup(string groupName, IReadOnlyList<FilterFieldItem> items)
        {
            GroupName = groupName;
            Items = items;
        }
    }

    public partial class FilterRuleViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _field = "Tag";

        private readonly IReadOnlyList<FilterFieldItem> _availableFields;

        public FilterFieldItem? SelectedField
        {
            get => _availableFields.FirstOrDefault(f => f.PropertyName == Field);
            set
            {
                if (value != null && value.PropertyName != Field)
                {
                    Field = value.PropertyName;
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

        public FilterFieldItem? SelectedTargetField
        {
            get => _availableFields.FirstOrDefault(f => f.PropertyName == Value);
            set
            {
                if (value != null && value.PropertyName != Value)
                {
                    Value = value.PropertyName;
                    OnPropertyChanged();
                }
            }
        }

        private static readonly List<string> NumericOperators = new() { "==", "!=", ">", ">=", "<", "<=" };
        private static readonly List<string> TextOperators = new() { "*=", "!*=", "==", "!=" };

        public List<string> OperatorsForField => Field?.ToLowerInvariant() switch
        {
            "sector" or "industry" or "tag" => TextOperators,
            _ => NumericOperators
        };

        partial void OnFieldChanged(string value)
        {
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

        public FilterRuleViewModel(FilterRule model, IReadOnlyList<FilterFieldItem> availableFields)
        {
            _availableFields = availableFields;
            Field = model.Field;
            Operator = model.Operator;
            Value = model.Value;
            IsCompareToField = model.IsCompareToField;
        }

        [RelayCommand]
        private void SelectField(FilterFieldItem? field)
        {
            if (field != null)
            {
                SelectedField = field;
            }
        }

        [RelayCommand]
        private void SelectTargetField(FilterFieldItem? field)
        {
            if (field != null)
            {
                SelectedTargetField = field;
            }
        }

        [RelayCommand]
        private void SetCompareToField(bool isCompareToField)
        {
            if (IsCompareToField != isCompareToField)
            {
                IsCompareToField = isCompareToField;
                if (IsCompareToField && SelectedTargetField == null)
                {
                    var defaultField = _availableFields.FirstOrDefault(f => f.PropertyName != Field) ?? _availableFields.FirstOrDefault();
                    if (defaultField != null)
                    {
                        Value = defaultField.PropertyName;
                    }
                }
            }
        }

        public FilterRule ToModel()
        {
            return new FilterRule
            {
                Field = Field,
                Operator = Operator,
                Value = Value,
                IsCompareToField = IsCompareToField
            };
        }
    }

    public class FilterFieldItem
    {
        public static readonly HashSet<string> ExcludedFieldKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "IsChecked", "Symbol", "Name", "LongBusinessSummary"
        };

        public string PropertyName { get; }
        public string DisplayName { get; }
        public ColumnCategory Category { get; }
        public bool IsHeader { get; }

        public FilterFieldItem(string propertyName, string displayName, ColumnCategory category = ColumnCategory.Basic, bool isHeader = false)
        {
            PropertyName = propertyName;
            DisplayName = displayName;
            Category = category;
            IsHeader = isHeader;
        }

        public static List<FilterFieldItem> CreateFields(ILocalizationService localizationService)
        {
            return new()
            {
                new("Tag", localizationService.GetString("Col_Tag") ?? "Tag", ColumnCategory.Basic),
                new("Sector", localizationService.GetString("Col_Sector") ?? "Sector", ColumnCategory.Basic),
                new("Industry", localizationService.GetString("Col_Industry") ?? "Industry", ColumnCategory.Basic),
                new("ReturnOnEquity", localizationService.GetString("Col_ReturnOnEquity") ?? "ROE", ColumnCategory.Ratio),
                new("TrailingPE", localizationService.GetString("Col_TrailingPE") ?? "P/E", ColumnCategory.Valuation),
                new("MarketCap", localizationService.GetString("Col_MarketCap") ?? "Market Cap", ColumnCategory.Financial)
            };
        }
    }
}
