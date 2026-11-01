using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class McpOutputSchemaTests
{
    private sealed class OutputSchemaFakeGateway : EmptyReadOnlyStockAnalyzerGateway
    {
        public override Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "MSFT", "7203-T" });

        public override Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default)
            => Task.FromResult<TickerMetadata?>(new TickerMetadata
            {
                ShortName = $"{symbol} Corp",
                LongName = $"{symbol} Corporation",
                Region = "US",
                Sector = "Technology",
                Industry = "Software",
                Currency = "USD",
            });

        public override Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
        {
            var bars = new List<CandleData>
            {
                new() { Timestamp = new DateTime(2024, 1, 1), Open = 100m, High = 105m, Low = 99m, Close = 104m, Volume = 1000 },
                new() { Timestamp = new DateTime(2024, 1, 2), Open = 104m, High = 108m, Low = 103m, Close = 107m, Volume = 1500 },
            };
            return Task.FromResult<IReadOnlyList<CandleData>>(bars.Take(Math.Min(count, bars.Count)).ToList());
        }
    }

    [Fact]
    public async Task ProofA_ToolsList_OnlyThreeToolsDeclareOutputSchema_OthersAreUnchanged()
    {
        // Proof obligation (a): Only the 3 rollout tools declare outputSchema.
        // All other tools omit outputSchema completely, preserving byte-exact shape (name, description, inputSchema, annotations).
        var gateway = new OutputSchemaFakeGateway();
        var limits = new McpLimits();
        var catalogProvider = new ScreenerCatalogProvider();
        var factory = IndicatorFactory.Default;
        var catalog = new IndicatorCatalog(factory, catalogProvider);
        var screenerCatalog = new ScreenerCatalog(catalogProvider, factory);
        var symbols = new SymbolResolver(gateway);

        var listTickers = new ListTickersTool(gateway, limits);
        var getCandles = new GetCandlesTool(gateway, symbols, limits);
        var getCapabilities = new GetCapabilitiesTool(catalog, screenerCatalog);
        var dummyTool = new FakeTool("sa_dummy_tool", (args, ct) => Task.FromResult("{}"));

        var dispatcher = McpTestSupport.CreateDispatcher(new IMcpTool[] { listTickers, getCandles, getCapabilities, dummyTool });

        // Initialize dispatcher
        await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test"}}}""", CancellationToken.None);

        string? listResponse = await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None);
        Assert.NotNull(listResponse);

        using var doc = JsonDocument.Parse(listResponse);
        JsonElement toolsArray = doc.RootElement.GetProperty("result").GetProperty("tools");

        var toolsWithOutputSchema = new List<string>();
        var toolsWithoutOutputSchema = new List<string>();

        foreach (JsonElement tool in toolsArray.EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            if (tool.TryGetProperty("outputSchema", out _))
            {
                toolsWithOutputSchema.Add(name);
            }
            else
            {
                toolsWithoutOutputSchema.Add(name);

                // Verify exact keys present in tools without outputSchema: name, description, inputSchema, annotations
                var propertyNames = tool.EnumerateObject().Select(p => p.Name).ToList();
                Assert.Equal(new[] { "name", "description", "inputSchema", "annotations" }, propertyNames);
            }
        }

        Assert.Equal(new[] { "sa_get_candles", "sa_get_capabilities", "sa_list_tickers" }, toolsWithOutputSchema.OrderBy(n => n).ToArray());
        Assert.Contains("sa_dummy_tool", toolsWithoutOutputSchema);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProofD_ListTickersOutput_ConformsToDeclaredSchema(bool includeMetadataSummary)
    {
        // Proof obligation (d): sa_list_tickers real output conforms to outputSchema
        var gateway = new OutputSchemaFakeGateway();
        var limits = new McpLimits();
        var tool = new ListTickersTool(gateway, limits);

        string args = JsonSerializer.Serialize(new
        {
            limit = 5,
            includeMetadataSummary = includeMetadataSummary,
        });

        string output = await tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None);

        Assert.NotNull(tool.OutputSchemaJson);
        using var schemaDoc = JsonDocument.Parse(tool.OutputSchemaJson);
        using var dataDoc = JsonDocument.Parse(output);

        AssertConformsToSchema(schemaDoc.RootElement, dataDoc.RootElement, "$");
    }

    [Theory]
    [InlineData("D1", false)]
    [InlineData("W1", true)]
    public async Task ProofD_GetCandlesOutput_ConformsToDeclaredSchema(string timeFrame, bool completeBarsOnly)
    {
        // Proof obligation (d): sa_get_candles real output conforms to outputSchema
        var gateway = new OutputSchemaFakeGateway();
        var limits = new McpLimits();
        var symbols = new SymbolResolver(gateway);
        var tool = new GetCandlesTool(gateway, symbols, limits);

        string args = JsonSerializer.Serialize(new
        {
            symbol = "MSFT",
            timeFrame = timeFrame,
            count = 10,
            completeBarsOnly = completeBarsOnly,
        });

        string output = await tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None);

        Assert.NotNull(tool.OutputSchemaJson);
        using var schemaDoc = JsonDocument.Parse(tool.OutputSchemaJson);
        using var dataDoc = JsonDocument.Parse(output);

        AssertConformsToSchema(schemaDoc.RootElement, dataDoc.RootElement, "$");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ProofD_GetCapabilitiesOutput_ConformsToDeclaredSchema(bool includeDetails, bool includeScreenerCatalog)
    {
        // Proof obligation (d): sa_get_capabilities real output conforms to outputSchema
        var catalogProvider = new ScreenerCatalogProvider();
        var factory = IndicatorFactory.Default;
        var catalog = new IndicatorCatalog(factory, catalogProvider);
        var screenerCatalog = new ScreenerCatalog(catalogProvider, factory);
        var tool = new GetCapabilitiesTool(catalog, screenerCatalog);

        string args = JsonSerializer.Serialize(new
        {
            includeDetails = includeDetails,
            includeScreenerCatalog = includeScreenerCatalog,
        });

        string output = await tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None);

        Assert.NotNull(tool.OutputSchemaJson);
        using var schemaDoc = JsonDocument.Parse(tool.OutputSchemaJson);
        using var dataDoc = JsonDocument.Parse(output);

        AssertConformsToSchema(schemaDoc.RootElement, dataDoc.RootElement, "$");
    }

    /// <summary>
    /// Recursive JSON Schema validator ensuring data conforms to declared schema (types, required, additionalProperties, items).
    /// Built without external dependencies.
    /// </summary>
    private static void AssertConformsToSchema(JsonElement schema, JsonElement data, string path)
    {
        if (schema.TryGetProperty("type", out JsonElement typeElem))
        {
            if (typeElem.ValueKind == JsonValueKind.String)
            {
                AssertType(typeElem.GetString()!, data, path);
            }
            else if (typeElem.ValueKind == JsonValueKind.Array)
            {
                var allowedTypes = typeElem.EnumerateArray().Select(e => e.GetString()!).ToList();
                bool matchesAny = allowedTypes.Any(t => IsTypeMatch(t, data));
                Assert.True(matchesAny, $"At {path}: expected one of [{string.Join(", ", allowedTypes)}], but got {data.ValueKind}.");
            }
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement reqElem))
            {
                foreach (JsonElement req in reqElem.EnumerateArray())
                {
                    string propName = req.GetString()!;
                    Assert.True(data.TryGetProperty(propName, out _), $"At {path}: missing required property '{propName}'.");
                }
            }

            bool additionalAllowed = true;
            if (schema.TryGetProperty("additionalProperties", out JsonElement addlElem) && addlElem.ValueKind == JsonValueKind.False)
            {
                additionalAllowed = false;
            }

            schema.TryGetProperty("properties", out JsonElement propsElem);

            foreach (JsonProperty prop in data.EnumerateObject())
            {
                if (propsElem.ValueKind == JsonValueKind.Object && propsElem.TryGetProperty(prop.Name, out JsonElement propSchema))
                {
                    AssertConformsToSchema(propSchema, prop.Value, $"{path}.{prop.Name}");
                }
                else
                {
                    Assert.True(additionalAllowed, $"At {path}: unexpected property '{prop.Name}' when additionalProperties is false.");
                }
            }
        }
        else if (data.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("items", out JsonElement itemsSchema))
            {
                int index = 0;
                foreach (JsonElement item in data.EnumerateArray())
                {
                    AssertConformsToSchema(itemsSchema, item, $"{path}[{index}]");
                    index++;
                }
            }
        }
    }

    private static bool IsTypeMatch(string typeName, JsonElement data) => typeName switch
    {
        "object" => data.ValueKind == JsonValueKind.Object,
        "array" => data.ValueKind == JsonValueKind.Array,
        "string" => data.ValueKind == JsonValueKind.String,
        "number" => data.ValueKind == JsonValueKind.Number,
        "integer" => data.ValueKind == JsonValueKind.Number && data.TryGetInt64(out _),
        "boolean" => data.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => data.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static void AssertType(string expectedType, JsonElement data, string path)
    {
        Assert.True(IsTypeMatch(expectedType, data), $"At {path}: expected {expectedType}, got {data.ValueKind}.");
    }
}
