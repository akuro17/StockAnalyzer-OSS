using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;

namespace StockAnalyzer.Avalonia;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// Maintains explicit overrides for ViewModels whose Views reside in sub-namespaces.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    /// <summary>
    /// Explicit ViewModel→View type mappings for cases where the default
    /// "ViewModel" → "View" string replacement cannot resolve the correct namespace.
    /// Add entries here when a View resides in a sub-folder (e.g., Views/Chart/).
    /// </summary>
    private static readonly Dictionary<Type, Type> _explicitMappings = new()
    {
        [typeof(ChartViewModel)] = typeof(Views.Chart.ChartView),
        [typeof(ThemeSettingsViewModel)] = typeof(ThemeSettingsView),
        [typeof(FontsSettingsViewModel)] = typeof(FontsSettingsView),
        [typeof(DrawingSettingsViewModel)] = typeof(Views.Dialogs.Chart.DrawingSettingsView),
        [typeof(CandlestickSettingsViewModel)] = typeof(Views.Dialogs.Chart.CandlestickSettingsView),
        [typeof(OhlcBarSettingsViewModel)] = typeof(Views.Dialogs.Chart.OhlcBarSettingsView),
        [typeof(RenkoSettingsViewModel)] = typeof(Views.Dialogs.Chart.RenkoSettingsView),
        [typeof(KagiSettingsViewModel)] = typeof(Views.Dialogs.Chart.KagiSettingsView),
        [typeof(PnfSettingsViewModel)] = typeof(Views.Dialogs.Chart.PnfSettingsView),
        [typeof(LineSettingsViewModel)] = typeof(Views.Dialogs.Chart.LineSettingsView),
        [typeof(AreaSettingsViewModel)] = typeof(Views.Dialogs.Chart.AreaSettingsView),
        [typeof(HeikinAshiSettingsViewModel)] = typeof(Views.Dialogs.Chart.HeikinAshiSettingsView),
        [typeof(ThreeLineBreakSettingsViewModel)] = typeof(Views.Dialogs.Chart.ThreeLineBreakSettingsView),
        [typeof(RelativePerformanceSettingsViewModel)] = typeof(Views.Dialogs.Chart.RelativePerformanceSettingsView),
        [typeof(ReverseWatchSettingsViewModel)] = typeof(Views.Dialogs.Chart.ReverseWatchSettingsView),
    };

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var vmType = param.GetType();

        // 1. Check explicit mappings first
        if (_explicitMappings.TryGetValue(vmType, out var viewType))
        {
            return (Control)Activator.CreateInstance(viewType)!;
        }

        // 2. Fallback: convention-based resolution
        var name = vmType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }
        
        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
