using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services.Drawing;

public class DrawingDocumentRepositoryTests : IDisposable
{
    private const string TestTicker = "ZZTEST-V2DOCREPO";
    private readonly List<string> _createdFiles = new();
    private readonly DrawingDocumentRepository _repository;
    private readonly DrawingDocumentSessionStore _sessionStore;

    public DrawingDocumentRepositoryTests()
    {
        _repository = new DrawingDocumentRepository();
        _sessionStore = new DrawingDocumentSessionStore();
    }

    private static string ResolveFilePath(TimeframeType timeframe)
    {
        var dir = PathDiscovery.ResolveDataPath(null, "Data/Drawings");
        return Path.Combine(dir, $"{TestTicker}.{timeframe}.json");
    }

    public void Dispose()
    {
        foreach (var file in _createdFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
                var bak = file + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
                var corrupted = file + ".corrupted";
                if (File.Exists(corrupted)) File.Delete(corrupted);
                var dir = Path.GetDirectoryName(file);
                if (Directory.Exists(dir))
                {
                    foreach (var extraBak in Directory.GetFiles(dir, $"{Path.GetFileName(file)}.bak*"))
                    {
                        File.Delete(extraBak);
                    }
                }
            }
            catch
            {
                // Best-effort test cleanup
            }
        }
    }

    [Fact]
    public async Task SaveAndLoad_V2Document_RoundTripsSuccessfully()
    {
        var key = new DrawingDocumentKey(TestTicker, TimeframeType.Daily);
        var path = ResolveFilePath(TimeframeType.Daily);
        _createdFiles.Add(path);

        var points = new[]
        {
            DrawingStoredPoint.CreateUtc(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), 1000m),
            DrawingStoredPoint.CreateUtc(new DateTime(2024, 3, 2, 0, 0, 0, DateTimeKind.Utc), 1050m)
        };

        var record = new DrawingObjectRecord(
            Guid.NewGuid(),
            nameof(TrendLineObject),
            PanelKey.Main,
            DrawingCoordinateKind.UtcTime,
            points,
            new Dictionary<string, object?> { ["thickness"] = 2.0 }
        );

        var context = new DrawingContextState(
            ChartDrawingContextType.Standard.ToString(),
            new[] { new DrawingLayerRecord(Guid.NewGuid(), "Default", PanelKey.Main, true, false, new[] { record.ObjectId }) },
            new[] { record }
        );

        var doc = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision: 1,
            new[] { context }
        );

        // Save
        var saveResult = await _repository.SaveDocumentAsync(doc);
        Assert.True(saveResult.IsSuccess);
        Assert.Equal(DrawingSaveStatus.Success, saveResult.Status);
        Assert.Equal(1, saveResult.CommittedRevision);
        Assert.True(File.Exists(path));

        // Load
        var loadResult = await _repository.LoadDocumentAsync(key);
        Assert.True(loadResult.IsSuccess);
        Assert.Equal(DrawingLoadStatus.Success, loadResult.Status);
        Assert.NotNull(loadResult.Document);

        var loadedDoc = loadResult.Document.Value;
        Assert.Equal(doc.Version, loadedDoc.Version);
        Assert.Equal(doc.Key, loadedDoc.Key);
        Assert.Equal(doc.Revision, loadedDoc.Revision);
        Assert.Single(loadedDoc.Contexts);

        var loadedRecord = loadedDoc.Contexts[0].Objects[0];
        Assert.Equal(record.ObjectId, loadedRecord.ObjectId);
        Assert.Equal(record.TypeName, loadedRecord.TypeName);
        Assert.Equal(points[0].UtcTime, loadedRecord.Points[0].UtcTime);
    }

    [Fact]
    public async Task LoadDocument_FileNotFound_ReturnsNotFoundStatus()
    {
        var key = new DrawingDocumentKey("ZZNONEXISTENT", TimeframeType.Weekly);
        var result = await _repository.LoadDocumentAsync(key);

        Assert.False(result.IsSuccess);
        Assert.Equal(DrawingLoadStatus.NotFound, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task LoadDocument_LegacyFormatFile_ReturnsMigrationRequired()
    {
        var key = new DrawingDocumentKey(TestTicker, TimeframeType.Weekly);
        var path = ResolveFilePath(TimeframeType.Weekly);
        _createdFiles.Add(path);

        // Write a legacy format JSON (without "version" property)
        var legacyJson = "{\n  \"Standard\": [],\n  \"Linear\": [],\n  \"Renko\": []\n}";
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, legacyJson);

        var result = await _repository.LoadDocumentAsync(key);

        Assert.False(result.IsSuccess);
        Assert.Equal(DrawingLoadStatus.MigrationRequired, result.Status);
        Assert.Null(result.Document);

        // Original file must remain untouched
        Assert.Equal(legacyJson, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SaveDocument_ExistingLegacyV1File_RejectsOverwrite()
    {
        // R08 test: Protect legacy V1 file from being overwritten by SaveDocumentAsync
        var key = new DrawingDocumentKey(TestTicker, TimeframeType.M15);
        var path = ResolveFilePath(TimeframeType.M15);
        _createdFiles.Add(path);

        var legacyJson = "{\n  \"Standard\": []\n}";
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, legacyJson);

        var dummyRecord = new DrawingObjectRecord(
            Guid.NewGuid(),
            nameof(TrendLineObject),
            PanelKey.Main,
            DrawingCoordinateKind.UtcTime,
            new[] { DrawingStoredPoint.CreateUtc(DateTime.UtcNow, 100m) },
            null
        );

        var v2Doc = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision: 1,
            new[] { new DrawingContextState("Standard", null, new[] { dummyRecord }) }
        );

        var saveResult = await _repository.SaveDocumentAsync(v2Doc);

        Assert.False(saveResult.IsSuccess);
        Assert.Equal(DrawingSaveStatus.IoFailure, saveResult.Status);
        Assert.Contains("Cannot overwrite legacy format", saveResult.ErrorMessage);

        // Verify legacy file was preserved intact
        Assert.Equal(legacyJson, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SaveDocument_CreatesBackupAndPreservesRevisionBackup()
    {
        var key = new DrawingDocumentKey(TestTicker, TimeframeType.Monthly);
        var path = ResolveFilePath(TimeframeType.Monthly);
        _createdFiles.Add(path);

        var dummyRecord = new DrawingObjectRecord(
            Guid.NewGuid(),
            nameof(TrendLineObject),
            PanelKey.Main,
            DrawingCoordinateKind.UtcTime,
            new[] { DrawingStoredPoint.CreateUtc(DateTime.UtcNow, 100m) },
            null
        );

        var doc1 = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision: 1,
            new[] { new DrawingContextState("Standard", null, new[] { dummyRecord }) }
        );

        var doc2 = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision: 2,
            new[] { new DrawingContextState("Standard", null, new[] { dummyRecord }) }
        );

        var doc3 = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision: 3,
            new[] { new DrawingContextState("Standard", null, new[] { dummyRecord }) }
        );

        // First save: no backup should exist yet
        await _repository.SaveDocumentAsync(doc1);
        var bakPath = path + ".bak";
        Assert.False(File.Exists(bakPath));

        // Second save: path.bak should be created containing doc1
        await _repository.SaveDocumentAsync(doc2);
        Assert.True(File.Exists(bakPath));

        // Third save: path.bak exists, so a generation backup should be created without overwriting .bak
        await _repository.SaveDocumentAsync(doc3);
        var dir = Path.GetDirectoryName(path)!;
        var extraBaks = Directory.GetFiles(dir, $"{Path.GetFileName(path)}.bak.rev3*");
        Assert.NotEmpty(extraBaks);
    }

    [Fact]
    public async Task SaveDocument_StaleRevision_Rejected()
    {
        var key = new DrawingDocumentKey(TestTicker, TimeframeType.H1);
        var path = ResolveFilePath(TimeframeType.H1);
        _createdFiles.Add(path);

        var dummyRecord = new DrawingObjectRecord(
            Guid.NewGuid(),
            nameof(TrendLineObject),
            PanelKey.Main,
            DrawingCoordinateKind.UtcTime,
            new[] { DrawingStoredPoint.CreateUtc(DateTime.UtcNow, 100m) },
            null
        );

        var docRev2 = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision: 2,
            new[] { new DrawingContextState("Standard", null, new[] { dummyRecord }) }
        );

        var docRev1 = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision: 1,
            new[] { new DrawingContextState("Standard", null, new[] { dummyRecord }) }
        );

        // Save revision 2 first
        var res1 = await _repository.SaveDocumentAsync(docRev2);
        Assert.True(res1.IsSuccess);

        // Try to save older revision 1 -> should be rejected
        var res2 = await _repository.SaveDocumentAsync(docRev1);
        Assert.False(res2.IsSuccess);
        Assert.Equal(DrawingSaveStatus.IoFailure, res2.Status);
        Assert.Contains("Stale revision", res2.ErrorMessage);
    }

    [Fact]
    public void SessionStore_NormalizedTicker_ProducesSameSessionReference()
    {
        // R04 test: 7203 and 7203-T normalize to the same key and produce the same session instance
        var key1 = new DrawingDocumentKey("7203", TimeframeType.Daily);
        var key2 = new DrawingDocumentKey("7203-T", TimeframeType.Daily);

        Assert.Equal(key1, key2);

        var session1 = _sessionStore.GetOrCreate(key1);
        var session2 = _sessionStore.GetOrCreate(key2);

        Assert.NotNull(session1);
        Assert.Same(session1, session2); // ReferenceEquals == true
    }

    [Fact]
    public void Session_TryUpdateDocument_AlignsRevisionAcrossStateAndEvent()
    {
        // R05 test: Session.Revision, CurrentDocument.Revision, and event notification must align
        var key = new DrawingDocumentKey("6758", TimeframeType.Daily);
        var session = _sessionStore.GetOrCreate(key);

        DrawingDocumentState? eventDoc = null;
        session.DocumentChanged += (_, doc) => eventDoc = doc;

        var inputDoc = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision: 10,
            null
        );

        Assert.True(session.TryUpdateDocument(inputDoc));

        Assert.NotNull(session.CurrentDocument);
        Assert.NotNull(eventDoc);
        Assert.Equal(10, session.Revision);
        Assert.Equal(10, session.CurrentDocument.Value.Revision);
        Assert.Equal(10, eventDoc.Value.Revision);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void Session_EditLocking_ProvidesMutualExclusion()
    {
        var key = new DrawingDocumentKey("6758", TimeframeType.Weekly);
        var session = _sessionStore.GetOrCreate(key);

        // Tab A acquires edit
        Assert.True(session.TryAcquireEdit(out var tokenA));
        Assert.NotEqual(Guid.Empty, tokenA);

        // Tab B attempts to acquire edit -> denied
        Assert.False(session.TryAcquireEdit(out var tokenB));
        Assert.Equal(Guid.Empty, tokenB);

        // Tab B attempts update with invalid token -> denied
        var dummyDoc = new DrawingDocumentState(DrawingDocumentState.CurrentSchemaVersion, key, 1, null);
        Assert.False(session.TryUpdateDocument(dummyDoc, token: Guid.NewGuid()));

        // Tab A updates successfully
        Assert.True(session.TryUpdateDocument(dummyDoc, token: tokenA));
        Assert.True(session.IsDirty);

        // Tab A releases edit
        session.ReleaseEdit(tokenA);

        // Now Tab B can acquire edit
        Assert.True(session.TryAcquireEdit(out var tokenBNew));
        Assert.NotEqual(Guid.Empty, tokenBNew);
    }

    [Fact]
    public void Session_TryUpdateDocument_MismatchedKey_Rejected()
    {
        // V03 test: Verify document key mismatch is rejected
        var key = new DrawingDocumentKey("7203", TimeframeType.Daily);
        var session = _sessionStore.GetOrCreate(key);

        var wrongKeyDoc = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            new DrawingDocumentKey("9984", TimeframeType.Daily),
            1,
            null
        );

        Assert.False(session.TryUpdateDocument(wrongKeyDoc));
        Assert.Null(session.CurrentDocument);

        Assert.False(session.TryReset(wrongKeyDoc));
        Assert.Null(session.CurrentDocument);
    }

    [Fact]
    public async Task SaveDocument_EmptyObjectsButCustomLayer_CreatesFile()
    {
        // V02 test: Even if objects count is 0, custom layer metadata must trigger file creation
        var key = new DrawingDocumentKey(TestTicker, TimeframeType.Daily);
        var path = ResolveFilePath(TimeframeType.Daily);
        _createdFiles.Add(path);

        var customLayer = new DrawingLayerRecord(
            Guid.NewGuid(),
            "Custom Support Layer",
            PanelKey.Main,
            isVisible: true,
            isEditLocked: false,
            null
        );

        var doc = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision: 1,
            new[] { new DrawingContextState("Standard", new[] { customLayer }, null) }
        );

        var saveResult = await _repository.SaveDocumentAsync(doc);
        Assert.True(saveResult.IsSuccess);
        Assert.True(File.Exists(path));

        var loadResult = await _repository.LoadDocumentAsync(key);
        Assert.True(loadResult.IsSuccess);
        var loadedLayer = loadResult.Document!.Value.Contexts[0].Layers[0];
        Assert.Equal("Custom Support Layer", loadedLayer.Name);
    }

}
