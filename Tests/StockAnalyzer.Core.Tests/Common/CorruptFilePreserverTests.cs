using System;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>D-3' Run B / W3: timestamped, de-duplicated, bounded copies of a file that must be protected.</summary>
public sealed class CorruptFilePreserverTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 5, 12, 30, 45, DateTimeKind.Utc);

    private readonly string _directory;
    private readonly string _file;
    private readonly ManualClock _clock = new(Start);
    private readonly CorruptFilePreserver _preserver;

    public CorruptFilePreserverTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sa_preserver_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "workspace.json");
        _preserver = new CorruptFilePreserver(timeProvider: _clock);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualClock(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Preserved copies only (files that merely share the prefix are not counted), in ordinal text order.</summary>
    private string[] Copies() => Directory
        .GetFiles(_directory, CorruptedFile.PreservedSearchPattern("workspace.json"))
        .Where(p => CorruptedFile.TryParsePreservedSuffix(p[(_file.Length + CorruptedFile.PreservedMarker.Length)..], out _, out _))
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToArray();

    private void Write(string content) => File.WriteAllText(_file, content);

    // ---- the copy is written under a temporary name and moved into place (L-4) -----------------------------------------

    [Fact]
    public void Copy_LeavesNoTemporaryFile_AndHasTheContentOfTheOriginal()
    {
        Write("{ \"damaged\": ");

        Assert.True(_preserver.TryPreserve(_file, out var copy));

        Assert.Equal(CorruptedFile.PreservedPathFor(_file, Start), copy);
        Assert.Equal("{ \"damaged\": ", File.ReadAllText(copy!));
        Assert.Empty(Directory.GetFiles(_directory, "*" + CorruptFilePreserver.TemporarySuffix));
    }

    [Fact]
    public void AnInterruptedCopyLeftover_IsNeverTakenForAPreservedCopy_AndDoesNotBlockTheNextOne()
    {
        Write("v1");
        var target = CorruptedFile.PreservedPathFor(_file, Start);
        File.WriteAllText(target + CorruptFilePreserver.TemporarySuffix, "half"); // what a crash in the middle of a copy would leave

        Assert.True(_preserver.TryPreserve(_file, out var copy));

        Assert.Equal(target, copy);
        Assert.Equal("v1", File.ReadAllText(target));
        Assert.Single(Copies());                                                        // the leftover was not counted as a copy
        Assert.Empty(Directory.GetFiles(_directory, "*" + CorruptFilePreserver.TemporarySuffix)); // and the retry replaced it and moved it away
    }

    [Fact]
    public void AFailedMove_ReportsFalse_AndRemovesTheUnfinishedCopy()
    {
        Write("v1");
        Directory.CreateDirectory(CorruptedFile.PreservedPathFor(_file, Start)); // the final name is taken by something that is not a file

        Assert.False(_preserver.TryPreserve(_file, out var copy));

        Assert.Null(copy);
        Assert.Empty(Directory.GetFiles(_directory, "*" + CorruptFilePreserver.TemporarySuffix));
        Assert.Equal("v1", File.ReadAllText(_file));
    }

    // ---- naming (CorruptedFile) -------------------------------------------------------------------------------

    [Fact]
    public void Naming_FirstCopyHasNoOrdinalAndLaterOnesStartAtTwo()
    {
        Assert.Equal(_file + ".corrupt.20261105T123045Z", CorruptedFile.PreservedPathFor(_file, Start));
        Assert.Equal(_file + ".corrupt.20261105T123045Z-2", CorruptedFile.PreservedPathFor(_file, Start, 2));
        Assert.Equal(_file + ".corrupt.20261105T123045Z-13", CorruptedFile.PreservedPathFor(_file, Start, 13));
    }

    [Fact]
    public void Naming_LocalTimestampIsConvertedToUtc()
    {
        var local = new DateTime(2026, 11, 5, 21, 30, 45, DateTimeKind.Local);
        var expected = local.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        Assert.EndsWith(".corrupt." + expected, CorruptedFile.PreservedPathFor(_file, local));
    }

    [Fact]
    public void Naming_ZeroOrNegativeOrdinal_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CorruptedFile.PreservedPathFor(_file, Start, 0));
    }

    [Theory]
    [InlineData("20261105T123045Z", 1)]
    [InlineData("20261105T123045Z-2", 2)]
    [InlineData("20261105T123045Z-10", 10)]
    public void Naming_ParseRoundTrips(string suffix, int ordinal)
    {
        Assert.True(CorruptedFile.TryParsePreservedSuffix(suffix, out var utc, out var parsedOrdinal));
        Assert.Equal(Start, utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(ordinal, parsedOrdinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("notes.txt")]
    [InlineData("20261105T123045")]
    [InlineData("20261105T123045Z-")]
    [InlineData("20261105T123045Z-1")]
    [InlineData("20261105T123045Z-0")]
    [InlineData("20261105T123045Z-x")]
    [InlineData("20261105T123045Z-2-3")]
    [InlineData("20261105T123045Z.bak")]
    [InlineData("20261305T123045Z")]
    public void Naming_ParseRejectsAnythingThatIsNotOurName(string suffix)
    {
        Assert.False(CorruptedFile.TryParsePreservedSuffix(suffix, out _, out _));
    }

    [Fact]
    public void Naming_ExistingCorruptedSuffixIsUnchanged()
    {
        Assert.Equal(_file + ".corrupted", CorruptedFile.PathFor(_file));
    }

    // ---- preserving -------------------------------------------------------------------------------------------

    [Fact]
    public void Preserve_MakesACopyAndLeavesTheOriginalUntouched()
    {
        Write("{ broken");
        var writeTime = File.GetLastWriteTimeUtc(_file);

        var ok = _preserver.TryPreserve(_file, out var preserved);

        Assert.True(ok);
        Assert.Equal(CorruptedFile.PreservedPathFor(_file, Start), preserved);
        Assert.Equal("{ broken", File.ReadAllText(preserved!));
        Assert.Equal("{ broken", File.ReadAllText(_file));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(_file));
        Assert.Single(Copies());
    }

    [Fact]
    public void Preserve_SameContentAgain_MakesNoNewCopyAndReturnsTheExistingOne()
    {
        Write("same");
        _preserver.TryPreserve(_file, out var first);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var ok = _preserver.TryPreserve(_file, out var second);

        Assert.True(ok);
        Assert.Equal(first, second);
        Assert.Single(Copies());
    }

    [Fact]
    public void Preserve_ChangedContent_MakesASecondCopy()
    {
        Write("one");
        _preserver.TryPreserve(_file, out var first);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write("two");

        _preserver.TryPreserve(_file, out var second);

        Assert.NotEqual(first, second);
        Assert.Equal(2, Copies().Length);
        Assert.Equal("two", File.ReadAllText(second!));
        Assert.Equal("one", File.ReadAllText(first!));
    }

    [Fact]
    public void Preserve_SameContentAsAnOlderButNotTheNewestCopy_StillMakesACopy()
    {
        Write("A");
        _preserver.TryPreserve(_file, out _);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write("B");
        _preserver.TryPreserve(_file, out _);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write("A");

        _preserver.TryPreserve(_file, out var third);

        Assert.Equal(3, Copies().Length);
        Assert.Equal("A", File.ReadAllText(third!));
    }

    [Fact]
    public void Preserve_DifferentContentInTheSameSecond_GetsAnOrdinalSuffix()
    {
        Write("one");
        _preserver.TryPreserve(_file, out var first);
        Write("two");
        _preserver.TryPreserve(_file, out var second);
        Write("three");
        _preserver.TryPreserve(_file, out var third);

        Assert.Equal(CorruptedFile.PreservedPathFor(_file, Start), first);
        Assert.Equal(CorruptedFile.PreservedPathFor(_file, Start, 2), second);
        Assert.Equal(CorruptedFile.PreservedPathFor(_file, Start, 3), third);
    }

    [Fact]
    public void Preserve_KeepsOnlyTheNewestCopies_AndDeletesTheOldest()
    {
        var made = new System.Collections.Generic.List<string>();
        for (var i = 0; i < CorruptFilePreserver.MaxPreservedCopies + 3; i++)
        {
            Write("content " + i);
            _preserver.TryPreserve(_file, out var path);
            made.Add(path!);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var remaining = Copies();

        Assert.Equal(CorruptFilePreserver.MaxPreservedCopies, remaining.Length);
        Assert.Equal(made.Skip(3).OrderBy(p => p, StringComparer.Ordinal), remaining);
        Assert.All(made.Take(3), p => Assert.False(File.Exists(p)));
    }

    [Fact]
    public void Preserve_RetentionOrdersByTimeThenOrdinal_NotByTextOrder()
    {
        // Ordinals 2..10 within one second: text order would put "-10" before "-2".
        for (var i = 0; i < CorruptFilePreserver.MaxPreservedCopies + 5; i++)
        {
            Write("content " + i);
            _preserver.TryPreserve(_file, out _);
        }

        var survivors = Copies().Select(p => p[(_file.Length + CorruptedFile.PreservedMarker.Length)..]).ToArray();

        Assert.Equal(CorruptFilePreserver.MaxPreservedCopies, survivors.Length);
        Assert.Contains("20261105T123045Z-10", survivors);
        Assert.DoesNotContain("20261105T123045Z", survivors);
    }

    [Fact]
    public void Preserve_ClockMovedBackwards_NeverDeletesTheCopyJustMade()
    {
        for (var i = 0; i < CorruptFilePreserver.MaxPreservedCopies; i++)
        {
            Write("old " + i);
            _preserver.TryPreserve(_file, out _);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        _clock.Advance(TimeSpan.FromDays(-30));
        Write("made with a wrong clock");
        _preserver.TryPreserve(_file, out var path);

        Assert.True(File.Exists(path));
        Assert.Equal(CorruptFilePreserver.MaxPreservedCopies, Copies().Length);
    }

    [Fact]
    public void Preserve_IgnoresAndNeverDeletesUnrelatedFiles()
    {
        var foreign = new[]
        {
            _file + ".corrupted",
            _file + ".corrupt.notes.txt",
            _file + ".corrupt.20261105T123045Z.bak",
            Path.Combine(_directory, "other.json.corrupt.20200101T000000Z"),
            _file + ".tmp"
        };
        foreach (var path in foreign)
        {
            File.WriteAllText(path, "keep me");
        }

        for (var i = 0; i < CorruptFilePreserver.MaxPreservedCopies + 2; i++)
        {
            Write("content " + i);
            _preserver.TryPreserve(_file, out _);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.All(foreign, p => Assert.Equal("keep me", File.ReadAllText(p)));
        Assert.Equal(CorruptFilePreserver.MaxPreservedCopies, Copies().Length);
    }

    [Fact]
    public void Preserve_MissingFile_ReturnsFalseAndCreatesNothing()
    {
        var ok = _preserver.TryPreserve(_file, out var path);

        Assert.False(ok);
        Assert.Null(path);
        Assert.Empty(Copies());
    }

    [Fact]
    public void Preserve_MissingDirectory_ReturnsFalse()
    {
        Assert.False(_preserver.TryPreserve(Path.Combine(_directory, "nope", "workspace.json"), out _));
    }

    [Fact]
    public void Preserve_CopyFails_ReturnsFalseWithoutThrowingAndKeepsTheOriginal()
    {
        Write("precious");
        // A directory squatting on the target name makes the copy fail (IOException / UnauthorizedAccessException).
        // The probe name is what the preserver would pick, because File.Exists is false for a directory.
        Directory.CreateDirectory(CorruptedFile.PreservedPathFor(_file, Start));

        var ok = _preserver.TryPreserve(_file, out var path);

        Assert.False(ok);
        Assert.Null(path);
        Assert.Equal("precious", File.ReadAllText(_file));
    }

    [Fact]
    public void Preserve_NullOrBlankPath_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => _preserver.TryPreserve(null!, out _));
        Assert.ThrowsAny<ArgumentException>(() => _preserver.TryPreserve("  ", out _));
    }

    [Fact]
    public void MaxPreservedCopies_IsFiveAsDecidedByTheOwner()
    {
        Assert.Equal(5, CorruptFilePreserver.MaxPreservedCopies);
    }
}
