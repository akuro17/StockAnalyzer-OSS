#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// The strict key check of the board's gateway (spec 5.12, wire version 4): an operation refuses a top-level argument key it does not accept, before it reads any value,
/// and the refusal changes nothing. The lists are the one definition <see cref="DiscussionOperationArguments"/>.
/// </summary>
public sealed class DiscussionOperationArgumentsTests : IDisposable
{
    private const string Unknown = "zzUnknown";

    private static readonly string[] ElevenNames =
    [
        DiscussionOperations.Create,
        DiscussionOperations.Join,
        DiscussionOperations.Leave,
        DiscussionOperations.Post,
        DiscussionOperations.Read,
        DiscussionOperations.StatusHubPart,
        DiscussionOperations.Close,
        DiscussionOperations.AttachPrecheck,
        DiscussionOperations.AttachCommit,
        DiscussionOperations.GetAnalysis,
        DiscussionOperations.Status,
    ];

    private readonly ITestOutputHelper _output;
    private readonly HubRig _rig = new();
    private readonly DiscussionHubGateway _gateway;

    public DiscussionOperationArgumentsTests(ITestOutputHelper output)
    {
        _output = output;
        _gateway = new DiscussionHubGateway(_rig.Hub, _rig.Settings);
    }

    public void Dispose() => _rig.Dispose();

    public static IEnumerable<object[]> Names() => ElevenNames.Select(name => new object[] { name });

    private Task<DiscussionReply> AskAsync(string operation, string arguments)
        => _gateway.ExecuteAsync(new DiscussionRequest(operation, "test", arguments), CancellationToken.None);

    private async Task<JsonDocument> OkAsync(string operation, string arguments)
    {
        DiscussionReply reply = await AskAsync(operation, arguments);
        Assert.True(reply.IsOk, $"{operation}: {reply.Failure?.Code} {reply.Failure?.Message}");
        return JsonDocument.Parse(reply.ResultJson!);
    }

    private async Task<DiscussionFailure> FailedAsync(string operation, string arguments)
    {
        DiscussionReply reply = await AskAsync(operation, arguments);
        Assert.False(reply.IsOk, $"{operation} was accepted: {reply.ResultJson}");
        return reply.Failure!;
    }

    private async Task<string> BoardAsync()
    {
        DiscussionReply reply = await AskAsync(DiscussionOperations.StatusHubPart, "{}");
        Assert.True(reply.IsOk);
        return reply.ResultJson!;
    }

    // ---- The lists --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheListsCoverTheTenWireOperationsAndTheLocalStatusAndNothingElse()
    {
        string[] constants = typeof(DiscussionOperations)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal(ElevenNames.Order(StringComparer.Ordinal), DiscussionOperationArguments.Operations.Order(StringComparer.Ordinal));

        // A new operation constant without a list fails here (and an operation name that is neither a wire operation nor the local status).
        foreach (string constant in constants)
        {
            Assert.True(DiscussionOperationArguments.HasOperation(constant), $"{constant} has no key list.");
        }

        string[] wire = DiscussionOperationArguments.Operations.Where(DiscussionOperations.IsWireOperation).ToArray();
        Assert.Equal(10, wire.Length);
        Assert.Equal(new[] { DiscussionOperations.Status }, DiscussionOperationArguments.Operations.Except(wire));
        Assert.False(DiscussionOperations.IsWireOperation(DiscussionOperations.Status));
        Assert.True(DiscussionOperations.IsWireOperation(DiscussionOperations.StatusHubPart));
    }

