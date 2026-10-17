using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Core.Tests.Docs;

/// <summary>
/// Every fenced yaml block of the documentation (the SA_*.md rules and the specifications are written as such blocks) must be valid YAML.
/// A block that does not parse cannot be read by a tool, and the defect stays unseen until someone looks at that one block.
/// </summary>
public sealed class SpecYamlBlockTests
{
    private const string DocsFolder = "docs", MarkdownPattern = "*.md", Fence = "```", YamlFence = "```yaml";
    private const string NoId = "(no id)";
    private const int MinimumBlocksInApiSpecification = 100;

    private static readonly Regex IdLine = new(@"^id:\s*(\S+)", RegexOptions.CultureInvariant);

    private sealed record Block(string File, int Line, string Id, string Text, bool Closed = true);

    private static IEnumerable<Block> BlocksOf(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!string.Equals(lines[i].TrimEnd(), YamlFence, StringComparison.Ordinal)) continue;
            int close = i + 1;
            while (close < lines.Length && !lines[close].StartsWith(Fence, StringComparison.Ordinal)) close++;
            bool closed = close < lines.Length && string.Equals(lines[close].TrimEnd(), Fence, StringComparison.Ordinal);
            var body = lines[(i + 1)..Math.Min(close, lines.Length)];
            string id = body.Select(l => IdLine.Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value ?? NoId;
            yield return new Block(relative, i + 1, id, string.Join('\n', body), closed);
            i = closed ? close : close - 1;   // a fence that opens the next block is not consumed by this one
        }
    }

    private static List<Block> AllBlocks()
    {
        string root = TestRepositoryRoot.Find();
        return Directory.EnumerateFiles(Path.Combine(root, DocsFolder), MarkdownPattern, SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .SelectMany(p => BlocksOf(root, p))
            .ToList();
    }

    private static string ParseError(Block block)
    {
        if (!block.Closed) return "the block has no closing fence";
        try
        {
            new YamlStream().Load(new StringReader(block.Text));
            return null;
        }
        catch (YamlException exception)
        {
            return $"{exception.Message} (document line {block.Line + exception.Start.Line})";
        }
    }

    [Fact]
    public void EveryYamlBlockOfTheDocumentationParses()
    {
        var failures = AllBlocks()
            .Select(b => (Block: b, Error: ParseError(b)))
            .Where(r => r.Error is not null)
            .Select(r => $"{r.Block.File}:{r.Block.Line} {r.Block.Id}: {r.Error}")
            .ToList();

        Assert.True(failures.Count == 0, "These yaml blocks do not parse:\n" + string.Join('\n', failures));
    }

    [Fact]
    public void TheScanFindsTheBlocksOfTheApiSpecification_SoAnEmptyScanCannotPass()
    {
        int count = AllBlocks().Count(b => b.File == "docs/ja/API_SPECIFICATION.md");
        Assert.True(count >= MinimumBlocksInApiSpecification, "Only " + count + " yaml blocks were found in docs/ja/API_SPECIFICATION.md.");
    }

    [Fact]
    public void ABlockWithAnUnescapedApostropheInASingleQuotedValueIsReported()
    {
        // The defect that went unseen in api_specification_0204: the apostrophe ends the quoted value early.
        Assert.NotNull(ParseError(new Block("sample.md", 1, NoId, "id: x\ndefinition:\n  - subject: s\n    value: 'the agent's bars (a) and (b): c'")));
        Assert.Null(ParseError(new Block("sample.md", 1, NoId, "id: x\ndefinition:\n  - subject: s\n    value: 'the agent''s bars (a) and (b): c'")));
        Assert.NotNull(ParseError(new Block("sample.md", 1, NoId, "id: x", Closed: false)));
    }
}
