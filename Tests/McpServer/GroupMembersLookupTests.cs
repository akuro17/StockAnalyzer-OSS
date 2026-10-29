using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>GroupMembersLookup: the shared groupId handling of sa_list_tickers, sa_run_screener and sa_filter_tickers.</summary>
public sealed class GroupMembersLookupTests
{
    private const string Parameter = "groupId";
    private const int Limit = 7;

    private sealed class StubResolver(Func<Guid, int, GroupMembers> resolve) : ITickerGroupResolver
    {
        public Task<GroupMembers> ResolveAsync(Guid groupId, int maxFilterEvaluationSymbols, CancellationToken cancellationToken = default)
            => Task.FromResult(resolve(groupId, maxFilterEvaluationSymbols));
    }

    private static GroupMembers Members(params string[] symbols)
        => new(symbols, 0, Array.Empty<string>(), TickerGroupKind.Watchlist);

    private static async Task<ToolException> RefusedAsync(ITickerGroupResolver resolver, string groupIdText)
        => await Assert.ThrowsAsync<ToolException>(() => GroupMembersLookup.ResolveAsync(resolver, groupIdText, Parameter, Limit, CancellationToken.None));

    [Fact]
    public async Task AFoundGroup_ReturnsItsParsedIdAndMembers_AndPassesTheLimitOn()
    {
        Guid id = Guid.NewGuid();
        int seenLimit = 0;
        var resolver = new StubResolver((groupId, limit) =>
        {
            seenLimit = limit;
            return groupId == id ? Members("AAPL", "MSFT") : throw new GroupNotFoundException(groupId);
        });

        ResolvedGroup resolved = await GroupMembersLookup.ResolveAsync(resolver, id.ToString("D"), Parameter, Limit, CancellationToken.None);

        Assert.Equal(id, resolved.Id);
        Assert.Equal(new[] { "AAPL", "MSFT" }, resolved.Members.Symbols);
        Assert.Equal(Limit, seenLimit);
    }

    [Fact]
    public async Task TextThatIsNotAGuid_IsInvalidArgument_AndTheResolverIsNotAsked()
    {
        bool asked = false;
        var resolver = new StubResolver((_, _) => { asked = true; return Members(); });

        ToolException error = await RefusedAsync(resolver, "not-a-guid");

        Assert.Equal(ToolErrorCodes.InvalidArgument, error.Code);
        Assert.Equal(Parameter, error.Parameter);
        Assert.False(asked);
    }

    [Fact]
    public async Task AGroupThatDoesNotExist_IsGroupNotFound_NamingTheId()
    {
        Guid id = Guid.NewGuid();
        var resolver = new StubResolver((groupId, _) => throw new GroupNotFoundException(groupId));

        ToolException error = await RefusedAsync(resolver, id.ToString("D"));

        Assert.Equal(ToolErrorCodes.GroupNotFound, error.Code);
        Assert.Equal(Parameter, error.Parameter);
        Assert.Contains(id.ToString("D"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilterThatNeedsTooManySymbols_IsLimitExceeded_WithTheSettingNamedInTheHint()
    {
        var resolver = new StubResolver((_, limit) => throw new FilterEvaluationLimitException(limit + 1, limit));

        ToolException error = await RefusedAsync(resolver, Guid.NewGuid().ToString("D"));

        Assert.Equal(ToolErrorCodes.LimitExceeded, error.Code);
        Assert.Equal(Parameter, error.Parameter);
        Assert.Contains("AiIntegration:MaxFilterEvaluationSymbols", error.Hint ?? string.Empty, StringComparison.Ordinal);
    }
}
