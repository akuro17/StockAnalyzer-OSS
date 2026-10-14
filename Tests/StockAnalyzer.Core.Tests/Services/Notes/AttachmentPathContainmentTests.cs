using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

/// <summary>
/// The app stores an attachment as <c>{guid}{extension}</c> inside the attachments folder. <c>Path.Combine</c> lets a stored name that starts with a root
/// (a UNC path, a drive path) replace the folder, and lets "..\" leave it, so a tampered database row could point the "open attachment" action anywhere.
/// <see cref="AttachmentRepository.TryGetFilePath"/> accepts only a plain file name.
/// </summary>
public class AttachmentPathContainmentTests
{
    private static Attachment WithStoredName(string storedFileName)
        => new(Guid.NewGuid(), storedFileName, "original.png", "image/png", 1, 1, 1, DateTime.UtcNow);

    private static AttachmentRepository CreateRepository(string tempDir)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
        return new AttachmentRepository(connectionManager, NullLogger<AttachmentRepository>.Instance);
    }

    [Fact]
    public async Task TryGetFilePath_ForASavedAttachment_ReturnsTheSamePathAsGetFilePath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_attachment_path_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            await new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance).InitializeAsync();
            var repository = new AttachmentRepository(connectionManager, NullLogger<AttachmentRepository>.Instance);

            using var bitmap = new SKBitmap(4, 4);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            var saved = await repository.SaveAsync(encoded.ToArray(), "shot.png");
            Assert.True(saved.Success);

            Assert.True(repository.TryGetFilePath(saved.Attachment!, out var path));

            Assert.Equal(repository.GetFilePath(saved.Attachment!), path);
            Assert.Equal(Path.Combine(tempDir, "Attachments", saved.Attachment!.StoredFileName), path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Theory]
    [InlineData("..\\outside.png")]
    [InlineData("../outside.png")]
    [InlineData("\\\\server\\share\\x.png")]
    [InlineData("//server/share/x.png")]
    [InlineData("C:\\Windows\\x.png")]
    [InlineData("/etc/x.png")]
    [InlineData("sub\\x.png")]
    [InlineData("sub/x.png")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData(" ")]
    public void TryGetFilePath_ForANameThatIsNotAPlainFileName_ReturnsFalseAndAnEmptyPath(string storedFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_attachment_path_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var repository = CreateRepository(tempDir);

            Assert.False(repository.TryGetFilePath(WithStoredName(storedFileName), out var path));

            Assert.Equal(string.Empty, path);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void GetFilePath_IsUnchanged_AndStillCombinesWithoutChecking()
    {
        // Characterization of the shared function that this change deliberately leaves alone.
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_attachment_path_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var repository = CreateRepository(tempDir);
            var guidName = Guid.NewGuid() + ".png";

            Assert.Equal(Path.Combine(tempDir, "Attachments", guidName), repository.GetFilePath(WithStoredName(guidName)));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
