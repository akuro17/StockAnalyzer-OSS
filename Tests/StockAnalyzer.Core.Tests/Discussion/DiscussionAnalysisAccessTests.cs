#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// MCP discussion optimization, Run B: the <c>paths</c> argument of <c>sa_discussion_get_analysis</c> (grammar of the path, resolution, exact number text, the shape of the answer)
/// and <c>includeContexts</c> of the status (the list of the contexts of one discussion, over the pipe too).
/// </summary>
public sealed class DiscussionAnalysisAccessTests : IDisposable
{
    private const string Result = """
        {"summary":{"latest":1234.567890123456789012345,"other":1E+2,"neg":-0.0,"p":0.10,"nul":null,"text":"x"},
         "series":[{"value":1.5},{"value":2.5}],
         "matchedValues":{"7203-T":{"left":1,"right":2},"^N225":{"a":3},"CL=F":{"a":4},"a.b":{"c":5},"d\"e":6},
         "0":"zero","list":[10,20]}
        """;

    private readonly HubRig _rig = new();
    private readonly DiscussionHubGateway _gateway;

    public DiscussionAnalysisAccessTests()
    {
        _gateway = new DiscussionHubGateway(_rig.Hub, _rig.Settings);
    }

    public void Dispose() => _rig.Dispose();

    private (string Id, string Token) OpenWithContext(string result = Result)
    {
        string id = _rig.NewSession();
        string token = _rig.Join(id, "Alice").Token;
        _rig.Hub.AttachCommit(id, token, "sa_x", "{}", result, null);
        return (id, token);
    }

    private async Task<DiscussionReply> GetAsync(string id, string token, object? paths, bool? includeResult = null)
    {
        var arguments = new Dictionary<string, object?> { ["sessionId"] = id, ["participantToken"] = token, ["contextId"] = 1 };
        if (paths is not null)
        {
            arguments["paths"] = paths;
        }

        if (includeResult is { } flag)
        {
            arguments["includeResult"] = flag;
        }

        return await _gateway.ExecuteAsync(new DiscussionRequest(DiscussionOperations.GetAnalysis, "test", JsonSerializer.Serialize(arguments)), CancellationToken.None);
    }

    private static IReadOnlyList<DiscussionJsonPathExtractor.Segment[]> Parse(params string[] paths) => DiscussionJsonPathExtractor.ParseAll(paths);

