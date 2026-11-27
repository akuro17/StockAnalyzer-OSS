using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.McpServer.Tools.Discussion;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class EvaluateExpressionsToolTests
{
    private static Task<McpMessageDispatcher> StartAsync(McpLimits? limits = null)
        => McpTestSupport.InitializedAsync(new IMcpTool[] { new EvaluateExpressionsTool(limits ?? new McpLimits()) }, limits);

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string argumentsJson, int id = 1)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_evaluate_expressions", argumentsJson, id))!;

    // ---- Group B1: Schema, argument validation, limits, normalization, metadata ----

    [Fact]
    public async Task ToolsListContainsSaEvaluateExpressionsWithAllFunctionSignatures()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string listJson = (await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""", CancellationToken.None))!;
        using JsonDocument doc = JsonDocument.Parse(listJson);

        JsonElement tool = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "sa_evaluate_expressions");

        string description = tool.GetProperty("description").GetString()!;
        Assert.Contains("stockanalyzer://docs/tools/sa_evaluate_expressions", description, StringComparison.Ordinal);

        string docPath = System.IO.Path.Combine(StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "docs", "MCP", "tools", "sa_evaluate_expressions.md");
        string toolDoc = System.IO.File.ReadAllText(docPath);
        foreach (CalculatorFunctionInfo fn in ExpressionCalculator.Functions)
        {
            Assert.Contains(fn.Signature, toolDoc, StringComparison.Ordinal);
        }

        Assert.False(tool.TryGetProperty("outputSchema", out _));
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    [Theory]
    [InlineData("{}", "expressions")]
    [InlineData("""{"expressions":[]}""", "expressions")]
    [InlineData("""{"expressions":[{"name":"a","expression":"1"}],"extra":1}""", "extra")]
    [InlineData("""{"expression":"1+1","expressions":[{"name":"a","expression":"1"}]}""", "expression")]
    [InlineData("""{"formula":"1+1","expressions":[{"name":"a","expression":"1"}]}""", "formula")]
    [InlineData("""{"variables":[1],"expressions":[{"name":"a","expression":"1"}]}""", "variables")]
    [InlineData("""{"expressions":[{"name":"a"}]}""", "expressions[0].expression")]
    [InlineData("""{"expressions":[{"expression":"1"}]}""", "expressions[0].name")]
    [InlineData("""{"expressions":[{"name":"a","expression":"1","extra":1}]}""", "expressions[0].extra")]
    public async Task SchemaAndMisnamedArgumentRefusalsReportExpectedParam(string argumentsJson, string expectedParam)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument response = await CallAsync(dispatcher, argumentsJson);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(expectedParam, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData(-1, ToolErrorCodes.InvalidArgument)]
    [InlineData(11, ToolErrorCodes.LimitExceeded)]
    public async Task DecimalPlacesOutOfRangeIsRefused(int decimalPlaces, string expectedCode)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument response = await CallAsync(dispatcher, $$"""{"decimalPlaces":{{decimalPlaces}},"expressions":[{"name":"a","expression":"1"}]}""");

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(expectedCode, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("decimalPlaces", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task CountsExceedingLimitsReturnLimitExceededNamingTheParameter()
    {
        var limits = new McpLimits { MaxCalculatorExpressions = 20, MaxCalculatorVariables = 50 };
        McpMessageDispatcher dispatcher = await StartAsync(limits);

        string tooManyExprs = string.Join(",", Enumerable.Range(0, 21).Select(i => $$"""{"name":"e{{i}}","expression":"1"}"""));
        using JsonDocument exprResp = await CallAsync(dispatcher, $$"""{"expressions":[{{tooManyExprs}}]}""");
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(exprResp));
        Assert.Equal("expressions", McpTestSupport.Structured(exprResp).GetProperty("param").GetString());

        string tooManyVars = string.Join(",", Enumerable.Range(0, 51).Select(i => $$""" "v{{i}}":1 """));
        using JsonDocument varResp = await CallAsync(dispatcher, $$"""{"variables":{ {{tooManyVars}} },"expressions":[{"name":"e0","expression":"1"}]}""");
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(varResp));
        Assert.Equal("variables", McpTestSupport.Structured(varResp).GetProperty("param").GetString());
    }

    [Fact]
    public async Task IdentifierLengthAndSyntaxBoundariesAreEnforced()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        string ok64 = new string('a', 64);
        using JsonDocument okResp = await CallAsync(dispatcher, $$"""{"variables":{"{{ok64}}":5},"expressions":[{"name":"_b0","expression":"{{ok64}} + 1"}]}""");
        Assert.False(McpTestSupport.IsError(okResp));
        Assert.Equal(6m, McpTestSupport.Structured(okResp).GetProperty("results")[0].GetProperty("value").GetDecimal());

        string bad65 = new string('a', 65);
        using JsonDocument bad65Var = await CallAsync(dispatcher, $$"""{"variables":{"{{bad65}}":5},"expressions":[{"name":"r","expression":"1"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(bad65Var));
        Assert.Equal($"variables.{bad65}", McpTestSupport.Structured(bad65Var).GetProperty("param").GetString());

        using JsonDocument emptyVar = await CallAsync(dispatcher, """{"variables":{"":1},"expressions":[{"name":"r","expression":"1"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(emptyVar));
        Assert.Equal("variables", McpTestSupport.Structured(emptyVar).GetProperty("param").GetString());

        using JsonDocument digitStart = await CallAsync(dispatcher, """{"expressions":[{"name":"1abc","expression":"1"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(digitStart));
        Assert.Equal("expressions[0].name", McpTestSupport.Structured(digitStart).GetProperty("param").GetString());

        using JsonDocument nonAscii = await CallAsync(dispatcher, """{"expressions":[{"name":"\u4FA1\u683C","expression":"1"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(nonAscii));
        Assert.Equal("expressions[0].name", McpTestSupport.Structured(nonAscii).GetProperty("param").GetString());
    }

    [Fact]
    public async Task ReservedFunctionNamesAreRejectedForVariablesAndExpressions()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        foreach (CalculatorFunctionInfo fn in ExpressionCalculator.Functions)
        {
            using JsonDocument varResp = await CallAsync(dispatcher, $$"""{"variables":{"{{fn.Name}}":1},"expressions":[{"name":"r","expression":"1"}]}""");
            Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(varResp));
            Assert.Equal($"variables.{fn.Name}", McpTestSupport.Structured(varResp).GetProperty("param").GetString());
            Assert.Equal("Function names are reserved.", McpTestSupport.Structured(varResp).GetProperty("hint").GetString());

            using JsonDocument exprResp = await CallAsync(dispatcher, $$"""{"expressions":[{"name":"{{fn.Name}}","expression":"1"}]}""");
            Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(exprResp));
            Assert.Equal("expressions[0].name", McpTestSupport.Structured(exprResp).GetProperty("param").GetString());
            Assert.Equal("Function names are reserved.", McpTestSupport.Structured(exprResp).GetProperty("hint").GetString());
        }
    }

    [Fact]
    public async Task DuplicateVariableAndExpressionNamesAreRejected()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument dupVars = await CallAsync(dispatcher, """{"variables":{"x":1,"x":2},"expressions":[{"name":"r","expression":"1"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(dupVars));
        Assert.Equal("variables.x", McpTestSupport.Structured(dupVars).GetProperty("param").GetString());

        using JsonDocument varExprCollision = await CallAsync(dispatcher, """{"variables":{"x":1},"expressions":[{"name":"x","expression":"2"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(varExprCollision));
        Assert.Equal("expressions[0].name", McpTestSupport.Structured(varExprCollision).GetProperty("param").GetString());

        using JsonDocument dupExprs = await CallAsync(dispatcher, """{"expressions":[{"name":"a","expression":"1"},{"name":"a","expression":"2"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(dupExprs));
        Assert.Equal("expressions[1].name", McpTestSupport.Structured(dupExprs).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData(""" "1" """)]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("1e400")]
    public async Task NonDecimalVariableValuesAreRejectedAsInvalidArgument(string rawValue)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, $$"""{"variables":{"x":{{rawValue}}},"expressions":[{"name":"r","expression":"1"}]}""");

        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(resp));
        Assert.Equal("variables.x", McpTestSupport.Structured(resp).GetProperty("param").GetString());
    }

    [Fact]
    public async Task JsonScientificNotationVariableWithinDecimalRangeIsAccepted()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """{"variables":{"x":1e2},"expressions":[{"name":"r","expression":"x + 5"}]}""");

        Assert.False(McpTestSupport.IsError(resp));
        Assert.Equal(105m, McpTestSupport.Structured(resp).GetProperty("results")[0].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task EmptyAndOverlongExpressionsAreRejectedBeforeEvaluation()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument emptyExpr = await CallAsync(dispatcher, """{"expressions":[{"name":"a","expression":""}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(emptyExpr));
        Assert.Equal("expressions[0].expression", McpTestSupport.Structured(emptyExpr).GetProperty("param").GetString());

        string tooLong = new string('1', CalculatorLimits.Default.MaxExpressionChars + 1);
        using JsonDocument longExpr = await CallAsync(dispatcher, $$"""{"expressions":[{"name":"a","expression":"{{tooLong}}"}]}""");
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(longExpr));
        Assert.Equal("expressions[0].expression", McpTestSupport.Structured(longExpr).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData("2.50", "2.5")]
    [InlineData("100", "100")]
    [InlineData("0.0", "0")]
    [InlineData("-0.00", "0")]
    [InlineData("1.005", "1.005")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    public void NormalizeDecimalStripsTrailingZerosAndNormalizesZero(string inputText, string expectedText)
    {
        decimal input = decimal.Parse(inputText, CultureInfo.InvariantCulture);
        decimal normalized = EvaluateExpressionsTool.NormalizeDecimal(input);

        Assert.Equal(expectedText, normalized.ToString(CultureInfo.InvariantCulture));
        if (normalized == 0m)
        {
            Assert.Equal(0, decimal.GetBits(normalized)[3]);
        }
    }

    [Fact]
    public void NormalizeDecimalPreservesDecimalExtremaAndNegativeZero()
    {
        Assert.Equal(decimal.MaxValue, EvaluateExpressionsTool.NormalizeDecimal(decimal.MaxValue));
        Assert.Equal(decimal.MinValue, EvaluateExpressionsTool.NormalizeDecimal(decimal.MinValue));

        decimal negZero = -0m;
        decimal normalizedNegZero = EvaluateExpressionsTool.NormalizeDecimal(negZero);
        Assert.Equal(0m, normalizedNegZero);
        Assert.Equal(0, decimal.GetBits(normalizedNegZero)[3]);
    }

    // ---- Group B2: Contract [B] vectors, chaining, dependencies, determinism, discussion, concurrency ----

    [Theory]
    [InlineData("1+2*3", null, "7")]
    [InlineData("-2^2", null, "-4")]
    [InlineData("7/2", null, "3.5")]
    [InlineData("max(3,1,2)", null, "3")]
    [InlineData("round(2.5,0)", null, "3")]
    [InlineData("change_ratio(100,110)", null, "0.1")]
    [InlineData("close - 2 * atr", """{"close":2950,"atr":48.5}""", "2853")]
    public async Task ContractVectorsMarkedB_SucceedThroughTool(string expression, string? variablesJson, string expectedText)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string varsArg = variablesJson is null ? string.Empty : $"\"variables\":{variablesJson},";
        using JsonDocument resp = await CallAsync(dispatcher, $"{{{varsArg}\"expressions\":[{{\"name\":\"v\",\"expression\":{JsonSerializer.Serialize(expression)}}}]}}");

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement item = McpTestSupport.Structured(resp).GetProperty("results")[0];
        Assert.Equal("Ok", item.GetProperty("status").GetString());
        Assert.Equal(decimal.Parse(expectedText, CultureInfo.InvariantCulture), item.GetProperty("value").GetDecimal());
    }

    [Theory]
    [InlineData("2^0.5", "NON_INTEGER_EXPONENT", 1)]
    [InlineData("1/0", "DIVISION_BY_ZERO", 1)]
    [InlineData("min(1)", "WRONG_ARGUMENT_COUNT", 0)]
    [InlineData("x + 1", "UNDEFINED_NAME", 0)]
    [InlineData("   ", "SYNTAX_ERROR", 3)]
    [InlineData("0.1 ^ -29", "ARITHMETIC_OVERFLOW", 4)]
    public async Task ContractErrorVectorsMarkedB_ReturnPerExpressionErrorsWithoutFailingCall(string expression, string expectedCode, int expectedPosition)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, $$"""{"expressions":[{"name":"v","expression":{{JsonSerializer.Serialize(expression)}}}]}""");

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement structured = McpTestSupport.Structured(resp);
        Assert.Equal(0, structured.GetProperty("okCount").GetInt32());
        Assert.Equal(1, structured.GetProperty("errorCount").GetInt32());

        JsonElement item = structured.GetProperty("results")[0];
        Assert.Equal("Error", item.GetProperty("status").GetString());
        Assert.Equal(expectedCode, item.GetProperty("errorCode").GetString());
        Assert.Equal(expectedPosition, item.GetProperty("errorPosition").GetInt32());
    }

    [Fact]
    public async Task SequentialChainingUsesUnroundedIntermediateValuesAndSupportsDecimalPlaces()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 2,
          "variables": { "x": 1.2345 },
          "expressions": [
            { "name": "a", "expression": "1/3" },
            { "name": "b", "expression": "a * 3" },
            { "name": "c", "expression": "2/3" },
            { "name": "d", "expression": "x" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(2, root.GetProperty("decimalPlaces").GetInt32());
        Assert.Equal(1.2345m, root.GetProperty("variables")[0].GetProperty("value").GetDecimal());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(0.33m, results[0].GetProperty("value").GetDecimal());
        Assert.Equal(1m, results[1].GetProperty("value").GetDecimal()); // proves unrounded 1/3*3 = 0.9999... -> 1.00 -> 1
        Assert.Equal(0.67m, results[2].GetProperty("value").GetDecimal());
        Assert.Equal(1.23m, results[3].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task HigherOrderBodyReferencingFailedOrLaterExpressionIsRefinedLikeAnyOtherReference()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "expressions": [
            { "name": "bad", "expression": "1 / 0" },
            { "name": "uses_bad", "expression": "quad_trapz(bad * x, x, 0, 1, 8)" },
            { "name": "uses_later", "expression": "quad_trapz(later * x, x, 0, 1, 8)" },
            { "name": "later", "expression": "2" },
            { "name": "typo", "expression": "mc_integrate(zzz * x, x, 0, 1, 64, 1)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement results = McpTestSupport.Structured(resp).GetProperty("results");

        Assert.Equal("DEPENDENCY_FAILED", results[1].GetProperty("errorCode").GetString());
        Assert.Equal("UNDEFINED_NAME", results[2].GetProperty("errorCode").GetString());
        Assert.Contains("declared after", results[2].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal("UNDEFINED_NAME", results[4].GetProperty("errorCode").GetString());
        Assert.Equal("mc_integrate(".Length, results[4].GetProperty("errorPosition").GetInt32());
        Assert.Contains("zzz", results[4].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DependencyFailedLaterReferenceAndSelfReferenceAreReportedDistinguishably()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "expressions": [
            { "name": "later", "expression": "ok + 1" },
            { "name": "ok", "expression": "1 + 1" },
            { "name": "bad", "expression": "1 / 0" },
            { "name": "dep1", "expression": "bad + 1" },
            { "name": "dep2", "expression": "dep1 + 1" },
            { "name": "self", "expression": "self + 1" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(1, root.GetProperty("okCount").GetInt32());
        Assert.Equal(5, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal("UNDEFINED_NAME", results[0].GetProperty("errorCode").GetString());
        Assert.Contains("declared after", results[0].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);

        Assert.Equal("Ok", results[1].GetProperty("status").GetString());
        Assert.Equal("DIVISION_BY_ZERO", results[2].GetProperty("errorCode").GetString());

        Assert.Equal("DEPENDENCY_FAILED", results[3].GetProperty("errorCode").GetString());
        Assert.Contains("failed", results[3].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);

        Assert.Equal("DEPENDENCY_FAILED", results[4].GetProperty("errorCode").GetString());
        Assert.Equal("UNDEFINED_NAME", results[5].GetProperty("errorCode").GetString());
        Assert.DoesNotContain("declared after", results[5].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdenticalRequestsProduceByteIdenticalDeterministicJson()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        const string args = """{"variables":{"a":10.50,"b":2.0},"expressions":[{"name":"s","expression":"a + b"},{"name":"bad","expression":"1/0"}]}""";

        string first = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(1, "sa_evaluate_expressions", args), CancellationToken.None))!;
        string second = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(1, "sa_evaluate_expressions", args), CancellationToken.None))!;

        Assert.Equal(first, second);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(first)), SHA256.HashData(Encoding.UTF8.GetBytes(second)));
    }

    [Fact]
    public async Task DiscussionAttachAndGetAnalysisRoundTripExactResult()
    {
        var settings = new DiscussionSettings();
        using var hub = new DiscussionHub(settings, startSweeper: false);
        using var log = new CommandLog(settings);
        var gateway = new DiscussionHubGateway(hub, settings, log, McpDisclaimerConstants.ResearchNotAdvice);
        var identity = new ClientIdentity();
        McpToolRegistry? registry = null;
        var tools = new IMcpTool[]
        {
            new EvaluateExpressionsTool(new McpLimits()),
            new DiscussionCreateTool(gateway, identity),
            new DiscussionJoinTool(gateway, identity),
            new DiscussionAttachAnalysisTool(gateway, identity, () => registry!),
            new DiscussionGetAnalysisTool(gateway, identity),
        };
        registry = new McpToolRegistry(tools);
        var sink = new LogSink(new StringWriter());
        var dispatcher = new McpMessageDispatcher(sink, registry, new ToolExecutor(new McpLimits(), new ErrorMessageSanitizer(Array.Empty<string?>()), sink), identity: identity);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using JsonDocument created = (await McpTestSupport.CallAsync(dispatcher, "sa_discussion_create", """{"title":"calc"}""", 1))!;
        string sessionId = McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
        using JsonDocument joined = (await McpTestSupport.CallAsync(dispatcher, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Analyst"}""", 2))!;
        string token = McpTestSupport.Structured(joined).GetProperty("participantToken").GetString()!;

        using JsonDocument attached = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_discussion_attach_analysis",
            $$$"""{"sessionId":"{{{sessionId}}}","participantToken":"{{{token}}}","tool":"sa_evaluate_expressions","arguments":{"variables":{"close":2950,"atr":48.5},"expressions":[{"name":"stop","expression":"close - 2 * atr"}]}}""",
            3))!;
        Assert.False(McpTestSupport.IsError(attached));
        int contextId = McpTestSupport.Structured(attached).GetProperty("contextId").GetInt32();

        using JsonDocument fetched = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_discussion_get_analysis",
            $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","contextId":{{contextId}}}""",
            4))!;
        Assert.False(McpTestSupport.IsError(fetched));
        JsonElement attachedResult = McpTestSupport.Structured(fetched).GetProperty("result");
        Assert.Equal(2853m, attachedResult.GetProperty("results")[0].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task ConcurrentCallsThroughExecutorProduceExactResults()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        Task<JsonDocument?> t1 = McpTestSupport.CallAsync(dispatcher, "sa_evaluate_expressions", """{"expressions":[{"name":"x","expression":"1+2*3"}]}""", 10);
        Task<JsonDocument?> t2 = McpTestSupport.CallAsync(dispatcher, "sa_evaluate_expressions", """{"expressions":[{"name":"y","expression":"change_ratio(100, 125)"}]}""", 11);

        using JsonDocument r1 = (await t1)!;
        using JsonDocument r2 = (await t2)!;

        Assert.Equal(7m, McpTestSupport.Structured(r1).GetProperty("results")[0].GetProperty("value").GetDecimal());
        Assert.Equal(0.25m, McpTestSupport.Structured(r2).GetProperty("results")[0].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task V7DataStructureOperationsEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "arrayVariables": { "c1": [1, 2, 3], "c2": [4, 5, 6], "px": [100, 101, 102] },
          "expressions": [
            { "name": "design", "expression": "mat_hstack(c1, c2)" },
            { "name": "col2", "expression": "mat_col(design, 3, 2, 1)" },
            { "name": "tr", "expression": "mat_transpose_rect(design, 3, 2)" },
            { "name": "lag", "expression": "arr_shift(px, 1, arr_get(px, 0))" },
            { "name": "scan", "expression": "arr_scan(acc + x, acc, x, c1, 0)" },
            { "name": "zip", "expression": "arr_zip_map(a * b, a, b, c1, c2)" },
            { "name": "bad", "expression": "arr_shift([], 1000, 0)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(10, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal(6, root.GetProperty("okCount").GetInt32());
        Assert.Equal(1, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        static decimal[] Values(JsonElement result) => result.GetProperty("values").EnumerateArray().Select(v => v.GetDecimal()).ToArray();
        Assert.Equal(new[] { 1m, 4m, 2m, 5m, 3m, 6m }, Values(results[0]));
        Assert.Equal(new[] { 4m, 5m, 6m }, Values(results[1]));
        Assert.Equal(new[] { 1m, 2m, 3m, 4m, 5m, 6m }, Values(results[2]));
        Assert.Equal(new[] { 100m, 100m, 101m }, Values(results[3]));
        Assert.Equal(new[] { 1m, 3m, 6m }, Values(results[4]));
        Assert.Equal(new[] { 4m, 10m, 18m }, Values(results[5]));
        Assert.Equal("Error", results[6].GetProperty("status").GetString());
        Assert.Equal("DOMAIN_ERROR", results[6].GetProperty("errorCode").GetString());
    }

    // ---- Group B3: Calculator v2 metadata, algebraic/transcendental functions, determinism ----

    [Fact]
    public async Task V2MetadataAndChainedFinancialFormulasSucceedEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 6,
          "variables": { "start": 100, "end": 162.889462677744140625, "years": 10, "dailyVol": 0.015 },
          "expressions": [
            { "name": "growth", "expression": "pow(end / start, 1 / years) - 1" },
            { "name": "ann_vol", "expression": "dailyVol * sqrt(252)" },
            { "name": "rem_floor", "expression": "mod(-10, 3)" },
            { "name": "signed_frac", "expression": "frac(-1.25)" },
            { "name": "sum_v", "expression": "sum(10, 20, 30)" },
            { "name": "avg_v", "expression": "avg(1, 1, 1)" },
            { "name": "log_e", "expression": "ln(exp(1))" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(10, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal("System.Decimal", root.GetProperty("arithmetic").GetString());
        Assert.Equal("IEEE-754 binary64 narrowed via ToDecimalOrNull (|x| < 7.9e27)", root.GetProperty("transcendentalArithmetic").GetString());
        Assert.Equal(7, root.GetProperty("okCount").GetInt32());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(0.05m, results[0].GetProperty("value").GetDecimal());
        Assert.Equal(0.238118m, results[1].GetProperty("value").GetDecimal());
        Assert.Equal(2m, results[2].GetProperty("value").GetDecimal());
        Assert.Equal(-0.25m, results[3].GetProperty("value").GetDecimal());
        Assert.Equal(60m, results[4].GetProperty("value").GetDecimal());
        Assert.Equal(1m, results[5].GetProperty("value").GetDecimal());
        Assert.Equal(1m, results[6].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task V2DomainAndOverflowErrorsReportPerExpressionAndPropagateDependencyFailed()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "expressions": [
            { "name": "bad_ln", "expression": "ln(0)" },
            { "name": "bad_sqrt", "expression": "sqrt(-1)" },
            { "name": "bad_pow", "expression": "pow(-2, 0.5)" },
            { "name": "bad_exp", "expression": "exp(65)" },
            { "name": "bad_pow_int", "expression": "pow(10, 29)" },
            { "name": "dep", "expression": "bad_ln + 1" },
            { "name": "ok_after", "expression": "pow(1.05, 10)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(1, root.GetProperty("okCount").GetInt32());
        Assert.Equal(6, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal("DOMAIN_ERROR", results[0].GetProperty("errorCode").GetString());
        Assert.Equal("DOMAIN_ERROR", results[1].GetProperty("errorCode").GetString());
        Assert.Equal("DOMAIN_ERROR", results[2].GetProperty("errorCode").GetString());
        Assert.Equal("ARITHMETIC_OVERFLOW", results[3].GetProperty("errorCode").GetString());
        Assert.Equal("ARITHMETIC_OVERFLOW", results[4].GetProperty("errorCode").GetString());
        Assert.Equal("DEPENDENCY_FAILED", results[5].GetProperty("errorCode").GetString());
        Assert.Equal("Ok", results[6].GetProperty("status").GetString());
        Assert.Equal(1.62889462677744140625m, results[6].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task V2TranscendentalCallsProduceByteIdenticalDeterministicJson()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        const string args = """{"variables":{"x":2,"y":1.5},"expressions":[{"name":"a","expression":"ln(x) + sqrt(y)"},{"name":"b","expression":"pow(x, y) * exp(-1)"}]}""";

        string first = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(1, "sa_evaluate_expressions", args), CancellationToken.None))!;
        string second = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(1, "sa_evaluate_expressions", args), CancellationToken.None))!;

        Assert.Equal(first, second);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(first)), SHA256.HashData(Encoding.UTF8.GetBytes(second)));
    }

    [Theory]
    [InlineData("""{"variables":[1],"expressions":[{"name":"a","expression":"1"}]}""", "variables")]
    [InlineData("""{"expressions":[123]}""", "expressions[0]")]
    [InlineData("""{"expressions":[{"expression":"1"}]}""", "expressions[0].name")]
    [InlineData("""{"expressions":[{"name":1,"expression":"1"}]}""", "expressions[0].name")]
    [InlineData("""{"expressions":[{"name":"a"}]}""", "expressions[0].expression")]
    [InlineData("""{"expressions":[{"name":"a","expression":1}]}""", "expressions[0].expression")]
    public async Task DirectInvokeAsyncRejectsMalformedJsonWithInvalidArgument(string rawJson, string expectedParam)
    {
        var tool = new EvaluateExpressionsTool(new McpLimits());
        using JsonDocument doc = JsonDocument.Parse(rawJson);

        ToolException ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.InvokeAsync(doc.RootElement, CancellationToken.None));

        Assert.Equal(ToolErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal(expectedParam, ex.Parameter);
    }

    // ---- Group B4: Calculator v3 built-in constants, nan/inf refusal, Phase-1 formulas & 32-arg stats ----

    [Fact]
    public async Task V3ConstantsAndPhase1FunctionsEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args32 = string.Join(", ", Enumerable.Range(1, 32));
        using JsonDocument resp = await CallAsync(dispatcher, $$"""
        {
          "decimalPlaces": 6,
          "expressions": [
            { "name": "sin_30", "expression": "sind(30)" },
            { "name": "tan_pi4", "expression": "round(tan(pi / 4), 6)" },
            { "name": "euler_id", "expression": "round(ln(e), 6)" },
            { "name": "med32", "expression": "median({{args32}})" },
            { "name": "bad_nan", "expression": "1 + nan" },
            { "name": "bad_inf", "expression": "cos(inf)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(10, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal(4, root.GetProperty("okCount").GetInt32());
        Assert.Equal(2, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(0.5m, results[0].GetProperty("value").GetDecimal());
        Assert.Equal(1m, results[1].GetProperty("value").GetDecimal());
        Assert.Equal(1m, results[2].GetProperty("value").GetDecimal());
        Assert.Equal(16.5m, results[3].GetProperty("value").GetDecimal());

        Assert.Equal("DOMAIN_ERROR", results[4].GetProperty("errorCode").GetString());
        Assert.Equal(4, results[4].GetProperty("errorPosition").GetInt32());
        Assert.Equal("DOMAIN_ERROR", results[5].GetProperty("errorCode").GetString());
        Assert.Equal(4, results[5].GetProperty("errorPosition").GetInt32());
    }

    [Theory]
    [InlineData("nan")]
    [InlineData("inf")]
    public async Task V3RefusesDeclaredNonFiniteIdentifiersInVariablesAndExpressions(string forbiddenName)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument varResp = await CallAsync(dispatcher, $$"""{"variables":{"{{forbiddenName}}":1},"expressions":[{"name":"r","expression":"1"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(varResp));
        Assert.Equal($"variables.{forbiddenName}", McpTestSupport.Structured(varResp).GetProperty("param").GetString());

        using JsonDocument exprResp = await CallAsync(dispatcher, $$"""{"expressions":[{"name":"{{forbiddenName}}","expression":"1"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(exprResp));
        Assert.Equal("expressions[0].name", McpTestSupport.Structured(exprResp).GetProperty("param").GetString());
    }

    [Fact]
    public async Task V3SpecialDistributionsAndExtendedStatisticsEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 6,
          "expressions": [
            { "name": "g5", "expression": "gamma(5)" },
            { "name": "bessel0", "expression": "bessel_j(0, 0)" },
            { "name": "norm_q", "expression": "norm_cdf(1.96)" },
            { "name": "gmean", "expression": "geomean(2, 8)" },
            { "name": "q50", "expression": "quantile(0.5, 1, 2, 3, 4)" },
            { "name": "bad_gamma", "expression": "gamma(0)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(5, root.GetProperty("okCount").GetInt32());
        Assert.Equal(1, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(24m, results[0].GetProperty("value").GetDecimal());
        Assert.Equal(1m, results[1].GetProperty("value").GetDecimal());
        Assert.Equal(0.975002m, results[2].GetProperty("value").GetDecimal());
        Assert.Equal(4m, results[3].GetProperty("value").GetDecimal());
        Assert.Equal(2.5m, results[4].GetProperty("value").GetDecimal());
        Assert.Equal("DOMAIN_ERROR", results[5].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task V3MatrixProjectionsEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 6,
          "expressions": [
            { "name": "d2", "expression": "det2(3, 5, 1, 2)" },
            { "name": "d3", "expression": "det3(6, 1, 1, 4, -2, 5, 2, 8, 7)" },
            { "name": "sx", "expression": "solve2_x(1, 2, 3, 4, 5, 6)" },
            { "name": "sy", "expression": "solve2_y(1, 2, 3, 4, 5, 6)" },
            { "name": "bad_sing", "expression": "solve2_x(1, 2, 2, 4, 3, 6)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(4, root.GetProperty("okCount").GetInt32());
        Assert.Equal(1, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(1m, results[0].GetProperty("value").GetDecimal());
        Assert.Equal(-306m, results[1].GetProperty("value").GetDecimal());
        Assert.Equal(-4m, results[2].GetProperty("value").GetDecimal());
        Assert.Equal(4.5m, results[3].GetProperty("value").GetDecimal());
        Assert.Equal("SINGULAR_MATRIX", results[4].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task V3HigherOrderNumericalAndQuadratureEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 6,
          "expressions": [
            { "name": "deriv", "expression": "diff_central(sin(x), x, pi / 3)" },
            { "name": "brent", "expression": "root_brent(x^2 - 2, x, 1, 2, 0.0000000001)" },
            { "name": "gk21", "expression": "quad_gk21(sin(x), x, 0, pi, 0.0000000001)" },
            { "name": "romberg", "expression": "quad_romberg(x^2, x, 0, 3, 0.0000000001)" },
            { "name": "bad_dummy", "expression": "quad_gk21(1, nan, 0, 1)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(4, root.GetProperty("okCount").GetInt32());
        Assert.Equal(1, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(0.5m, results[0].GetProperty("value").GetDecimal());
        Assert.Equal(1.414214m, results[1].GetProperty("value").GetDecimal());
        Assert.Equal(2m, results[2].GetProperty("value").GetDecimal());
        Assert.Equal(9m, results[3].GetProperty("value").GetDecimal());
        Assert.Equal("DOMAIN_ERROR", results[4].GetProperty("errorCode").GetString());
        Assert.Equal(13, results[4].GetProperty("errorPosition").GetInt32());
    }

    [Fact]
    public async Task V4ArrayVariablesAndChainingEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 6,
          "variables": { "lag": 1 },
          "arrayVariables": {
            "prices": [100, 102, 104, 107, 111],
            "weights": [1, 2, 3, 4]
          },
          "expressions": [
            { "name": "tail", "expression": "arr_window(prices, 4)" },
            { "name": "fail_arr", "expression": "arr_get(tail, 10)" },
            { "name": "dep_arr", "expression": "arr_len(fail_arr)" },
            { "name": "w_mean", "expression": "weighted_mean(weights, tail)" },
            { "name": "poly", "expression": "poly_project(prices, 2, lag)" },
            { "name": "corr", "expression": "mat_nearest_corr([1, 0.9, 0.9, 0.9, 1, 0.9, 0.9, 0.9, 1])" },
            { "name": "corr_flags", "expression": "mat_validate_corr(corr)" },
            { "name": "is_corr", "expression": "mat_is_corr(corr)" },
            { "name": "sum_tail", "expression": "sum(tail)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(10, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("arrayVariables").GetArrayLength());
        Assert.Equal(7, root.GetProperty("okCount").GetInt32());
        Assert.Equal(2, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal("array", results[0].GetProperty("valueType").GetString());
        decimal[] tail = results[0].GetProperty("values").EnumerateArray().Select(e => e.GetDecimal()).ToArray();
        Assert.Equal([102m, 104m, 107m, 111m], tail);

        Assert.Equal("DOMAIN_ERROR", results[1].GetProperty("errorCode").GetString());
        Assert.Equal("DEPENDENCY_FAILED", results[2].GetProperty("errorCode").GetString());
        Assert.Equal(107.5m, results[3].GetProperty("value").GetDecimal());
        Assert.True(results[4].GetProperty("value").GetDecimal() > 115m);
        Assert.Equal("array", results[5].GetProperty("valueType").GetString());
        Assert.Equal(9, results[5].GetProperty("values").GetArrayLength());
        Assert.Equal(0m, results[6].GetProperty("value").GetDecimal());
        Assert.Equal(1m, results[7].GetProperty("value").GetDecimal());
        Assert.Equal(424m, results[8].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task V4ArrayVariablesValidationAndBudgetLimitsAreEnforced()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        // Scalar and array variable names share the declared identifier namespace.
        using JsonDocument dup = await CallAsync(dispatcher, """
        {
          "variables": { "x": 1 },
          "arrayVariables": { "x": [1, 2] },
          "expressions": [{ "name": "a", "expression": "1" }]
        }
        """);
        Assert.True(McpTestSupport.IsError(dup));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(dup));

        // Combined scalar + array variable count cannot exceed MaxCalculatorVariables (50).
        string manyArrays = string.Join(",", Enumerable.Range(0, 49).Select(i => $"\"a{i}\":[{i}]"));
        using JsonDocument tooMany = await CallAsync(dispatcher, $$"""
        {
          "variables": { "v1": 1, "v2": 2 },
          "arrayVariables": { {{manyArrays}} },
          "expressions": [{ "name": "a", "expression": "v1 + v2" }]
        }
        """);
        Assert.True(McpTestSupport.IsError(tooMany));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(tooMany));

        // Combined input arrayVariables budget (2048) exceeded is refused, while 4*512=2048 + derived arr_window (<=4096 arena) and [] pass.
        string bigArr = "[" + string.Join(",", Enumerable.Range(0, 512)) + "]";
        string fiveBigArrays = string.Join(",", Enumerable.Range(0, 5).Select(i => $"\"b{i}\":{bigArr}"));
        using JsonDocument overBudget = await CallAsync(dispatcher, $$"""
        {
          "arrayVariables": { {{fiveBigArrays}} },
          "expressions": [{ "name": "a", "expression": "1" }]
        }
        """);
        Assert.True(McpTestSupport.IsError(overBudget));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(overBudget));

        string fourBigArrays = string.Join(",", Enumerable.Range(0, 4).Select(i => $"\"b{i}\":{bigArr}"));
        using JsonDocument exactInputCapAndEmpty = await CallAsync(dispatcher, $$"""
        {
          "arrayVariables": { {{fourBigArrays}}, "emptyArr": [] },
          "expressions": [
            { "name": "win", "expression": "arr_window(b0, 4)" },
            { "name": "emptyLen", "expression": "arr_len(emptyArr)" },
            { "name": "emptySum", "expression": "sum(emptyArr)" }
          ]
        }
        """);
        Assert.False(McpTestSupport.IsError(exactInputCapAndEmpty));
        JsonElement capResults = McpTestSupport.Structured(exactInputCapAndEmpty).GetProperty("results");
        Assert.Equal("Ok", capResults[0].GetProperty("status").GetString());
        Assert.Equal(0m, capResults[1].GetProperty("value").GetDecimal());
        Assert.Equal("Error", capResults[2].GetProperty("status").GetString());
        Assert.Equal("DOMAIN_ERROR", capResults[2].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task V4LinearDiagnosticsAndRegressionEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 6,
          "arrayVariables": {
            "X": [1, 0, 2, 1, 3, 0, 4, 2, 5, 1, 6, 3],
            "y": [4, 7, 8, 12, 13, 17]
          },
          "expressions": [
            { "name": "ols_b", "expression": "ols_fit(X, y)" },
            { "name": "pred", "expression": "ols_predict([7, 2], ols_b)" },
            { "name": "ridge", "expression": "ridge_fit(X, y, 0.01)" },
            { "name": "pca", "expression": "pca_project(X, 6, 2, 1)" },
            { "name": "cov_mat", "expression": "[4, 2, 2, 9]" },
            { "name": "cov_flags", "expression": "mat_validate_cov(cov_mat, 1)" },
            { "name": "corr_r", "expression": "mat_cov_to_corr(cov_mat)" },
            { "name": "rcond2", "expression": "mat_rcond([2, 0, 0, 1], 2, 2, 2)" },
            { "name": "cond1", "expression": "mat_cond([2, 0, 0, 1], 2, 2, 1)" },
            { "name": "mahal", "expression": "mahalanobis([2, 3], [0, 0], [1, 0, 0, 1])" },
            { "name": "bad_cov", "expression": "mat_validate_cov([1, 5, 5, 1], 1)" },
            { "name": "flag_not_pd", "expression": "has_flag(bad_cov, 7)" },
            { "name": "is_corr_psd", "expression": "mat_is_corr([1, 1, 1, 1], 0.000000001, 0)" },
            { "name": "is_corr_pd", "expression": "mat_is_corr([1, 1, 1, 1], 0.000000001, 1)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(14, root.GetProperty("okCount").GetInt32());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        decimal[] beta = results[0].GetProperty("values").EnumerateArray().Select(e => e.GetDecimal()).ToArray();
        Assert.Equal([2m, 2m, 1m], beta);
        Assert.Equal(18m, results[1].GetProperty("value").GetDecimal());
        Assert.Equal(3, results[2].GetProperty("values").GetArrayLength());
        Assert.Equal(6, results[3].GetProperty("values").GetArrayLength());
        Assert.Equal(0m, results[5].GetProperty("value").GetDecimal());
        decimal[] corr = results[6].GetProperty("values").EnumerateArray().Select(e => e.GetDecimal()).ToArray();
        Assert.Equal(1m, corr[0]);
        Assert.Equal(0.333333m, corr[1]);
        Assert.Equal(0.5m, results[7].GetProperty("value").GetDecimal());
        Assert.Equal(2m, results[8].GetProperty("value").GetDecimal());
        Assert.InRange(results[9].GetProperty("value").GetDecimal(), 3.60555m, 3.60556m);
        Assert.Equal(1m, results[11].GetProperty("value").GetDecimal());
        Assert.Equal(1m, results[12].GetProperty("value").GetDecimal());
        Assert.Equal(0m, results[13].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task V4RobustStatsTimeSeriesResamplingMcAndPdeEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 6,
          "arrayVariables": {
            "ret": [1, 2, 1.5, 3, 2.5, 4, 3.5, 5, 4.5, 6, 5.5, 7],
            "pvals": [0.01, 0.04, 0.03, 0.2],
            "heat": [0, 1, 2, 1, 0]
          },
          "expressions": [
            { "name": "hub", "expression": "huber_loc(ret)" },
            { "name": "ts_slope", "expression": "theil_sen(ret)" },
            { "name": "acf1", "expression": "acf(ret, 1)" },
            { "name": "pacfs", "expression": "pacf_series(ret, 2)" },
            { "name": "bh", "expression": "p_adjust(pvals, 2)" },
            { "name": "k_sum", "expression": "diag_kahan_sum([10000000000000000, 1, -10000000000000000])" },
            { "name": "ci", "expression": "boot_ci(ret, 0, 60, 2, 0.1, 42, 0)" },
            { "name": "mc_nd", "expression": "mc_integrate_nd(x + y, [x, y], [0, 0], [1, 2], 128, 7, 0)" },
            { "name": "rich", "expression": "fd_richardson(x^3, x, 2, 0.001, 2)" },
            { "name": "pde_cn", "expression": "fd_pde_1d(heat, 0.1, 0.2, 0.1, 5, 2, 0)" },
            { "name": "dl", "expression": "diff_lag(ret, 2, 1)" },
            { "name": "rich_arr_arg", "expression": "fd_richardson(x^3, x, arr_get(heat, 2), 0.001, 2)" },
            { "name": "mc1_arr_arg", "expression": "mc_integrate(x, x, 0, arr_get(heat, 2), 64, 1)" },
            { "name": "mc_nd_arr_arg", "expression": "mc_integrate_nd(x, [x], [0], [arr_get(heat, 2)], 64, 1)" },
            { "name": "mc_nd_captured_array", "expression": "mc_integrate_nd(sum(heat) * x, [x], [0], [1], 64, 1)" },
            { "name": "mc_nd_unknown_body", "expression": "mc_integrate_nd(sum(missing_arr) * x, [x], [0], [1], 64, 1)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(10, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal(15, root.GetProperty("okCount").GetInt32());
        Assert.Equal(1, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.True(results[0].GetProperty("value").GetDecimal() > 2m);
        Assert.True(results[1].GetProperty("value").GetDecimal() > 0m);
        Assert.True(results[2].GetProperty("value").GetDecimal() > 0m);
        Assert.Equal(2, results[3].GetProperty("values").GetArrayLength());
        Assert.Equal(4, results[4].GetProperty("values").GetArrayLength());
        Assert.Equal(1m, results[5].GetProperty("value").GetDecimal());
        Assert.Equal(4, results[6].GetProperty("values").GetArrayLength());
        Assert.InRange(results[7].GetProperty("value").GetDecimal(), 2.9m, 3.1m);
        Assert.Equal(12m, results[8].GetProperty("value").GetDecimal());
        Assert.Equal(5, results[9].GetProperty("values").GetArrayLength());
        Assert.Equal(1m, results[10].GetProperty("value").GetDecimal());
        Assert.Equal(12m, results[11].GetProperty("value").GetDecimal());
        Assert.InRange(results[12].GetProperty("value").GetDecimal(), 1.9m, 2.1m);
        Assert.InRange(results[13].GetProperty("value").GetDecimal(), 1.9m, 2.1m);
        // v6 allows higher-order bodies to read outer array variables via DslArrayArenaSnapshot (`sum(heat) == 4`, integral over [0,1] of 4x dx == 2).
        Assert.Equal("Ok", results[14].GetProperty("status").GetString());
        Assert.InRange(results[14].GetProperty("value").GetDecimal(), 1.9m, 2.1m);
        Assert.Equal("Error", results[15].GetProperty("status").GetString());
        Assert.Equal("UNDEFINED_NAME", results[15].GetProperty("errorCode").GetString());
        Assert.Equal("mc_integrate_nd(sum(".Length, results[15].GetProperty("errorPosition").GetInt32());
        Assert.Contains("missing_arr", results[15].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1e-5", 0.00001)]
    [InlineData("1E+3", 1000)]
    [InlineData("1e5", 100000)]
    public async Task ScientificNotationEvaluatesExactDecimalInV6(string expression, double expectedValue)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, $$"""{"expressions":[{"name":"sci","expression":"{{expression}}"}]}""");

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement structured = McpTestSupport.Structured(resp);
        Assert.Equal(10, structured.GetProperty("calculatorVersion").GetInt32());
        JsonElement item = structured.GetProperty("results")[0];
        Assert.Equal("Ok", item.GetProperty("status").GetString());
        Assert.Equal((decimal)expectedValue, item.GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task V6DomainsAndOperatorsEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 6,
          "variables": {
            "dStart": 18993,
            "dEnd": 19358
          },
          "arrayVariables": {
            "vals": [10, 11, 12, 20, 21, 22],
            "grp": [1, 1, 1, 2, 2, 2],
            "cf": [-1000, 300, 400, 500],
            "dates": [18262, 18628, 18993, 19358]
          },
          "expressions": [
            { "name": "ds", "expression": "date_serial(2022, 1, 1)" },
            { "name": "yf", "expression": "yearfrac(dStart, dEnd, 3)" },
            { "name": "irr_x", "expression": "xirr(cf, dates)" },
            { "name": "anova", "expression": "anova_1way(vals, grp)" },
            { "name": "gev", "expression": "gev_cdf(1, 0, 1, 0)" },
            { "name": "comb_op", "expression": "sigma(i * 1e0, i, 1, 5) > 10 ? vals[3] : vals[0]" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(10, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal(6, root.GetProperty("okCount").GetInt32());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());
        JsonElement results = root.GetProperty("results");
        Assert.Equal(18993m, results[0].GetProperty("value").GetDecimal());
        Assert.Equal(1m, results[1].GetProperty("value").GetDecimal());
        Assert.InRange(results[2].GetProperty("value").GetDecimal(), 0.08m, 0.12m);
        Assert.Equal(4, results[3].GetProperty("values").GetArrayLength());
        Assert.True(results[3].GetProperty("values")[0].GetDecimal() > 100m);
        Assert.InRange(results[4].GetProperty("value").GetDecimal(), 0.692m, 0.693m);
        Assert.Equal(20m, results[5].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task V6Phase2FinanceGeometryAndInverseEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 6,
          "arrayVariables": {
            "xs": [1, 2, 3, 4],
            "ys": [3, 5, 7, 9],
            "w": [1, 1, 1, 1],
            "cov": [0.04, 0, 0, 0.09]
          },
          "expressions": [
            { "name": "inv", "expression": "inv_func(x * x, x, 9, 0, 10)" },
            { "name": "coeffs", "expression": "wls_fit(xs, ys, w)" },
            { "name": "ytm", "expression": "bond_ytm(1000, 1000, 0.05, 10, 2)" },
            { "name": "minw", "expression": "port_min_var_weights(cov)" },
            { "name": "rho", "expression": "bs_rho(100, 100, 1, 0.05, 0.2, 1, 0)" },
            { "name": "tri", "expression": "tri_scalene(3, 4, 5)" },
            { "name": "nball", "expression": "nball_vol(4, 1)" },
            { "name": "rot", "expression": "rot3d_pt([1, 0, 2], [0, 0, 1], pi / 2)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(10, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal(8, root.GetProperty("okCount").GetInt32());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(3m, results[0].GetProperty("value").GetDecimal());
        Assert.Equal(1m, results[1].GetProperty("values")[0].GetDecimal());
        Assert.Equal(2m, results[1].GetProperty("values")[1].GetDecimal());
        Assert.Equal(0.05m, results[2].GetProperty("value").GetDecimal());
        Assert.Equal(2, results[3].GetProperty("values").GetArrayLength());
        Assert.True(results[4].GetProperty("value").GetDecimal() > 0m);
        Assert.Equal(6m, results[5].GetProperty("values")[0].GetDecimal());
        Assert.InRange(results[6].GetProperty("value").GetDecimal(), 4.93m, 4.94m);
        Assert.Equal(0m, results[7].GetProperty("values")[0].GetDecimal());
        Assert.Equal(1m, results[7].GetProperty("values")[1].GetDecimal());
        Assert.Equal(2m, results[7].GetProperty("values")[2].GetDecimal());
    }

    [Fact]
    public async Task V8ExpressivenessFunctionsAndAllowFunctionNameVariablesWorkEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        // Opt-in allowFunctionNameVariables allows variable and expression names matching functions while nan/inf/duplicates remain rejected
        using JsonDocument optInResp = await CallAsync(dispatcher, """
        {
          "allowFunctionNameVariables": true,
          "variables": {
            "sum": 10,
            "irr": 2
          },
          "expressions": [
            { "name": "step", "expression": "sum + irr + sum(1, 2, 3)" },
            { "name": "yf30e", "expression": "yearfrac(date_serial(2020, 1, 31), date_serial(2020, 3, 31), 4)" },
            { "name": "cop", "expression": "copula_gauss_cdf(0.5, 0.5, 0.5)" },
            { "name": "opt_div", "expression": "binomial_option_price_div(100, 100, 1, 0.05, 0.03, 0.2, 20, 0)" },
            { "name": "dirty", "expression": "bond_price_dirty(1000, 0.06, 0.05, 45.5, 182.5, 10, 2)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(optInResp));
        JsonElement root = McpTestSupport.Structured(optInResp);
        Assert.Equal(10, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal(5, root.GetProperty("okCount").GetInt32());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(18m, results[0].GetProperty("value").GetDecimal());
        Assert.InRange(Math.Abs(results[1].GetProperty("value").GetDecimal() - (60m / 360m)), 0m, 1e-12m);
        Assert.InRange(Math.Abs(results[2].GetProperty("value").GetDecimal() - (1m / 3m)), 0m, 1e-12m);
        Assert.True(results[3].GetProperty("value").GetDecimal() > 0m);
        Assert.True(results[4].GetProperty("value").GetDecimal() > 1000m);

        // nan / inf / duplicate remain rejected even when allowFunctionNameVariables is true
        using JsonDocument nanResp = await CallAsync(dispatcher, """{"allowFunctionNameVariables":true,"variables":{"nan":1},"expressions":[{"name":"r","expression":"1"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(nanResp));
        using JsonDocument dupResp = await CallAsync(dispatcher, """{"allowFunctionNameVariables":true,"variables":{"sum":1},"expressions":[{"name":"sum","expression":"2"}]}""");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(dupResp));

        // Same-name function/variable/higher-order coexistence under allowFunctionNameVariables: true
        using JsonDocument coexistResp = await CallAsync(dispatcher, """
        {
          "allowFunctionNameVariables": true,
          "arrayVariables": {
            "sum": [2, 4, 6, 8]
          },
          "expressions": [
            { "name": "arr_sub", "expression": "sum(sum) + sum[0]" },
            { "name": "roll_shadow", "expression": "arr_rolling_map(sum(sum) / arr_len(sum), sum, sum, 3)" }
          ]
        }
        """);
        Assert.False(McpTestSupport.IsError(coexistResp));
        JsonElement coexistResults = McpTestSupport.Structured(coexistResp).GetProperty("results");
        Assert.Equal(22m, coexistResults[0].GetProperty("value").GetDecimal());
        Assert.Equal([4m, 6m], coexistResults[1].GetProperty("values").EnumerateArray().Select(e => e.GetDecimal()).ToArray());
    }

    [Fact]
    public async Task ArimaForecastAndRefineErrorParenthesisHintWorkEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string seriesJson = "[" + string.Join(",", Enumerable.Range(0, 40).Select(t => (100m + 0.5m * t + (t % 3 == 0 ? 0.6m : -0.3m)).ToString(CultureInfo.InvariantCulture))) + "]";

        using JsonDocument resp = await CallAsync(dispatcher, $$"""
        {
          "allowFunctionNameVariables": true,
          "arrayVariables": {
            "s": {{seriesJson}}
          },
          "expressions": [
            { "name": "fc", "expression": "arima_forecast(s, 1, 1, 1, 2)" },
            { "name": "roll_fc", "expression": "arr_rolling_map(arima_forecast(w, 1, 0, 0, 1)[0], w, s, 25)[0]" },
            { "name": "bad_horizon", "expression": "arima_forecast(s, 1, 1, 1, 65)" },
            { "name": "typo_fn", "expression": "sin + 1" },
            { "name": "later_ref", "expression": "sum + 1" },
            { "name": "arr_missing", "expression": "sin[0] + 1" },
            { "name": "sum", "expression": "sum + 1" },
            { "name": "self_var", "expression": "self_var + 2" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(2, root.GetProperty("okCount").GetInt32());
        Assert.Equal(6, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal("Ok", results[0].GetProperty("status").GetString());
        decimal[] fc = results[0].GetProperty("values").EnumerateArray().Select(e => e.GetDecimal()).ToArray();
        Assert.Equal(4, fc.Length);
        Assert.True(fc[3] >= fc[2]); // SE(2) >= SE(1)

        Assert.Equal("Ok", results[1].GetProperty("status").GetString());
        Assert.True(results[1].GetProperty("value").GetDecimal() > 90m);

        Assert.Equal("DOMAIN_ERROR", results[2].GetProperty("errorCode").GetString());

        // RefineError parenthesis hint: preserves UNDEFINED_NAME, position, and "Undefined name 'sin'" prefix
        Assert.Equal("UNDEFINED_NAME", results[3].GetProperty("errorCode").GetString());
        Assert.Equal(0, results[3].GetProperty("errorPosition").GetInt32());
        string typoMsg = results[3].GetProperty("errorMessage").GetString()!;
        Assert.StartsWith("Undefined name 'sin'.", typoMsg, StringComparison.Ordinal);
        Assert.Contains("sin(...)", typoMsg, StringComparison.Ordinal);

        // RefineError priority guard: later expression named 'sum' reports "declared after" instead of parenthesis hint
        Assert.Equal("UNDEFINED_NAME", results[4].GetProperty("errorCode").GetString());
        Assert.Contains("declared after", results[4].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("parentheses", results[4].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);

        // RefineError subscript guard: 'sin[0]' (adjacent '[') must NOT suggest parentheses
        Assert.Equal("UNDEFINED_NAME", results[5].GetProperty("errorCode").GetString());
        Assert.StartsWith("Undefined name 'sin'.", results[5].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("parentheses", results[5].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);

        Assert.Equal("UNDEFINED_NAME", results[6].GetProperty("errorCode").GetString());
        string selfFnMsg = results[6].GetProperty("errorMessage").GetString()!;
        Assert.StartsWith("Undefined name 'sum'.", selfFnMsg, StringComparison.Ordinal);
        Assert.Contains("cannot reference itself", selfFnMsg, StringComparison.Ordinal);
        Assert.DoesNotContain("parentheses", selfFnMsg, StringComparison.Ordinal);

        Assert.Equal("UNDEFINED_NAME", results[7].GetProperty("errorCode").GetString());
        Assert.Contains("cannot reference itself", results[7].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);

        // Undeclared array subscript matching reserved function name must NOT suggest parentheses
        using JsonDocument subResp = await CallAsync(dispatcher, """
        {
          "expressions": [
            { "name": "sub_only", "expression": "sum [0] + 1" }
          ]
        }
        """);
        Assert.False(McpTestSupport.IsError(subResp));
        JsonElement subRes = McpTestSupport.Structured(subResp).GetProperty("results")[0];
        Assert.Equal("UNDEFINED_NAME", subRes.GetProperty("errorCode").GetString());
        string subMsg = subRes.GetProperty("errorMessage").GetString()!;
        Assert.StartsWith("Undefined name 'sum'.", subMsg, StringComparison.Ordinal);
        Assert.DoesNotContain("parentheses", subMsg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V9BuiltinFunctionsEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 4,
          "arrayVariables": {
            "h": [10, 12, 12, 11, 14, 15, 13],
            "l": [8, 9, 10, 9, 11, 12, 10],
            "c": [9, 11, 11.5, 10, 13, 14, 12],
            "v": [100, 120, 110, 90, 200, 220, 150],
            "sess": [1, 1, 1, 1, 2, 2, 2]
          },
          "expressions": [
            { "name": "rmax", "expression": "arr_rolling_max(h, 3, -1)" },
            { "name": "bs_min", "expression": "arr_rolling_bars_since_min(l, 3, -1)" },
            { "name": "piv", "expression": "pivot_high_causal(h, 1, 1, -1)" },
            { "name": "orb_h", "expression": "arr_session_range(h, l, sess, 2, 0, -1)" },
            { "name": "poc", "expression": "vp_poc(c, v, 3)" },
            { "name": "dtw", "expression": "dtw_distance([0, 1, 0], [0, 1, 1, 0])" },
            { "name": "cusum", "expression": "cusum_filter(c, 2)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(10, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal(7, root.GetProperty("okCount").GetInt32());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        decimal[] Values(int idx) => results[idx].GetProperty("values").EnumerateArray().Select(static e => e.GetDecimal()).ToArray();
        Assert.Equal(new[] { -1m, -1m, 12m, 12m, 14m, 15m, 15m }, Values(0));
        Assert.Equal(new[] { -1m, -1m, 2m, 0m, 1m, 2m, 0m }, Values(1));
        Assert.Equal(new[] { -1m, -1m, -1m, 12m, -1m, -1m, 15m }, Values(2));
        Assert.Equal(new[] { -1m, 12m, 12m, 12m, -1m, 15m, 15m }, Values(3));
        Assert.True(results[4].GetProperty("value").GetDecimal() > 11m);
        Assert.Equal(0m, results[5].GetProperty("value").GetDecimal());
        Assert.Equal(new[] { 0m, 1m, 0m, 0m, 1m, 0m, -1m }, Values(6));
    }

    [Fact]
    public async Task V10ExtensionsAndNewFunctionsEvaluateEndToEnd()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument resp = await CallAsync(dispatcher, """
        {
          "decimalPlaces": 4,
          "arrayVariables": {
            "p": [10, 15, 10, 20],
            "v": [20, 50, 30, 10]
          },
          "expressions": [
            { "name": "idx", "expression": "rdp_simplify(p, 4, 1)" },
            { "name": "wave", "expression": "arr_interp_at(idx, p)" },
            { "name": "zmean", "expression": "arr_zip_rolling_map(sum(x) / 2, [x], [p], 2, -1)" },
            { "name": "va", "expression": "vp_value_area([100, 105, 110], [30, 50, 20], 3, 0.7)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(10, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal(4, root.GetProperty("okCount").GetInt32());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());

        JsonElement results = root.GetProperty("results");
        decimal[] Values(int idx) => results[idx].GetProperty("values").EnumerateArray().Select(static e => e.GetDecimal()).ToArray();
        Assert.Equal(new[] { 0m, 1m, 2m, 3m }, Values(0));
        Assert.Equal(new[] { 10m, 15m, 10m, 20m }, Values(1));
        Assert.Equal(new[] { -1m, 12.5m, 12.5m, 15m }, Values(2));
        Assert.Equal(new[] { 100m, 105m, 106.6667m }, Values(3));
    }
}
