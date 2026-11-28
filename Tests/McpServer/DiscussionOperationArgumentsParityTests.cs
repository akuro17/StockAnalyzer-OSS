using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.McpServer.Tools.Discussion;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The hub refuses an argument key it does not know (wire version 4); its lists (<see cref="DiscussionOperationArguments"/>) are a second place where an argument is named
/// besides the input schema of a tool. This guard keeps the two in step in both directions, so that neither a schema property is refused by the hub nor a list key is left
/// without a tool, and it fails when an operation constant has no list.
/// </summary>
public sealed class DiscussionOperationArgumentsParityTests : IDisposable
{
    /// <summary>
    /// The one declared set of keys that a list holds and no caller ever sends: the attach tool computes <c>result</c> itself and sends it only on the wire (see
    /// <see cref="DiscussionArgumentNames.Result"/>). There is NO local-only exclusion for <c>commandTail</c>: it is a schema property of the status tool and a member of both status lists.
    /// </summary>
    private static readonly Dictionary<string, string[]> InternalKeys = new(StringComparer.Ordinal)
    {
        [DiscussionOperations.AttachCommit] = [DiscussionArgumentNames.Result],
    };

    private readonly DiscussionSettings _settings = new();
    private readonly DiscussionHub _hub;
    private readonly DiscussionHubGateway _gateway;

    public DiscussionOperationArgumentsParityTests()
    {
        _hub = new DiscussionHub(_settings, startSweeper: false);
        _gateway = new DiscussionHubGateway(_hub, _settings);
    }

    public void Dispose() => _hub.Dispose();

    private static HashSet<string> SchemaKeys(IMcpTool tool)
    {
        using JsonDocument schema = JsonDocument.Parse(tool.InputSchemaJson);
        return schema.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> Listed(string operation)
    {
        HashSet<string> keys = DiscussionOperationArguments.For(operation).ToHashSet(StringComparer.Ordinal);
        if (InternalKeys.TryGetValue(operation, out string[]? internalKeys))
        {
            keys.ExceptWith(internalKeys);
        }

        return keys;
    }

    private IEnumerable<(IMcpTool Tool, string[] Operations)> ToolsAndTheirOperations()
    {
        var identity = new ClientIdentity();
        yield return (new DiscussionCreateTool(_gateway, identity), [DiscussionOperations.Create]);
        yield return (new DiscussionJoinTool(_gateway, identity), [DiscussionOperations.Join]);
        yield return (new DiscussionLeaveTool(_gateway, identity), [DiscussionOperations.Leave]);
        yield return (new DiscussionPostTool(_gateway, identity, _settings), [DiscussionOperations.Post]);
        yield return (new DiscussionReadTool(_gateway, identity, _settings), [DiscussionOperations.Read]);

        // The status is asked as "status" by the process that holds the board and arrives as "status_hub" over the pipe: one key set, two names.
        yield return (new DiscussionStatusTool(_gateway, identity), [DiscussionOperations.Status, DiscussionOperations.StatusHubPart]);
        yield return (new DiscussionCloseTool(_gateway, identity), [DiscussionOperations.Close]);
        yield return (new DiscussionGetAnalysisTool(_gateway, identity), [DiscussionOperations.GetAnalysis]);
    }

    [Fact]
    public void TheSchemaOfEveryToolAndTheKeyListsOfItsOperationsNameTheSameArguments()
    {
        foreach ((IMcpTool tool, string[] operations) in ToolsAndTheirOperations())
        {
            HashSet<string> schema = SchemaKeys(tool);
            foreach (string operation in operations)
            {
                HashSet<string> listed = Listed(operation);
                Assert.True(
                    schema.SetEquals(listed),
                    $"{tool.Name} / {operation}: only in the schema [{string.Join(", ", schema.Except(listed))}], only in the list [{string.Join(", ", listed.Except(schema))}].");
            }
        }
    }

    [Fact]
    public void TheAttachToolSchemaIsTheUnionOfTheTwoAttachListsWithoutTheInternalKey()
    {
        var attach = new DiscussionAttachAnalysisTool(_gateway, new ClientIdentity(), () => throw new InvalidOperationException("The schema needs no registry."));
        HashSet<string> schema = SchemaKeys(attach);
        HashSet<string> precheck = Listed(DiscussionOperations.AttachPrecheck);
        HashSet<string> commit = Listed(DiscussionOperations.AttachCommit);

        Assert.True(precheck.IsSubsetOf(schema), "attach_precheck holds a key that the attach tool does not offer.");
        Assert.True(commit.IsSubsetOf(schema), "attach_commit holds a key that the attach tool does not offer.");
        Assert.True(schema.SetEquals(precheck.Union(commit)), $"Schema and the union of the attach lists differ: [{string.Join(", ", schema.Except(precheck.Union(commit)))}].");
        Assert.DoesNotContain(DiscussionArgumentNames.Result, schema);
    }

    [Fact]
    public void EveryOperationConstantHasAListAndExactlyTheTenWireOperationsAndTheLocalStatusAreListed()
    {
        string[] constants = typeof(DiscussionOperations)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        foreach (string constant in constants)
        {
            Assert.True(DiscussionOperationArguments.HasOperation(constant), $"The operation '{constant}' has no key list in DiscussionOperationArguments.");
        }

        Assert.Equal(11, DiscussionOperationArguments.Operations.Count);
        Assert.Equal(10, DiscussionOperationArguments.Operations.Count(DiscussionOperations.IsWireOperation));
    }
}
