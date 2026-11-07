using System.Text.Json;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>A tool that returns an image next to its JSON (a real implementation of the image tool interface; no project data type is faked).</summary>
internal sealed class FakeImageTool : IMcpImageTool
{
    private readonly Func<ToolImageResult> _result;

    public FakeImageTool(string name, Func<ToolImageResult> result)
    {
        Name = name;
        _result = result;
    }

    public string Name { get; }

    public string Description => "Test image tool.";

    public string InputSchemaJson => ToolSchema.Object().Build();

    public bool IsHeavy => false;

    public Task<ToolImageResult> InvokeImageAsync(JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult(_result());
}

/// <summary>The protocol side of an image tool (plan 15, T3a): the image block, the unchanged shape of every other tool, and the size limit.</summary>
public sealed class ImageToolResultTests
{
    private static readonly byte[] SamplePng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0xFF };

    [Fact]
    public async Task AnImageToolReturnsTheJsonAsTextFirstAndThePngAsAnImageBlockAfterIt()
    {
        const string Json = """{"style":"test","widthPx":12}""";
        var tool = new FakeImageTool("sa_fake_image", () => new ToolImageResult(Json, SamplePng));
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { tool });

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_fake_image"))!;

        Assert.False(McpTestSupport.IsError(response));
        JsonElement content = McpTestSupport.Result(response).GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal(Json, content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("image/png", content[1].GetProperty("mimeType").GetString());
        Assert.Equal(SamplePng, Convert.FromBase64String(content[1].GetProperty("data").GetString()!));
        Assert.Equal(12, McpTestSupport.Structured(response).GetProperty("widthPx").GetInt32());
    }

    [Fact]
    public async Task AToolWithoutAnImageStillReturnsExactlyOneTextBlock()
    {
        var tool = new FakeTool("sa_fake_plain", (_, _) => Task.FromResult("""{"value":1}"""));
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { tool });

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_fake_plain"))!;

        JsonElement content = McpTestSupport.Result(response).GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task TheJsonOnlyEntryOfAnImageToolReturnsTheStructuredJsonWithoutTheImage()
    {
        IMcpTool tool = new FakeImageTool("sa_fake_image", () => new ToolImageResult("""{"a":1}""", SamplePng));

        string json = await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.Equal("""{"a":1}""", json);
    }

    [Fact]
    public async Task AFailureOfAnImageToolIsAnOrdinaryErrorResultWithoutAnImage()
    {
        var tool = new FakeImageTool("sa_fake_image", () => throw new ToolException(ToolErrorCodes.InvalidArgument, "bad", "barCount"));
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { tool });

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_fake_image"))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(1, McpTestSupport.Result(response).GetProperty("content").GetArrayLength());
    }

    [Fact]
    public async Task AnImageWhoseBase64FormAloneExceedsTheResponseLimitIsRefusedBeforeTheTextIsBuilt()
    {
        var limits = new McpLimits { MaxResponseBytes = 64 * 1024 };
        byte[] png = new byte[60_000];
        var tool = new FakeImageTool("sa_fake_image", () => new ToolImageResult("{}", png));
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { tool }, limits);

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_fake_image"))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(response));
        Assert.Contains("barCount", McpTestSupport.Structured(response).GetProperty("hint").GetString());
    }

    [Fact]
    public async Task AnImageUnderTheEstimateButOverTheLimitByTheRealTextIsStillRefused_AndNothingIsTruncated()
    {
        // 49,100 bytes -> 65,468 Base64 characters: the estimate (65,472 with the JSON twice) fits 64 KiB, the real result (with the block wrappers) does not.
        var limits = new McpLimits { MaxResponseBytes = 64 * 1024 };
        byte[] png = new byte[49_100];
        var tool = new FakeImageTool("sa_fake_image", () => new ToolImageResult("{}", png));
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { tool }, limits);

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_fake_image"))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(response));
    }

    [Fact]
    public async Task AnImageThatFitsTheLimitIsReturnedWhole()
    {
        var limits = new McpLimits { MaxResponseBytes = 64 * 1024 };
        byte[] png = new byte[30_000];
        var tool = new FakeImageTool("sa_fake_image", () => new ToolImageResult("{}", png));
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { tool }, limits);

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_fake_image"))!;

        Assert.False(McpTestSupport.IsError(response));
        JsonElement content = McpTestSupport.Result(response).GetProperty("content");
        Assert.Equal(30_000, Convert.FromBase64String(content[1].GetProperty("data").GetString()!).Length);
    }
}
