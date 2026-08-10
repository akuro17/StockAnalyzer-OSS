using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Controls.Templates;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Interfaces;

namespace StockAnalyzer.Avalonia.Common;

public static class TreeDataGridExtensions
{
    private class DynamicColumnsOwner { }

    public static readonly AttachedProperty<IEnumerable<WatchlistColumnMetadata>?> DynamicColumnsProperty =
        AvaloniaProperty.RegisterAttached<TreeDataGrid, IEnumerable<WatchlistColumnMetadata>?>(
            "DynamicColumns", typeof(DynamicColumnsOwner));

    public static IEnumerable<WatchlistColumnMetadata>? GetDynamicColumns(TreeDataGrid element) =>
        element.GetValue(DynamicColumnsProperty);

    public static void SetDynamicColumns(TreeDataGrid element, IEnumerable<WatchlistColumnMetadata>? value) =>
        element.SetValue(DynamicColumnsProperty, value);

    private static readonly ConditionalWeakTable<global::Avalonia.Controls.Models.TreeDataGrid.IColumn, string> _columnIdMap = new();

    public static string? GetColumnId(global::Avalonia.Controls.Models.TreeDataGrid.IColumn? element)
    {
        if (element == null) return null;
        if (_columnIdMap.TryGetValue(element, out var id)) return id;
        return null;
    }

    public static void SetColumnId(global::Avalonia.Controls.Models.TreeDataGrid.IColumn? element, string? value)
    {
        if (element == null || value == null) return;
        _columnIdMap.AddOrUpdate(element, value);
    }

    public static readonly AttachedProperty<System.Collections.IEnumerable?> ItemsProperty =
        AvaloniaProperty.RegisterAttached<TreeDataGrid, System.Collections.IEnumerable?>(
            "Items", typeof(TreeDataGridExtensions));

    public static System.Collections.IEnumerable? GetItems(TreeDataGrid element) => element.GetValue(ItemsProperty);
    public static void SetItems(TreeDataGrid element, System.Collections.IEnumerable? value) => element.SetValue(ItemsProperty, value);

