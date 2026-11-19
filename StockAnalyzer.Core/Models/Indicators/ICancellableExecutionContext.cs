using System.Threading;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// An <see cref="IExecutionContext"/> that also carries the cancellation token of the call that created it.
/// The context is created per indicator call, so the token never lingers on a reusable indicator instance.
/// An indicator with a long-running loop may observe the token; <see cref="System.OperationCanceledException"/>
/// raised for it must not be converted into a failure result.
/// </summary>
public interface ICancellableExecutionContext : IExecutionContext
{
    /// <summary>
    /// Gets the token of the call that owns this context. <see cref="CancellationToken.None"/> when the call is not cancellable.
    /// </summary>
    CancellationToken CancellationToken { get; }
}
