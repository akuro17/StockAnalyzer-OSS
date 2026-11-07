using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Discussion;

namespace StockAnalyzer.McpServer.Tools.Discussion;

/// <summary>
/// The discussion gateway, resolved from the service provider the first time a discussion tool is called. Building the board starts its expiry timer, so a command that
/// only lists the tools (<c>--help</c>) and a server that is never asked to discuss never build it.
/// </summary>
internal sealed class DeferredDiscussionGateway : IDiscussionGateway
{
    private readonly Lazy<IDiscussionGateway> _inner;

    public DeferredDiscussionGateway(IServiceProvider services)
    {
        _inner = new Lazy<IDiscussionGateway>(() => services.GetRequiredService<IDiscussionGateway>(), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<DiscussionReply> ExecuteAsync(DiscussionRequest request, CancellationToken cancellationToken)
        => _inner.Value.ExecuteAsync(request, cancellationToken);
}
