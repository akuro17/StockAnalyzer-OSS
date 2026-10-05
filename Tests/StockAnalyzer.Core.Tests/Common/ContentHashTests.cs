using System;
using System.IO;
using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

public sealed class ContentHashTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sa_content_hash_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void OfFile_IsTheLowerCaseSha256OfTheBytes()
    {
        File.WriteAllBytes(_path, "abc"u8.ToArray());

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ContentHash.OfFile(_path));
        Assert.True(ContentHash.IsLowerHex64(ContentHash.OfFile(_path)));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLowerHex64_AcceptsExactlySixtyFourLowerCaseHexCharacters(string? value, bool expected) =>
        Assert.Equal(expected, ContentHash.IsLowerHex64(value));
}