    // ---- The grammar ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANameIsAPropertyAnIndexIsAnArrayPositionAndAQuotedKeyIsAPropertyEvenWhenItLooksLikeANumber()
    {
        DiscussionJsonPathExtractor.Segment[] segments = Parse("a.0", "a[0]", "a[\"0\"]").Select(s => s[1]).ToArray();

        Assert.Equal(new DiscussionJsonPathExtractor.Segment("0", 0), segments[0]);
        Assert.Equal(new DiscussionJsonPathExtractor.Segment(null, 0), segments[1]);
        Assert.Equal(new DiscussionJsonPathExtractor.Segment("0", 0), segments[2]);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a.b")]
    [InlineData("A_1.b2.C")]
    [InlineData("a[0]")]
    [InlineData("a[999999]")]
    [InlineData("a[0][1]")]
    [InlineData("a.0")]
    [InlineData("a[\"b\"]")]
    [InlineData("a[\"b.c\"]")]
    [InlineData("a[\"7203-T\"].x")]
    [InlineData("a[\"^N225\"]")]
    [InlineData("a[\"CL=F\"]")]
    [InlineData("a[\"\\\"\"]")]
    [InlineData("a[\"\\\\\"]")]
    [InlineData("a[\"\u65E5\u672C\"]")]
    [InlineData("a[\"\U0001F4C8\"]")]
    [InlineData("a[\"x\"][\"y\"]")]
    public void APathThatKeepsToTheGrammarIsAccepted(string path)
    {
        Assert.Single(Parse(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[0]")]
    [InlineData("[\"a\"]")]
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("a..b")]
    [InlineData("a.[0]")]
    [InlineData("a.[\"x\"]")]
    [InlineData("a[")]
    [InlineData("a[]")]
    [InlineData("a[01]")]
    [InlineData("a[1000000]")]
    [InlineData("a[-1]")]
    [InlineData("a[1.5]")]
    [InlineData("a[\"\"]")]
    [InlineData("a[\"x")]
    [InlineData("a[\"x\"")]
    [InlineData("a[\"x\"]]")]
    [InlineData("a[\"x\\ny\"]")]
    [InlineData("a[\"x\\\"]")]
    [InlineData("a[\"x\ty\"]")]
    [InlineData("a[0]b")]
    [InlineData("a b")]
    [InlineData("a-b")]
    [InlineData("$.a")]
    [InlineData("a.*")]
    [InlineData("\u65E5\u672C")]
    public void APathThatBreaksTheGrammarIsRefusedWithInvalidArgumentOnPaths(string path)
    {
        DiscussionException error = Assert.Throws<DiscussionException>(() => Parse(path));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "paths"), (error.Code, error.Parameter));
        Assert.StartsWith("paths[0]", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void AnUnpairedSurrogateInAQuotedKeyIsRefused(int surrogate)
    {
        // Built at run time: a lone surrogate cannot be stored in an attribute argument.
        string path = "a[\"" + (char)surrogate + "\"]";

        DiscussionException error = Assert.Throws<DiscussionException>(() => Parse(path));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "paths"), (error.Code, error.Parameter));
    }

    [Fact]
    public void TheBoundsOfThePathsAreExact()
    {
        string sixteenSegments = "a" + string.Concat(Enumerable.Repeat(".a", 15));
        Assert.Equal(16, Parse(sixteenSegments).Single().Length);
        Assert.Throws<DiscussionException>(() => Parse(sixteenSegments + ".a"));

        Assert.Single(Parse(new string('a', DiscussionExtensionLimits.MaxJsonExtractPathChars)));
        Assert.Throws<DiscussionException>(() => Parse(new string('a', DiscussionExtensionLimits.MaxJsonExtractPathChars + 1)));

        string[] sixteenPaths = Enumerable.Range(0, 16).Select(i => $"p{i}").ToArray();
        Assert.Equal(16, Parse(sixteenPaths).Count);
        Assert.Throws<DiscussionException>(() => Parse(sixteenPaths.Append("p16").ToArray()));
        Assert.Throws<DiscussionException>(() => Parse(Array.Empty<string>()));
    }

    [Fact]
    public void TheLargestArrayIndexAndItsDigitLimitAreTheSameFact()
    {
        // MaxJsonExtractIndex is the number the descriptions show; the parser enforces the digit count. The two constants must agree.
        string largest = DiscussionExtensionLimits.MaxJsonExtractIndex.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(DiscussionExtensionLimits.MaxJsonExtractIndexDigits, largest.Length);
        Assert.All(largest, digit => Assert.Equal('9', digit));

        Assert.Single(Parse($"a[{largest}]"));
        Assert.Throws<DiscussionException>(() => Parse($"a[{DiscussionExtensionLimits.MaxJsonExtractIndex + 1}]"));
    }

    [Fact]
    public void ARepeatedPathIsRefusedButTwoSpellingsOfOnePlaceAreAllowed()
    {
        DiscussionException error = Assert.Throws<DiscussionException>(() => Parse("a.b", "c", "a.b"));
        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "paths"), (error.Code, error.Parameter));
        Assert.StartsWith("paths[2]", error.Message, StringComparison.Ordinal);

