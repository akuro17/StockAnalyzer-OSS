using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StockAnalyzer.Core.Models.UI;

/// <summary>
/// Represents a view item within a workspace panel tab.
/// </summary>
public sealed class WorkspaceViewItem : INotifyPropertyChanged
{
    private string _title = string.Empty;

    public required string Id { get; init; }

    /// <summary>
    /// The ID of the container (e.g. DetachedWindow) currently hosting this item.
    /// </summary>
    public string? ContainerId { get; set; }

    /// <summary>
    /// Display title for the tab.
    /// </summary>
    public string Title 
    { 
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// The ViewModel instance associated with this view.
    /// </summary>
    public required object ViewModel { get; init; }

    /// <summary>
    /// The name of the panel this item was last attached to (e.g. "Left", "Bottom").
    /// </summary>
    public string? OriginalPanelName { get; set; }

    /// <summary>
    /// Whether the item is currently in a detached state.
    /// </summary>
    public bool IsDetached { get; set; }

    /// <summary>
    /// Whether the item is an independent window (Tab Window) that should not be restored to main tabs.
    /// </summary>
    public bool IsIndependent { get; set; }

    /// <summary>
    /// Whether the item can be closed by the user.
    /// </summary>
    public bool CanClose { get; set; } = true;

    /// <summary>
    /// Geometry properties for restoration. NAN indicates no saved state.
    /// </summary>
    public double DetachedX { get; set; } = double.NaN;
    public double DetachedY { get; set; } = double.NaN;
    public double DetachedWidth { get; set; } = double.NaN;
    public double DetachedHeight { get; set; } = double.NaN;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
