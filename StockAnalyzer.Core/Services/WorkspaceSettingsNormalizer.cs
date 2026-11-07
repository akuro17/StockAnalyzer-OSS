using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Stage 1 normalisation of a freshly read <see cref="WorkspaceSettings"/>: makes it safe to apply without touching any view model.
/// It moves implicit clean-up that the restore code already performs (healing indicators one by one, ignoring unparseable
/// enums, clamping) to where its effect can be reported; it adds no new meaning.
/// Warning paths refer to positions in the lists as they are handed in (earlier removals shift later indices).
/// </summary>
public static class WorkspaceSettingsNormalizer
{
    private const string IndicatorsMember = nameof(WorkspaceSettings.Indicators);

    /// <summary>
    /// Normalises <paramref name="settings"/> in place (it must be an instance the caller owns, e.g. produced by the reader).
    /// Idempotent: a second call reports nothing.
    /// </summary>
    /// <returns>
    /// One warning per change. <see cref="WorkspaceWarningSeverity.Lossy"/> only when an indicator had to be removed;
    /// everything else is <see cref="WorkspaceWarningSeverity.Cosmetic"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">When <paramref name="settings"/> is null.</exception>
    public static IReadOnlyList<WorkspaceLoadWarning> Normalize(WorkspaceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var warnings = new List<WorkspaceLoadWarning>();

        CleanTopLevelLists(settings, warnings);
        HealIndicators(settings.Indicators, IndicatorsMember, warnings);

        for (var i = 0; i < settings.PanelCharts.Count; i++)
        {
            var panel = settings.PanelCharts[i];
            var path = $"{nameof(WorkspaceSettings.PanelCharts)}[{i}].{IndicatorsMember}";
            panel.Indicators = CleanNestedList(panel.Indicators, path, warnings);
            HealIndicators(panel.Indicators, path, warnings);
        }

        for (var i = 0; i < settings.DetachedTabs.Count; i++)
        {
            var tab = settings.DetachedTabs[i];
            var path = $"{nameof(WorkspaceSettings.DetachedTabs)}[{i}].{IndicatorsMember}";
            tab.Indicators = CleanNestedList(tab.Indicators, path, warnings);
            HealIndicators(tab.Indicators, path, warnings);
            NormalizeDetachedGeometry(tab, $"{nameof(WorkspaceSettings.DetachedTabs)}[{i}]", warnings);
        }

        NormalizeEnumText(settings, warnings);
        NormalizePanelSizes(settings, warnings);
        NormalizeSelectedWatchlist(settings, warnings);
        return warnings;
    }

    // ---- lists ----------------------------------------------------------------------------------------------

    /// <summary>Every top-level <c>List&lt;T&gt;</c> section: null becomes empty, null entries are removed.</summary>
    private static void CleanTopLevelLists(WorkspaceSettings settings, List<WorkspaceLoadWarning> warnings)
    {
        foreach (var property in typeof(WorkspaceSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!IsList(property.PropertyType) || property.SetMethod is not { IsPublic: true })
            {
                continue;
            }

            if (property.GetValue(settings) is IList list)
            {
                RemoveNullEntries(list, property.Name, warnings);
            }
            else
            {
                property.SetValue(settings, Activator.CreateInstance(property.PropertyType));
                warnings.Add(Cosmetic(property.Name, "A null list was replaced by an empty list."));
            }
        }
    }

    private static List<CoreIndicatorSettings> CleanNestedList(List<CoreIndicatorSettings>? list, string path, List<WorkspaceLoadWarning> warnings)
    {
        if (list is null)
        {
            warnings.Add(Cosmetic(path, "A null list was replaced by an empty list."));
            return new List<CoreIndicatorSettings>();
        }

        RemoveNullEntries(list, path, warnings);
        return list;
    }

