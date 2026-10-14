namespace StockAnalyzer.Core.Tests.Common;

using System;
using StockAnalyzer.Core.Common;
using Xunit;

public class ExceptionSanitizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void Sanitize_HandlesNullOrEmptyInput(string? input, string expected)
    {
        var result = ExceptionSanitizer.Sanitize(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(@"C:\Users\JohnDoe\AppData\Local\StockAnalyzer\data.db", @"<USER_DIR>\AppData\Local\StockAnalyzer\data.db")]
    [InlineData(@"d:\users\alice\documents\report.pdf", @"<USER_DIR>\documents\report.pdf")]
    [InlineData(@"C:\Documents and Settings\Bob\data.log", @"<USER_DIR>\data.log")]
    [InlineData(@"/Users/charlie/Library/Application Support", @"<USER_DIR>/Library/Application Support")]
    [InlineData(@"/home/david/.config/StockAnalyzer", @"<USER_DIR>/.config/StockAnalyzer")]
    public void Sanitize_MasksUserProfileDirectories(string input, string expected)
    {
        var result = ExceptionSanitizer.Sanitize(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Sanitize_PreservesNonSensitivePaths()
    {
        var input = @"C:\Program Files\StockAnalyzer\app.dll";
        var result = ExceptionSanitizer.Sanitize(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Sanitize_TruncatesExcessivelyLongStrings()
    {
        var largeString = new string('A', 70000);
        var result = ExceptionSanitizer.Sanitize(largeString);

        Assert.True(result.Length < 70000);
        Assert.Contains("[... TRUNCATED FOR SECURITY]", result);
    }

    [Fact]
    public void FormatStartupException_NullReturnsDefaultMessage()
    {
        var result = ExceptionSanitizer.FormatStartupException(null);
        Assert.Equal("An unknown error occurred during startup.", result);
    }

    [Fact]
    public void FormatStartupException_FormatsAndSanitizesExceptionMessage()
    {
        var ex = new InvalidOperationException(@"Failed to read file from C:\Users\TestUser\secret.json");
        var result = ExceptionSanitizer.FormatStartupException(ex);

        Assert.Contains("System.InvalidOperationException", result);
        Assert.Contains(@"<USER_DIR>\secret.json", result);
        Assert.DoesNotContain("TestUser", result);
        Assert.Contains("logs/", result);
    }
}
