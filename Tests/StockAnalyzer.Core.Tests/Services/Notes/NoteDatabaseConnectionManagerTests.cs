using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class NoteDatabaseConnectionManagerTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_notes_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    [Fact]
    public async Task OpenConnectionAsync_CreatesDatabaseFile_InOverriddenDirectory()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var manager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);

            Assert.False(File.Exists(manager.DatabasePath));

            using var connection = await manager.OpenConnectionAsync();

            Assert.True(File.Exists(manager.DatabasePath));
            Assert.Equal(Path.Combine(tempDir, "notes.db"), manager.DatabasePath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task OpenConnectionAsync_ReturnsOpenConnection()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var manager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);

            using var connection = await manager.OpenConnectionAsync();

            Assert.Equal(ConnectionState.Open, connection.State);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task OpenConnectionAsync_SetsJournalModeToWal()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var manager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);

            using var connection = await manager.OpenConnectionAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            var mode = (string)(await command.ExecuteScalarAsync())!;

            Assert.Equal("wal", mode, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task OpenConnectionAsync_CalledTwice_BothConnectionsAreUsable()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var manager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);

            using var first = await manager.OpenConnectionAsync();
            using var second = await manager.OpenConnectionAsync();

            using var command1 = first.CreateCommand();
            command1.CommandText = "SELECT 1;";
            Assert.Equal(1L, await command1.ExecuteScalarAsync());

            using var command2 = second.CreateCommand();
            command2.CommandText = "SELECT 2;";
            Assert.Equal(2L, await command2.ExecuteScalarAsync());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
