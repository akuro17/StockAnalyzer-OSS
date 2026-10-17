using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>One finished read of the stored bar counts with the inputs it was made for (the words of the warnings use these, not the inputs of a later edit).</summary>
public sealed record SufficiencySnapshot(TrainingSufficiencyResult Result, int RequiredBars, int Window, int Horizon, int Splits, int Gap, int SampleCap,
    DateOnly? End);

/// <summary>
/// The words of the two warnings under the data period of the Training Wizard (<c>TrainingDataSufficiency</c> gives the figures). It owns the number format and the
/// culture of those texts, so the view model holds no format literal: the culture is <see cref="DisplayFormat.Culture"/> (SA_ARCHITECTURE_RULES, Culture Invariance).
/// </summary>
public static class TrainingSufficiencyDisplay
{
    private const string NumberFormat = "N0";

    /// <summary>Tickers that have too few bars in the period, with the start that would fix it and the tickers no start can fix; empty when no ticker is short.</summary>
    public static string RequirementWarning(SufficiencySnapshot snapshot)
    {
        var result = snapshot.Result;
        if (result.ShortSymbols.Count == 0) return string.Empty;
        var text = LocalizationManager.Instance;
        string line = string.Format(DisplayFormat.Culture, text["TrainingWizard_Sufficiency_Short"], Number(snapshot.RequiredBars), snapshot.Window, snapshot.Horizon,
            snapshot.Splits, snapshot.Gap, SymbolList(result.ShortSymbols, info => info.BarsInRange));
        if (result.MinimumStart is { } minimumStart)
        {
            string end = snapshot.End is { } endDate ? Date(endDate) : text["TrainingWizard_Sufficiency_LatestBar"];
            line += " " + string.Format(DisplayFormat.Culture, text["TrainingWizard_Sufficiency_StartHint"], Date(minimumStart), Number(snapshot.RequiredBars), end);
        }
        if (result.Unreachable.Count > 0)
            line += " " + string.Format(DisplayFormat.Culture, text["TrainingWizard_Sufficiency_Unreachable"], SymbolList(result.Unreachable, info => info.BarsUpToEnd));
        return line;
    }

    /// <summary>The sample estimate against the cap and what to change; empty when the estimate fits.</summary>
    public static string SampleLimitWarning(SufficiencySnapshot snapshot)
    {
        var result = snapshot.Result;
        if (!result.OverLimit) return string.Empty;
        var text = LocalizationManager.Instance;
        string line = string.Format(DisplayFormat.Culture, text["TrainingWizard_Sufficiency_Limit"], Number(result.TotalEstimate), result.TickerCount, Number(snapshot.SampleCap));
        string tail;
        if (result.MinimumStart is { } minimumStart && result.TotalAtMinimumStart is { } atMinimum && atMinimum <= snapshot.SampleCap)
            tail = string.Format(DisplayFormat.Culture, text["TrainingWizard_Sufficiency_LimitStart"], Date(minimumStart), Number(atMinimum));
        else if (result.MaxTickers == 0)
            tail = string.Format(DisplayFormat.Culture, text["TrainingWizard_Sufficiency_LimitSingle"], Number(result.EstimateAtRequiredBars), Number(snapshot.SampleCap),
                Number(TrainingResourceOverrides.MaximumSamples));
        else if (result.MaxTickers is { } maxTickers && maxTickers < result.TickerCount)
            tail = string.Format(DisplayFormat.Culture, text["TrainingWizard_Sufficiency_LimitTickers"], maxTickers, result.TickerCount - maxTickers);
        else
            tail = text["TrainingWizard_Sufficiency_LimitFewer"];
        return line + " " + tail;
    }

    /// <summary>"SYM (n bars), SYM (n bars)" for the first <see cref="TrainingWizardUiLayout.SufficiencyListedSymbolsMax"/> symbols, then "+k more".</summary>
    private static string SymbolList(IReadOnlyList<SymbolBarInfo> infos, Func<SymbolBarInfo, int> bars)
    {
        var text = LocalizationManager.Instance;
        string list = string.Join(", ", infos.Take(TrainingWizardUiLayout.SufficiencyListedSymbolsMax)
            .Select(info => string.Format(DisplayFormat.Culture, text["TrainingWizard_Sufficiency_SymbolBars"], info.Symbol, Number(bars(info)))));
        int more = infos.Count - TrainingWizardUiLayout.SufficiencyListedSymbolsMax;
        return more > 0 ? list + " " + string.Format(DisplayFormat.Culture, text["TrainingWizard_Sufficiency_More"], more) : list;
    }

    private static string Number(long value) => value.ToString(NumberFormat, DisplayFormat.Culture);

    private static string Date(DateOnly date) => DisplayFormat.Date(date.ToDateTime(TimeOnly.MinValue));
}
