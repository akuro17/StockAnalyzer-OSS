using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.ReadOnly;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary><see cref="FileReadOnlyDrawingDocumentSource"/>: the saved drawing files are read as they are, never created, converted, backed up or quarantined.</summary>
public sealed class ReadOnlyDrawingDocumentSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_ro_drawings_" + Guid.NewGuid().ToString("N"));

    public ReadOnlyDrawingDocumentSourceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FileReadOnlyDrawingDocumentSource Source() => new(_root);

    private static DrawingDocumentState Document(string ticker, TimeframeType timeframe, params DrawingObjectRecord[] objects)
    {
        var layer = new DrawingLayerRecord(Guid.NewGuid(), "Default Layer", PanelKey.Main, true, false, objects.Select(o => o.ObjectId).ToArray());
        var context = new DrawingContextState("Standard", new[] { layer }, objects);
        return new DrawingDocumentState(DrawingDocumentState.CurrentSchemaVersion, new DrawingDocumentKey(ticker, timeframe), 3, new[] { context });
    }

    private static DrawingObjectRecord TrendLine()
    {
        var points = new[]
        {
            DrawingStoredPoint.CreateUtc(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), 100.5m),
            DrawingStoredPoint.CreateUtc(new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc), 120m),
        };
        var parameters = new Dictionary<string, object?> { ["thickness"] = 2.0, ["color"] = "#FFFF0000", ["isLocked"] = false };
        return new DrawingObjectRecord(Guid.NewGuid(), "TrendLine", PanelKey.Main, DrawingCoordinateKind.UtcTime, points, parameters);
    }

    private string Write(string fileName, string content)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Json(DrawingDocumentState state) => JsonSerializer.Serialize(state, DrawingDocumentJsonOptions.Create());

    private Dictionary<string, long> Files() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, path => new FileInfo(path).Length);

    [Fact]
    public async Task AFileLargerThanTheByteLimit_IsDataUnavailable_AndIsNotRead_WhileAFileAtTheLimitIsRead()
    {
        string json = Json(Document("7203-T", TimeframeType.Daily, TrendLine()));
        int size = System.Text.Encoding.UTF8.GetByteCount(json);
        string path = Write("7203-T.Daily.json", json);
        byte[] before = File.ReadAllBytes(path);

        DrawingDocumentReadResult atTheLimit = await new FileReadOnlyDrawingDocumentSource(_root, size).ReadAsync("7203-T", TimeFrame.D1);
        Assert.Equal(DrawingDocumentReadStatus.Read, atTheLimit.Status);

        var error = await Assert.ThrowsAsync<DataUnavailableException>(() => new FileReadOnlyDrawingDocumentSource(_root, size - 1).ReadAsync("7203-T", TimeFrame.D1));
        Assert.DoesNotContain(_root, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void TheByteLimitMustBePositive_AndTheDefaultIsOneMebibyte()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileReadOnlyDrawingDocumentSource(_root, 0));
        Assert.Equal(1024 * 1024, FileReadOnlyDrawingDocumentSource.DefaultMaxFileBytes);
    }

    [Fact]
    public async Task NoFile_IsNotFound_AndNothingIsCreated_EvenWhenTheFolderDoesNotExist()
    {
        DrawingDocumentReadResult result = await Source().ReadAsync("7203-T", TimeFrame.D1);
        Assert.Equal(DrawingDocumentReadStatus.NotFound, result.Status);
        Assert.Null(result.Document);

        string missing = Path.Combine(_root, "not_there");
        DrawingDocumentReadResult inMissingFolder = await new FileReadOnlyDrawingDocumentSource(missing).ReadAsync("7203-T", TimeFrame.D1);
        Assert.Equal(DrawingDocumentReadStatus.NotFound, inMissingFolder.Status);
        Assert.False(Directory.Exists(missing));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task AV2Document_IsRead_AsTheApplicationWroteIt_AndTheFileIsLeftAlone()
    {
        DrawingObjectRecord trend = TrendLine();
        string path = Write("7203-T.Daily.json", Json(Document("7203-T", TimeframeType.Daily, trend)));
        byte[] before = File.ReadAllBytes(path);
        Dictionary<string, long> filesBefore = Files();

        DrawingDocumentReadResult result = await Source().ReadAsync("7203.T", TimeFrame.D1);

        Assert.Equal(DrawingDocumentReadStatus.Read, result.Status);
        DrawingContextState context = Assert.Single(result.Document!.Value.Contexts);
        DrawingObjectRecord read = Assert.Single(context.Objects);
        Assert.Equal(trend.ObjectId, read.ObjectId);
        Assert.Equal("TrendLine", read.TypeName);
        Assert.Equal(DrawingCoordinateKind.UtcTime, read.CoordinateKind);
        Assert.Equal(100.5m, read.Points[0].YValue);
        Assert.Equal(new DateTime(2026, 2, 5), read.Points[1].UtcTime!.Value.Date);
        Assert.Equal(JsonValueKind.Number, ((JsonElement)read.StyleAndParameters["thickness"]!).ValueKind);
        Assert.Equal("#FFFF0000", ((JsonElement)read.StyleAndParameters["color"]!).GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(filesBefore, Files());
    }

    [Theory]
    [InlineData(TimeFrame.D1, "Daily")]
    [InlineData(TimeFrame.W1, "Weekly")]
    [InlineData(TimeFrame.MN1, "Monthly")]
    public async Task TheTimeFrameChoosesTheFile(TimeFrame frame, string suffix)
    {
        Write($"MSFT.{suffix}.json", Json(Document("MSFT", Enum.Parse<TimeframeType>(suffix))));

        Assert.Equal(DrawingDocumentReadStatus.Read, (await Source().ReadAsync("msft", frame)).Status);
        Assert.Equal(DrawingDocumentReadStatus.NotFound, (await Source().ReadAsync("AAPL", frame)).Status);
    }

    [Fact]
    public async Task ALegacyFile_IsReportedAndNeverConverted()
    {
        string path = Write("7203-T.Daily.json", "{\"Objects\":{\"Standard\":[]},\"Layers\":{}}");
        byte[] before = File.ReadAllBytes(path);

        DrawingDocumentReadResult result = await Source().ReadAsync("7203-T", TimeFrame.D1);

        Assert.Equal(DrawingDocumentReadStatus.LegacyFormat, result.Status);
        Assert.Null(result.Document);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Files());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("{\"version\":3,\"contexts\":[]}")]
    [InlineData("{\"version\":\"two\"}")]
    public async Task AFileThatCannotBeUsed_IsDataUnavailable_NotNoDrawings_AndNoCorruptedCopyIsMade(string content)
    {
        string path = Write("7203-T.Daily.json", content);
        byte[] before = File.ReadAllBytes(path);

        await Assert.ThrowsAsync<DataUnavailableException>(() => Source().ReadAsync("7203-T", TimeFrame.D1));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Files());
    }

    [Fact]
    public async Task ADocumentOfAnotherSymbolOrTimeFrame_IsDataUnavailable()
    {
        Write("7203-T.Daily.json", Json(Document("MSFT", TimeframeType.Daily)));
        await Assert.ThrowsAsync<DataUnavailableException>(() => Source().ReadAsync("7203-T", TimeFrame.D1));

        Write("MSFT.Weekly.json", Json(Document("MSFT", TimeframeType.Daily)));
        await Assert.ThrowsAsync<DataUnavailableException>(() => Source().ReadAsync("MSFT", TimeFrame.W1));
    }

    [Fact]
    public async Task AFileTheApplicationHoldsOpenForWriting_CanStillBeRead()
    {
        string path = Write("MSFT.Daily.json", Json(Document("MSFT", TimeframeType.Daily, TrendLine())));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Write | FileShare.Delete);

        DrawingDocumentReadResult result = await Source().ReadAsync("MSFT", TimeFrame.D1);

        Assert.Equal(DrawingDocumentReadStatus.Read, result.Status);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("AAPL+MSFT")]
    [InlineData("")]
    public async Task ASymbolThatCannotHaveAFile_IsNotFound(string symbol)
    {
        Assert.Equal(DrawingDocumentReadStatus.NotFound, (await Source().ReadAsync(symbol, TimeFrame.D1)).Status);
    }

    [Theory]
    [InlineData(TimeFrame.H1)]
    [InlineData(TimeFrame.M5)]
    public async Task AnIntradayTimeFrame_IsRefused(TimeFrame frame)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Source().ReadAsync("MSFT", frame));
    }

    [Theory]
    [InlineData("7203", TimeframeType.Daily, "7203-T.Daily.json")]
    [InlineData("7203.t", TimeframeType.Weekly, "7203-T.Weekly.json")]
    [InlineData("msft", TimeframeType.Monthly, "MSFT.Monthly.json")]
    public void DrawingFileNames_UsesTheNormalizedTicker(string ticker, TimeframeType timeframe, string expected)
    {
        Assert.Equal(DrawingFileNameRefusal.None, DrawingFileNames.TryCreate(ticker, timeframe, out string normalized, out string fileName));
        Assert.Equal(expected, fileName);
        Assert.Equal(expected.Substring(0, expected.IndexOf('.')), normalized);
    }

    [Theory]
    [InlineData(null, DrawingFileNameRefusal.Empty)]
    [InlineData("  ", DrawingFileNameRefusal.Empty)]
    [InlineData("CON", DrawingFileNameRefusal.ReservedDeviceName)]
    [InlineData("a b", DrawingFileNameRefusal.InvalidCharacters)]
    [InlineData("AAPL+MSFT", DrawingFileNameRefusal.SyntheticExpression)]
    public void DrawingFileNames_NamesTheReasonThereIsNoFileName(string? ticker, DrawingFileNameRefusal expected)
    {
        Assert.Equal(expected, DrawingFileNames.TryCreate(ticker, TimeframeType.Daily, out string normalized, out string fileName));
        Assert.Empty(normalized);
        Assert.Empty(fileName);
    }
}
