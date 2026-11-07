#nullable enable
using System;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

public sealed class TrainingRunIdTests
{
    [Fact]
    public void TheTimeOfARunIdIsReadBackAsUtcToTheMillisecond()
    {
        Assert.True(TrainingRunId.TryGetTimestamp("20260901-102030123-abcdef12", out DateTimeOffset started));

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 20, 30, 123, TimeSpan.Zero), started);
        Assert.Equal(TimeSpan.Zero, started.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-09-01")]
    [InlineData("20260901-102030123-ABCDEF12")]   // upper-case hex is not the format
    [InlineData("20260901-102030123-abcdef1")]    // suffix too short
    [InlineData("20261301-102030123-abcdef12")]   // month 13 matches the pattern but is no date
    [InlineData("20260901-256030123-abcdef12")]   // hour 25
    [InlineData("20260230-102030123-abcdef12")]   // 30 February
    public void AnythingThatIsNotARunIdHasNoTime(string? text)
    {
        Assert.False(TrainingRunId.TryGetTimestamp(text, out DateTimeOffset started));
        Assert.Equal(default, started);
    }

    [Fact]
    public void ANewIdIsAlwaysBuiltFromTheUtcTimeEvenWhenALocalOffsetIsGiven()
    {
        var local = new DateTimeOffset(2026, 9, 1, 19, 20, 30, 123, TimeSpan.FromHours(9));

        string id = TrainingRunId.New(local);

        Assert.StartsWith("20260901-102030123-", id, StringComparison.Ordinal);
        Assert.True(TrainingRunId.IsValid(id));
        Assert.True(TrainingRunId.TryGetTimestamp(id, out DateTimeOffset started));
        Assert.Equal(local.ToUniversalTime(), started);
    }
}
