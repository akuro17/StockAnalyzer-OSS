using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>Add/Edit Group dialog of the condition tree editor: the group's name (nested groups only) and its AND/OR operator.</summary>
public partial class ConditionGroupEditDialogViewModel : ViewModelBase
{
    public ConditionGroupEditDialogViewModel(ConditionGroupEditRequest request)
    {
        System.ArgumentNullException.ThrowIfNull(request);
        IsNew = request.IsNew;
        AllowName = request.AllowName;
        MaxNameLength = request.MaxNameLength;
        _name = request.Name ?? string.Empty;
        _isAndOperator = request.Operator == LogicalOperator.And;
    }

    /// <summary>True for "Add Group", false for "Edit Group" (only the title differs).</summary>
    public bool IsNew { get; }

    /// <summary>False for a fixed Long/Short root: only its operator can be edited.</summary>
    public bool AllowName { get; }

    /// <summary>Longest name; the configured <c>Backtest:MaxConditionGroupNameLength</c> (the input box refuses more characters).</summary>
    public int MaxNameLength { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOrOperator))]
    private bool _isAndOperator;

    /// <summary>The OR radio button; the two buttons always show opposite states.</summary>
    public bool IsOrOperator
    {
        get => !IsAndOperator;
        set => IsAndOperator = !value;
    }

    /// <summary>The confirmed content (name normalization is the tree's job; a root never returns a name).</summary>
    public ConditionGroupEditResult BuildResult()
        => new(AllowName ? Name : null, IsAndOperator ? LogicalOperator.And : LogicalOperator.Or);
}
