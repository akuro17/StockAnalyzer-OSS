using System.Reflection;
using StockAnalyzer.Core.ReadOnly;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The real gateway behind a proxy whose backtest batch (either overload) fails with an OverflowException the way a failing batch item does: tagged with the item
/// index (<see cref="BacktestBatchFailure.ItemIndexKey"/>) or, with a null index, untagged. Every other call is forwarded. It stands for an overflow that the
/// engine's diagnostic entry point does not convert (a strategy's own), which a real run reaches only at extreme prices.
/// </summary>
public class OverflowThrowingGateway : DispatchProxy
{
    private static readonly MethodInfo FromException = typeof(Task).GetMethods()
        .Single(method => method.Name == nameof(Task.FromException) && method.IsGenericMethodDefinition);

    private IReadOnlyStockAnalyzerGateway _target = null!;
    private int? _index;

    public static IReadOnlyStockAnalyzerGateway Create(IReadOnlyStockAnalyzerGateway target, int? index)
    {
        var proxy = DispatchProxy.Create<IReadOnlyStockAnalyzerGateway, OverflowThrowingGateway>();
        var self = (OverflowThrowingGateway)(object)proxy;
        self._target = target;
        self._index = index;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IReadOnlyStockAnalyzerGateway.RunBacktestBatchAsync))
        {
            var error = new OverflowException("Arithmetic operation resulted in an overflow.");
            if (_index is { } index)
            {
                error.Data[BacktestBatchFailure.ItemIndexKey] = index;
            }

            // The overloads return Task<IReadOnlyList<T>> for different T: a faulted task of the very type asked for.
            Type resultType = targetMethod.ReturnType.GetGenericArguments()[0];
            return FromException.MakeGenericMethod(resultType).Invoke(null, new object[] { error });
        }

        try
        {
            return targetMethod.Invoke(_target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }
}
