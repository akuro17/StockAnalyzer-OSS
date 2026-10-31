using System.IO;
using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// The name of the kept copy of an unreadable file is part of what users and support find on disk. The stores that keep such a copy take it from
/// <see cref="CorruptedFile"/> (constraint check V-1); this test pins the value itself, so a rename is a deliberate act and not a side effect.
/// </summary>
public sealed class CorruptedFileTests
{
    [Fact]
    public void Suffix_IsTheNameUsersFindOnDisk()
    {
        Assert.Equal(".corrupted", CorruptedFile.Suffix);
    }

    [Fact]
    public void PathFor_AppendsTheSuffixToTheWholeFileName()
    {
        string path = Path.Combine("data", "chart_settings.json");

        Assert.Equal(path + ".corrupted", CorruptedFile.PathFor(path));
    }
}
