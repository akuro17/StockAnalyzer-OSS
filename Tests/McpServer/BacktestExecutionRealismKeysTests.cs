using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T9 (decision D-9): the five execution-realism keys of the <c>account</c> object (maxVolumeParticipationRatio, volumeConstraintMode, shortBorrowAnnualRate,
/// borrowFeeBasisMode, priceLimitFillMode) through the real composition. A call that names none binds, runs and echoes exactly as before; a call that names one changes the ENGINE
/// result (not only the echo). The stored series is a sine wave of 400 daily bars with one volume for every bar.
/// </summary>
public sealed class BacktestExecutionRealismKeysTests : IDisposable
{
    private static readonly string[] BaseAccountKeys =
    {
        "initialCapital", "commissionFlat", "commissionPerUnit", "slippageRatio", "sizingModel", "sizingParameter", "initialMarginRatio", "maintenanceMarginRatio", "liquidationPenaltyRatio",
    };

    private readonly BacktestDiagnosticsTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static string WithAccount(string account) => ",\"account\":" + account;

    private static async Task<JsonDocument> RunAsync(McpMessageDispatcher dispatcher, string account, string extra = "")
        => await BacktestDiagnosticsTestHost.RunAsync(dispatcher, BacktestDiagnosticsTestHost.Period + WithAccount(account) + extra);

    private static JsonElement Ok(JsonDocument response) => BacktestDiagnosticsTestHost.Result(response);

    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    private static int TotalTrades(JsonElement result) => result.GetProperty("trades").GetProperty("totalTrades").GetInt32();

    private static decimal[] Quantities(JsonElement result) => result.GetProperty("trades").GetProperty("items").EnumerateArray().Select(t => t.GetProperty("quantity").GetDecimal()).ToArray();

    private static decimal FinalEquity(JsonElement result) => result.GetProperty("equity").GetProperty("final").GetDecimal();

    private async Task<decimal> StoredBarVolumeAsync(McpMessageDispatcher dispatcher)
    {
        using JsonDocument candles = (await McpTestSupport.CallAsync(dispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":400}"""))!;
        decimal[] volumes = McpTestSupport.Structured(candles).GetProperty("candles").EnumerateArray().Select(c => c.GetProperty("volume").GetDecimal()).Distinct().ToArray();
        Assert.Single(volumes); // the fixture's precondition: every bar has the same volume, so a participation cap binds on every bar alike
        return volumes[0];
    }

    // ---- a call that names none is unchanged ------------------------------------------------------------------------------------

    [Fact]
    public async Task ACallThatNamesNoneEchoesTheNineOldAccountKeysOnly()
    {
        var dispatcher = await _host.StartAsync();

        using var withoutAccount = await BacktestDiagnosticsTestHost.RunAsync(dispatcher, BacktestDiagnosticsTestHost.Period);
        using var withAccount = await RunAsync(dispatcher, """{"initialCapital":2000000}""");

        Assert.Equal(BaseAccountKeys, Keys(Ok(withoutAccount).GetProperty("effectiveSettings").GetProperty("account")));
        Assert.Equal(BaseAccountKeys, Keys(Ok(withAccount).GetProperty("effectiveSettings").GetProperty("account")));
    }

    [Fact]
    public async Task NamingTheDefaultsGivesTheSameFingerprintAndHashAsLeavingThemOut_AndOnlyTheEchoDiffers()
    {
        var dispatcher = await _host.StartAsync();

        using var omitted = await RunAsync(dispatcher, """{"sizingParameter":10}""");
        using var explicitDefaults = await RunAsync(
            dispatcher,
            """{"sizingParameter":10,"volumeConstraintMode":"RejectIfExceeds","shortBorrowAnnualRate":0,"borrowFeeBasisMode":"EntryPriceFixed","priceLimitFillMode":"AlwaysFill"}""");

        JsonElement before = Ok(omitted);
        JsonElement after = Ok(explicitDefaults);
        Assert.Equal(before.GetProperty("reproducibilityHash").GetString(), after.GetProperty("reproducibilityHash").GetString());
        Assert.Equal(before.GetProperty("runFingerprint").GetProperty("sha256").GetString(), after.GetProperty("runFingerprint").GetProperty("sha256").GetString());
        Assert.Equal(before.GetProperty("trades").GetRawText(), after.GetProperty("trades").GetRawText());
        Assert.Equal(before.GetProperty("report").GetRawText(), after.GetProperty("report").GetRawText());
        Assert.NotEqual(Keys(before.GetProperty("effectiveSettings").GetProperty("account")), Keys(after.GetProperty("effectiveSettings").GetProperty("account")));
    }

    // ---- the echo ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheNamedKeysAreEchoed_AfterLiquidationPenaltyRatio_InTheFixedOrder()
    {
        var dispatcher = await _host.StartAsync();

        using var all = await RunAsync(
            dispatcher,
            """{"priceLimitFillMode":"RejectIfAtLimitPrice","borrowFeeBasisMode":"MarkToMarketDaily","shortBorrowAnnualRate":0.02,"volumeConstraintMode":"ClampAndCancelRemainder","maxVolumeParticipationRatio":0.25}""");
        using var some = await RunAsync(dispatcher, """{"shortBorrowAnnualRate":0.03,"priceLimitFillMode":"AlwaysFill"}""");

        JsonElement account = Ok(all).GetProperty("effectiveSettings").GetProperty("account");
        Assert.Equal(BaseAccountKeys.Concat(McpBacktestSettingsExposure.ExecutionRealismKeys), Keys(account));
        Assert.Equal(0.25m, account.GetProperty("maxVolumeParticipationRatio").GetDecimal());
        Assert.Equal("ClampAndCancelRemainder", account.GetProperty("volumeConstraintMode").GetString());
        Assert.Equal(0.02m, account.GetProperty("shortBorrowAnnualRate").GetDecimal());
        Assert.Equal("MarkToMarketDaily", account.GetProperty("borrowFeeBasisMode").GetString());
        Assert.Equal("RejectIfAtLimitPrice", account.GetProperty("priceLimitFillMode").GetString());

        JsonElement partial = Ok(some).GetProperty("effectiveSettings").GetProperty("account");
        Assert.Equal(BaseAccountKeys.Concat(new[] { "shortBorrowAnnualRate", "priceLimitFillMode" }), Keys(partial));
    }

    // ---- the engine result changes ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AParticipationCapThatBindsRejectsOrClampsTheOrders_AndTheDefaultRunDoesNot()
    {
        var dispatcher = await _host.StartAsync();
        decimal volume = await StoredBarVolumeAsync(dispatcher);
        const decimal OrderQuantity = 10m;
        decimal ratio = OrderQuantity / volume / 2m; // the cap is half the order, so it binds on every bar
        string ratioText = ratio.ToString(CultureInfo.InvariantCulture);

        using var unlimited = await RunAsync(dispatcher, """{"sizingParameter":10}""");
        using var rejected = await RunAsync(dispatcher, $$"""{"sizingParameter":10,"maxVolumeParticipationRatio":{{ratioText}},"volumeConstraintMode":"RejectIfExceeds"}""");
        using var clamped = await RunAsync(dispatcher, $$"""{"sizingParameter":10,"maxVolumeParticipationRatio":{{ratioText}},"volumeConstraintMode":"ClampAndCancelRemainder"}""");

        JsonElement free = Ok(unlimited);
        Assert.True(TotalTrades(free) > 0);
        Assert.All(Quantities(free), quantity => Assert.Equal(OrderQuantity, quantity));

        JsonElement reject = Ok(rejected);
        Assert.True(TotalTrades(reject) < TotalTrades(free), "RejectIfExceeds must reject the orders the cap binds");
        Assert.Equal(0, TotalTrades(reject));

        JsonElement clamp = Ok(clamped);
        Assert.True(TotalTrades(clamp) > 0);
        Assert.All(Quantities(clamp), quantity => Assert.True(quantity > 0m && quantity <= volume * ratio, $"clamped quantity {quantity} is above the cap {volume * ratio}"));
        Assert.NotEqual(free.GetProperty("reproducibilityHash").GetString(), clamp.GetProperty("reproducibilityHash").GetString());
        Assert.NotEqual(reject.GetProperty("reproducibilityHash").GetString(), clamp.GetProperty("reproducibilityHash").GetString());
    }

    [Fact]
    public async Task TheCarryOverModeIsAcceptedAndRunsTheEngine()
    {
        var dispatcher = await _host.StartAsync();
        decimal volume = await StoredBarVolumeAsync(dispatcher);
        string ratioText = (10m / volume / 2m).ToString(CultureInfo.InvariantCulture);

        using var carried = await RunAsync(dispatcher, $$"""{"sizingParameter":10,"maxVolumeParticipationRatio":{{ratioText}},"volumeConstraintMode":"ClampAndCarryOverRemainder"}""");

        JsonElement result = Ok(carried);
        Assert.Equal("ClampAndCarryOverRemainder", result.GetProperty("effectiveSettings").GetProperty("account").GetProperty("volumeConstraintMode").GetString());
        Assert.True(TotalTrades(result) > 0);
    }

    [Fact]
    public async Task ABorrowRateLowersTheEquityOfAShortTrade_AndTheBasisModeChangesTheFee()
    {
        var dispatcher = await _host.StartAsync();
        const string ShortConditions = """{"entryShort":{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}},"exitShort":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}}""";
        async Task<JsonElement> ShortRunAsync(string account)
        {
            using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"MSFT","conditions":{{ShortConditions}}{{BacktestDiagnosticsTestHost.Period}},"account":{{account}}}"""))!;
            return Ok(response).Clone();
        }

        JsonElement free = await ShortRunAsync("""{"sizingParameter":10}""");
        JsonElement zero = await ShortRunAsync("""{"sizingParameter":10,"shortBorrowAnnualRate":0}""");
        JsonElement borrowing = await ShortRunAsync("""{"sizingParameter":10,"shortBorrowAnnualRate":0.5}""");
        JsonElement markToMarket = await ShortRunAsync("""{"sizingParameter":10,"shortBorrowAnnualRate":0.5,"borrowFeeBasisMode":"MarkToMarketDaily"}""");

        Assert.True(TotalTrades(free) > 0);
        Assert.Equal("Short", free.GetProperty("trades").GetProperty("items")[0].GetProperty("side").GetString());
        Assert.Equal(FinalEquity(free), FinalEquity(zero));
        Assert.True(FinalEquity(borrowing) < FinalEquity(free), $"borrow fee must lower the final equity ({FinalEquity(borrowing)} vs {FinalEquity(free)})");
        Assert.NotEqual(FinalEquity(borrowing), FinalEquity(markToMarket));
    }

    [Fact]
    public async Task ThePriceLimitModeIsAcceptedAndEchoed()
    {
        var dispatcher = await _host.StartAsync();

        using var response = await RunAsync(dispatcher, """{"priceLimitFillMode":"RejectIfAtLimitPrice"}""");

        Assert.Equal("RejectIfAtLimitPrice", Ok(response).GetProperty("effectiveSettings").GetProperty("account").GetProperty("priceLimitFillMode").GetString());
    }

    // ---- refusals ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"maxVolumeParticipationRatio":0}""", "account.maxVolumeParticipationRatio")]
    [InlineData("""{"maxVolumeParticipationRatio":-0.1}""", "account.maxVolumeParticipationRatio")]
    [InlineData("""{"maxVolumeParticipationRatio":1.5}""", "account.maxVolumeParticipationRatio")]
    [InlineData("""{"maxVolumeParticipationRatio":null}""", "account.maxVolumeParticipationRatio")]
    [InlineData("""{"maxVolumeParticipationRatio":"0.5"}""", "account.maxVolumeParticipationRatio")]
    [InlineData("""{"shortBorrowAnnualRate":-0.01}""", "account.shortBorrowAnnualRate")]
    [InlineData("""{"shortBorrowAnnualRate":1.01}""", "account.shortBorrowAnnualRate")]
    [InlineData("""{"shortBorrowAnnualRate":"high"}""", "account.shortBorrowAnnualRate")]
    [InlineData("""{"volumeConstraintMode":"Nope"}""", "account.volumeConstraintMode")]
    [InlineData("""{"volumeConstraintMode":1}""", "account.volumeConstraintMode")]
    [InlineData("""{"borrowFeeBasisMode":"Daily"}""", "account.borrowFeeBasisMode")]
    [InlineData("""{"priceLimitFillMode":"Maybe"}""", "account.priceLimitFillMode")]
    public async Task ARefusedValueIsInvalidArgumentNamingTheKey(string account, string param)
    {
        var dispatcher = await _host.StartAsync();

        using var response = await RunAsync(dispatcher, account);

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal(param, BacktestDiagnosticsTestHost.Param(response));
    }

    [Fact]
    public async Task AnEnumRefusalListsTheMembersReadFromTheEnumsAndTheBoundariesAreAccepted()
    {
        var dispatcher = await _host.StartAsync();

        using var refused = await RunAsync(dispatcher, """{"priceLimitFillMode":"Maybe"}""");
        using var whole = await RunAsync(dispatcher, """{"maxVolumeParticipationRatio":1,"shortBorrowAnnualRate":1}""");

        string hint = McpTestSupport.Structured(refused).GetProperty("hint").GetString()!;
        Assert.All(Enum.GetNames<PriceLimitFillMode>(), name => Assert.Contains(name, hint));
        Assert.False(McpTestSupport.IsError(whole), McpTestSupport.Structured(whole).GetRawText());
    }

    // ---- the other three tools --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOptimizeAndWalkForwardToolsAcceptTheKeys_AndTheyReachTheEngine()
    {
        var dispatcher = await _host.StartAsync();
        decimal volume = await StoredBarVolumeAsync(dispatcher);
        string capped = (10m / volume / 2m).ToString(CultureInfo.InvariantCulture);
        string sma = """{"indicator":"SMA","parameters":{"Period":{"$param":"period"}}}""";
        string conditions = $$$"""{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{{{sma}}}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{{{sma}}}}}""";
        string ranges = """[{"name":"period","start":3,"end":6,"step":1}]""";
        string optimize(string account) => $$$"""{"symbol":"MSFT","conditions":{{{conditions}}},"parameterRanges":{{{ranges}}},"objective":"totalPnL"{{{BacktestDiagnosticsTestHost.Period}}},"account":{{{account}}}}""";
        string walk(string account) => $$$"""{"symbol":"MSFT","conditions":{{{conditions}}},"parameterRanges":{{{ranges}}},"objective":"totalPnL","windows":{"generator":{"mode":"Rolling","inSampleBars":100,"outOfSampleBars":40,"stepBars":40}},"account":{{{account}}}}""";

        using var freeOptimize = (await McpTestSupport.CallAsync(dispatcher, "sa_optimize_backtest", optimize("""{"sizingParameter":10}""")))!;
        using var cappedOptimize = (await McpTestSupport.CallAsync(dispatcher, "sa_optimize_backtest", optimize($$"""{"sizingParameter":10,"maxVolumeParticipationRatio":{{capped}},"volumeConstraintMode":"RejectIfExceeds"}""")))!;
        using var freeWalk = (await McpTestSupport.CallAsync(dispatcher, "sa_run_walk_forward_analysis", walk("""{"sizingParameter":10}""")))!;
        using var cappedWalk = (await McpTestSupport.CallAsync(dispatcher, "sa_run_walk_forward_analysis", walk($$"""{"sizingParameter":10,"maxVolumeParticipationRatio":{{capped}},"volumeConstraintMode":"RejectIfExceeds"}""")))!;

        JsonElement free = Ok(freeOptimize);
        JsonElement cap = Ok(cappedOptimize);
        Assert.True(free.GetProperty("ranking").EnumerateArray().Any(r => r.GetProperty("totalTrades").GetInt32() > 0));
        Assert.All(cap.GetProperty("ranking").EnumerateArray(), r => Assert.Equal(0, r.GetProperty("totalTrades").GetInt32()));
        Assert.NotEqual(Ok(freeWalk).GetRawText(), Ok(cappedWalk).GetRawText());
    }

    [Fact]
    public async Task ThePortfolioToolAcceptsTheKeys_AndEverySleeveRunsWithThem()
    {
        var dispatcher = await _host.StartAsync();
        decimal volume = await StoredBarVolumeAsync(dispatcher);
        string capped = (10m / volume / 2m).ToString(CultureInfo.InvariantCulture);
        string run(string account) => $$"""{"symbols":["MSFT"],"conditions":{{BacktestDiagnosticsTestHost.Conditions}}{{BacktestDiagnosticsTestHost.Period}},"account":{{account}}}""";

        using var free = (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest", run("""{"initialCapital":1000000,"sizingParameter":10}""")))!;
        using var cap = (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest", run($$"""{"initialCapital":1000000,"sizingParameter":10,"maxVolumeParticipationRatio":{{capped}},"volumeConstraintMode":"RejectIfExceeds"}""")))!;

        JsonElement before = Ok(free);
        JsonElement after = Ok(cap);
        Assert.True(PooledTradeCount(before) > 0);
        Assert.Equal(0, PooledTradeCount(after)); // the sleeve configuration carries the cap into the engine
    }

    private static int PooledTradeCount(JsonElement result)
    {
        foreach (JsonProperty property in result.EnumerateObject())
        {
            if (property.Name == "pooledTradeCount")
            {
                return property.Value.GetInt32();
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    return PooledTradeCount(property.Value);
                }
                catch (KeyNotFoundException)
                {
                    // not in this branch
                }
            }
        }

        throw new KeyNotFoundException("pooledTradeCount");
    }

    // ---- the spelling of a value never changes the echo, the hash or the fingerprint ---------------------------------------------

    [Fact]
    public async Task TheSpellingOfAnEnumNameChangesNeitherTheEchoNorTheHashNorTheFingerprint_OfARunOrOfAPortfolioSleeve()
    {
        var dispatcher = await _host.StartAsync();
        decimal volume = await StoredBarVolumeAsync(dispatcher);
        string ratio = (10m / volume / 2m).ToString(CultureInfo.InvariantCulture);
        string account(string constraint, string basis, string fill)
            => $$"""{"sizingParameter":10,"maxVolumeParticipationRatio":{{ratio}},"volumeConstraintMode":"{{constraint}}","shortBorrowAnnualRate":0.02,"borrowFeeBasisMode":"{{basis}}","priceLimitFillMode":"{{fill}}"}""";
        string[] spellings =
        {
            account("ClampAndCancelRemainder", "MarkToMarketDaily", "RejectIfAtLimitPrice"),
            account("clampandcancelremainder", "marktomarketdaily", "rejectifatlimitprice"),
            account("  CLAMPANDCANCELREMAINDER ", "MARKTOMARKETDAILY", " REJECTIFATLIMITPRICE"),
        };

        var runs = new List<JsonElement>();
        var sleeves = new List<string?>();
        var documents = new List<JsonDocument>();
        foreach (string spelling in spellings)
        {
            JsonDocument single = await RunAsync(dispatcher, spelling);
            documents.Add(single);
            runs.Add(Ok(single));
            JsonDocument portfolio = (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest", $$"""{"symbols":["MSFT"],"conditions":{{BacktestDiagnosticsTestHost.Conditions}}{{BacktestDiagnosticsTestHost.Period}},"account":{{spelling}}}"""))!;
            documents.Add(portfolio);
            sleeves.Add(Ok(portfolio).GetProperty("symbolResults")[0].GetProperty("runFingerprint").GetProperty("sha256").GetString());
        }

        try
        {
            foreach (JsonElement run in runs)
            {
                JsonElement echo = run.GetProperty("effectiveSettings").GetProperty("account");
                Assert.Equal("ClampAndCancelRemainder", echo.GetProperty("volumeConstraintMode").GetString());
                Assert.Equal("MarkToMarketDaily", echo.GetProperty("borrowFeeBasisMode").GetString());
                Assert.Equal("RejectIfAtLimitPrice", echo.GetProperty("priceLimitFillMode").GetString());
                Assert.Equal(runs[0].GetProperty("reproducibilityHash").GetString(), run.GetProperty("reproducibilityHash").GetString());
                Assert.Equal(runs[0].GetProperty("runFingerprint").GetProperty("sha256").GetString(), run.GetProperty("runFingerprint").GetProperty("sha256").GetString());
                Assert.Equal(runs[0].GetProperty("effectiveSettings").GetRawText(), run.GetProperty("effectiveSettings").GetRawText());
            }

            Assert.All(sleeves, sha => Assert.Equal(sleeves[0], sha));
            Assert.False(string.IsNullOrEmpty(sleeves[0]));
        }
        finally
        {
            documents.ForEach(d => d.Dispose());
        }
    }

    [Fact]
    public async Task TheDecimalScaleOfANumberIsPartOfTheFingerprintButNotOfTheReproducibilityHash()
    {
        var dispatcher = await _host.StartAsync();

        using var omitted = await RunAsync(dispatcher, """{"sizingParameter":10}""");
        using var zero = await RunAsync(dispatcher, """{"sizingParameter":10,"shortBorrowAnnualRate":0}""");
        using var zeroPointZero = await RunAsync(dispatcher, """{"sizingParameter":10,"shortBorrowAnnualRate":0.0}""");
        using var twoPercent = await RunAsync(dispatcher, """{"sizingParameter":10,"shortBorrowAnnualRate":0.02}""");
        using var twoPercentPadded = await RunAsync(dispatcher, """{"sizingParameter":10,"shortBorrowAnnualRate":0.020}""");

        static string Fingerprint(JsonDocument d) => Ok(d).GetProperty("runFingerprint").GetProperty("sha256").GetString()!;
        static string Reproducibility(JsonDocument d) => Ok(d).GetProperty("reproducibilityHash").GetString()!;
        Assert.Equal(Fingerprint(omitted), Fingerprint(zero));
        Assert.NotEqual(Fingerprint(omitted), Fingerprint(zeroPointZero)); // the engine encodes the decimal scale and claims no semantic normalisation
        Assert.NotEqual(Fingerprint(twoPercent), Fingerprint(twoPercentPadded));
        // reproducibilityHash holds the results and the nine older settings, not the five values: the spelling of the number does not reach it
        Assert.Equal(Reproducibility(omitted), Reproducibility(zero));
        Assert.Equal(Reproducibility(omitted), Reproducibility(zeroPointZero));
        Assert.Equal(Reproducibility(twoPercent), Reproducibility(twoPercentPadded));
    }

    // ---- schema and description -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDescriptionNamesEveryKeyAndTheSemanticNote()
    {
        var dispatcher = await _host.StartAsync();

        string response = (await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!;
        using JsonDocument listing = JsonDocument.Parse(response);
        string description = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "sa_run_backtest").GetProperty("description").GetString()!;

        Assert.Contains("docs/MCP/tools/sa_run_backtest.md", description, StringComparison.Ordinal);
        string doc = System.IO.File.ReadAllText(System.IO.Path.Combine(StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "docs", "MCP", "tools", "sa_run_backtest.md"));
        Assert.All(McpBacktestSettingsExposure.ExecutionRealismKeys, key => Assert.Contains(key, doc));
        Assert.Contains($"has no effect without {McpBacktestSettingsExposure.MaxVolumeParticipationRatioKey}", doc);
        Assert.Contains("same reproducibilityHash", doc);
        Assert.Contains("same runFingerprint when the number is written with the same number of decimals", doc);
        Assert.Contains("the fingerprint encodes the decimal scale", doc);
        Assert.All(Enum.GetNames<VolumeConstraintMode>().Concat(Enum.GetNames<BorrowFeeBasisMode>()).Concat(Enum.GetNames<PriceLimitFillMode>()), name => Assert.Contains(name, doc));
    }
}
