using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
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

    /// <summary>
    /// Block ids that are used twice in one file today (file|id). They are other sessions' content and some are referenced by related_ids, so they are pinned instead of
    /// renamed: a NEW duplicate fails the test, and a repaired one must be removed from this list (<see cref="TheKnownDuplicateIdsAreStillDuplicated"/>).
    /// </summary>
    private static readonly string[] KnownDuplicateIds =
    {
        "docs/SA_UI_INTERACTION.md|sa_ui_interaction_0045",
        "docs/SA_UI_INTERACTION.md|sa_ui_interaction_0046",
        "docs/ja/API_SPECIFICATION.md|api_specification_0106",
        "docs/ja/API_SPECIFICATION.md|api_specification_0211",
        "docs/ja/API_SPECIFICATION.md|api_specification_0220",
        "docs/ja/API_SPECIFICATION.md|api_specification_0221",
        "docs/ja/Future_Improvements.md|future_improvements_20261012_0001"
    };

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

    private static IEnumerable<string> DuplicateIds(IEnumerable<Block> blocks)
        => blocks.Where(b => b.Id != NoId)
            .GroupBy(b => (b.File, b.Id))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.File + "|" + g.Key.Id);

    /// <summary>The first key that appears twice in one mapping (the YAML parser accepts it silently and the last value wins), or null.</summary>
    private static string DuplicateKeyError(Block block)
    {
        var frames = new Stack<(bool IsMapping, HashSet<string> Keys, bool[] ExpectKey)>();
        try
        {
            var parser = new Parser(new StringReader(block.Text));
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case MappingStart:
                        frames.Push((true, new HashSet<string>(StringComparer.Ordinal), new[] { true }));
                        break;
                    case SequenceStart:
                        frames.Push((false, null, null));
                        break;
                    case MappingEnd:
                    case SequenceEnd:
                        frames.Pop();
                        Toggle(frames);
                        break;
                    case Scalar scalar:
                        if (frames.Count > 0 && frames.Peek().IsMapping)
                        {
                            var frame = frames.Peek();
                            if (frame.ExpectKey[0] && !frame.Keys.Add(scalar.Value))
                            {
                                return $"duplicate key '{scalar.Value}' (document line {block.Line + scalar.Start.Line})";
                            }
                        }

                        Toggle(frames);
                        break;
                    case AnchorAlias:
                        Toggle(frames);
                        break;
                }
            }
        }
        catch (YamlException)
        {
            return null; // a block that does not parse is reported by EveryYamlBlockOfTheDocumentationParses
        }

        return null;
    }

    private static void Toggle(Stack<(bool IsMapping, HashSet<string> Keys, bool[] ExpectKey)> frames)
    {
        if (frames.Count > 0 && frames.Peek().IsMapping)
        {
            var expect = frames.Peek().ExpectKey;
            expect[0] = !expect[0];
        }
    }

    [Fact]
    public void NoBlockIdIsUsedTwiceInAFile_ExceptTheKnownOnes()
    {
        var added = DuplicateIds(AllBlocks()).Except(KnownDuplicateIds).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(added.Count == 0, "These block ids are used twice in one file (an id must be unique in its file):" + Environment.NewLine + string.Join(Environment.NewLine, added));
    }

    [Fact]
    public void TheKnownDuplicateIdsAreStillDuplicated_SoTheListShrinksWhenOneIsRepaired()
    {
        var current = DuplicateIds(AllBlocks()).ToHashSet(StringComparer.Ordinal);
        var repaired = KnownDuplicateIds.Where(k => !current.Contains(k)).ToList();

        Assert.True(repaired.Count == 0, "These ids are no longer duplicated; remove them from KnownDuplicateIds:" + Environment.NewLine + string.Join(Environment.NewLine, repaired));
    }

    [Fact]
    public void NoYamlMappingOfTheDocumentationHasADuplicateKey()
    {
        var failures = AllBlocks()
            .Select(b => (Block: b, Error: DuplicateKeyError(b)))
            .Where(r => r.Error is not null)
            .Select(r => $"{r.Block.File}:{r.Block.Line} {r.Block.Id}: {r.Error}")
            .ToList();

        Assert.True(failures.Count == 0, "These yaml blocks repeat a key inside one mapping:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static Block Sample(params string[] yamlLines) => new("a.md", 1, NoId, string.Join('\n', yamlLines));

    [Fact]
    public void TheDuplicateDetectorsReportARepeatedIdAndARepeatedKey_AndAcceptRepeatsInDifferentMappings()
    {
        var a = new Block("a.md", 1, "x_1", "id: x_1");
        var b = new Block("a.md", 9, "x_1", "id: x_1");
        var other = new Block("b.md", 1, "x_1", "id: x_1");
        Assert.Equal(new[] { "a.md|x_1" }, DuplicateIds(new[] { a, b, other }).ToArray());

        Assert.NotNull(DuplicateKeyError(Sample("id: x", "value: 1", "value: 2")));
        Assert.NotNull(DuplicateKeyError(Sample("items:", "  - subject: s", "    nested:", "      k: 1", "      k: 2")));
        // The same key in two different mappings (every list item has a subject and a value) is normal.
        Assert.Null(DuplicateKeyError(Sample("items:", "  - subject: s", "    value: 1", "  - subject: t", "    value: 2", "other:", "  value: 3")));
        Assert.Null(DuplicateKeyError(Sample("a: [1, 2]", "b: {c: 1, d: 2}", "c: x")));
    }
}
