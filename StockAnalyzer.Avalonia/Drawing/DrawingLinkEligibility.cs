using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Value snapshot of one Layers Panel row used to decide link/unlink availability.
/// </summary>
public readonly record struct LinkCandidate(
    Guid Id,
    bool IsTargeted,
    bool IsLinkableType,
    bool EffectiveEditAllowed,
    PanelKey Panel,
    LinkRole Role);

/// <summary>
/// Pure eligibility rules for the Layers Panel "link as parent" / "unlink" buttons.
/// Candidates are passed in the panel's display order; new children keep that order.
/// </summary>
public static class DrawingLinkEligibility
{
    /// <summary>
    /// A drawing type can join a link group unless its Translate is a specified no-op
    /// (<see cref="FreehandObject"/> and <see cref="InformationObject"/> never move), which would make it
    /// "linked but not following".
    /// </summary>
    public static bool IsLinkableType(IChartObject obj) => obj is not FreehandObject && obj is not InformationObject;

    /// <summary>
    /// Ids that pressing "link" on <paramref name="parent"/> would newly add as children (empty when linking is unavailable).
    /// </summary>
    public static IReadOnlyList<Guid> GetNewChildIds(LinkCandidate parent, IReadOnlyList<LinkCandidate> all)
    {
        if (!parent.IsLinkableType || !parent.EffectiveEditAllowed) return Array.Empty<Guid>();
        if (parent.Role == LinkRole.Child) return Array.Empty<Guid>();

        var result = new List<Guid>();
        for (int i = 0; i < all.Count; i++)
        {
            var s = all[i];
            if (!s.IsTargeted || s.Id == parent.Id) continue;
            if (!s.IsLinkableType || !s.EffectiveEditAllowed) continue;
            if (s.Panel != parent.Panel) continue;

            if (s.Role != LinkRole.None)
            {
                // Already in this parent's own group (nothing to add) or in a different group (not eligible).
                continue;
            }
            result.Add(s.Id);
        }
        return result;
    }

    public static bool CanLink(LinkCandidate parent, IReadOnlyList<LinkCandidate> all)
        => GetNewChildIds(parent, all).Count > 0;

    /// <summary>Unlink is available when the pressed row or any targeted row is linked. Lock state is intentionally not consulted.</summary>
    public static bool CanUnlink(LinkCandidate pressed, IReadOnlyList<LinkCandidate> all)
    {
        if (pressed.Role != LinkRole.None) return true;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].IsTargeted && all[i].Role != LinkRole.None) return true;
        }
        return false;
    }

    /// <summary>Ids whose groups are dissolved by unlink: the pressed row plus every targeted row.</summary>
    public static IReadOnlyList<Guid> GetUnlinkOperandIds(LinkCandidate pressed, IReadOnlyList<LinkCandidate> all)
    {
        var result = new List<Guid> { pressed.Id };
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].IsTargeted && all[i].Id != pressed.Id) result.Add(all[i].Id);
        }
        return result;
    }
}
