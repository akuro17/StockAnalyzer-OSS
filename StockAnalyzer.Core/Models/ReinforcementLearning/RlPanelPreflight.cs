using System;
using System.Collections.Immutable;
using System.Linq;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Why a resolved symbol can or cannot be part of a panel run. Never inferred silently: every symbol of the selection has exactly one status.</summary>
public enum RlPanelSymbolStatus { Ready, MissingFile, NotEquity, CurrencyMismatch, TimezoneMismatch, NoValidBlock, ReadError }

/// <summary>
/// One symbol of the selection. <see cref="BarsInPeriod"/> counts stored bars with a date in the period (zero when the files could not be
/// read); <see cref="RequiredBarsPerBlock"/> is the floor for one block; <see cref="Detail"/> is technical text for diagnostics, never UI wording.
/// </summary>
public sealed record RlPanelSymbolPreflight(string Symbol, RlPanelSymbolStatus Status, int BarsInPeriod, int RequiredBarsPerBlock, string Detail,
    RlPanelSymbolRole Role = RlPanelSymbolRole.None);

/// <summary>
/// Read-only feasibility of a panel run before anything is captured. The panel numbers describe the <c>Ready</c> symbols only (the set a run
/// would use): union calendar length, the smallest calendar the catalog rule accepts, and blocks found against the minimum per partition for the
/// training symbol group. <see cref="HoldoutSplitRisk"/> is true when the symbol holdout, which is decided only at capture, could leave a
/// training cell short; the engine still checks the real split and raises InsufficientData (defense in depth).
/// <see cref="FirstValidationDate"/> is the calendar date at which the validation partition of that panel begins (null when no symbol is usable or the
/// calendar is too short); the dialog admits a transfer base run against it before anything is captured.
/// </summary>
public sealed record PanelPreflight(DateOnly Start, DateOnly EndExclusive, RlPanelEvaluationMode Mode,
    ImmutableArray<RlPanelSymbolPreflight> Symbols, int CalendarLength, int RequiredCalendarLength,
    ImmutableArray<RlPanelCellCount> Cells, bool Feasible, bool HoldoutSplitRisk, DateOnly? FirstValidationDate = null)
{
    public int ReadyCount => Symbols.Count(s => s.Status == RlPanelSymbolStatus.Ready);
}

public enum RlPanelStartBlocker { None, NoSymbols, SingleSymbol, NotAllReady, TooFewSymbols, TooManySymbols, Infeasible }

/// <summary>Outcome of the Start rules for one selection and preflight; the view model only renders it.</summary>
public sealed record RlPanelStartDecision(bool CanStart, RlPanelStartBlocker Blocker, bool SymbolHoldout, bool HoldoutUnavailable,
    ImmutableArray<string> TrainingSymbols, ImmutableArray<string> ExcludedSymbols);
