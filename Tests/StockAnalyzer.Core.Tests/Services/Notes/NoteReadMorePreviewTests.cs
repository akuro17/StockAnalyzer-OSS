using System;
using System.Linq;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class NoteReadMorePreviewTests
{
    // The popup label is caller-supplied (localized) text; any distinctive value proves it is used verbatim.
    private const string TestLabel = "MORE-LABEL";

    [Fact]
    public void RequiresCollapse_WhenBodyShorterThanBothThresholds_ReturnsFalse()
    {
        Assert.False(NoteReadMorePreview.RequiresCollapse("short body", maxCharacters: 150, maxLines: 5));
    }

    [Fact]
    public void RequiresCollapse_WhenBodyExceedsCharacterThreshold_ReturnsTrue()
    {
        var body = new string('a', 151);
        Assert.True(NoteReadMorePreview.RequiresCollapse(body, maxCharacters: 150, maxLines: 5));
    }

    [Fact]
    public void RequiresCollapse_WhenBodyExceedsLineThreshold_ReturnsTrue()
    {
        var body = string.Join("\n", new[] { "1", "2", "3", "4", "5", "6" }); // 5 newlines
        Assert.True(NoteReadMorePreview.RequiresCollapse(body, maxCharacters: 150, maxLines: 4));
    }

    [Fact]
    public void BuildCollapsedText_WhenCollapseNotRequired_ReturnsBodyUnchanged()
    {
        var body = "short body\nwith one newline";
        Assert.Equal(body, NoteReadMorePreview.BuildCollapsedText(body, maxCharacters: 150, maxLines: 5));
    }

    [Fact]
    public void BuildCollapsedText_WhenOverCharacterLimit_TruncatesToMaxCharacters()
    {
        var body = new string('a', 200);
        var result = NoteReadMorePreview.BuildCollapsedText(body, maxCharacters: 150, maxLines: 5);
        Assert.Equal(150, result.Length);
        Assert.Equal(new string('a', 150), result);
    }

    [Fact]
    public void BuildCollapsedText_WhenOverLineLimit_TruncatesToMaxLinesFirst()
    {
        var body = "line1\nline2\nline3\nline4\nline5\nline6";
        var result = NoteReadMorePreview.BuildCollapsedText(body, maxCharacters: 150, maxLines: 3);
        Assert.Equal("line1\nline2\nline3", result);
    }

    [Fact]
    public void BuildCollapsedText_PreservesNewlinesInTheCollapsedResult()
    {
        var body = new string('a', 10) + "\n" + new string('b', 10);
        var result = NoteReadMorePreview.BuildCollapsedText(body, maxCharacters: 150, maxLines: 5);
        Assert.Contains('\n', result);
    }

    // Regression test (sa_constraint_check / SA_ARCHITECTURE_RULES.md §6 "Defensive Line-Ending
    // Parsing"): a body using Windows CRLF line endings used to leave a trailing '\r' attached to
    // each split line, since the old split only recognized '\n'.
    [Fact]
    public void BuildCollapsedText_WithCrlfLineEndings_DoesNotLeaveTrailingCarriageReturns()
    {
        var body = "line1\r\nline2\r\nline3\r\nline4\r\nline5\r\nline6";
        var result = NoteReadMorePreview.BuildCollapsedText(body, maxCharacters: 150, maxLines: 3);
        Assert.Equal("line1\nline2\nline3", result);
        Assert.DoesNotContain('\r', result);
    }

    // Regression test (sa_minimal_fix, Note Tab Polish round): a character-count cut that lands in
    // the middle of a [[note-image:{guid}]] placeholder token (embedded by Task E, Note Tab
    // Enhancements) used to leave the dangling token fragment as literal, unrecognizable text in the
    // collapsed preview instead of being dropped - NoteImageTokenExtractor.Tokenize can only match a
    // complete, well-formed token, so a partial one renders as plain garbled text.
    // sa_minimal_fix (Inline Mode "+N" expand bug investigation): the character-count budget used to
    // treat a [[note-image:{guid}]] placeholder token's own 51-character markup as literal prose
    // length, so a Note with just a few inline images and no other text collapsed prematurely,
    // silently dropping trailing images from the preview even though there was nothing for a reader
    // to "read more" of. An image token now counts as ImageTokenCharacterWeight (1) toward the
    // budget - what it will actually render as (a small image) - instead of its internal markup
    // length. Superseded the old raw-length "dangling mid-token cut" tests below, since with
    // per-character-weight cutting an image token is now always handled atomically (fully included
    // or fully excluded) - a cut can never land inside one anymore.
    [Fact]
    public void RequiresCollapse_WithSeveralImageTokensAndNoOtherText_DoesNotRequireCollapse()
    {
        var body = string.Concat(Enumerable.Range(0, 5).Select(_ => NoteImageTokenExtractor.Build(Guid.NewGuid())));

        Assert.False(NoteReadMorePreview.RequiresCollapse(body, maxCharacters: 150, maxLines: 5));
    }

    [Fact]
    public void BuildCollapsedText_WhenIncludingTheNextImageTokenWouldExceedBudget_DropsItAtomically()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var token = NoteImageTokenExtractor.Build(id);
        var body = new string('a', 50) + token; // effective length: 50 (text) + 1 (token weight) = 51

        var result = NoteReadMorePreview.BuildCollapsedText(body, maxCharacters: 50, maxLines: 5);

        Assert.Equal(new string('a', 50), result);
        Assert.DoesNotContain("[[note-image:", result);
    }

    [Fact]
    public void BuildCollapsedText_KeepsWholeImageTokens_ThenTruncatesTrailingPlainTextByEffectiveLength()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var token = NoteImageTokenExtractor.Build(id);
        var body = new string('a', 10) + token + new string('b', 100); // effective length: 10 + 1 + 100 = 111

        var result = NoteReadMorePreview.BuildCollapsedText(body, maxCharacters: 60, maxLines: 5);

        // budget: 10 (a's) + 1 (whole token) = 11 consumed, 49 remaining for the trailing b's.
        Assert.Equal(new string('a', 10) + token + new string('b', 49), result);
    }

    // --- Tickers-tab Notes column / popup helpers ---

    [Fact]
    public void BuildSingleLineText_ShortBody_ReturnsFlattenedUnchanged()
    {
        Assert.Equal("line one", NoteReadMorePreview.BuildSingleLineText("line one", maxCharacters: 150));
    }

    [Fact]
    public void BuildSingleLineText_CrLfAndLfAndCr_EachBecomeOneSpace()
    {
        Assert.Equal("a b c d", NoteReadMorePreview.BuildSingleLineText("a\r\nb\nc\rd", maxCharacters: 150));
    }

    [Fact]
    public void BuildSingleLineText_OverMaxCharacters_CutsAtMaxCharactersWithoutSuffix()
    {
        var result = NoteReadMorePreview.BuildSingleLineText(new string('a', 30), maxCharacters: 10);

        Assert.Equal(new string('a', 10), result);
    }

    [Fact]
    public void BuildSingleLineText_NewlineCountsAsOneCharacter_BeforeCap()
    {
        // "abc\ndef" flattens to "abc def" (7 chars); cap 5 -> "abc d".
        Assert.Equal("abc d", NoteReadMorePreview.BuildSingleLineText("abc\ndef", maxCharacters: 5));
    }

    [Fact]
    public void BuildSingleLineText_ImageTokenAtBoundary_IsNotSplit()
    {
        var token = NoteImageTokenExtractor.Build(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var body = new string('a', 5) + token + "bbbb";

        var result = NoteReadMorePreview.BuildSingleLineText(body, maxCharacters: 5);

        Assert.Equal(new string('a', 5), result);
    }

    [Fact]
    public void BuildSingleLineText_EmptyBody_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, NoteReadMorePreview.BuildSingleLineText(string.Empty, maxCharacters: 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildSingleLineText_MaxCharactersBelowOne_Throws(int maxCharacters)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NoteReadMorePreview.BuildSingleLineText("x", maxCharacters));
    }

    [Fact]
    public void BuildPopupText_WithinThresholds_ReturnsBodyUnchangedWithoutLabel()
    {
        Assert.Equal("short\nbody", NoteReadMorePreview.BuildPopupText("short\nbody", maxCharacters: 150, maxLines: 5, readMoreLabel: TestLabel));
    }

    [Fact]
    public void BuildPopupText_ExactlyAtMaxCharacters_HasNoLabel()
    {
        var body = new string('a', 20);

        Assert.Equal(body, NoteReadMorePreview.BuildPopupText(body, maxCharacters: 20, maxLines: 5, readMoreLabel: TestLabel));
    }

    [Fact]
    public void BuildPopupText_OverMaxCharacters_ReturnsCollapsedPlusNewlinePlusLabel()
    {
        var result = NoteReadMorePreview.BuildPopupText(new string('a', 30), maxCharacters: 10, maxLines: 5, readMoreLabel: TestLabel);

        Assert.Equal(new string('a', 10) + "\n" + TestLabel, result);
    }

    [Fact]
    public void BuildPopupText_OverMaxLines_ReturnsLineLimitedPlusLabel()
    {
        var body = string.Join("\n", new[] { "1", "2", "3", "4" }); // 3 newlines

        var result = NoteReadMorePreview.BuildPopupText(body, maxCharacters: 150, maxLines: 2, readMoreLabel: TestLabel);

        Assert.Equal("1\n2\n" + TestLabel, result);
    }

    [Fact]
    public void BuildPopupText_RemovesImageTokensAndUrls_BeforeApplyingLimits()
    {
        var token = NoteImageTokenExtractor.Build(Guid.NewGuid());
        var body = $"see {token}https://example.com/very/long/path/that/would/exceed/the/limit end";

        var result = NoteReadMorePreview.BuildPopupText(body, maxCharacters: 8, maxLines: 5, readMoreLabel: TestLabel);

        // Visible text is "see  end" (8 chars) -> within the limit, so no label; no token, no URL.
        Assert.Equal("see  end", result);
    }

    [Fact]
    public void BuildPopupText_WhenOnlyImagesAndUrls_ReturnsEmpty()
    {
        var token = NoteImageTokenExtractor.Build(Guid.NewGuid());

        Assert.Equal(string.Empty, NoteReadMorePreview.BuildPopupText($"{token} https://example.com", maxCharacters: 10, maxLines: 5, readMoreLabel: TestLabel));
    }

    [Fact]
    public void BuildPopupText_EmptyBody_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, NoteReadMorePreview.BuildPopupText(string.Empty, maxCharacters: 10, maxLines: 5, readMoreLabel: TestLabel));
    }

    [Fact]
    public void BuildPopupText_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => NoteReadMorePreview.BuildPopupText(null!, 10, 5, TestLabel));
        Assert.Throws<ArgumentOutOfRangeException>(() => NoteReadMorePreview.BuildPopupText("x", 0, 5, TestLabel));
        Assert.Throws<ArgumentOutOfRangeException>(() => NoteReadMorePreview.BuildPopupText("x", 10, 0, TestLabel));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildPopupText_BlankLabel_Throws(string? label)
    {
        Assert.ThrowsAny<ArgumentException>(() => NoteReadMorePreview.BuildPopupText("x", 10, 5, label!));
    }

    [Fact]
    public void RemoveImagesAndUrls_KeepsOtherTextAndTrailingPunctuation()
    {
        Assert.Equal("go () now", NoteReadMorePreview.RemoveImagesAndUrls("go (https://example.com/a) now"));
    }
}
