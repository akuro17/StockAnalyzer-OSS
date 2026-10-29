using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>A saved ticker group that was found, with its parsed id.</summary>
internal readonly record struct ResolvedGroup(Guid Id, GroupMembers Members);

/// <summary>
/// The one definition of how a tool turns a <c>groupId</c> argument into the members of a saved ticker group: the text must be a Guid
/// (INVALID_ARGUMENT), the group must exist (GROUP_NOT_FOUND) and its filter evaluation must stay within the limit (LIMIT_EXCEEDED).
/// </summary>
internal static class GroupMembersLookup
{
    public static async Task<ResolvedGroup> ResolveAsync(
        ITickerGroupResolver resolver, string groupIdText, string parameter, int maxEvaluationSymbols, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(groupIdText, out Guid groupId))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{parameter}' must be a valid Guid.", parameter);
        }

        try
        {
            GroupMembers members = await resolver.ResolveAsync(groupId, maxEvaluationSymbols, cancellationToken).ConfigureAwait(false);
            return new ResolvedGroup(groupId, members);
        }
        catch (GroupNotFoundException)
        {
            throw new ToolException(ToolErrorCodes.GroupNotFound, $"Group '{groupId}' was not found in the workspace.", parameter, "Use sa_list_ticker_groups to see available group ids.");
        }
        catch (FilterEvaluationLimitException ex)
        {
            throw new ToolException(ToolErrorCodes.LimitExceeded, ex.Message, parameter, $"Raise AiIntegration:{nameof(McpLimits.MaxFilterEvaluationSymbols)} or narrow the group.");
        }
    }
}
