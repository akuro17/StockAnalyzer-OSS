using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>What the Add/Edit Group dialog is opened with. <paramref name="AllowName"/> is false for a fixed Long/Short root (only its AND/OR can be edited).</summary>
public sealed record ConditionGroupEditRequest(bool IsNew, string? Name, LogicalOperator Operator, bool AllowName, int MaxNameLength);

/// <summary>The confirmed content of the Add/Edit Group dialog; the dialog returns null when it is cancelled.</summary>
public sealed record ConditionGroupEditResult(string? Name, LogicalOperator Operator);
