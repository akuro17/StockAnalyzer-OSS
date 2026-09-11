using System;
using System.Linq;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class NoteImageTokenExtractorTests
{
    [Fact]
    public void Build_ReturnsBracketedFormattedGuid()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var token = NoteImageTokenExtractor.Build(id);

        Assert.Equal("[[note-image:11111111-2222-3333-4444-555555555555]]", token);
    }

    [Fact]
    public void Tokenize_MixedPlainAndImageText_YieldsAlternatingTokensInOrder()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var body = $"見て{NoteImageTokenExtractor.Build(id)}良い";

        var tokens = NoteImageTokenExtractor.Tokenize(body).ToList();

        Assert.Equal(3, tokens.Count);
        Assert.Equal(("見て", (Guid?)null), (tokens[0].Text, tokens[0].AttachmentId));
        Assert.Equal((NoteImageTokenExtractor.Build(id), (Guid?)id), (tokens[1].Text, tokens[1].AttachmentId));
        Assert.Equal(("良い", (Guid?)null), (tokens[2].Text, tokens[2].AttachmentId));
    }

    [Fact]
    public void Tokenize_MultipleImageTokens_YieldsEachWithItsOwnId()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var body = $"{NoteImageTokenExtractor.Build(id1)}中間{NoteImageTokenExtractor.Build(id2)}";

        var tokens = NoteImageTokenExtractor.Tokenize(body).Where(t => t.AttachmentId is not null).ToList();

        Assert.Equal(new Guid?[] { id1, id2 }, tokens.Select(t => t.AttachmentId));
    }

    [Fact]
    public void Tokenize_NullOrEmptyBody_YieldsNoTokens()
    {
        Assert.Empty(NoteImageTokenExtractor.Tokenize(null));
        Assert.Empty(NoteImageTokenExtractor.Tokenize(""));
    }

    [Fact]
    public void Tokenize_BodyWithNoImageTokens_YieldsSinglePlainToken()
    {
        var tokens = NoteImageTokenExtractor.Tokenize("no image tokens here").ToList();

        Assert.Single(tokens);
        Assert.Null(tokens[0].AttachmentId);
    }

    [Fact]
    public void RemoveToken_RemovesOnlyTheMatchingId()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var body = $"a{NoteImageTokenExtractor.Build(id1)}b{NoteImageTokenExtractor.Build(id2)}c";

        var result = NoteImageTokenExtractor.RemoveToken(body, id1);

        Assert.Equal($"ab{NoteImageTokenExtractor.Build(id2)}c", result);
    }

    [Fact]
    public void ReplaceToken_SwapsOldIdForNewId()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var body = $"見て{NoteImageTokenExtractor.Build(oldId)}良い";

        var result = NoteImageTokenExtractor.ReplaceToken(body, oldId, newId);

        Assert.Equal($"見て{NoteImageTokenExtractor.Build(newId)}良い", result);
    }
}
