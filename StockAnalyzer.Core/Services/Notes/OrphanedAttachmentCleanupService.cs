using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// The result of an orphaned-attachment startup scan (spec section 4.5): detection only, no files
/// are ever deleted by this type or by <see cref="OrphanedAttachmentCleanupService"/>.
/// </summary>
public sealed record OrphanedAttachmentReport(IReadOnlyList<string> OrphanedFileNames)
{
    public bool HasOrphans => OrphanedFileNames.Count > 0;
}

/// <summary>
/// App-startup entry point for detecting orphaned attachment files (spec section 4.5: files under
/// Data\Notes\Attachments\ that no Note - including logically-deleted ones - references). This is
/// a thin orchestration wrapper, not a second implementation of the detection algorithm: the
/// actual file-vs-Note-reference comparison already lives in
/// <see cref="AttachmentRepository.FindOrphanedFilesAsync"/> (Step 90-1-7), reused here as-is per
/// the project's "no duplicated domain logic" rule. This type's own job is only to give the
/// startup scan a dedicated, semantically-named call site and a small result type a future
/// cleanup UI can bind against.
/// </summary>
public sealed class OrphanedAttachmentCleanupService
{
    private readonly AttachmentRepository _attachmentRepository;
    private readonly ILogger<OrphanedAttachmentCleanupService>? _logger;

    public OrphanedAttachmentCleanupService(AttachmentRepository attachmentRepository, ILogger<OrphanedAttachmentCleanupService>? logger = null)
    {
        _attachmentRepository = attachmentRepository;
        _logger = logger ?? NullLogger<OrphanedAttachmentCleanupService>.Instance;
    }

    /// <summary>Scans for orphaned attachment files and reports them; never deletes anything.</summary>
    public async Task<OrphanedAttachmentReport> DetectOrphansAsync(CancellationToken ct = default)
    {
        var orphanedFileNames = await _attachmentRepository.FindOrphanedFilesAsync(ct).ConfigureAwait(false);

        if (orphanedFileNames.Count > 0)
        {
            _logger?.LogInformation("Startup scan detected {Count} orphaned attachment file(s) under Data\\Notes\\Attachments\\.", orphanedFileNames.Count);
        }
        else
        {
            _logger?.LogDebug("Startup scan found no orphaned attachment files.");
        }

        return new OrphanedAttachmentReport(orphanedFileNames);
    }
}
