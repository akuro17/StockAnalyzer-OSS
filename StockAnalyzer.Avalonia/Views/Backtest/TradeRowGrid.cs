using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.Views.Backtest;

/// <summary>
/// Dynamically renders trade history row cells based on the currently active trade columns.
/// Keeps SharedSizeGroup synchronized with the Trade History header grid for pixel-perfect column alignment.
/// </summary>
public sealed class TradeRowGrid : Grid
{
    public static readonly StyledProperty<IReadOnlyList<string>?> ActiveColumnsProperty =
        AvaloniaProperty.Register<TradeRowGrid, IReadOnlyList<string>?>(nameof(ActiveColumns));

    public IReadOnlyList<string>? ActiveColumns
    {
        get => GetValue(ActiveColumnsProperty);
        set => SetValue(ActiveColumnsProperty, value);
    }

    static TradeRowGrid()
    {
        ActiveColumnsProperty.Changed.AddClassHandler<TradeRowGrid>((grid, _) => grid.Rebuild());
    }

    public TradeRowGrid()
    {
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ActiveColumns == null && this.FindAncestorOfType<BacktestResultsView>() is { } view && view.DataContext is BacktestResultsViewModel vm)
        {
            ActiveColumns = vm.ActiveTradeColumns;
        }
        LocalizationManager.Instance.LocaleChanged += OnLocaleChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationManager.Instance.LocaleChanged -= OnLocaleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLocaleChanged(object? sender, EventArgs e)
    {
        Rebuild();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Rebuild();
    }

    private void Rebuild()
    {
        ColumnDefinitions.Clear();
        Children.Clear();

        var cols = ActiveColumns;
        if (cols == null || cols.Count == 0 || DataContext is not BacktestTradeRow row)
        {
            return;
        }

        for (int i = 0; i < cols.Count; i++)
        {
            string colId = cols[i];
            ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)
            {
                SharedSizeGroup = colId,
                MinWidth = GetColumnMinWidth(colId)
            });

            var cell = CreateCell(colId, row, i);
            Grid.SetColumn(cell, i);
            Children.Add(cell);
        }
    }

    private static double GetColumnMinWidth(string colId) =>
        BacktestTradeHistoryColumnChooserViewModel.GetColumnMinWidth(colId);

    private Border CreateCell(string colId, BacktestTradeRow row, int columnIndex)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = Brushes.Transparent,
            MinWidth = GetColumnMinWidth(colId)
        };
        border.Classes.Add("TableCell");
        border.Classes.Add($"Col{colId}");

        var tb = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        tb.Classes.Add("CellValue");
        Grid.SetColumn(tb, columnIndex);

        switch (colId)
        {
            case "TradeId":
                tb.Classes.Add("NumericValue");
                tb.Text = row.TradeId.ToString(CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "Side":
                tb.FontWeight = FontWeight.SemiBold;
                tb.TextAlignment = TextAlignment.Center;
                tb.Text = row.Side.ToString();
                break;

            case "EntryTime":
                tb.Classes.Add("NumericValue");
                tb.Text = row.EntryTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "EntryPrice":
                tb.Classes.Add("NumericValue");
                tb.Text = row.EntryPrice.ToString("F4", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "ExitTime":
                tb.Classes.Add("NumericValue");
                tb.Text = row.ExitTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "ExitPrice":
                tb.Classes.Add("NumericValue");
                tb.Text = row.ExitPrice.ToString("F4", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "Quantity":
                tb.Classes.Add("NumericValue");
                tb.Text = row.Quantity.ToString("F4", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "EntryValue":
                tb.Classes.Add("NumericValue");
                tb.Text = row.EntryValue.ToString("F2", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "ExitValue":
                tb.Classes.Add("NumericValue");
                tb.Text = row.ExitValue.ToString("F2", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "Commission":
                tb.Classes.Add("NumericValue");
                tb.Text = row.Commission.ToString("F2", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "Slippage":
                tb.Classes.Add("NumericValue");
                tb.Text = row.Slippage.ToString("F2", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "ClosedNet":
                tb.Classes.Add("NumericValue");
                tb.Text = row.ClosedNet.ToString("F4", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                IBrush? pnlBrush = row.PnLSemantic switch
                {
                    BacktestMetricSemantic.Plus => this.FindResource("Brush.Semantic.Profit") as IBrush,
                    BacktestMetricSemantic.Minus => this.FindResource("Brush.Semantic.Loss") as IBrush,
                    _ => this.FindResource("Brush.Text.Primary") as IBrush
                };
                if (pnlBrush != null)
                {
                    tb.Foreground = pnlBrush;
                }
                break;

            case "PnLPercent":
                tb.Classes.Add("NumericValue");
                tb.Text = row.PnLPercent.HasValue ? (row.PnLPercent.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%") : "-";
                tb.TextAlignment = TextAlignment.Right;
                IBrush? pnlPctBrush = row.PnLPercentSemantic switch
                {
                    BacktestMetricSemantic.Plus => this.FindResource("Brush.Semantic.Profit") as IBrush,
                    BacktestMetricSemantic.Minus => this.FindResource("Brush.Semantic.Loss") as IBrush,
                    _ => this.FindResource("Brush.Text.Primary") as IBrush
                };
                if (pnlPctBrush != null)
                {
                    tb.Foreground = pnlPctBrush;
                }
                break;

            case "MfePrice":
                tb.Classes.Add("NumericValue");
                tb.Text = row.MfePrice.ToString("F4", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "MfePercent":
                tb.Classes.Add("NumericValue");
                tb.Text = row.MfePercent.HasValue ? (row.MfePercent.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%") : "-";
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "MaePrice":
                tb.Classes.Add("NumericValue");
                tb.Text = row.MaePrice.ToString("F4", CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "MaePercent":
                tb.Classes.Add("NumericValue");
                tb.Text = row.MaePercent.HasValue ? (row.MaePercent.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%") : "-";
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "HoldingBars":
                tb.Classes.Add("NumericValue");
                tb.Text = row.HoldingBars.ToString(CultureInfo.InvariantCulture);
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "Duration":
                tb.Classes.Add("NumericValue");
                tb.Text = row.Duration.TotalDays >= 1.0
                    ? $"{(int)row.Duration.TotalDays}d {row.Duration.Hours}h"
                    : $"{(int)row.Duration.TotalHours}h {row.Duration.Minutes}m";
                tb.TextAlignment = TextAlignment.Right;
                break;

            case "EntrySignal":
                tb.Text = row.EntrySignal;
                tb.TextAlignment = TextAlignment.Left;
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
                ToolTip.SetTip(tb, row.EntrySignal);
                break;

            case "ExitReason":
                tb.Text = row.ExitReason;
                tb.TextAlignment = TextAlignment.Left;
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
                ToolTip.SetTip(tb, row.ExitReason);
                break;

            case "ForcedLiquidation":
                tb.TextAlignment = TextAlignment.Center;
                if (row.IsForcedLiquidation)
                {
                    tb.Text = LocalizationManager.Instance["Backtest_TradeList_ForcedLiquidation_Badge"] ?? "FORCED";
                    if (this.FindResource("Brush.Semantic.Error") is IBrush errBrush)
                    {
                        tb.Foreground = errBrush;
                    }
                }
                else
                {
                    tb.Text = string.Empty;
                }
                break;

            default:
                tb.Text = string.Empty;
                break;
        }

        border.Child = tb;
        return border;
    }
}
