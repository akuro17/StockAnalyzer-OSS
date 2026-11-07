using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_calculate_indicators with the real gateway and pipeline (MCP Part 4 review, Run 5b): a call whose token is cancelled starts no further indicator of the same calculation. The
/// indicator factory behind the pipeline is wrapped so that it counts the indicators that are started and cancels the call when the first one is created. (The checks between the time
/// frames and before the answer are covered by <see cref="CalculateIndicatorsCancellationTests"/>.)
/// </summary>
public sealed class CalculateIndicatorsCancellationBetweenIndicatorsTests : IDisposable
{
    private const string Arguments = """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a","type":"SMA","parameters":{"Period":3}},{"id":"b","type":"SMA","parameters":{"Period":5}},{"id":"c","type":"SMA","parameters":{"Period":8}}],"outputTailCount":3}""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(120, "MSFT");
    private readonly ServiceProvider _services;
    private readonly CountingFactory _factory = new();
    private readonly CalculateIndicatorsTool _tool;

    public CalculateIndicatorsCancellationBetweenIndicatorsTests()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
        var collection = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration);
        collection.AddSingleton<IIndicatorFactory>(_factory); // the last registration wins: the pipeline starts its indicators through the counting wrapper
        _services = collection.BuildServiceProvider();
        var gateway = _services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();

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
    public void TheGatewayTheServerUsesChecksTheTokenBetweenIndicators()
    {
        Assert.IsAssignableFrom<ICancellableIndicatorCalculation>(_services.GetRequiredService<IReadOnlyStockAnalyzerGateway>());
    }

    [Fact]
    public async Task ACallCancelledWhileTheFirstIndicatorRunsStartsNeitherTheSecondNorTheThird()
    {
        using var source = new CancellationTokenSource();
        _factory.OnCreate = number => { if (number == 1) source.Cancel(); };
        using JsonDocument arguments = JsonDocument.Parse(Arguments);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _tool.InvokeAsync(arguments.RootElement, source.Token));

        Assert.Equal(1, _factory.Created);
    }

    [Fact]
    public async Task WithoutACancellationAllThreeIndicatorsAreStartedAndAnswered()
    {
        using JsonDocument arguments = JsonDocument.Parse(Arguments);

        using JsonDocument answer = JsonDocument.Parse(await _tool.InvokeAsync(arguments.RootElement, CancellationToken.None));

        Assert.Equal(3, _factory.Created);
        JsonElement results = answer.RootElement.GetProperty("results");
        foreach (string id in new[] { "a", "b", "c" })
        {
            Assert.True(results.GetProperty(id).GetProperty("isSuccessful").GetBoolean());
        }
    }

    private sealed class CountingFactory : IIndicatorFactory
    {
        private readonly IndicatorFactory _real = new();
        private int _created;

        public Action<int>? OnCreate { get; set; }

        public int Created => Volatile.Read(ref _created);

        public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null)
        {
            int number = Interlocked.Increment(ref _created);
            OnCreate?.Invoke(number);
            return _real.Create(type, parameters);
        }

        public bool IsRegistered(IndicatorType type) => _real.IsRegistered(type);

        public IEnumerable<IndicatorType> GetRegisteredTypes() => _real.GetRegisteredTypes();
    }
}
