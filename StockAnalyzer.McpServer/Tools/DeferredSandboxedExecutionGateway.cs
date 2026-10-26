using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Execution;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// The execution gateway, created the first time something needs it. Building the gateway cleans up old sandbox sessions and reserves a session folder, so
/// a command that only lists the tools (<c>--help</c>) never builds it; a running server builds it once, before the first request (<see cref="Prepare"/>).
/// A build that fails is tried again by the next call, because a temporary fault (a full disk, a file held by a scanner) must not need a restart; a wrong
/// setting (<see cref="ExecutionLimitsException"/>) is permanent and is reported again without building anything again. Builds never overlap.
/// </summary>
internal sealed class DeferredSandboxedExecutionGateway : ISandboxedExecutionGateway
{
    private readonly IServiceProvider _services;
    private readonly object _gate = new();
    private ISandboxedExecutionGateway? _built;
    private ExecutionLimitsException? _permanentFailure;

    public DeferredSandboxedExecutionGateway(IServiceProvider services)
    {
        _services = services;
    }

    private ISandboxedExecutionGateway Inner
    {
        get
        {
            lock (_gate)
            {
                if (_built is not null)
                {
                    return _built;
                }

                if (_permanentFailure is not null)
                {
                    throw _permanentFailure;
                }

                try
                {
                    return _built = _services.GetRequiredService<ISandboxedExecutionGateway>();
                }
                catch (ExecutionLimitsException error)
                {
                    _permanentFailure = error;
                    throw;
                }
            }
        }
    }

    /// <summary>Builds the gateway now (and so runs the start-up clean-up).</summary>
    public void Prepare() => _ = Inner;

    public Task<TrainingJobTicket> StartTrainingAsync(EphemeralTrainingRequest request, CancellationToken cancellationToken)
        => Inner.StartTrainingAsync(request, cancellationToken);

    public Task<TrainingJobTicket> StartRlTrainingAsync(EphemeralRlRequest request, CancellationToken cancellationToken)
        => Inner.StartRlTrainingAsync(request, cancellationToken);

    public Task<TrainingJobTicket> StartPortfolioRlTrainingAsync(EphemeralPortfolioRlRequest request, CancellationToken cancellationToken)
        => Inner.StartPortfolioRlTrainingAsync(request, cancellationToken);

    public TrainingJobStatus GetStatus(string jobId) => Inner.GetStatus(jobId);

    public TrainingJobState Cancel(string jobId) => Inner.Cancel(jobId);

    public EphemeralArtifact? FindArtifact(string artifactIdOrLatest) => Inner.FindArtifact(artifactIdOrLatest);

    public ArtifactLease LeaseArtifact(string artifactId) => Inner.LeaseArtifact(artifactId);

    public ArtifactLease LeaseRegisteredModel(string modelId) => Inner.LeaseRegisteredModel(modelId);
}
