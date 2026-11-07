using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SkiaSharp;
using StockAnalyzer.Core.Rendering;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Plan 15, T3b: sa_render_chart_image through the real composition on a stored data tree: the image block and the mapping, the proofs (the close row of every bar has the
/// up colour at the reported position; an overlay is warmed up over the whole window and equals sa_calculate_indicators), the validation and the hints (rules R-1 and R-4),
/// the markers, the limits and the response size.
/// </summary>
public sealed class RenderChartImageToolTests : IDisposable
{
    private const int StoredDays = 400;
    private static readonly DateTime FirstDay = new(2024, 1, 1);
    private static readonly DateTime LastDay = FirstDay.AddDays(StoredDays - 1);
    private const string Frame = "\"symbol\":\"MSFT\",\"timeFrame\":\"D1\"";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] limitOverrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> RenderAsync(McpMessageDispatcher dispatcher, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_render_chart_image", $"{{{Frame}{extra}}}"))!;

    private static byte[] PngOf(JsonDocument response)
    {
        JsonElement content = McpTestSupport.Result(response).GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("image/png", content[1].GetProperty("mimeType").GetString());
        return Convert.FromBase64String(content[1].GetProperty("data").GetString()!);
    }

    private static SKColor Pixel(SKBitmap bitmap, double x, double y) => bitmap.GetPixel((int)Math.Floor(x), (int)Math.Floor(y));

    private static decimal?[] Values(JsonElement array)
        => array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()).ToArray();

    private static string Param(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("param").GetString()!;

    private static string Hint(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("hint").GetString()!;

    [Fact]
    public async Task AnImageOfTheStoredBarsComesBackAsPngWithTheMappingAndTheStyle()
    {
        using JsonDocument response = await RenderAsync(await StartAsync(), ",\"barCount\":30");

        Assert.False(McpTestSupport.IsError(response));
        byte[] png = PngOf(response);
        using SKBitmap bitmap = SKBitmap.Decode(png);
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(1200, bitmap.Width);
        Assert.Equal(800, bitmap.Height);
        Assert.Equal("AiRecognitionV1", result.GetProperty("style").GetString());
        Assert.Equal("MSFT", result.GetProperty("symbol").GetString());
        Assert.Equal(1200, result.GetProperty("widthPx").GetInt32());
        Assert.Equal(30, result.GetProperty("barCountDrawn").GetInt32());
        Assert.Equal(LastDay.AddDays(-29).ToString("yyyy-MM-dd"), result.GetProperty("firstBarDate").GetString());
        Assert.Equal(LastDay.ToString("yyyy-MM-dd"), result.GetProperty("lastBarDate").GetString());
        Assert.Equal(30, result.GetProperty("bars").GetArrayLength());
        Assert.Equal(0, result.GetProperty("barsOmittedCount").GetInt32());
        Assert.Equal(new[] { "price", "volume" }, result.GetProperty("panels").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(0, result.GetProperty("legend").GetArrayLength());
        Assert.Equal(AiChartImageStyle.UpHex, result.GetProperty("colors").GetProperty("upHex").GetString());
        Assert.Equal(AiChartImageStyle.DojiHex, result.GetProperty("colors").GetProperty("dojiHex").GetString());
        Assert.True(result.TryGetProperty("units", out _));
        Assert.Contains("Do not read prices", result.GetProperty("notes")[0].GetString());
        Assert.Equal(result.GetRawText(), McpTestSupport.Result(response).GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task TheCloseRowOfEveryDrawnBarHasTheUpColourAtTheReportedPositionAndTheCloseYMatchesTheAxes()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument candles = (await McpTestSupport.CallAsync(dispatcher, "sa_get_candles", $"{{{Frame},\"count\":40}}"))!;
        decimal[] closes = McpTestSupport.Structured(candles).GetProperty("candles").EnumerateArray().Select(c => c.GetProperty("close").GetDecimal()).ToArray();
        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":40");
        using SKBitmap bitmap = SKBitmap.Decode(PngOf(response));
        JsonElement result = McpTestSupport.Structured(response);
        JsonElement axes = result.GetProperty("axes");
        decimal min = axes.GetProperty("priceMin").GetDecimal();
        decimal max = axes.GetProperty("priceMax").GetDecimal();
        double top = axes.GetProperty("pricePlotTopPx").GetDouble();
        double bottom = axes.GetProperty("pricePlotBottomPx").GetDouble();
        SKColor up = AiChartImageStyle.Color(AiChartImageStyle.UpHex);

        JsonElement[] bars = result.GetProperty("bars").EnumerateArray().ToArray();
        Assert.Equal(40, bars.Length);
        for (int i = 0; i < bars.Length; i++)
        {
            double expectedY = top + ((bottom - top) * (1 - (double)((closes[i] - min) / (max - min))));
            Assert.InRange(Math.Abs(bars[i].GetProperty("closeYPx").GetDouble() - expectedY), 0, 0.006);
            Assert.Equal(up, Pixel(bitmap, bars[i].GetProperty("xPx").GetDouble(), bars[i].GetProperty("closeYPx").GetDouble()));
        }
    }

    [Fact]
    public async Task AnOverlayIsCalculatedOverTheWholeWindowSoItIsAlreadyWarmedUpAtTheFirstDrawnBar_AndEqualsSaCalculateIndicators()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument calculated = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_calculate_indicators",
            $"{{{Frame},\"indicators\":[{{\"id\":\"e\",\"type\":\"EMA\",\"parameters\":{{\"Period\":50}}}}],\"outputTailCount\":30}}"))!;
        decimal?[] ema = Values(McpTestSupport.Structured(calculated).GetProperty("results").GetProperty("e").GetProperty("series").GetProperty("EMA"));
        Assert.Equal(30, ema.Length);
        Assert.All(ema, v => Assert.NotNull(v));

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":30,\"overlays\":[{\"id\":\"e\",\"type\":\"EMA\",\"parameters\":{\"Period\":50}}]");
        using SKBitmap bitmap = SKBitmap.Decode(PngOf(response));
        JsonElement result = McpTestSupport.Structured(response);
        JsonElement axes = result.GetProperty("axes");
        decimal min = axes.GetProperty("priceMin").GetDecimal();
        decimal max = axes.GetProperty("priceMax").GetDecimal();
        double top = axes.GetProperty("pricePlotTopPx").GetDouble();
        double bottom = axes.GetProperty("pricePlotBottomPx").GetDouble();
        SKColor line = AiChartImageStyle.Color(AiChartImageStyle.MovingAverageShortHex);
        JsonElement[] bars = result.GetProperty("bars").EnumerateArray().ToArray();

        // The first, a middle and the last drawn bar: the line passes through the row of the value of sa_calculate_indicators at that bar.
        foreach (int i in new[] { 0, 14, 29 })
        {
            double y = top + ((bottom - top) * (1 - (double)((ema[i]!.Value - min) / (max - min))));
            Assert.Equal(line, Pixel(bitmap, bars[i].GetProperty("xPx").GetDouble(), Math.Round(y, 2)));
        }

        JsonElement legend = result.GetProperty("legend");
        Assert.Equal(1, legend.GetArrayLength());
        Assert.Equal("e", legend[0].GetProperty("id").GetString());
        Assert.Equal(AiChartImageStyle.MovingAverageShortHex, legend[0].GetProperty("colorHex").GetString());
        Assert.Equal("Solid", legend[0].GetProperty("lineStyle").GetString());
    }

    [Fact]
    public async Task AnOscillatorGetsAPanelOfItsOwn_AndAnIndicatorInTheWrongListIsRefusedWithTheRightListNamed()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument good = await RenderAsync(dispatcher, ",\"oscillators\":[{\"id\":\"r\",\"type\":\"RSI\"}],\"overlays\":[{\"id\":\"s\",\"type\":\"SMA\",\"parameters\":{\"Period\":10}}]");
        Assert.False(McpTestSupport.IsError(good));
        JsonElement panels = McpTestSupport.Structured(good).GetProperty("panels");
        Assert.Equal(new[] { "price", "volume", "r" }, panels.EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal("Oscillator", panels[2].GetProperty("kind").GetString());

        using JsonDocument rsiAsOverlay = await RenderAsync(dispatcher, ",\"overlays\":[{\"id\":\"r\",\"type\":\"RSI\"}]");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(rsiAsOverlay));
        Assert.Equal("overlays[0].type", Param(rsiAsOverlay));
        Assert.Contains("oscillators", Hint(rsiAsOverlay));

        using JsonDocument smaAsOscillator = await RenderAsync(dispatcher, ",\"oscillators\":[{\"id\":\"s\",\"type\":\"SMA\"}]");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(smaAsOscillator));
        Assert.Equal("oscillators[0].type", Param(smaAsOscillator));
        Assert.Contains("overlays", Hint(smaAsOscillator));
    }

    [Theory]
    [InlineData("Ichimoku")]
    [InlineData("ParabolicSAR")]
    [InlineData("VolumeProfile")]
    public async Task ATypeTheChartDrawsInASpecialWayIsRefusedWithAHint(string type)
    {
        using JsonDocument response = await RenderAsync(await StartAsync(), $",\"overlays\":[{{\"id\":\"x\",\"type\":\"{type}\"}}]");

        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("overlays[0].type", Param(response));
        Assert.Contains("sa_calculate_indicators", Hint(response));
    }

    [Fact]
    public async Task AnIndicatorThatLooksAheadIsRefusedWhereverItIsListed_AndTheHintNamesTheToolsThatCanHelp()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        const string zigzag = "{\"id\":\"zz\",\"type\":\"ZigZag\",\"parameters\":{\"Threshold\":3}}";

        using JsonDocument asOverlay = await RenderAsync(dispatcher, $",\"overlays\":[{zigzag}]");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(asOverlay));
        Assert.Equal("overlays[0].type", Param(asOverlay));
        Assert.Contains("'zz' (ZigZag) looks ahead", McpTestSupport.Structured(asOverlay).GetProperty("message").GetString());
        Assert.Contains("sa_calculate_indicators", Hint(asOverlay));
        Assert.Contains("nonCausalSeries", Hint(asOverlay));
        Assert.Contains("sa_analyze_market_structure", Hint(asOverlay));

        // Looking ahead is checked before the list: the right list is not suggested for an indicator that cannot be drawn anywhere.
        using JsonDocument asOscillator = await RenderAsync(dispatcher, $",\"oscillators\":[{zigzag}]");
        Assert.Equal("oscillators[0].type", Param(asOscillator));
        Assert.Contains("looks ahead", McpTestSupport.Structured(asOscillator).GetProperty("message").GetString());
        Assert.DoesNotContain("Put it in", Hint(asOscillator));
    }

    [Fact]
    public async Task AnIndicatorThatTakesAnInputThatLooksAheadIsRefusedAtTheKeyThatNamesTheInput()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        const string zigzag = "{\"id\":\"zz\",\"type\":\"ZigZag\"}";
        const string smaOfZigzag = "{\"id\":\"sma\",\"type\":\"SMA\",\"parameters\":{\"Period\":5},\"sourceIndicatorId\":\"zz\"}";

        // The array is checked from the front: the ZigZag itself first, the indicator that uses it when it comes first.
        using JsonDocument zigzagFirst = await RenderAsync(dispatcher, $",\"overlays\":[{zigzag},{smaOfZigzag}]");
        Assert.Equal("overlays[0].type", Param(zigzagFirst));

        using JsonDocument userFirst = await RenderAsync(dispatcher, $",\"overlays\":[{smaOfZigzag},{zigzag}]");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(userFirst));
        Assert.Equal("overlays[0].sourceIndicatorId", Param(userFirst));
        string message = McpTestSupport.Structured(userFirst).GetProperty("message").GetString()!;
        Assert.Contains("'sma' (SMA)", message);
        Assert.Contains("'zz' (ZigZag)", message);
        Assert.Contains("looks ahead", message);

        // Two steps away: the message still names the indicator that looks ahead, the param is the first input of the indicator that is refused.
        using JsonDocument twoSteps = await RenderAsync(
            dispatcher,
            $",\"overlays\":[{{\"id\":\"ema\",\"type\":\"EMA\",\"parameters\":{{\"Period\":5}},\"sourceIndicatorId\":\"sma\"}},{smaOfZigzag},{zigzag}]");
        Assert.Equal("overlays[0].sourceIndicatorId", Param(twoSteps));
        Assert.Contains("'zz' (ZigZag)", McpTestSupport.Structured(twoSteps).GetProperty("message").GetString());

        // The dynamic-period input is a chain too; the source input is named first when both look ahead.
        using JsonDocument dynamic = await RenderAsync(
            dispatcher,
            $",\"overlays\":[{{\"id\":\"sma\",\"type\":\"SMA\",\"parameters\":{{\"Period\":5}},\"dynamicPeriodIndicatorId\":\"zz\"}},{zigzag}]");
        Assert.Equal("overlays[0].dynamicPeriodIndicatorId", Param(dynamic));
        using JsonDocument both = await RenderAsync(
            dispatcher,
            $",\"overlays\":[{{\"id\":\"sma\",\"type\":\"SMA\",\"parameters\":{{\"Period\":5}},\"sourceIndicatorId\":\"zz\",\"dynamicPeriodIndicatorId\":\"zz\"}},{zigzag}]");
        Assert.Equal("overlays[0].sourceIndicatorId", Param(both));

        // A chain of indicators that look back only is still drawn.
        using JsonDocument causal = await RenderAsync(
            dispatcher,
            ",\"barCount\":30,\"overlays\":[{\"id\":\"ema\",\"type\":\"EMA\",\"parameters\":{\"Period\":5}},{\"id\":\"sma\",\"type\":\"SMA\",\"parameters\":{\"Period\":5},\"sourceIndicatorId\":\"ema\"}]");
        Assert.False(McpTestSupport.IsError(causal));
    }

    [Fact]
    public async Task AnIndicatorThatCannotBeDrawnIsRefusedBeforeTheSymbolIsLookedUp()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        // An unknown symbol would be SYMBOL_NOT_FOUND once the symbol is looked up; the structure of the request is judged first, so no stored data is touched.
        using JsonDocument lookAhead = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_render_chart_image",
            "{\"symbol\":\"NOSUCH\",\"timeFrame\":\"D1\",\"overlays\":[{\"id\":\"zz\",\"type\":\"ZigZag\"}]}"))!;
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(lookAhead));
        Assert.Equal("overlays[0].type", Param(lookAhead));

        using JsonDocument wrongList = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_render_chart_image",
            "{\"symbol\":\"NOSUCH\",\"timeFrame\":\"D1\",\"overlays\":[{\"id\":\"r\",\"type\":\"RSI\"}]}"))!;
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(wrongList));

        using JsonDocument plain = (await McpTestSupport.CallAsync(dispatcher, "sa_render_chart_image", "{\"symbol\":\"NOSUCH\",\"timeFrame\":\"D1\"}"))!;
        Assert.Equal(ToolErrorCodes.SymbolNotFound, McpTestSupport.ErrorCodeOf(plain));
    }

    [Fact]
    public async Task AnIndicatorWithItsOwnTimeFrameOrASecondSymbolOrASiblingToolArgumentIsRefused()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument ownFrame = await RenderAsync(dispatcher, ",\"overlays\":[{\"id\":\"s\",\"type\":\"SMA\",\"timeFrame\":\"W1\"}]");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(ownFrame));

        using JsonDocument crossTicker = await RenderAsync(dispatcher, ",\"oscillators\":[{\"id\":\"c\",\"type\":\"Correlation\",\"parameters\":{\"ComparisonSymbol\":\"MSFT\"}}]");
        Assert.True(McpTestSupport.IsError(crossTicker));
        Assert.Equal("oscillators[0].type", Param(crossTicker));

        using JsonDocument misnamed = await RenderAsync(dispatcher, ",\"indicators\":[{\"id\":\"s\",\"type\":\"SMA\"}]");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(misnamed));
        Assert.Contains("overlays", Hint(misnamed));

        using JsonDocument derived = await RenderAsync(dispatcher, ",\"derived\":[]");
        Assert.Contains("sa_calculate_indicators", Hint(derived));
    }

    [Fact]
    public async Task TooManyBarsForTheWidthIsRefusedAndTheHintStatesWhatFits()
    {
        using JsonDocument response = await RenderAsync(await StartAsync(), ",\"barCount\":300");

        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("barCount", Param(response));
        // Width 1200: plot 1116 px, 4 px per bar -> 279 bars fit; 300 bars need 300 x 4 + 84 = 1284.
        Assert.Contains("at most 279", Hint(response));
        Assert.Contains("width 1284", Hint(response));
    }

    [Fact]
    public async Task ABarCountThatNeedsMoreThanTheWidthLimitSaysOnlyToReduceTheBarCount()
    {
        using JsonDocument response = await RenderAsync(await StartAsync(), ",\"barCount\":500,\"width\":1920");

        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Contains("Reduce barCount", Hint(response));
        Assert.DoesNotContain("increase width", Hint(response));
    }

    [Fact]
    public async Task AHeightThatCannotHoldThePanelsIsRefusedAndTheHintStatesTheHeightThatDoes()
    {
        int required = AiChartLayout.CheckSize(1200, 100, 200, true, 2).MinHeightPx;
        string oscillators = ",\"oscillators\":[{\"id\":\"r\",\"type\":\"RSI\"},{\"id\":\"m\",\"type\":\"MACD\"}]";
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument tooLow = await RenderAsync(dispatcher, $",\"height\":{required - 1}{oscillators}");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(tooLow));
        Assert.Equal("height", Param(tooLow));
        Assert.Contains($"height {required}", Hint(tooLow));

        using JsonDocument enough = await RenderAsync(dispatcher, $",\"height\":{required}{oscillators}");
        Assert.False(McpTestSupport.IsError(enough));
        Assert.Equal(4, McpTestSupport.Structured(enough).GetProperty("panels").GetArrayLength());
    }

    [Fact]
    public async Task ASizeBelowTheStyleMinimumOrALookbackBelowTheBarCountIsRefused()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument narrow = await RenderAsync(dispatcher, ",\"width\":100");
        Assert.True(McpTestSupport.IsError(narrow));
        using JsonDocument low = await RenderAsync(dispatcher, ",\"height\":100");
        Assert.True(McpTestSupport.IsError(low));

        using JsonDocument lookback = await RenderAsync(dispatcher, ",\"barCount\":50,\"lookbackBars\":10");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(lookback));
        Assert.Equal("lookbackBars", Param(lookback));
    }

    [Fact]
    public async Task AMarkerMustNameTheDateOfADrawnBarExactly_AndIsDrawnUnderTheLowOfItsBar()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string drawnDate = LastDay.AddDays(-10).ToString("yyyy-MM-dd");
        string outsideDate = LastDay.AddDays(-100).ToString("yyyy-MM-dd");

        using JsonDocument outside = await RenderAsync(dispatcher, $",\"barCount\":30,\"markers\":[{{\"date\":\"{outsideDate}\",\"side\":\"Buy\"}}]");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(outside));
        Assert.Equal("markers[0].date", Param(outside));
        Assert.Contains(LastDay.AddDays(-29).ToString("yyyy-MM-dd"), Hint(outside));
        Assert.Contains(LastDay.ToString("yyyy-MM-dd"), Hint(outside));

        using JsonDocument malformed = await RenderAsync(dispatcher, ",\"barCount\":30,\"markers\":[{\"date\":\"10/02/2025\",\"side\":\"Buy\"}]");
        Assert.Equal("markers[0].date", Param(malformed));

        using JsonDocument drawn = await RenderAsync(dispatcher, $",\"barCount\":30,\"markers\":[{{\"date\":\"{drawnDate}\",\"side\":\"Buy\"}}]");
        using SKBitmap bitmap = SKBitmap.Decode(PngOf(drawn));
        JsonElement result = McpTestSupport.Structured(drawn);
        JsonElement bar = result.GetProperty("bars").EnumerateArray().Single(b => b.GetProperty("date").GetString() == drawnDate);
        Assert.Equal(bar.GetProperty("index").GetInt32(), result.GetProperty("markers")[0].GetProperty("barIndex").GetInt32());
        double markerY = Math.Floor(bar.GetProperty("lowYPx").GetDouble()) + 1 + AiChartImageStyle.MarkerGapPx + 4;
        Assert.Equal(AiChartImageStyle.Color(AiChartImageStyle.SignalHex), Pixel(bitmap, bar.GetProperty("xPx").GetDouble(), markerY));
    }

    [Fact]
    public async Task TheEndDateEndsTheWindow_AndWithoutVolumeThereIsOnePanel()
    {
        string endDate = LastDay.AddDays(-20).ToString("yyyy-MM-dd");

        using JsonDocument response = await RenderAsync(await StartAsync(), $",\"barCount\":10,\"endDate\":\"{endDate}\",\"includeVolume\":false,\"background\":\"LightGray\"");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(endDate, result.GetProperty("lastBarDate").GetString());
        Assert.Equal(endDate, result.GetProperty("endDate").GetString());
        Assert.Equal(1, result.GetProperty("panels").GetArrayLength());
        Assert.Equal("LightGray", result.GetProperty("background").GetString());
        using SKBitmap bitmap = SKBitmap.Decode(PngOf(response));
        Assert.Equal(SKColor.Parse(AiChartImageStyle.BackgroundLightGrayHex), bitmap.GetPixel(0, 0));
    }

    [Fact]
    public async Task BarsBeyondTheListingLimitAreCountedNeverSilentlyDropped()
    {
        using JsonDocument response = await RenderAsync(await StartAsync(("MaxImageBarsListed", "10")), ",\"barCount\":40");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(40, result.GetProperty("barCountDrawn").GetInt32());
        Assert.Equal(10, result.GetProperty("bars").GetArrayLength());
        Assert.Equal(30, result.GetProperty("barsOmittedCount").GetInt32());
        Assert.Equal(39, result.GetProperty("bars")[9].GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task AnImageOverTheResponseSizeLimitIsRefusedAndNothingIsCut()
    {
        // 400 bars at 1920 x 1200 with volume: the PNG alone is larger than the 64 KiB band minimum once Base64 encoded.
        using JsonDocument response = await RenderAsync(await StartAsync(("MaxResponseBytes", "65536")), ",\"barCount\":400,\"width\":1920,\"height\":1200");

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(1, McpTestSupport.Result(response).GetProperty("content").GetArrayLength());
    }

    [Fact]
    public async Task TheSameRequestGivesTheSamePngAndAnUnknownSymbolIsAnOrdinaryError()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument first = await RenderAsync(dispatcher, ",\"barCount\":60,\"overlays\":[{\"id\":\"s\",\"type\":\"SMA\",\"parameters\":{\"Period\":5}}]");
        using JsonDocument second = await RenderAsync(dispatcher, ",\"barCount\":60,\"overlays\":[{\"id\":\"s\",\"type\":\"SMA\",\"parameters\":{\"Period\":5}}]");
        Assert.Equal(PngOf(first), PngOf(second));

        using JsonDocument unknown = (await McpTestSupport.CallAsync(dispatcher, "sa_render_chart_image", "{\"symbol\":\"NOSUCH\",\"timeFrame\":\"D1\"}"))!;
        Assert.Equal(ToolErrorCodes.SymbolNotFound, McpTestSupport.ErrorCodeOf(unknown));
    }

    [Fact]
    public async Task AnIndicatorWithNoValueInTheDrawnWindowIsNamedInTheNotesAndNotDrawn()
    {
        // SMA(5000) needs more bars than are stored: every value is null, so there is nothing to draw.
        using JsonDocument response = await RenderAsync(await StartAsync(), ",\"barCount\":30,\"overlays\":[{\"id\":\"long\",\"type\":\"SMA\",\"parameters\":{\"Period\":5000}}]");

        if (McpTestSupport.IsError(response))
        {
            // A period above the parameter's own range is refused by the binder; either way nothing is silently drawn.
            Assert.Equal(ToolErrorCodes.InvalidIndicatorParameter, McpTestSupport.ErrorCodeOf(response));
            return;
        }

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(0, result.GetProperty("legend").GetArrayLength());
        Assert.Contains(result.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("'long'", StringComparison.Ordinal));
    }

    [Fact]
    public void TheToolIsListedAsAnImageToolWithItsLimitsInTheSchema()
    {
        var limits = new StockAnalyzer.McpServer.Limits.McpLimits();
        var tool = (IMcpTool)new RenderChartImageTool(new EmptyReadOnlyStockAnalyzerGateway(), null!, null!, null!, null!, limits);

        using JsonDocument schema = JsonDocument.Parse(tool.InputSchemaJson);
        JsonElement properties = schema.RootElement.GetProperty("properties");
        Assert.True(tool is IMcpImageTool);
        Assert.Equal(limits.MaxChartImageWidth, properties.GetProperty("width").GetProperty("maximum").GetInt64());
        Assert.Equal(limits.DefaultChartImageHeight, properties.GetProperty("height").GetProperty("default").GetInt64());
        Assert.Equal(limits.DefaultChartImageBars, properties.GetProperty("barCount").GetProperty("default").GetInt64());
        Assert.Equal(new[] { "symbol", "timeFrame" }, schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }
}
