using System;
using System.IO;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class McpPublishConfigurationTests
{
    private static readonly Lazy<string> RepoRoot = new(TestRepositoryRoot.Find);

    [Theory]
    [InlineData(".github/workflows/publish-dev.yml")]
    [InlineData(".github/workflows/publish-release.yml")]
    public void GitHubWorkflowIncludesMcpServerCommandReference(string workflowPath)
    {
        string fullPath = Path.Combine(RepoRoot.Value, workflowPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullPath), $"Workflow file not found: {workflowPath}");
        string content = File.ReadAllText(fullPath);
        Assert.Contains("Ensure MCP Server Command Reference in publish output", content, StringComparison.Ordinal);
        Assert.Contains("docs/MCP_SERVER_COMMAND_REFERENCE.md", content, StringComparison.Ordinal);
        Assert.Contains("Verified docs/MCP_SERVER_COMMAND_REFERENCE.md in publish directory.", content, StringComparison.Ordinal);
    }
}
