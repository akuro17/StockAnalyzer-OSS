using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class AttachmentRepositoryTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_attachments_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static byte[] CreateTestImageBytes(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static async Task<(NoteRepository NoteRepository, AttachmentRepository AttachmentRepository, NoteDatabaseConnectionManager ConnectionManager)> CreateInitializedRepositoriesAsync(string tempDir)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
        var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
        await schemaInitializer.InitializeAsync();

        var noteRepository = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
        var attachmentRepository = new AttachmentRepository(connectionManager, NullLogger<AttachmentRepository>.Instance);
        return (noteRepository, attachmentRepository, connectionManager);
    }

    [Fact]
    public async Task SaveAsync_WithValidPng_SavesFileAndRow()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);
            var imageBytes = CreateTestImageBytes(200, 100, SKEncodedImageFormat.Png);

            var result = await attachmentRepository.SaveAsync(imageBytes, "chart_screenshot.png");

            Assert.True(result.Success);
            Assert.NotNull(result.Attachment);
            Assert.Equal("image/png", result.Attachment!.MimeType);
            Assert.Equal(200, result.Attachment.Width);
            Assert.Equal(100, result.Attachment.Height);
            Assert.Equal("chart_screenshot.png", result.Attachment.OriginalFileName);

            var storedFilePath = Path.Combine(tempDir, "Attachments", result.Attachment.StoredFileName);
            Assert.True(File.Exists(storedFilePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetByIdAsync_ForSavedAttachment_ReturnsMatchingMetadata()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);
            var imageBytes = CreateTestImageBytes(64, 48, SKEncodedImageFormat.Png);
            var saved = await attachmentRepository.SaveAsync(imageBytes, "thumb.png");

            var fetched = await attachmentRepository.GetByIdAsync(saved.Attachment!.Id);

            Assert.NotNull(fetched);
            Assert.Equal(saved.Attachment.Id, fetched!.Id);
            Assert.Equal(saved.Attachment.StoredFileName, fetched.StoredFileName);
            Assert.Equal(64, fetched.Width);
            Assert.Equal(48, fetched.Height);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetByIdAsync_ForUnknownId_ReturnsNull()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);

            var fetched = await attachmentRepository.GetByIdAsync(Guid.NewGuid());

            Assert.Null(fetched);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetFilePath_ForSavedAttachment_PointsToTheFileOnDisk()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);
            var imageBytes = CreateTestImageBytes(64, 48, SKEncodedImageFormat.Png);
            var saved = await attachmentRepository.SaveAsync(imageBytes, "thumb.png");

            var filePath = attachmentRepository.GetFilePath(saved.Attachment!);

            Assert.True(File.Exists(filePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveAsync_WithValidJpeg_SavesFileAndRow()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);
            var imageBytes = CreateTestImageBytes(150, 150, SKEncodedImageFormat.Jpeg);

            var result = await attachmentRepository.SaveAsync(imageBytes, "photo.jpg");

            Assert.True(result.Success);
            Assert.Equal("image/jpeg", result.Attachment!.MimeType);
            Assert.EndsWith(".jpg", result.Attachment.StoredFileName);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveAsync_WithUnsupportedExtension_FailsWithoutSavingFile()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);
            var imageBytes = CreateTestImageBytes(50, 50);

            var result = await attachmentRepository.SaveAsync(imageBytes, "animation.gif");

            Assert.False(result.Success);
            Assert.Null(result.Attachment);
            Assert.NotNull(result.FailureReason);
            Assert.Empty(Directory.GetFiles(Path.Combine(tempDir, "Attachments")));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveAsync_WithResolutionAboveLimit_Fails()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);
            // Width exceeds the 4000px limit; kept short on height so the test stays fast.
            var imageBytes = CreateTestImageBytes(4001, 10, SKEncodedImageFormat.Png);

            var result = await attachmentRepository.SaveAsync(imageBytes, "too_wide.png");

            Assert.False(result.Success);
            Assert.Null(result.Attachment);
            Assert.Contains("resolution", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveAsync_WithCorruptedBytes_FailsGracefully()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);
            var garbageBytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };

            var result = await attachmentRepository.SaveAsync(garbageBytes, "broken.png");

            Assert.False(result.Success);
            Assert.Null(result.Attachment);
            Assert.NotNull(result.FailureReason);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveAsync_MultipleImages_OneCorruptedOneValid_OnlyCorruptedFails()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);
            var validBytes = CreateTestImageBytes(80, 80, SKEncodedImageFormat.Png);
            var corruptedBytes = new byte[] { 0xFF, 0xFF, 0xFF };

            var validResult = await attachmentRepository.SaveAsync(validBytes, "valid.png");
            var corruptedResult = await attachmentRepository.SaveAsync(corruptedBytes, "corrupted.png");

            Assert.True(validResult.Success);
            Assert.False(corruptedResult.Success);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task FindOrphanedFilesAsync_DetectsFileNotReferencedByAnyNote()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (noteRepository, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);

            var linkedResult = await attachmentRepository.SaveAsync(CreateTestImageBytes(60, 60), "linked.png");
            Assert.True(linkedResult.Success);
            var note = new Note(Guid.NewGuid(), "note with attachment", DateTime.Now, DateTime.Now)
            {
                AttachmentIds = ImmutableArray.Create(linkedResult.Attachment!.Id),
            };
            await noteRepository.CreateAsync(note);

            var unlinkedResult = await attachmentRepository.SaveAsync(CreateTestImageBytes(60, 60), "unlinked.png");
            Assert.True(unlinkedResult.Success);

            var orphans = await attachmentRepository.FindOrphanedFilesAsync();

            Assert.DoesNotContain(linkedResult.Attachment.StoredFileName, orphans);
            Assert.Contains(unlinkedResult.Attachment!.StoredFileName, orphans);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task FindOrphanedFilesAsync_TreatsAttachmentsOfSoftDeletedNotesAsStillReferenced()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (noteRepository, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);

            var attachmentResult = await attachmentRepository.SaveAsync(CreateTestImageBytes(60, 60), "linked.png");
            var note = new Note(Guid.NewGuid(), "note with attachment", DateTime.Now, DateTime.Now)
            {
                AttachmentIds = ImmutableArray.Create(attachmentResult.Attachment!.Id),
            };
            await noteRepository.CreateAsync(note);
            await noteRepository.SoftDeleteAsync(note.Id);

            var orphans = await attachmentRepository.FindOrphanedFilesAsync();

            Assert.DoesNotContain(attachmentResult.Attachment.StoredFileName, orphans);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task FindOrphanedFilesAsync_WhenAttachmentsDirectoryIsEmpty_ReturnsEmpty()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, attachmentRepository, _) = await CreateInitializedRepositoriesAsync(tempDir);

            var orphans = await attachmentRepository.FindOrphanedFilesAsync();

            Assert.Empty(orphans);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
