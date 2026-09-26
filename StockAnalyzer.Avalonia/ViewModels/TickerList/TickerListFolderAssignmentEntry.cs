using System;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.ViewModels.TickerList;

/// <summary>
/// Leaf entry for the ticker grid's "Add To Custom Category" nested submenu
/// (<see cref="TickerListViewModel.AssignableCategoryGroups"/>). Wraps a
/// <see cref="StockAnalyzer.Core.Models.Settings.TickerListFolderSettings"/>'s <c>Id</c> together
/// with its own display label (just the folder name - the parent category name is shown one level
/// up, by <see cref="TickerAssignableCategoryGroup"/>), kept separate from the live
/// <see cref="TickerListFolderNode"/> so the menu's binding source isn't coupled to mutable
/// tree-node identity that gets rebuilt on every sync.
/// </summary>
public sealed class TickerListFolderAssignmentEntry
{
    public Guid FolderId { get; }

    public string Label { get; }

    public TickerListFolderAssignmentEntry(Guid folderId, string label)
    {
        FolderId = folderId;
        Label = label;
    }
}

/// <summary>
/// First-level entry for the ticker grid's "Add To Custom Category" nested submenu: one per
/// non-empty custom parent category, opening into its own <see cref="Folders"/> as a nested
/// flyout. A parent category is never itself an assignment target - only entering its submenu and
/// picking a <see cref="TickerListFolderAssignmentEntry"/> is.
/// </summary>
public sealed class TickerAssignableCategoryGroup
{
    public string CategoryName { get; }

    public IReadOnlyList<TickerListFolderAssignmentEntry> Folders { get; }

    public TickerAssignableCategoryGroup(string categoryName, IReadOnlyList<TickerListFolderAssignmentEntry> folders)
    {
        CategoryName = categoryName;
        Folders = folders;
    }
}
