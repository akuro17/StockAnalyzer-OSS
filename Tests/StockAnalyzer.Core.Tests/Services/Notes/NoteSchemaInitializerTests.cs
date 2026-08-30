using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class NoteSchemaInitializerTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_notes_schema_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static async Task<List<string>> GetTableNamesAsync(NoteDatabaseConnectionManager manager)
    {
        using var connection = await manager.OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        using var reader = await command.ExecuteReaderAsync();

        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static async Task<long> GetUserVersionAsync(NoteDatabaseConnectionManager manager)
    {
        using var connection = await manager.OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task InitializeAsync_CreatesNotesAndAttachmentsTables()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            var initializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);

            await initializer.InitializeAsync();

            var tables = await GetTableNamesAsync(connectionManager);
            Assert.Contains("Notes", tables);
            Assert.Contains("Attachments", tables);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task InitializeAsync_SetsUserVersionToCurrentSchemaVersion()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            var initializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);

            await initializer.InitializeAsync();

            var version = await GetUserVersionAsync(connectionManager);
            Assert.Equal(2, version);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_IsIdempotentAndDoesNotThrow()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            var initializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);

            await initializer.InitializeAsync();
            await initializer.InitializeAsync();

            var tables = await GetTableNamesAsync(connectionManager);
            Assert.Contains("Notes", tables);
            Assert.Contains("Attachments", tables);
            Assert.Equal(2, await GetUserVersionAsync(connectionManager));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task InitializeAsync_CreatesExpectedIndexes()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            var initializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);

            await initializer.InitializeAsync();

            using var connection = await connectionManager.OpenConnectionAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='index' ORDER BY name;";
            using var reader = await command.ExecuteReaderAsync();

            var indexNames = new List<string>();
            while (await reader.ReadAsync())
            {
                indexNames.Add(reader.GetString(0));
            }

            Assert.Contains("IX_Notes_RelatedTicker", indexNames);
            Assert.Contains("IX_Notes_CreatedAt", indexNames);
            Assert.Contains("IX_Notes_QuotedNoteId", indexNames);
            Assert.Contains("IX_Notes_ParentNoteId", indexNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task InitializeAsync_CreatesQuotedNoteIdAndParentNoteIdColumns()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            var initializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);

            await initializer.InitializeAsync();

            var columns = await GetNotesColumnNamesAsync(connectionManager);
            Assert.Contains("QuotedNoteId", columns);
            Assert.Contains("ParentNoteId", columns);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task InitializeAsync_UpgradesFromVersion1_PreservesExistingRowsAndAddsThreadColumns()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);

            // Simulate a pre-existing v1 database (schema without QuotedNoteId/ParentNoteId).
            var existingNoteId = Guid.NewGuid().ToString();
            using (var connection = await connectionManager.OpenConnectionAsync())
            {
                using (var createTable = connection.CreateCommand())
                {
                    createTable.CommandText = """
                        CREATE TABLE Notes (
                            Id TEXT PRIMARY KEY NOT NULL,
                            Body TEXT NOT NULL,
                            CreatedAt TEXT NOT NULL,
                            UpdatedAt TEXT NOT NULL,
                            ChartAnchorDate TEXT NULL,
                            ChartTimeframe INTEGER NULL,
                            RelatedTicker TEXT NULL,
                            HashtagsJson TEXT NOT NULL DEFAULT '[]',
                            AttachmentIdsJson TEXT NOT NULL DEFAULT '[]',
                            LinkUrlsJson TEXT NOT NULL DEFAULT '[]',
                            IsPinned INTEGER NOT NULL DEFAULT 0,
                            IsDeleted INTEGER NOT NULL DEFAULT 0
                        );
                        """;
                    await createTable.ExecuteNonQueryAsync();
                }

                using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = """
                        INSERT INTO Notes (Id, Body, CreatedAt, UpdatedAt, RelatedTicker, HashtagsJson, AttachmentIdsJson, LinkUrlsJson, IsPinned, IsDeleted)
                        VALUES ($id, 'pre-migration note', '2026-08-12T10:00:00', '2026-08-12T10:00:00', '7203.T', '[]', '[]', '[]', 0, 0);
                        """;
                    insert.Parameters.AddWithValue("$id", existingNoteId);
                    await insert.ExecuteNonQueryAsync();
                }

                using (var setVersion = connection.CreateCommand())
                {
                    setVersion.CommandText = "PRAGMA user_version = 1;";
                    await setVersion.ExecuteNonQueryAsync();
                }
            }

            var initializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
            await initializer.InitializeAsync();

            Assert.Equal(2, await GetUserVersionAsync(connectionManager));

            var columns = await GetNotesColumnNamesAsync(connectionManager);
            Assert.Contains("QuotedNoteId", columns);
            Assert.Contains("ParentNoteId", columns);

            using var verifyConnection = await connectionManager.OpenConnectionAsync();
            using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "SELECT Body, QuotedNoteId, ParentNoteId FROM Notes WHERE Id = $id;";
            verifyCommand.Parameters.AddWithValue("$id", existingNoteId);
            using var reader = await verifyCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("pre-migration note", reader.GetString(0));
            Assert.True(await reader.IsDBNullAsync(1));
            Assert.True(await reader.IsDBNullAsync(2));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    private static async Task<List<string>> GetNotesColumnNamesAsync(NoteDatabaseConnectionManager manager)
    {
        using var connection = await manager.OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Notes);";
        using var reader = await command.ExecuteReaderAsync();

        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }
        return names;
    }

    [Fact]
    public async Task InitializeAsync_NotesTableAcceptsInsertWithAllColumns()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            var initializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
            await initializer.InitializeAsync();

            using var connection = await connectionManager.OpenConnectionAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Notes (Id, Body, CreatedAt, UpdatedAt, ChartAnchorDate, ChartTimeframe, RelatedTicker, HashtagsJson, AttachmentIdsJson, LinkUrlsJson, IsPinned, IsDeleted)
                VALUES ($id, 'test body', '2026-08-12T10:00:00', '2026-08-12T10:00:00', NULL, NULL, '7203.T', '[]', '[]', '[]', 0, 0);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            var rowsAffected = await command.ExecuteNonQueryAsync();

            Assert.Equal(1, rowsAffected);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
