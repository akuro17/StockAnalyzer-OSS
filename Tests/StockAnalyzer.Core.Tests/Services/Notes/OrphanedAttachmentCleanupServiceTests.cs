using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class OrphanedAttachmentCleanupServiceTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_orphan_cleanup_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static byte[] CreateTestPngBytes()
    {
        using var bitmap = new SkiaSharp.SKBitmap(20, 20);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.CornflowerBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static async Task<(OrphanedAttachmentCleanupService CleanupService, AttachmentRepository AttachmentRepository, NoteRepository NoteRepository)> CreateServicesAsync(string tempDir)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
        var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
        await schemaInitializer.InitializeAsync();

        var noteRepository = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
        var attachmentRepository = new AttachmentRepository(connectionManager, NullLogger<AttachmentRepository>.Instance);
        var cleanupService = new OrphanedAttachmentCleanupService(attachmentRepository, NullLogger<OrphanedAttachmentCleanupService>.Instance);

        return (cleanupService, attachmentRepository, noteRepository);
    }

    [Fact]
    public async Task DetectOrphansAsync_ReportsFileNotReferencedByAnyNote()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (cleanupService, attachmentRepository, _) = await CreateServicesAsync(tempDir);
            var result = await attachmentRepository.SaveAsync(CreateTestPngBytes(), "unlinked.png");
            Assert.True(result.Success);

            var report = await cleanupService.DetectOrphansAsync();

            Assert.True(report.HasOrphans);
            Assert.Contains(result.Attachment!.StoredFileName, report.OrphanedFileNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task DetectOrphansAsync_WhenAttachmentIsReferencedByANote_IsNotReported()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (cleanupService, attachmentRepository, noteRepository) = await CreateServicesAsync(tempDir);
            var result = await attachmentRepository.SaveAsync(CreateTestPngBytes(), "linked.png");
            var note = new Note(Guid.NewGuid(), "note with attachment", DateTime.Now, DateTime.Now)
            {
                AttachmentIds = ImmutableArray.Create(result.Attachment!.Id),
            };
            await noteRepository.CreateAsync(note);

            var report = await cleanupService.DetectOrphansAsync();

            Assert.False(report.HasOrphans);
            Assert.DoesNotContain(result.Attachment.StoredFileName, report.OrphanedFileNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task DetectOrphansAsync_WhenNoAttachmentsExist_ReturnsEmptyReport()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (cleanupService, _, _) = await CreateServicesAsync(tempDir);

            var report = await cleanupService.DetectOrphansAsync();

            Assert.False(report.HasOrphans);
            Assert.Empty(report.OrphanedFileNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task DetectOrphansAsync_NeverDeletesTheOrphanedFile()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (cleanupService, attachmentRepository, _) = await CreateServicesAsync(tempDir);
            var result = await attachmentRepository.SaveAsync(CreateTestPngBytes(), "unlinked.png");
            var filePath = Path.Combine(tempDir, "Attachments", result.Attachment!.StoredFileName);
            Assert.True(File.Exists(filePath));

            await cleanupService.DetectOrphansAsync();

            Assert.True(File.Exists(filePath)); // detection only - spec section 4.5
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
