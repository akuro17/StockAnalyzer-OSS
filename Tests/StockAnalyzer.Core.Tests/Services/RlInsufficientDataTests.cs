#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlInsufficientDataTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CatalogWithoutCompleteEpisodeSupportIsClassifiedAsInsufficientData(bool smoke)
    {
        var error = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.BuildCatalog(
            ImmutableArray<CandleData>.Empty, ImmutableArray<RlCorporateAction>.Empty, ImmutableArray<DateOnly>.Empty, smoke));
        Assert.True(RlSourceContract.IsInsufficientData(error));
    }

    [Fact]
    public void OnlyInvalidDataExceptionsCarryingTheMarkerAreInsufficientData()
    {
        Assert.True(RlSourceContract.IsInsufficientData(new InvalidDataException(RlSourceContract.InsufficientData("detail"))));
        Assert.False(RlSourceContract.IsInsufficientData(new InvalidDataException("Invalid or unavailable source bar 3; no repair is permitted.")));
        Assert.False(RlSourceContract.IsInsufficientData(new ArgumentException(RlSourceContract.InsufficientData("detail"))));
    }
}
