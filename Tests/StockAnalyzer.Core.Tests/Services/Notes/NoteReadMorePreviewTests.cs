using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class NoteReadMorePreviewTests
{
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
}