    private static void RemoveNullEntries(IList list, string path, List<WorkspaceLoadWarning> warnings)
    {
        var removed = 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is null)
            {
                list.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            warnings.Add(Cosmetic(path, $"{removed.ToString(CultureInfo.InvariantCulture)} null entr{(removed == 1 ? "y was" : "ies were")} removed."));
        }
    }

    private static bool IsList(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);

    // ---- indicators -----------------------------------------------------------------------------------------

    /// <summary>Heals each indicator on its own; one that cannot be healed is removed so it cannot abort the restore.</summary>
    private static void HealIndicators(List<CoreIndicatorSettings> indicators, string path, List<WorkspaceLoadWarning> warnings)
    {
        var healthy = new List<CoreIndicatorSettings>(indicators.Count);
        for (var i = 0; i < indicators.Count; i++)
        {
            try
            {
                DefaultCoreIndicatorSettings.AutoHeal(indicators[i]);
                healthy.Add(indicators[i]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(new WorkspaceLoadWarning(
                    WorkspaceWarningSeverity.Lossy,
                    $"{path}[{i}]",
                    $"The indicator ({indicators[i].TypeEnum}) could not be restored and was removed: {ex.GetType().Name}: {ex.Message}"));
            }
        }

        if (healthy.Count != indicators.Count)
        {
            indicators.Clear();
            indicators.AddRange(healthy);
        }
    }

    // ---- scalars --------------------------------------------------------------------------------------------

    /// <summary>
    /// Text that the restore code would silently ignore becomes null. The parse flags mirror the apply sites exactly:
    /// timeframe ignores case (WorkspaceCoordinator), chart type does not (MainWindowViewModel.RestoreChartSettings).
    /// A number that is not a defined member also counts as unparseable.
    /// </summary>
    private static void NormalizeEnumText(WorkspaceSettings settings, List<WorkspaceLoadWarning> warnings)
    {
        if (settings.SelectedTimeframe is not null
            && !(Enum.TryParse<TimeframeType>(settings.SelectedTimeframe, ignoreCase: true, out var timeframe) && Enum.IsDefined(timeframe)))
        {
            warnings.Add(Cosmetic(nameof(WorkspaceSettings.SelectedTimeframe), $"'{settings.SelectedTimeframe}' is not a known timeframe and was cleared."));
            settings.SelectedTimeframe = null;
        }

        if (settings.ChartType is not null
            && !(Enum.TryParse<ChartType>(settings.ChartType, out var chartType) && Enum.IsDefined(chartType)))
        {
            warnings.Add(Cosmetic(nameof(WorkspaceSettings.ChartType), $"'{settings.ChartType}' is not a known chart type and was cleared."));
            settings.ChartType = null;
        }
    }

    /// <summary>Non-finite sizes become the layout defaults. Range clamping stays in the apply code.</summary>
    private static void NormalizePanelSizes(WorkspaceSettings settings, List<WorkspaceLoadWarning> warnings)
    {
        settings.LeftPanelWidth = FiniteOr(settings.LeftPanelWidth, LayoutConstants.DefaultLeftWidth, nameof(WorkspaceSettings.LeftPanelWidth), warnings);
        settings.RightPanelWidth = FiniteOr(settings.RightPanelWidth, LayoutConstants.DefaultRightWidth, nameof(WorkspaceSettings.RightPanelWidth), warnings);
        settings.TopPanelHeight = FiniteOr(settings.TopPanelHeight, LayoutConstants.DefaultTopHeight, nameof(WorkspaceSettings.TopPanelHeight), warnings);
        settings.BottomPanelHeight = FiniteOr(settings.BottomPanelHeight, LayoutConstants.DefaultBottomHeight, nameof(WorkspaceSettings.BottomPanelHeight), warnings);
    }

    private static void NormalizeDetachedGeometry(DetachedTabInfo tab, string path, List<WorkspaceLoadWarning> warnings)
    {
        tab.X = FiniteOr(tab.X, LayoutConstants.DefaultDetachedWindowX, $"{path}.{nameof(DetachedTabInfo.X)}", warnings);
        tab.Y = FiniteOr(tab.Y, LayoutConstants.DefaultDetachedWindowY, $"{path}.{nameof(DetachedTabInfo.Y)}", warnings);
        tab.Width = FiniteOr(tab.Width, LayoutConstants.DefaultDetachedWindowWidth, $"{path}.{nameof(DetachedTabInfo.Width)}", warnings);
        tab.Height = FiniteOr(tab.Height, LayoutConstants.DefaultDetachedWindowHeight, $"{path}.{nameof(DetachedTabInfo.Height)}", warnings);
    }

    private static double FiniteOr(double value, double fallback, string path, List<WorkspaceLoadWarning> warnings)
    {
        if (double.IsFinite(value))
        {
            return value;
        }

        warnings.Add(Cosmetic(path, $"A non-finite value was replaced by the default {fallback.ToString(CultureInfo.InvariantCulture)}."));
        return fallback;
    }

    private static void NormalizeSelectedWatchlist(WorkspaceSettings settings, List<WorkspaceLoadWarning> warnings)
    {
        if (settings.SelectedWatchlistId is { } id && settings.WatchlistProfiles.All(p => p.Id != id))
        {
            warnings.Add(Cosmetic(nameof(WorkspaceSettings.SelectedWatchlistId), "The selected watchlist does not exist and the selection was cleared."));
            settings.SelectedWatchlistId = null;
        }
    }

    private static WorkspaceLoadWarning Cosmetic(string path, string message) =>
        new(WorkspaceWarningSeverity.Cosmetic, path, message);
}
