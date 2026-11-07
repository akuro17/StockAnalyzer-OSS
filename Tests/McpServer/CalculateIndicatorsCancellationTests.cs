using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_calculate_indicators checks the cancellation token between the calculations of its time frames and before it writes the answer. The real gateway is wrapped with a
/// <see cref="DispatchProxy"/> that counts the calculations and cancels right after the first one has completed. A proxy does not implement the token-aware gateway interface, so these
/// tests exercise the checkpoints of the tool itself; the check between the indicators of one calculation is covered by <see cref="CalculateIndicatorsCancellationBetweenIndicatorsTests"/>.
/// </summary>
public sealed class CalculateIndicatorsCancellationTests : IDisposable
{
    private const string Symbol = "MSFT";

    /// <summary>One indicator on the call's bars (D1) and one on W1 bars: two calculations, the second one after the checkpoint.</summary>
    private const string Arguments = """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"d","type":"SMA","parameters":{"Period":5}},{"id":"w","type":"SMA","parameters":{"Period":3},"timeFrame":"W1"}],"outputTailCount":3}""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(120, Symbol);
    private readonly ServiceProvider _services;
    private readonly CalculateIndicatorsTool _tool;
    private readonly CountingGateway _probe;

    public CalculateIndicatorsCancellationTests()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
        _services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration).BuildServiceProvider();
        var real = _services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
        IReadOnlyStockAnalyzerGateway gateway = CountingGateway.Create(real, out _probe);

        var factory = new IndicatorFactory();
        var catalogProvider = new ScreenerCatalogProvider();
        _tool = new CalculateIndicatorsTool(
            gateway,
            new SymbolResolver(gateway),
            new IndicatorCatalog(factory, catalogProvider),
            new IndicatorSettingsBinder(catalogProvider, factory),
            new IndicatorChainPlan(factory),
            new ErrorMessageSanitizer(Array.Empty<string?>()),
            new McpLimits());
    }

    public void Dispose()
    {
        _services.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public async Task ACallCancelledAfterTheFirstCalculationStartsNoFurtherTimeFrame()
    {
        using var source = new CancellationTokenSource();
        _probe.AfterFirstCalculation = source.Cancel;
        using JsonDocument arguments = JsonDocument.Parse(Arguments);

        await Assert.ThrowsAsync<OperationCanceledException>(() => _tool.InvokeAsync(arguments.RootElement, source.Token));

        Assert.Equal(1, _probe.Calculations);
    }

    [Fact]
    public async Task WithoutACancellationBothTimeFramesAreCalculatedAndTheAnswerIsComplete()
    {
        using JsonDocument arguments = JsonDocument.Parse(Arguments);

        using JsonDocument answer = JsonDocument.Parse(await _tool.InvokeAsync(arguments.RootElement, CancellationToken.None));

        Assert.Equal(2, _probe.Calculations);
        JsonElement results = answer.RootElement.GetProperty("results");
        Assert.True(results.GetProperty("d").GetProperty("isSuccessful").GetBoolean());
        Assert.True(results.GetProperty("w").GetProperty("isSuccessful").GetBoolean());
    }

    /// <summary>The real gateway behind a proxy: every call is forwarded; <c>CalculateIndicatorsAsync</c> is counted, and the first one runs a callback once it has completed.</summary>
    public class CountingGateway : DispatchProxy
    {
        private IReadOnlyStockAnalyzerGateway _target = null!;
        private int _calculations;

        public int Calculations => Volatile.Read(ref _calculations);

        public Action? AfterFirstCalculation { get; set; }

        public static IReadOnlyStockAnalyzerGateway Create(IReadOnlyStockAnalyzerGateway target, out CountingGateway probe)
        {
            var proxy = DispatchProxy.Create<IReadOnlyStockAnalyzerGateway, CountingGateway>();
            probe = (CountingGateway)(object)proxy;
            probe._target = target;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                object? result = targetMethod!.Invoke(_target, args);
                if (targetMethod.Name != nameof(IReadOnlyStockAnalyzerGateway.CalculateIndicatorsAsync))
                {
                    return result;
                }

                int number = Interlocked.Increment(ref _calculations);
                return AfterAsync((Task<Dictionary<string, IIndicatorResult>>)result!, number);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }

        private async Task<Dictionary<string, IIndicatorResult>> AfterAsync(Task<Dictionary<string, IIndicatorResult>> calculation, int number)
        {
            Dictionary<string, IIndicatorResult> results = await calculation.ConfigureAwait(false);
            if (number == 1)
            {
                AfterFirstCalculation?.Invoke();
            }

            return results;
        }
    }
}
