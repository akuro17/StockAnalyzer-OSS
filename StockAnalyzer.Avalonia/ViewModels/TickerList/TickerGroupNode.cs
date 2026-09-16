using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Avalonia.ViewModels.TickerList;

/// <summary>
/// SSoT for the PathIcon geometry (SVG path data) shown per <see cref="TickerGroupNode"/> kind, so
/// the Tickers tab's own TreeDataTemplates (<c>TickerListView.axaml</c>) and any other consumer that
/// needs the same shape (e.g. the Notes tab's unified search suggestion box) never drift apart
/// (sa_constraint_check Phase 2: previously duplicated as literal strings in two files). Plain
/// strings, not <c>Avalonia.Media.Geometry</c>, to keep this ViewModel-layer type Avalonia-agnostic
/// (SA_ARCHITECTURE_RULES.md "Platform Agnosticism" / Primitive Barrier) - XAML consumers bind via
/// <c>{x:Static}</c> and let PathIcon.Data's own string-to-Geometry TypeConverter handle the rest.
/// </summary>
public static class TickerGroupIconGeometries
{
    public const string Watchlist = "M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z";
    public const string Portfolio = "M10,2H14A2,2 0 0,1 16,4V6H20A2,2 0 0,1 22,8V19A2,2 0 0,1 20,21H4C2.89,21 2,20.1 2,19V8C2,6.89 2.89,6 4,6H8V4C8,2.89 8.89,2 10,2M14,6V4H10V6H14Z";
    public const string Filter = "M10,18h4v-2h-4V18z M3,6v2h18V6H3z M6,13h12v-2H6V13z";
}

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