    [Fact]
    public void StatusAndStatusHubShareOneKeySetThatHoldsCommandTail()
    {
        string[] expected = [DiscussionArgumentNames.SessionId, DiscussionArgumentNames.AuditTail, DiscussionArgumentNames.CommandTail, DiscussionArgumentNames.IncludeContexts];

        Assert.Equal(expected.Order(StringComparer.Ordinal), DiscussionOperationArguments.For(DiscussionOperations.Status).Order(StringComparer.Ordinal));
        Assert.Equal(expected.Order(StringComparer.Ordinal), DiscussionOperationArguments.For(DiscussionOperations.StatusHubPart).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryKeyOfEveryListIsADeclaredArgumentName()
    {
        HashSet<string> declared = typeof(DiscussionArgumentNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string operation in DiscussionOperationArguments.Operations)
        {
            foreach (string key in DiscussionOperationArguments.For(operation))
            {
                Assert.Contains(key, declared);
            }
        }
    }

    [Fact]
    public void ANameWithoutAListHasNoKeysAndIsNotKnown()
    {
        Assert.False(DiscussionOperationArguments.HasOperation("nope"));
        Assert.Empty(DiscussionOperationArguments.For("nope"));
        Assert.False(DiscussionOperationArguments.IsKnown("nope", DiscussionArgumentNames.SessionId));
        Assert.False(DiscussionOperationArguments.IsKnown(DiscussionOperations.Read, "SessionId"));
    }

    // ---- The refusal ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Names))]
    public async Task AnUnknownKeyIsRefusedBeforeAnyValueIsRead(string operation)
    {
        // Every other argument is missing, which would be its own error: the unknown key is named first.
        DiscussionFailure failure = await FailedAsync(operation, $$"""{"{{Unknown}}":1}""");

        Assert.Equal(DiscussionErrorCodes.InvalidArgument, failure.Code);
        Assert.Equal(Unknown, failure.Parameter);
        Assert.Equal($"Unknown argument '{Unknown}' for operation '{operation}'.", failure.Message);
        Assert.Equal(DiscussionRouterTexts.UnknownArgumentHint, failure.Hint);
    }

    [Fact]
    public async Task ARefusedCallChangesNothingOnTheBoard()
    {
        string id = _rig.NewSession();
        (string token, _) = _rig.Join(id, "Alice");
        _rig.Post(id, token, "first");
        string before = await BoardAsync();

        string[] requests =
        [
            $$"""{"title":"t","{{Unknown}}":true}""",
            $$"""{"sessionId":"{{id}}","displayName":"Bob","{{Unknown}}":true}""",
            $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","content":"second","{{Unknown}}":true}""",
            $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","{{Unknown}}":true}""",
        ];
        string[] operations = [DiscussionOperations.Create, DiscussionOperations.Join, DiscussionOperations.Post, DiscussionOperations.Close];
        for (int i = 0; i < operations.Length; i++)
        {
            DiscussionFailure failure = await FailedAsync(operations[i], requests[i]);
            Assert.Equal(Unknown, failure.Parameter);
        }

        DiscussionFailure commit = await FailedAsync(
            DiscussionOperations.AttachCommit,
            $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","tool":"sa_x","result":"{}","{{Unknown}}":1}""");
        Assert.Equal(Unknown, commit.Parameter);

        Assert.Equal(before, await BoardAsync());
    }

    [Fact]
    public async Task AKeyWithANullValueIsRefusedToo()
    {
        DiscussionFailure failure = await FailedAsync(DiscussionOperations.Create, $$"""{"title":"t","{{Unknown}}":null}""");

        Assert.Equal(Unknown, failure.Parameter);
    }

    [Fact]
    public async Task TheFirstUnknownKeyInDocumentOrderIsTheOneNamed()
    {
        DiscussionFailure failure = await FailedAsync(DiscussionOperations.Create, """{"title":"t","bKey":1,"aKey":2}""");

        Assert.Equal("bKey", failure.Parameter);
    }

    [Fact]
    public async Task ALongUnknownKeyIsEchoedCutToTheSharedTextBoundAndTheRefusalStaysSmall()
    {
        string key = new('k', 100_000);

        DiscussionFailure failure = await FailedAsync(DiscussionOperations.Read, $$"""{"{{key}}":1}""");

        Assert.Equal(DiscussionErrorCodes.InvalidArgument, failure.Code);
        Assert.Equal(new string('k', DiscussionLimits.CommandLogTextMaxChars), failure.Parameter);
        Assert.Equal($"Unknown argument '{failure.Parameter}' for operation 'read'.", failure.Message);
        Assert.True(failure.Message.Length < 200, $"The refusal is {failure.Message.Length} characters long.");
    }

    [Fact]
    public async Task TheCutNeverSplitsASurrogatePair()
    {
        // The letters fill the bound but one; the pair then straddles the bound and is dropped whole.
        string letters = new('k', DiscussionLimits.CommandLogTextMaxChars - 1);
        string key = letters + char.ConvertFromUtf32(0x1F600) + "tail";

        DiscussionFailure failure = await FailedAsync(DiscussionOperations.Read, JsonSerializer.Serialize(new Dictionary<string, int> { [key] = 1 }));

        Assert.Equal(letters, failure.Parameter);
        Assert.False(char.IsHighSurrogate(failure.Parameter![^1]));
    }

    [Fact]
    public async Task AnEscapedSpellingOfAKnownKeyIsTheSameKeyAndOfAnUnknownKeyTheSameUnknownKey()
    {
        // JSON allows a unicode escape for any letter of a key; the key test compares the unescaped name, whether it is done by text or by bytes. The backslash is built, not typed.
        string backslash = char.ConvertFromUtf32(92);

        using JsonDocument created = await OkAsync(DiscussionOperations.Create, $$"""{"{{backslash}}u0074itle":"escaped"}""");
        DiscussionFailure failure = await FailedAsync(DiscussionOperations.Create, $$"""{"title":"t","zz{{backslash}}u0055nknown":1}""");

        Assert.Equal("escaped", created.RootElement.GetProperty("title").GetString());
        Assert.Equal(Unknown, failure.Parameter);
    }

    [Fact]
    public void CheckingAKnownKeyByItsBytesAllocatesNothingWhileTheTextFormAllocatesTheName()
    {
        // Measurement for the choice of the byte form (the board is not a render loop, so the figures are printed and only "zero" is asserted for the byte form).
        using JsonDocument document = JsonDocument.Parse(
            """{"sessionId":"S","participantToken":"t","afterSequence":0,"limit":5,"waitSeconds":0,"compact":true,"headerMode":"none","excludeEvents":true,"excludeOwn":true,"view":"summary","waitForTurn":false}""");
        const int Rounds = 2000;
        JsonProperty[] properties = document.RootElement.EnumerateObject().ToArray();
        int known = 0;

        long ByteForm()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < Rounds; round++)
            {
                foreach (JsonProperty property in properties)
                {
                    known += DiscussionOperationArguments.IsKnown(DiscussionOperations.Read, property) ? 1 : 0;
                }
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        long TextForm()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < Rounds; round++)
            {
                foreach (JsonProperty property in properties)
                {
                    known += DiscussionOperationArguments.IsKnown(DiscussionOperations.Read, property.Name) ? 1 : 0;
                }
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        _ = ByteForm(); // warm-up: the JIT and the tiering allocate once
        _ = TextForm();
        long bytes = ByteForm();
        long text = TextForm();

        _output.WriteLine($"{Rounds} requests of {properties.Length} known keys: byte form {bytes} bytes allocated, text form {text} bytes ({text / Rounds} bytes per request)");
        Assert.Equal(0, bytes);
        Assert.True(text > 0, "the text form is expected to allocate the names");
        Assert.Equal(4 * Rounds * properties.Length, known);
    }

    [Fact]
    public async Task KeysAreCaseSensitive()
    {
        DiscussionFailure failure = await FailedAsync(DiscussionOperations.Create, """{"Title":"t"}""");

        Assert.Equal("Title", failure.Parameter);
    }

    [Fact]
    public async Task AnOperationWithoutAListKeepsItsOwnAnswer()
    {
        DiscussionFailure failure = await FailedAsync("nope", $$"""{"{{Unknown}}":1}""");

        Assert.Equal(DiscussionErrorCodes.InvalidArgument, failure.Code);
        Assert.Equal("operation", failure.Parameter);
        Assert.Null(failure.Hint);
    }

    [Fact]
    public async Task ANestedObjectKeepsItsOwnChecksAndIsNotKeyChecked()
    {
        string id = _rig.NewSession();
        (string token, _) = _rig.Join(id, "Alice");

        // "interpretation" is one known top-level key; what is inside it is judged by the interpretation parser, not by the key list.
        using JsonDocument ok = await OkAsync(
            DiscussionOperations.Post,
            $$$"""{"sessionId":"{{{id}}}","participantToken":"{{{token}}}","interpretation":{"summary":"s","{{{Unknown}}}":1}}""");

        Assert.True(ok.RootElement.GetProperty("sequence").GetInt64() > 0);
    }

    // ---- Nothing known is refused -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryOperationStillAcceptsEveryKeyOfItsList()
    {
        using JsonDocument created = await OkAsync(DiscussionOperations.Create, """{"title":"t","topic":"x","turnMode":"free"}""");
        string id = created.RootElement.GetProperty("sessionId").GetString()!;
        using JsonDocument joined = await OkAsync(DiscussionOperations.Join, $$"""{"sessionId":"{{id}}","displayName":"Alice"}""");
        string token = joined.RootElement.GetProperty("participantToken").GetString()!;

        using JsonDocument posted = await OkAsync(
            DiscussionOperations.Post,
            $$$"""{"sessionId":"{{{id}}}","participantToken":"{{{token}}}","content":"hello","pass":false,"replyToSequence":1,"contextIds":[],"basedOnSequence":0,"clientRequestId":"r1","interpretation":{"summary":"s"}}""");
        Assert.True(posted.RootElement.GetProperty("sequence").GetInt64() > 0);

        using JsonDocument read = await OkAsync(
            DiscussionOperations.Read,
            $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","afterSequence":0,"limit":5,"waitSeconds":0,"compact":false,"headerMode":"full","excludeEvents":false,"view":"full","waitForTurn":false}""");
        Assert.True(read.RootElement.GetProperty("messages").GetArrayLength() > 0);
        using JsonDocument tail = await OkAsync(DiscussionOperations.Read, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","tail":2}""");
        Assert.True(tail.RootElement.GetProperty("messages").GetArrayLength() > 0);

        using JsonDocument precheck = await OkAsync(DiscussionOperations.AttachPrecheck, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}""");
        Assert.True(precheck.RootElement.GetProperty("ok").GetBoolean());
        using JsonDocument commit = await OkAsync(
            DiscussionOperations.AttachCommit,
            $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","tool":"sa_x","arguments":{"a":1},"result":"{\"v\":1}","note":"n","analysisIntent":"TrendDetection"}""");
        int contextId = commit.RootElement.GetProperty("contextId").GetInt32();
        using JsonDocument analysis = await OkAsync(
            DiscussionOperations.GetAnalysis,
            $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","contextId":{{contextId}},"includeResult":true,"paths":["v"]}""");
        Assert.Equal(contextId, analysis.RootElement.GetProperty("contextId").GetInt32());

        const string StatusKeys = """{"sessionId":"{{ID}}","auditTail":2,"commandTail":0,"includeContexts":true}""";
        using JsonDocument status = await OkAsync(DiscussionOperations.Status, StatusKeys.Replace("{{ID}}", id, StringComparison.Ordinal));
        using JsonDocument hubPart = await OkAsync(DiscussionOperations.StatusHubPart, StatusKeys.Replace("{{ID}}", id, StringComparison.Ordinal));
        Assert.Equal(id, status.RootElement.GetProperty("session").GetProperty("id").GetString());
        Assert.Equal(id, hubPart.RootElement.GetProperty("session").GetProperty("id").GetString());

        using JsonDocument left = await OkAsync(DiscussionOperations.Leave, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}""");
        Assert.False(left.RootElement.GetProperty("alreadyLeft").GetBoolean());
        using JsonDocument closer = await OkAsync(DiscussionOperations.Join, $$"""{"sessionId":"{{id}}","displayName":"Bob"}""");
        string closerToken = closer.RootElement.GetProperty("participantToken").GetString()!;
        using JsonDocument closed = await OkAsync(DiscussionOperations.Close, $$"""{"sessionId":"{{id}}","participantToken":"{{closerToken}}"}""");
        Assert.Equal("closed", closed.RootElement.GetProperty("state").GetString());
    }
}
