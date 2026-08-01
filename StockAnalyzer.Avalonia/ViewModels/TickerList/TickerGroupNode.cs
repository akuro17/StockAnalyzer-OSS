using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Avalonia.ViewModels.TickerList;

/// <summary>
/// Base class for all nodes in the hierarchical ticker list.
/// Implements IEquatable based on Guid Id for zero-allocation differential updates.
/// </summary>
public abstract partial class TickerGroupNode : ObservableObject, IEquatable<TickerGroupNode>
{
    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    public Guid Id { get; }

    /// <summary>
    /// Collection of child nodes. Null for leaf nodes.
    /// </summary>
    public ObservableCollection<TickerGroupNode>? Children { get; protected set; }

    protected TickerGroupNode(Guid id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public bool Equals(TickerGroupNode? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id.Equals(other.Id);
    }

    public override bool Equals(object? obj) => Equals(obj as TickerGroupNode);
    
    public override int GetHashCode() => Id.GetHashCode();

    public static bool operator ==(TickerGroupNode? left, TickerGroupNode? right) => Equals(left, right);
    public static bool operator !=(TickerGroupNode? left, TickerGroupNode? right) => !Equals(left, right);
}

/// <summary>
/// Node representing the "All Tickers" source.
/// </summary>
public sealed partial class AllTickersNode : TickerGroupNode
{
    public static readonly Guid StaticId = new("00000000-0000-0000-0000-000000000001");
    public AllTickersNode(string displayName) : base(StaticId, displayName)
    {
        Children = new ObservableCollection<TickerGroupNode>();
    }
}

/// <summary>
/// Parent node representing a category (e.g., "Watchlists").
/// </summary>
public sealed partial class CategoryNode : TickerGroupNode
{
    public bool IsWatchlistCategory => Id == new Guid("00000000-0000-0000-0000-000000000002");
    public CategoryNode(Guid id, string displayName) : base(id, displayName)
    {
        Children = new ObservableCollection<TickerGroupNode>();
    }
}

/// <summary>
/// Node representing an individual watchlist profile.
/// </summary>
public sealed partial class WatchlistNode : TickerGroupNode
{
    public WatchlistProfile Profile { get; private set; }
    public WatchlistNode(WatchlistProfile profile) : base(profile.Id, profile.Name)
    {
        Profile = profile;
        Children = new ObservableCollection<TickerGroupNode>();
    }

    public void UpdateProfile(WatchlistProfile profile)
    {
        Profile = profile;
        DisplayName = profile.Name;
    }
}

/// <summary>
/// Node representing an individual portfolio.
/// </summary>
public sealed partial class PortfolioNode : TickerGroupNode
{
    public WatchlistProfile Profile { get; private set; }
    public PortfolioNode(WatchlistProfile profile) : base(profile.Id, profile.Name)
    {
        Profile = profile;
        Children = new ObservableCollection<TickerGroupNode>();
    }

    public void UpdateProfile(WatchlistProfile profile)
    {
        Profile = profile;
        DisplayName = profile.Name;
    }
}

/// <summary>
/// Node representing a Metadata Tag Filter.
/// </summary>
public sealed partial class FilterNode : TickerGroupNode
{
    public StockAnalyzer.Core.Models.Settings.FilterSettings Settings { get; private set; }
    
    public FilterNode(StockAnalyzer.Core.Models.Settings.FilterSettings settings) : base(settings.Id, settings.Name)
    {
        Settings = settings;
        Children = new ObservableCollection<TickerGroupNode>();
    }

    public void UpdateSettings(StockAnalyzer.Core.Models.Settings.FilterSettings settings)
    {
        Settings = settings;
        DisplayName = settings.Name;
    }
}

/// <summary>
/// Action-only node (e.g., "Create New...").
/// Execution does not change selection in the tree.
/// </summary>
public sealed partial class ActionNode : TickerGroupNode
{
    public ICommand Command { get; }
    public ActionNode(Guid id, string displayName, ICommand command) : base(id, displayName)
    {
        Command = command;
    }
}
