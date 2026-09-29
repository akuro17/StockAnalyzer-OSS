using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Notes;

public class NoteAndAttachmentModelTests
{
    [Fact]
    public void Note_DefaultCollections_AreEmptyNotNull()
    {
        var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);

        Assert.Empty(note.Hashtags);
        Assert.Empty(note.AttachmentIds);
        Assert.Empty(note.LinkUrls);
        Assert.False(note.IsPinned);
        Assert.False(note.IsDeleted);
        Assert.Null(note.RelatedTicker);
        Assert.Null(note.ChartAnchorDate);
        Assert.Null(note.ChartTimeframe);
        Assert.Null(note.QuotedNoteId);
        Assert.Null(note.ParentNoteId);
    }

    [Fact]
    public void Note_WithExpression_ProducesNewImmutableCopy()
    {
        var original = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now)
        {
            RelatedTicker = "7203.T",
            ChartTimeframe = TimeFrame.D1,
            Hashtags = ImmutableArray.Create("ev", "china"),
        };

        var deleted = original with { IsDeleted = true };

        Assert.False(original.IsDeleted);
        Assert.True(deleted.IsDeleted);
        Assert.Equal("7203.T", deleted.RelatedTicker);
        Assert.Equal(TimeFrame.D1, deleted.ChartTimeframe);
        Assert.Equal(2, deleted.Hashtags.Length);
    }

    [Fact]
    public void Note_QuotedNoteIdAndParentNoteId_AreIndependentlySettable()
    {
        var quotedId = Guid.NewGuid();
        var parentId = Guid.NewGuid();

        var quoteNote = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now) { QuotedNoteId = quotedId };
        var replyNote = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now) { ParentNoteId = parentId };

        Assert.Equal(quotedId, quoteNote.QuotedNoteId);
        Assert.Null(quoteNote.ParentNoteId);
        Assert.Equal(parentId, replyNote.ParentNoteId);
        Assert.Null(replyNote.QuotedNoteId);
    }

    [Fact]
    public void Note_RecordEquality_IsValueBased()
    {
        var id = Guid.NewGuid();
        var createdAt = new DateTime(2026, 8, 12, 10, 0, 0);

        var a = new Note(id, "body", createdAt, createdAt);
        var b = new Note(id, "body", createdAt, createdAt);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Attachment_ConstructsWithAllFields()
    {
        var id = Guid.NewGuid();
        var createdAt = DateTime.Now;

        var attachment = new Attachment(id, "abc123.jpg", "chart_screenshot.jpg", "image/jpeg", 1920, 1080, 204800, createdAt);

        Assert.Equal(id, attachment.Id);
        Assert.Equal("abc123.jpg", attachment.StoredFileName);
        Assert.Equal("chart_screenshot.jpg", attachment.OriginalFileName);
        Assert.Equal("image/jpeg", attachment.MimeType);
        Assert.Equal(1920, attachment.Width);
        Assert.Equal(1080, attachment.Height);
        Assert.Equal(204800, attachment.FileSizeBytes);
        Assert.Equal(createdAt, attachment.CreatedAt);
    }
}
