namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Thrown when a requested ticker group ID is not present in the workspace tree.
/// </summary>
public sealed class GroupNotFoundException : Exception
{
    public Guid GroupId { get; }

    public GroupNotFoundException(Guid groupId)
        : base($"Group '{groupId}' was not found in the workspace.")
    {
        GroupId = groupId;
    }
}
