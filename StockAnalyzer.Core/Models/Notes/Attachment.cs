using System;

namespace StockAnalyzer.Core.Models.Notes;

/// <summary>
/// An immutable image attachment record for a Note. The file itself is stored on disk under
/// Data\Notes\Attachments\{StoredFileName}; this record holds only the metadata (spec section 3).
/// </summary>
public sealed record Attachment(
    Guid Id,
    string StoredFileName,
    string OriginalFileName,
    string MimeType,
    int Width,
    int Height,
    long FileSizeBytes,
    DateTime CreatedAt);