    public static readonly AttachedProperty<object?> SelectedItemProperty =
        AvaloniaProperty.RegisterAttached<TreeDataGrid, object?>(
            "SelectedItem", typeof(TreeDataGridExtensions), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public static object? GetSelectedItem(TreeDataGrid element) => element.GetValue(SelectedItemProperty);
    public static void SetSelectedItem(TreeDataGrid element, object? value) => element.SetValue(SelectedItemProperty, value);

    public static readonly AttachedProperty<string?> SortMemberProperty =
        AvaloniaProperty.RegisterAttached<TreeDataGrid, string?>(
            "SortMember", typeof(TreeDataGridExtensions), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public static string? GetSortMember(TreeDataGrid element) => element.GetValue(SortMemberProperty);
    public static void SetSortMember(TreeDataGrid element, string? value) => element.SetValue(SortMemberProperty, value);

    public static readonly AttachedProperty<int> SortDirectionProperty =
        AvaloniaProperty.RegisterAttached<TreeDataGrid, int>(
            "SortDirection", typeof(TreeDataGridExtensions), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public static int GetSortDirection(TreeDataGrid element) => element.GetValue(SortDirectionProperty);
    public static void SetSortDirection(TreeDataGrid element, int value) => element.SetValue(SortDirectionProperty, value);

    static TreeDataGridExtensions()
    {
        DynamicColumnsProperty.Changed.AddClassHandler<TreeDataGrid>(OnDynamicColumnsChanged);
        ItemsProperty.Changed.AddClassHandler<TreeDataGrid>(OnItemsChanged);
        SelectedItemProperty.Changed.AddClassHandler<TreeDataGrid>(OnSelectedItemChanged);
        SortMemberProperty.Changed.AddClassHandler<TreeDataGrid>(OnSortChanged);
        SortDirectionProperty.Changed.AddClassHandler<TreeDataGrid>(OnSortChanged);
    }

    private class GridLifecycleHandler : IDisposable
    {
        private readonly WeakReference<TreeDataGrid> _gridRef;
        private readonly INotifyCollectionChanged? _collection;
        private readonly IDisposable? _sourceSubscription;

        public GridLifecycleHandler(TreeDataGrid grid, INotifyCollectionChanged? collection)
        {
            _gridRef = new WeakReference<TreeDataGrid>(grid);
            _collection = collection;

            if (_collection != null)
            {
                _collection.CollectionChanged += OnCollectionChanged;
            }

            _sourceSubscription = grid.GetObservable(TreeDataGrid.SourceProperty).Subscribe(newSource =>
            {
                if (newSource != null && _gridRef.TryGetTarget(out var g))
                {
                    UpdateColumns(g);
                }
            });

            grid.DetachedFromVisualTree += OnDetached;
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_gridRef.TryGetTarget(out var g))
            {
                if (GetIsUpdatingColumns(g)) return;
                UpdateColumns(g);
            }
        }

        private void OnDetached(object? sender, global::Avalonia.VisualTreeAttachmentEventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_collection != null)
            {
                _collection.CollectionChanged -= OnCollectionChanged;
            }
            _sourceSubscription?.Dispose();
            if (_gridRef.TryGetTarget(out var grid))
            {
                grid.DetachedFromVisualTree -= OnDetached;
                _gridHandlers.Remove(grid);
            }
        }
    }

    private static readonly ConditionalWeakTable<TreeDataGrid, GridLifecycleHandler> _gridHandlers = new();

    private class IsUpdatingColumnsOwner { }

    public static readonly AttachedProperty<bool> IsUpdatingColumnsProperty =
        AvaloniaProperty.RegisterAttached<TreeDataGrid, bool>(
            "IsUpdatingColumns", typeof(IsUpdatingColumnsOwner));

    public static bool GetIsUpdatingColumns(TreeDataGrid element) =>
        element.GetValue(IsUpdatingColumnsProperty);

    public static void SetIsUpdatingColumns(TreeDataGrid element, bool value) =>
        element.SetValue(IsUpdatingColumnsProperty, value);

    private static string? _pendingSortColumn;
    private static int _pendingSortDirection;

    private static void OnItemsChanged(TreeDataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        var items = e.NewValue as System.Collections.IEnumerable;
        if (items == null)
        {
            grid.Source = null;
            return;
        }

        // Initialize source with WatchlistItemViewModel (Project specific specialized grid)
        var source = new FlatTreeDataGridSource<WatchlistItemViewModel>(items.Cast<WatchlistItemViewModel>());
        grid.Source = source;
        
        // Setup bi-directional selection sync
        var selected = GetSelectedItem(grid) as WatchlistItemViewModel;
        if (selected != null)
        {
            int index = GetItemIndex(source.Items, selected);
            if (index >= 0 && source.RowSelection != null) source.RowSelection.Select(index);
        }

        if (source.RowSelection != null)
        {
            source.RowSelection.PropertyChanged += (s, args) => 
            {
                if (args.PropertyName == nameof(source.RowSelection.SelectedItem))
                {
                    SetSelectedItem(grid, source.RowSelection.SelectedItem);
                }
            };
        }

        // Setup bi-directional sorting sync (Grid -> ViewModel) by observing column SortDirection changes
        if (source.Columns is System.Collections.Specialized.INotifyCollectionChanged colList)
        {
            void HookColumn(IColumn col)
            {
                if (col is System.ComponentModel.INotifyPropertyChanged npc)
                {
                    npc.PropertyChanged += (sender, args) =>
                    {
                        if (GetIsUpdatingColumns(grid)) return; // Prevent reentrancy during programmatic sorting

                        if (sender is IColumn colSender)
                        {
                            var colId = GetColumnId(colSender);
                            if (colId != null)
                            {
                                if (args.PropertyName == "SortDirection")
                                {
                                    var dir = colSender.SortDirection == System.ComponentModel.ListSortDirection.Ascending ? 1 : 
                                              colSender.SortDirection == System.ComponentModel.ListSortDirection.Descending ? 2 : 0;
                                    
                                    // Only update the VM if it actually differs, to prevent circular updates
                                    if (dir != 0)
                                    {
                                        if (GetSortMember(grid) != colId) SetSortMember(grid, colId);
                                        if (GetSortDirection(grid) != dir) SetSortDirection(grid, dir);
                                    }
                                    else
                                    {
                                        // If sort was removed
                                        if (GetSortMember(grid) == colId)
                                        {
                                            SetSortMember(grid, null);
                                            SetSortDirection(grid, 0);
                                        }
                                    }
                                }
                                 else if (args.PropertyName == "Width" || args.PropertyName == "ActualWidth")
                                 {
                                     var metadata = GetDynamicColumns(grid);
                                     if (metadata is IList<WatchlistColumnMetadata> metaList)
                                     {
                                         for (int i = 0; i < metaList.Count; i++)
                                         {
                                             if (string.Equals(metaList[i].MemberName, colId, StringComparison.OrdinalIgnoreCase))
                                             {
                                                 double wVal = colSender.Width.IsAbsolute ? colSender.Width.Value : 0;
                                                 if (wVal > 0)
                                                 {
                                                     string widthStr = Math.Round(wVal).ToString(System.Globalization.CultureInfo.InvariantCulture);
                                                     if (metaList[i].Width != widthStr)
                                                     {
                                                         try
                                                         {
                                                             SetIsUpdatingColumns(grid, true);
                                                             metaList[i] = metaList[i] with { Width = widthStr };
                                                         }
                                                         finally
                                                         {
                                                             SetIsUpdatingColumns(grid, false);
                                                         }
                                                     }
                                                 }
                                                 break;
                                             }
                                         }
                                     }
                                 }
                            }
                        }
                    };
                }
            }

            foreach (var col in source.Columns) HookColumn(col);

            colList.CollectionChanged += (s, args) =>
            {
                if (args.NewItems != null)
                {
                    foreach (IColumn col in args.NewItems) HookColumn(col);
                }
            };
        }

        UpdateColumns(grid);
    }

    private static void OnSelectedItemChanged(TreeDataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        var source = grid.Source as FlatTreeDataGridSource<WatchlistItemViewModel>;
        if (source?.RowSelection != null)
        {
            var item = e.NewValue as WatchlistItemViewModel;
            if (item == null)
            {
                source.RowSelection.Clear();
            }
            else
            {
                int index = GetItemIndex(source.Items, item);
                if (index >= 0)
                {
                    source.RowSelection.Select(index);
                }
            }
        }
    }

    private static void OnSortChanged(TreeDataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        // Optimization: Sort changes are usually handled during UpdateColumns or via user interaction.
        // We only trigger UpdateColumns if the value actually changed from the VM side.
        UpdateColumns(grid);
    }

    /// <summary>
    /// Sets a pending sort state that will be applied after the next UpdateColumns completes.
    /// </summary>
    public static void SetPendingSort(string? columnName, int direction)
    {
        // Note: This legacy method is still used by components not yet migrated to SortMemberProperty
        _pendingSortColumn = columnName;
        _pendingSortDirection = direction;
    }

    private static void OnDynamicColumnsChanged(TreeDataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        if (GetIsUpdatingColumns(grid)) return;

        // Avoid duplicate subscriptions for the same grid
        if (!_gridHandlers.TryGetValue(grid, out _))
        {
            var handler = new GridLifecycleHandler(grid, e.NewValue as INotifyCollectionChanged);
            _gridHandlers.Add(grid, handler);
        }

        UpdateColumns(grid);
    }

    private static void UpdateColumns(TreeDataGrid grid)
    {
        // Use Post to ensure we're on the UI thread and let any pending layout finish
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() => 
        {
            try 
            {
                SetIsUpdatingColumns(grid, true);

                var metadata = GetDynamicColumns(grid);
                var source = grid.Source;

                if (metadata == null || source == null) return;

                var columns = source.Columns as System.Collections.IList;
                if (columns == null) return;

                var sortedMeta = metadata.ToList();

                // 1. Cache existing columns by ID
                var existingCols = new Dictionary<string, IColumn>();
                foreach (var col in columns.Cast<IColumn>())
                {
                    var id = GetColumnId(col);
                    if (id != null)
                    {
                        existingCols[id] = col;
                    }
                }

                // 2. Rebuild the columns list in correct order
                columns.Clear();
                foreach (var meta in sortedMeta)
                {
                    var id = meta.MemberName;
                    if (existingCols.TryGetValue(id, out var col))
                    {
                        columns.Add(col);
                    }
                    else
                    {
                        var app = Application.Current as StockAnalyzer.Avalonia.App;
                        var localizationService = app?.Services?.GetService<ILocalizationService>();
                        var header = meta.MemberName.StartsWith("Indicator_") 
                            ? meta.HeaderKey 
                            : (localizationService?.GetString(meta.HeaderKey) ?? meta.HeaderKey);
                        double pxWidth = double.TryParse(WatchlistConstants.WidthDefault, out var defW) ? defW : 80;
                        if (!string.IsNullOrEmpty(meta.Width) && meta.Width != "Auto" && double.TryParse(meta.Width, out var parsedW))
                        {
                            pxWidth = Math.Max(WatchlistConstants.DefaultColumnMinWidth, parsedW);
                        }
                        else
                        {
                            if (meta.MemberName == "IsChecked" && double.TryParse(WatchlistConstants.WidthSelect, out var wSel)) pxWidth = wSel;
                            else if (meta.MemberName == "Symbol" && double.TryParse(WatchlistConstants.WidthSymbol, out var wSym)) pxWidth = wSym;
                            else if (meta.MemberName == "Name" && double.TryParse(WatchlistConstants.WidthName, out var wName)) pxWidth = wName;
                            else if (meta.MemberName == "Tag" && double.TryParse(WatchlistConstants.WidthTag, out var wTag)) pxWidth = wTag;
                            else if (meta.MemberName == "Notes" && double.TryParse(WatchlistConstants.WidthNotes, out var wNotes)) pxWidth = wNotes;
                        }
                        var width = new GridLength(pxWidth, GridUnitType.Pixel);
                        
                        var newCol = CreateColumn(grid, header, meta.MemberName, width);
                        if (newCol != null)
                        {
                            SetColumnId(newCol, id);
                            columns.Add(newCol);
                        }
                    }
                }

                // No manual Source resetting is needed; TreeDataGrid tracks Columns' CollectionChanged events.
                
                // 3. Apply sorting (Pending or Property-bound)
                string? sortCol = _pendingSortColumn ?? GetSortMember(grid);
                int sortDirInt = _pendingSortColumn != null ? _pendingSortDirection : GetSortDirection(grid);

                if (!string.IsNullOrEmpty(sortCol))
                {
                    bool applied = false;
                    for (int i = 0; i < columns.Count; i++)
                    {
                        var col = columns[i] as IColumn;
                        if (col == null) continue;
                        var colId = GetColumnId(col);
                        if (colId == sortCol)
                        {
                            var sortDir = sortDirInt == 1 
                                ? System.ComponentModel.ListSortDirection.Ascending 
                                : System.ComponentModel.ListSortDirection.Descending;
                            source.SortBy(col, sortDir);
                            applied = true;
                            break;
                        }
                    }

                    if (applied)
                    {
                        _pendingSortColumn = null;
                        _pendingSortDirection = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateColumns: {ex.Message}");
            }
            finally
            {
                SetIsUpdatingColumns(grid, false);
            }
        });
    }

    private static IColumn? CreateColumn(TreeDataGrid grid, string header, string memberName, GridLength width)
    {
        switch (memberName)
        {
            case "IsChecked":
                return new TemplateColumn<WatchlistItemViewModel>(
                    header: CreateHeaderCheckBox(grid),
                    cellTemplate: new FuncDataTemplate<WatchlistItemViewModel>((item, _) =>
                    {
                        var cb = new global::Avalonia.Controls.CheckBox
                        {
                            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                            HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                            MinWidth = 0,
                            MinHeight = 0,
                            Padding = new global::Avalonia.Thickness(0),
                            Margin = new global::Avalonia.Thickness(0)
                        };
                        cb.Bind(global::Avalonia.Controls.CheckBox.IsCheckedProperty, new global::Avalonia.Data.Binding(nameof(WatchlistItemViewModel.IsChecked)) { Mode = global::Avalonia.Data.BindingMode.TwoWay });
                        
                        var cellContainer = new global::Avalonia.Controls.Grid
                        {
                            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch,
                            Margin = new global::Avalonia.Thickness(0)
                        };
                        cellContainer.Children.Add(cb);
                        return cellContainer;
                    }),
                    cellEditingTemplate: null,
                      options: new global::Avalonia.Controls.Models.TreeDataGrid.TemplateColumnOptions<WatchlistItemViewModel>
                      {
                          CanUserResizeColumn = true,
                          CanUserSortColumn = false,
                          MinWidth = new GridLength(WatchlistConstants.DefaultColumnMinWidth, GridUnitType.Pixel),
                          MaxWidth = new GridLength(WatchlistConstants.DefaultColumnMaxWidth, GridUnitType.Pixel)
                      },
                    width: width);
            case "Symbol":
                return CreateTextColumn(header, nameof(WatchlistItemViewModel.Symbol), width, true);
            case "Name":
                return CreateTextColumn(header, nameof(WatchlistItemViewModel.Name), width, true);
            case "Change":
                return CreateColoredColumn(header, nameof(WatchlistItemViewModel.DisplayChange), nameof(WatchlistItemViewModel.Change), width, 
                    new global::Avalonia.Controls.Models.TreeDataGrid.TemplateColumnOptions<WatchlistItemViewModel>
                    {
                        CanUserResizeColumn = true,
                        MinWidth = new GridLength(WatchlistConstants.DefaultColumnMinWidth, GridUnitType.Pixel),
                        MaxWidth = new GridLength(WatchlistConstants.DefaultColumnMaxWidth, GridUnitType.Pixel),
                        CompareAscending = (a, b) => Comparer<decimal>.Default.Compare(a == null ? 0 : a.Change, b == null ? 0 : b.Change),
                        CompareDescending = (a, b) => Comparer<decimal>.Default.Compare(b == null ? 0 : b.Change, a == null ? 0 : a.Change)
                    });
            case "ChangePercent":
            case "Ratio":
                return CreateColoredColumn(header, nameof(WatchlistItemViewModel.DisplayChangePercent), nameof(WatchlistItemViewModel.ChangePercent), width,
                    new global::Avalonia.Controls.Models.TreeDataGrid.TemplateColumnOptions<WatchlistItemViewModel>
                    {
                        CanUserResizeColumn = true,
                        MinWidth = new GridLength(WatchlistConstants.DefaultColumnMinWidth, GridUnitType.Pixel),
                        MaxWidth = new GridLength(WatchlistConstants.DefaultColumnMaxWidth, GridUnitType.Pixel),
                        CompareAscending = (a, b) => Comparer<double>.Default.Compare(a == null ? 0 : a.ChangePercent, b == null ? 0 : b.ChangePercent),
                        CompareDescending = (a, b) => Comparer<double>.Default.Compare(b == null ? 0 : b.ChangePercent, a == null ? 0 : a.ChangePercent)
                    });
            case "Tag":
                return new TemplateColumn<WatchlistItemViewModel>(
                    header: header,
                    cellTemplate: new FuncDataTemplate<WatchlistItemViewModel>((_, _) =>
                    {
                        return new StockAnalyzer.Avalonia.Views.Controls.TagCellControl();
                    }),
                    cellEditingTemplate: null,
                    options: new global::Avalonia.Controls.Models.TreeDataGrid.TemplateColumnOptions<WatchlistItemViewModel>
                    {
                        CanUserResizeColumn = true,
                        CanUserSortColumn = true,
                        MinWidth = new GridLength(WatchlistConstants.DefaultColumnMinWidth, GridUnitType.Pixel),
                        MaxWidth = new GridLength(WatchlistConstants.DefaultColumnMaxWidth, GridUnitType.Pixel),
                        CompareAscending = (a, b) => string.Compare(a?.Tag, b?.Tag, StringComparison.OrdinalIgnoreCase),
                        CompareDescending = (a, b) => string.Compare(b?.Tag, a?.Tag, StringComparison.OrdinalIgnoreCase)
                    },
                    width: width);
            case "Notes":
                return new TemplateColumn<WatchlistItemViewModel>(
                    header: header,
                    cellTemplate: new FuncDataTemplate<WatchlistItemViewModel>((_, _) =>
                    {
                        return new StockAnalyzer.Avalonia.Views.Controls.NotesCellControl();
                    }),
                    cellEditingTemplate: null,
                    options: new global::Avalonia.Controls.Models.TreeDataGrid.TemplateColumnOptions<WatchlistItemViewModel>
                    {
                        CanUserResizeColumn = true,
                        CanUserSortColumn = true,
                        MinWidth = new GridLength(WatchlistConstants.DefaultColumnMinWidth, GridUnitType.Pixel),
                        MaxWidth = new GridLength(WatchlistConstants.DefaultColumnMaxWidth, GridUnitType.Pixel),
                        CompareAscending = (a, b) => string.Compare(a?.Notes, b?.Notes, StringComparison.OrdinalIgnoreCase),
                        CompareDescending = (a, b) => string.Compare(b?.Notes, a?.Notes, StringComparison.OrdinalIgnoreCase)
                    },
                    width: width);
            case "Long":
                return CreateTextColumn(header, nameof(WatchlistItemViewModel.DisplayLong), width, false);
            case "ExitLong":
                return CreateTextColumn(header, nameof(WatchlistItemViewModel.DisplayExitLong), width, false);
            case "StopLossLong":
                return CreateTextColumn(header, nameof(WatchlistItemViewModel.DisplayStopLossLong), width, false);
            case "Short":
                return CreateTextColumn(header, nameof(WatchlistItemViewModel.DisplayShort), width, false);
            case "ExitShort":
                return CreateTextColumn(header, nameof(WatchlistItemViewModel.DisplayExitShort), width, false);
            case "StopLossShort":
                return CreateTextColumn(header, nameof(WatchlistItemViewModel.DisplayStopLossShort), width, false);
            case "IsLong":
                return CreateSignalColumn(header, nameof(WatchlistItemViewModel.DisplayIsLong), nameof(WatchlistItemViewModel.ToolTipIsLong), width);
            case "IsTPLong":
                return CreateSignalColumn(header, nameof(WatchlistItemViewModel.DisplayIsTPLong), nameof(WatchlistItemViewModel.ToolTipIsTPLong), width);
            case "IsSLLong":
                return CreateSignalColumn(header, nameof(WatchlistItemViewModel.DisplayIsSLLong), nameof(WatchlistItemViewModel.ToolTipIsSLLong), width);
            case "IsShort":
                return CreateSignalColumn(header, nameof(WatchlistItemViewModel.DisplayIsShort), nameof(WatchlistItemViewModel.ToolTipIsShort), width);
            case "IsTPShort":
                return CreateSignalColumn(header, nameof(WatchlistItemViewModel.DisplayIsTPShort), nameof(WatchlistItemViewModel.ToolTipIsTPShort), width);
            case "IsSLShort":
                return CreateSignalColumn(header, nameof(WatchlistItemViewModel.DisplayIsSLShort), nameof(WatchlistItemViewModel.ToolTipIsSLShort), width);
            default:
                if (memberName.StartsWith("Indicator_"))
                {
                    return CreateDynamicIndicatorColumn(header, memberName, width);
                }
                if (IsSignedNumericColumn(memberName))
                {
                    return CreateColoredColumn(header, memberName, width);
                }
                bool isText = memberName == "LongBusinessSummary" || 
                              memberName == "Region" || 
                              memberName == "QuoteType" || 
                              memberName == "ExchangeTimezoneName" || 
                              memberName == "RecommendationKey" ||
                              memberName == "Sector" ||
                              memberName == "Industry";
                return CreateTextColumn(header, "Display" + memberName, width, isText);
        }
    }

    private static IColumn CreateDynamicIndicatorColumn(string header, string memberName, GridLength width)
    {
        return new TemplateColumn<WatchlistItemViewModel>(
            header: header,
            cellTemplate: new FuncDataTemplate<WatchlistItemViewModel>((item, _) =>
            {
                var tb = new global::Avalonia.Controls.TextBlock
                {
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                    Margin = new global::Avalonia.Thickness(4, 0)
                };
                if (item != null)
                {
                    var val = item.GetRawValue(memberName);
                    tb.Text = FormatIndicatorValue(val);
                }
                return tb;
            }),
            cellEditingTemplate: null,
            width: width,
            options: new global::Avalonia.Controls.Models.TreeDataGrid.TemplateColumnOptions<WatchlistItemViewModel>
            {
                CanUserResizeColumn = true,
                MinWidth = new GridLength(WatchlistConstants.DefaultColumnMinWidth, GridUnitType.Pixel),
                MaxWidth = new GridLength(WatchlistConstants.DefaultColumnMaxWidth, GridUnitType.Pixel),
                CompareAscending = (a, b) => CompareDynamicValues(a?.GetRawValue(memberName), b?.GetRawValue(memberName)),
                CompareDescending = (a, b) => CompareDynamicValues(b?.GetRawValue(memberName), a?.GetRawValue(memberName))
            });
    }

    private static string FormatIndicatorValue(object? val)
    {
        if (val == null) return "-";
        if (val is bool b) return b ? "True" : "False";
        if (val is int i) return i.ToString(WatchlistConstants.FormatInteger);
        if (val is long l) return l.ToString(WatchlistConstants.FormatInteger);

        if (val is decimal d)
        {
            if (d == 0m) return 0m.ToString(WatchlistConstants.FormatIndicatorDecimal);
            decimal absD = Math.Abs(d);
            string fmt = absD < 0.01m ? WatchlistConstants.FormatMicroDecimal : WatchlistConstants.FormatIndicatorDecimal;
            return d.ToString(fmt);
        }
        if (val is double db)
        {
            if (double.IsNaN(db) || double.IsInfinity(db)) return "-";
            if (db == 0.0) return 0.0.ToString(WatchlistConstants.FormatIndicatorDecimal);
            double absDb = Math.Abs(db);
            string fmt = absDb < 0.01 ? WatchlistConstants.FormatMicroDecimal : WatchlistConstants.FormatIndicatorDecimal;
            return db.ToString(fmt);
        }
        if (val is float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return "-";
            if (f == 0f) return 0f.ToString(WatchlistConstants.FormatIndicatorDecimal);
            float absF = Math.Abs(f);
            string fmt = absF < 0.01f ? WatchlistConstants.FormatMicroDecimal : WatchlistConstants.FormatIndicatorDecimal;
            return f.ToString(fmt);
        }

        return val.ToString() ?? "-";
    }

    private static int CompareDynamicValues(object? valA, object? valB)
    {
        if (valA == null && valB == null) return 0;
        if (valA == null) return -1;
        if (valB == null) return 1;
        if (valA.GetType() == valB.GetType() && valA is IComparable comp) return comp.CompareTo(valB);
        try
        {
            double numA = Convert.ToDouble(valA);
            double numB = Convert.ToDouble(valB);
            return numA.CompareTo(numB);
        }
        catch
        {
            return string.Compare(valA?.ToString() ?? "", valB?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsSignedNumericColumn(string memberName)
    {
        return memberName switch
        {
            "ReturnOnEquity" or "ReturnOnAssets" or "GrossMargins" or "OperatingMargins" or "ProfitMargins" or
            "Ebitda" or "FreeCashflow" or "OperatingCashflow" or "RevenueGrowth" or "EarningsGrowth" or
            "EarningsYield" or "FcfYield" or "FcfMargin" or "EbitdaMargins" or "OperatingCashFlowYield" or "NetCashRatio" or
            "TrailingPE" or "ForwardPE" or "TrailingEps" or "ForwardEps" or "Beta" or "PegRatio" or 
            "EnterpriseToEbitda" or "PriceToCashFlowRatio" or "BookValue" or "GmtOffSetMilliseconds" or 
            "TargetHighPrice" or "TargetLowPrice" or "TargetMeanPrice" or "TargetMedianPrice" or 
            "RecommendationMean" or "NumberOfAnalystOpinions" => true,
            _ => false
        };
    }

    private static object CreateHeaderCheckBox(TreeDataGrid grid)
    {
        var cb = new global::Avalonia.Controls.CheckBox
        {
            IsThreeState = true,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new global::Avalonia.Thickness(0),
            Margin = new global::Avalonia.Thickness(0)
        };

        var headerContainer = new global::Avalonia.Controls.Grid
        {
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch,
            Margin = new global::Avalonia.Thickness(0)
        };
        headerContainer.Children.Add(cb);

        // Initial binding
        if (grid.DataContext != null)
        {
            cb.Bind(global::Avalonia.Controls.CheckBox.IsCheckedProperty, 
                new global::Avalonia.Data.Binding("SelectAll") 
                { 
                    Source = grid.DataContext, 
                    Mode = global::Avalonia.Data.BindingMode.TwoWay 
                });
        }

        // Re-bind when DataContext changes to ensure SelectAll remains synced
        grid.GetObservable(Control.DataContextProperty).Subscribe(dc => 
        {
            if (dc != null)
            {
                cb.Bind(global::Avalonia.Controls.CheckBox.IsCheckedProperty, 
                    new global::Avalonia.Data.Binding("SelectAll") 
                    { 
                        Source = dc, 
                        Mode = global::Avalonia.Data.BindingMode.TwoWay 
                    });
            }
        });

        return headerContainer;
    }

    private static readonly ChangeColorConverter _changeColorConverter = new();

    private static IColumn CreateColoredColumn(string header, string memberName, GridLength width)
    {
        string textProperty = "Display" + memberName;
        string valueProperty = memberName;
        return new TemplateColumn<WatchlistItemViewModel>(
            header: header,
            cellTemplate: new FuncDataTemplate<WatchlistItemViewModel>((item, _) =>
            {
                var tb = new global::Avalonia.Controls.TextBlock
                {
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new global::Avalonia.Thickness(4, 0)
                };
                tb.Bind(global::Avalonia.Controls.TextBlock.TextProperty, 
                    new global::Avalonia.Data.Binding(textProperty));
                tb.Bind(global::Avalonia.Controls.TextBlock.HorizontalAlignmentProperty,
                    new global::Avalonia.Data.Binding(textProperty) { Converter = AlignmentConverter.RightInstance });
                tb.Bind(global::Avalonia.Controls.TextBlock.ForegroundProperty, 
                    new global::Avalonia.Data.Binding(valueProperty) { Converter = _changeColorConverter });
                return tb;
            }),
            cellEditingTemplate: null,
            width: width,
            options: new global::Avalonia.Controls.Models.TreeDataGrid.TemplateColumnOptions<WatchlistItemViewModel>
            {
                CompareAscending = (a, b) => 
                {
                    if (a == null && b == null) return 0;
                    if (a == null) return -1;
                    if (b == null) return 1;
                    
                    var valA = a.GetRawValue(memberName);
                    var valB = b.GetRawValue(memberName);
                    
                    if (valA == null && valB == null) return 0;
                    if (valA == null) return -1;
                    if (valB == null) return 1;
                    
                    if (valA.GetType() == valB.GetType())
                    {
                        return ((IComparable)valA).CompareTo(valB);
                    }
                    
                    try
                    {
                        double numA = Convert.ToDouble(valA);
                        double numB = Convert.ToDouble(valB);
                        return numA.CompareTo(numB);
                    }
                    catch
                    {
                        return string.Compare(valA.ToString(), valB.ToString(), StringComparison.OrdinalIgnoreCase);
                    }
                },
                CompareDescending = (a, b) => 
                {
                    if (a == null && b == null) return 0;
                    if (a == null) return 1;
                    if (b == null) return -1;
                    
                    var valA = a.GetRawValue(memberName);
                    var valB = b.GetRawValue(memberName);
                    
                    if (valA == null && valB == null) return 0;
                    if (valA == null) return 1;
                    if (valB == null) return -1;
                    
                    if (valA.GetType() == valB.GetType())
                    {
                        return ((IComparable)valB).CompareTo(valA);
                    }
                    
                    try
                    {
                        double numA = Convert.ToDouble(valA);
                        double numB = Convert.ToDouble(valB);
                        return numB.CompareTo(numA);
                    }
                    catch
                    {
                        return string.Compare(valB.ToString(), valA.ToString(), StringComparison.OrdinalIgnoreCase);
                    }
                }
            });
    }

    private static IColumn CreateColoredColumn(string header, string textProperty, string valueProperty, GridLength width, global::Avalonia.Controls.Models.TreeDataGrid.TemplateColumnOptions<WatchlistItemViewModel> options)
    {
        if (options != null)
        {
            options.CanUserResizeColumn = true;
            options.MinWidth = new GridLength(WatchlistConstants.DefaultColumnMinWidth, GridUnitType.Pixel);
            options.MaxWidth = new GridLength(WatchlistConstants.DefaultColumnMaxWidth, GridUnitType.Pixel);
        }
        return new TemplateColumn<WatchlistItemViewModel>(
            header: header,
            cellTemplate: new FuncDataTemplate<WatchlistItemViewModel>((item, _) =>
            {
                var tb = new global::Avalonia.Controls.TextBlock
                {
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new global::Avalonia.Thickness(4, 0)
                };
                tb.Bind(global::Avalonia.Controls.TextBlock.TextProperty, 
                    new global::Avalonia.Data.Binding(textProperty));
                tb.Bind(global::Avalonia.Controls.TextBlock.HorizontalAlignmentProperty,
                    new global::Avalonia.Data.Binding(textProperty) { Converter = AlignmentConverter.RightInstance });
                tb.Bind(global::Avalonia.Controls.TextBlock.ForegroundProperty, 
                    new global::Avalonia.Data.Binding(valueProperty) { Converter = _changeColorConverter });
                return tb;
            }),
            cellEditingTemplate: null,
            width: width,
            options: options);
    }

    private class ChangeColorConverter : global::Avalonia.Data.Converters.IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            IBrush? successBrush = null;
            IBrush? errorBrush = null;
            IBrush? neutralBrush = null;

            if (global::Avalonia.Application.Current != null)
            {
                if (global::Avalonia.Application.Current.TryGetResource("Brush.Semantic.Success", null, out var successRes) && successRes is IBrush sb)
                {
                    successBrush = sb;
                }
                if (global::Avalonia.Application.Current.TryGetResource("Brush.Semantic.Error", null, out var errorRes) && errorRes is IBrush eb)
                {
                    errorBrush = eb;
                }
                if (global::Avalonia.Application.Current.TryGetResource("Brush.Semantic.Neutral", null, out var neutralRes) && neutralRes is IBrush nb)
                {
                    neutralBrush = nb;
                }
            }

            successBrush ??= Brushes.LimeGreen;
            errorBrush ??= Brushes.Crimson;
            neutralBrush ??= Brushes.Gray;

            if (value is double d)
            {
                if (d > 0) return successBrush;
                if (d < 0) return errorBrush;
                return neutralBrush;
            }
            else if (value is decimal m)
            {
                if (m > 0) return successBrush;
                if (m < 0) return errorBrush;
                return neutralBrush;
            }
            else if (value is long l)
            {
                if (l > 0) return successBrush;
                if (l < 0) return errorBrush;
                return neutralBrush;
            }
            else if (value is int iVal)
            {
                if (iVal > 0) return successBrush;
                if (iVal < 0) return errorBrush;
                return neutralBrush;
            }
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    private static readonly SignalColorConverter _signalColorConverter = new();

    private class SignalColorConverter : global::Avalonia.Data.Converters.IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            IBrush? successBrush = null;
            IBrush? errorBrush = null;
            IBrush? neutralBrush = null;

            if (global::Avalonia.Application.Current != null)
            {
                if (global::Avalonia.Application.Current.TryGetResource("Brush.Semantic.Success", null, out var successRes) && successRes is IBrush sb)
                {
                    successBrush = sb;
                }
                if (global::Avalonia.Application.Current.TryGetResource("Brush.Semantic.Error", null, out var errorRes) && errorRes is IBrush eb)
                {
                    errorBrush = eb;
                }
                if (global::Avalonia.Application.Current.TryGetResource("Brush.Semantic.Neutral", null, out var neutralRes) && neutralRes is IBrush nb)
                {
                    neutralBrush = nb;
                }
            }

            successBrush ??= Brushes.LimeGreen;
            errorBrush ??= Brushes.Crimson;
            neutralBrush ??= Brushes.Gray;

            if (value is bool bVal)
            {
                return bVal ? successBrush : errorBrush;
            }
            else if (value is string strVal)
            {
                if (strVal.Equals("True", StringComparison.OrdinalIgnoreCase)) return successBrush;
                if (strVal.Equals("False", StringComparison.OrdinalIgnoreCase)) return errorBrush;
                return neutralBrush;
            }

            return neutralBrush;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    private class AlignmentConverter : global::Avalonia.Data.Converters.IValueConverter
    {
        public static readonly AlignmentConverter LeftInstance = new(global::Avalonia.Layout.HorizontalAlignment.Left);
        public static readonly AlignmentConverter RightInstance = new(global::Avalonia.Layout.HorizontalAlignment.Right);

        private readonly global::Avalonia.Layout.HorizontalAlignment _defaultAlign;

        private AlignmentConverter(global::Avalonia.Layout.HorizontalAlignment defaultAlign)
        {
            _defaultAlign = defaultAlign;
        }

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var s = value?.ToString();
            if (string.IsNullOrEmpty(s) || s == "-" || s == "N/A" || s == "None")
            {
                return global::Avalonia.Layout.HorizontalAlignment.Center;
            }
            return _defaultAlign;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    private static int GetItemIndex<T>(IEnumerable<T> items, T item)
    {
        if (items is System.Collections.IList list) return list.IndexOf(item);
        int i = 0;
        foreach (var x in items)
        {
            if (Equals(x, item)) return i;
            i++;
        }
        return -1;
    }

    private static IColumn CreateTextColumn(string header, string propertyName, GridLength width, bool leftAlign = false)
    {
        // Extract raw memberName (strip "Display" prefix) for retrieving sorting value
        var rawMemberName = propertyName.StartsWith("Display") ? propertyName.Substring(7) : propertyName;

        return new TemplateColumn<WatchlistItemViewModel>(
            header: header,
            cellTemplate: new FuncDataTemplate<WatchlistItemViewModel>((item, _) =>
            {
                var tb = new global::Avalonia.Controls.TextBlock
                {
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new global::Avalonia.Thickness(4, 0)
                };
                tb.Bind(global::Avalonia.Controls.TextBlock.TextProperty, 
                    new global::Avalonia.Data.Binding(propertyName));
                tb.Bind(global::Avalonia.Controls.TextBlock.HorizontalAlignmentProperty,
                    new global::Avalonia.Data.Binding(propertyName) { Converter = leftAlign ? AlignmentConverter.LeftInstance : AlignmentConverter.RightInstance });
                return tb;
            }),
            cellEditingTemplate: null,
            width: width,
            options: new global::Avalonia.Controls.Models.TreeDataGrid.TemplateColumnOptions<WatchlistItemViewModel>
            {
                 CanUserResizeColumn = true,
                 MinWidth = new GridLength(WatchlistConstants.DefaultColumnMinWidth, GridUnitType.Pixel),
                 MaxWidth = new GridLength(WatchlistConstants.DefaultColumnMaxWidth, GridUnitType.Pixel),
                 CompareAscending = (a, b) => 
                 {
                     if (a == null && b == null) return 0;
                     if (a == null) return -1;
                     if (b == null) return 1;
                     
                     var valA = a.GetRawValue(rawMemberName);
                     var valB = b.GetRawValue(rawMemberName);
                     
                     if (valA == null && valB == null) return 0;
                     if (valA == null) return -1;
                     if (valB == null) return 1;
                     
                     if (valA.GetType() == valB.GetType())
                     {
                         return ((IComparable)valA).CompareTo(valB);
                     }
                     
                     try
                     {
                         double numA = Convert.ToDouble(valA);
                         double numB = Convert.ToDouble(valB);
                         return numA.CompareTo(numB);
                     }
                     catch
                     {
                         return string.Compare(valA.ToString(), valB.ToString(), StringComparison.OrdinalIgnoreCase);
                     }
                 },
                 CompareDescending = (a, b) => 
                 {
                     if (a == null && b == null) return 0;
                     if (a == null) return 1;
                     if (b == null) return -1;
                     
                     var valA = a.GetRawValue(rawMemberName);
                     var valB = b.GetRawValue(rawMemberName);
                     
                     if (valA == null && valB == null) return 0;
                     if (valA == null) return 1;
                     if (valB == null) return -1;
                     
                     if (valA.GetType() == valB.GetType())
                     {
                         return ((IComparable)valB).CompareTo(valA);
                     }
                     
                     try
                     {
                         double numA = Convert.ToDouble(valA);
                         double numB = Convert.ToDouble(valB);
                         return numB.CompareTo(numA);
                     }
                     catch
                     {
                         return string.Compare(valB.ToString(), valA.ToString(), StringComparison.OrdinalIgnoreCase);
                     }
                 }
            });
    }

    private static IColumn CreateSignalColumn(string header, string displayProp, string toolTipProp, GridLength width)
    {
        var rawMemberName = displayProp.StartsWith("Display") ? displayProp.Substring(7) : displayProp;
        return new TemplateColumn<WatchlistItemViewModel>(
            header: header,
            cellTemplate: new FuncDataTemplate<WatchlistItemViewModel>((item, _) =>
            {
                var tb = new global::Avalonia.Controls.TextBlock
                {
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                    Margin = new global::Avalonia.Thickness(4, 0)
                };
                tb.Bind(global::Avalonia.Controls.TextBlock.TextProperty, new global::Avalonia.Data.Binding(displayProp));
                tb.Bind(global::Avalonia.Controls.TextBlock.ForegroundProperty, new global::Avalonia.Data.Binding(rawMemberName) { Converter = _signalColorConverter });
                tb.Bind(global::Avalonia.Controls.ToolTip.TipProperty, new global::Avalonia.Data.Binding(toolTipProp));
                return tb;
            }),
            cellEditingTemplate: null,
            width: width,
            options: new global::Avalonia.Controls.Models.TreeDataGrid.TemplateColumnOptions<WatchlistItemViewModel>
            {
                CanUserResizeColumn = true,
                MinWidth = new GridLength(WatchlistConstants.DefaultColumnMinWidth, GridUnitType.Pixel),
                MaxWidth = new GridLength(WatchlistConstants.DefaultColumnMaxWidth, GridUnitType.Pixel),
                CompareAscending = (a, b) =>
                {
                    if (a == null && b == null) return 0;
                    if (a == null) return -1;
                    if (b == null) return 1;
                    var valA = a.GetRawValue(rawMemberName);
                    var valB = b.GetRawValue(rawMemberName);
                    return Nullable.Compare(valA as bool?, valB as bool?);
                },
                CompareDescending = (a, b) =>
                {
                    if (a == null && b == null) return 0;
                    if (a == null) return 1;
                    if (b == null) return -1;
                    var valA = a.GetRawValue(rawMemberName);
                    var valB = b.GetRawValue(rawMemberName);
                    return Nullable.Compare(valB as bool?, valA as bool?);
                }
            });
    }

}
