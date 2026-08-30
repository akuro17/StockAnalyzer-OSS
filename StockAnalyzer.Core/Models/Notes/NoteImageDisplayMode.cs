namespace StockAnalyzer.Core.Models.Notes;

/// <summary>Where an attached image renders on a posted Note (Settings &gt; Notes). AttachmentList
/// (default) keeps today's behavior - every image in a fixed row below the body. Inline renders
/// each image at the exact text-cursor position it was attached at during composition.</summary>
public enum NoteImageDisplayMode
{
    AttachmentList,
    Inline,
}
