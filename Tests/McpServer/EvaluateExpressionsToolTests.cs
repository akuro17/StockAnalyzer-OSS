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
        Assert.Contains("docs/MCP/tools/sa_evaluate_expressions.md", description, StringComparison.Ordinal);

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
            { "name": "cagr", "expression": "pow(end / start, 1 / years) - 1" },
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
        Assert.Equal(4, root.GetProperty("calculatorVersion").GetInt32());
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
        Assert.Equal(4, root.GetProperty("calculatorVersion").GetInt32());
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
        Assert.Equal(4, root.GetProperty("calculatorVersion").GetInt32());
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
            { "name": "cov_s", "expression": "[4, 2, 2, 9]" },
            { "name": "cov_flags", "expression": "mat_validate_cov(cov_s, 1)" },
            { "name": "corr_r", "expression": "mat_cov_to_corr(cov_s)" },
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
            { "name": "mc_nd_bad_body", "expression": "mc_integrate_nd(sum(heat) * x, [x], [0], [1], 64, 1)" }
          ]
        }
        """);

        Assert.False(McpTestSupport.IsError(resp));
        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal(4, root.GetProperty("calculatorVersion").GetInt32());
        Assert.Equal(14, root.GetProperty("okCount").GetInt32());
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
        Assert.Equal("Error", results[14].GetProperty("status").GetString());
        // The integrand is evaluated in an isolated scope that does not see array variables, so `heat` inside `sum(heat) * x` is an undefined name there. The body error keeps its own
        // code (UNDEFINED_NAME), the offending name and its position in the whole expression (never DOMAIN_ERROR, never an exception).
        Assert.Equal("UNDEFINED_NAME", results[14].GetProperty("errorCode").GetString());
        Assert.Equal("mc_integrate_nd(sum(".Length, results[14].GetProperty("errorPosition").GetInt32());
        Assert.Contains("heat", results[14].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
    }
}