        Assert.Equal(2, Parse("a.b", "a[\"b\"]").Count);
    }

    // ---- The answer of get_analysis ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PathsReturnTheFieldsWithTheExactTextOfEveryNumberAndListWhatIsMissing()
    {
        (string id, string token) = OpenWithContext();
        string[] paths =
        {
            "summary.latest", "summary.other", "summary.neg", "summary.p", "summary.nul", "summary.text", "series[1].value",
            "matchedValues[\"7203-T\"].left", "matchedValues[\"^N225\"].a", "matchedValues[\"CL=F\"].a", "matchedValues[\"a.b\"].c", "matchedValues[\"d\\\"e\"]",
            "0", "list[0]", "series[5].value", "summary.latest.x",
        };

        DiscussionReply reply = await GetAsync(id, token, paths);

        Assert.True(reply.IsOk, reply.Failure?.Message);
        using JsonDocument document = JsonDocument.Parse(reply.ResultJson!);
        JsonElement root = document.RootElement;
        Assert.False(root.TryGetProperty("result", out _));
        JsonElement extracted = root.GetProperty("extracted");
        Assert.Equal("1234.567890123456789012345", extracted.GetProperty("summary.latest").GetRawText());
        Assert.Equal("1E+2", extracted.GetProperty("summary.other").GetRawText());
        Assert.Equal("-0.0", extracted.GetProperty("summary.neg").GetRawText());
        Assert.Equal("0.10", extracted.GetProperty("summary.p").GetRawText());
        Assert.Equal(JsonValueKind.Null, extracted.GetProperty("summary.nul").ValueKind);
        Assert.Equal("x", extracted.GetProperty("summary.text").GetString());
        Assert.Equal("2.5", extracted.GetProperty("series[1].value").GetRawText());
        Assert.Equal("1", extracted.GetProperty("matchedValues[\"7203-T\"].left").GetRawText());
        Assert.Equal("3", extracted.GetProperty("matchedValues[\"^N225\"].a").GetRawText());
        Assert.Equal("4", extracted.GetProperty("matchedValues[\"CL=F\"].a").GetRawText());
        Assert.Equal("5", extracted.GetProperty("matchedValues[\"a.b\"].c").GetRawText());
        Assert.Equal("6", extracted.GetProperty("matchedValues[\"d\\\"e\"]").GetRawText());
        Assert.Equal("zero", extracted.GetProperty("0").GetString());
        Assert.Equal("10", extracted.GetProperty("list[0]").GetRawText());
        Assert.Equal(
            new[] { "series[5].value", "summary.latest.x" },
            root.GetProperty("missingPaths").EnumerateArray().Select(e => e.GetString()));

        // The order of the request is kept, and the description of the context is still there.
        Assert.Equal(paths.Take(14), extracted.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("contextId").GetInt32());
        Assert.Equal(64, root.GetProperty("sha256").GetString()!.Length);

        // A path that is not there, one that goes through a value of the wrong kind, and one that names an index of an object are all missing.
        DiscussionReply other = await GetAsync(id, token, new[] { "nothing", "summary[0]", "list.a", "list[2]" });
        using JsonDocument otherDocument = JsonDocument.Parse(other.ResultJson!);
        Assert.Equal(0, otherDocument.RootElement.GetProperty("extracted").EnumerateObject().Count());
        Assert.Equal(new[] { "nothing", "summary[0]", "list.a", "list[2]" }, otherDocument.RootElement.GetProperty("missingPaths").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task AnObjectOrArrayIsReturnedWithTheTextOfItsMembersUnchanged()
    {
        (string id, string token) = OpenWithContext();

        DiscussionReply reply = await GetAsync(id, token, new[] { "summary", "series" });

        using JsonDocument document = JsonDocument.Parse(reply.ResultJson!);
        JsonElement extracted = document.RootElement.GetProperty("extracted");
        Assert.Equal("1234.567890123456789012345", extracted.GetProperty("summary").GetProperty("latest").GetRawText());
        Assert.Equal(2, extracted.GetProperty("series").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("missingPaths").GetArrayLength());
    }

    [Fact]
    public async Task WithoutPathsTheAnswerIsTheWholeResultAsBefore()
    {
        (string id, string token) = OpenWithContext();

        DiscussionReply plain = await GetAsync(id, token, paths: null);
        DiscussionReply described = await GetAsync(id, token, paths: null, includeResult: false);

        using JsonDocument document = JsonDocument.Parse(plain.ResultJson!);
        Assert.True(document.RootElement.TryGetProperty("result", out _));
        Assert.False(document.RootElement.TryGetProperty("extracted", out _));
        Assert.False(document.RootElement.TryGetProperty("missingPaths", out _));
        using JsonDocument description = JsonDocument.Parse(described.ResultJson!);
        Assert.False(description.RootElement.TryGetProperty("result", out _));
        Assert.False(description.RootElement.TryGetProperty("extracted", out _));
    }

    [Fact]
    public async Task PathsWithIncludeResultFalseAreRefused()
    {
        (string id, string token) = OpenWithContext();

        DiscussionReply reply = await GetAsync(id, token, new[] { "summary" }, includeResult: false);

        Assert.False(reply.IsOk);
        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "paths", "paths cannot be combined with includeResult false."), (reply.Failure!.Code, reply.Failure.Parameter, reply.Failure.Message));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"summary\"")]
    [InlineData("[1]")]
    [InlineData("[\"a b\"]")]
    [InlineData("[\"a\",\"a\"]")]
    public async Task AnInvalidPathsArgumentIsInvalidArgument(string pathsJson)
    {
        (string id, string token) = OpenWithContext();

        DiscussionReply reply = await _gateway.ExecuteAsync(
            new DiscussionRequest(DiscussionOperations.GetAnalysis, "test", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","contextId":1,"paths":{{pathsJson}} }"""), CancellationToken.None);

        Assert.False(reply.IsOk);
        Assert.Equal(DiscussionErrorCodes.InvalidArgument, reply.Failure!.Code);
        Assert.Equal("paths", reply.Failure.Parameter);
    }

    [Fact]
    public async Task ADuplicatePropertyNameResolvesLikeTheDocumentParserDoes()
    {
        (string id, string token) = OpenWithContext("""{"a":1,"a":2}""");

        DiscussionReply reply = await GetAsync(id, token, new[] { "a" });

        using JsonDocument document = JsonDocument.Parse(reply.ResultJson!);
        // Pinned characterization of JsonElement.TryGetProperty on System.Text.Json (.NET 8): the last property of that name wins.
        Assert.Equal("2", document.RootElement.GetProperty("extracted").GetProperty("a").GetRawText());
    }

    [Fact]
    public async Task AReaderWhoHasLeftMayStillExtract()
    {
        (string id, string token) = OpenWithContext();
        _rig.Hub.Leave(id, token);

        DiscussionReply reply = await GetAsync(id, token, new[] { "summary.text" });

        Assert.True(reply.IsOk, reply.Failure?.Message);
    }

    // ---- includeContexts ---------------------------------------------------------------------------------------------------------------

    private async Task<DiscussionReply> StatusAsync(string arguments)
        => await _gateway.ExecuteAsync(new DiscussionRequest(DiscussionOperations.Status, "test", arguments), CancellationToken.None);

    [Fact]
    public async Task IncludeContextsListsTheContextsOfOneDiscussionWithoutTheirContent()
    {
        string id = _rig.NewSession();
        string alice = _rig.Join(id, "Alice").Token;
        string bob = _rig.Join(id, "Bob").Token;
        _rig.Hub.AttachCommit(id, alice, "sa_x", "{}", Result, "a note", "TrendDetection");
        _rig.Hub.AttachCommit(id, bob, "sa_y", "{\"n\":1}", """{"v":1}""", null);

        DiscussionReply reply = await StatusAsync($$"""{"sessionId":"{{id}}","includeContexts":true}""");

        Assert.True(reply.IsOk, reply.Failure?.Message);
        using JsonDocument document = JsonDocument.Parse(reply.ResultJson!);
        JsonElement session = document.RootElement.GetProperty("session");
        JsonElement[] contexts = session.GetProperty("contexts").EnumerateArray().ToArray();
        Assert.Equal(2, contexts.Length);
        Assert.Equal(
            new[] { "contextId", "tool", "attachedBy", "analysisIntent", "resultBytes", "sha256", "sequence", "createdUtc" },
            contexts[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal((1, "sa_x", "p1", "TrendDetection"), (contexts[0].GetProperty("contextId").GetInt32(), contexts[0].GetProperty("tool").GetString(), contexts[0].GetProperty("attachedBy").GetString(), contexts[0].GetProperty("analysisIntent").GetString()));
        Assert.Equal(Encoding.UTF8.GetByteCount(Result), contexts[0].GetProperty("resultBytes").GetInt32());
        Assert.Equal(64, contexts[0].GetProperty("sha256").GetString()!.Length);
        Assert.Equal(3, contexts[0].GetProperty("sequence").GetInt32());
        Assert.Equal((2, "sa_y", "p2", JsonValueKind.Null), (contexts[1].GetProperty("contextId").GetInt32(), contexts[1].GetProperty("tool").GetString(), contexts[1].GetProperty("attachedBy").GetString(), contexts[1].GetProperty("analysisIntent").ValueKind));
        Assert.DoesNotContain("a note", reply.ResultJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("provenance", reply.ResultJson!, StringComparison.Ordinal);
        Assert.Equal(2, session.GetProperty("contextCount").GetInt32());
    }

    [Fact]
    public async Task IncludeContextsOfADiscussionWithoutContextsIsAnEmptyList()
    {
        string id = _rig.NewSession();

        DiscussionReply reply = await StatusAsync($$"""{"sessionId":"{{id}}","includeContexts":true}""");

        using JsonDocument document = JsonDocument.Parse(reply.ResultJson!);
        Assert.Equal(0, document.RootElement.GetProperty("session").GetProperty("contexts").GetArrayLength());
    }

    [Fact]
    public async Task WithoutIncludeContextsTheStatusIsUnchanged()
    {
        (string id, _) = OpenWithContext();

        DiscussionReply omitted = await StatusAsync($$"""{"sessionId":"{{id}}"}""");
        DiscussionReply explicitFalse = await StatusAsync($$"""{"sessionId":"{{id}}","includeContexts":false}""");
        DiscussionReply all = await StatusAsync("{}");

        Assert.Equal(omitted.ResultJson, explicitFalse.ResultJson);
        Assert.DoesNotContain("\"contexts\"", omitted.ResultJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"contexts\"", all.ResultJson!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"includeContexts":true}""")]
    [InlineData("""{"includeContexts":true,"auditTail":5}""")]
    public async Task IncludeContextsWithoutASessionIdIsInvalidArgument(string arguments)
    {
        OpenWithContext();

        DiscussionReply reply = await StatusAsync(arguments);

        Assert.False(reply.IsOk);
        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "includeContexts", "includeContexts requires sessionId."), (reply.Failure!.Code, reply.Failure.Parameter, reply.Failure.Message));
    }

    [Fact]
    public async Task AnUnknownSessionWithIncludeContextsIsNotFound()
    {
        DiscussionReply reply = await StatusAsync("""{"sessionId":"Sxxxxxx","includeContexts":true}""");

        Assert.Equal(DiscussionErrorCodes.DiscussionNotFound, reply.Failure!.Code);
    }

    [Fact]
    public async Task TheContextsReachAClientOfAnotherServerOverThePipe()
    {
        string suffix = "ctx" + Guid.NewGuid().ToString("N")[..8];
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dhub-ctx-data");
        var transport = new DiscussionTransportOptions(suffix, true, 10, 3000);
        using var hub = new DiscussionGatewayRouter(new DiscussionSettings(), TimeProvider.System, null, null, folder, transport);
        using var remote = new DiscussionGatewayRouter(new DiscussionSettings(), TimeProvider.System, null, null, folder, transport);

        DiscussionReply created = await hub.ExecuteAsync(new DiscussionRequest(DiscussionOperations.Create, "a", """{"title":"t"}"""), CancellationToken.None);
        using JsonDocument createdJson = JsonDocument.Parse(created.ResultJson!);
        string id = createdJson.RootElement.GetProperty("sessionId").GetString()!;
        DiscussionReply joined = await remote.ExecuteAsync(new DiscussionRequest(DiscussionOperations.Join, "b", $$"""{"sessionId":"{{id}}","displayName":"Alice"}"""), CancellationToken.None);
        using JsonDocument joinedJson = JsonDocument.Parse(joined.ResultJson!);
        string token = joinedJson.RootElement.GetProperty("participantToken").GetString()!;
        hub.LocalHub!.AttachCommit(id, token, "sa_x", "{}", Result, null, "TrendDetection");

        DiscussionReply status = await remote.ExecuteAsync(new DiscussionRequest(DiscussionOperations.Status, "b", $$"""{"sessionId":"{{id}}","includeContexts":true}"""), CancellationToken.None);
        DiscussionReply extracted = await remote.ExecuteAsync(
            new DiscussionRequest(DiscussionOperations.GetAnalysis, "b", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","contextId":1,"paths":["summary.latest"]}"""), CancellationToken.None);

        Assert.True(status.IsOk, status.Failure?.Message);
        using JsonDocument statusJson = JsonDocument.Parse(status.ResultJson!);
        Assert.Equal("remote", statusJson.RootElement.GetProperty("role").GetString());
        Assert.Equal("sa_x", statusJson.RootElement.GetProperty("session").GetProperty("contexts")[0].GetProperty("tool").GetString());
        Assert.True(extracted.IsOk, extracted.Failure?.Message);
        using JsonDocument extractedJson = JsonDocument.Parse(extracted.ResultJson!);
        Assert.Equal("1234.567890123456789012345", extractedJson.RootElement.GetProperty("extracted").GetProperty("summary.latest").GetRawText());
    }
}
