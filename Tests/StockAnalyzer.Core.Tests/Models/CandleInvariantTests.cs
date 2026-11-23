using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public sealed class CandleInvariantTests
{
    private static readonly DateTime SampleDate = new(2022, 5, 17, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DescribeIsEmptyExactlyWhenIsValidAndAllPricesArePositiveAcrossValueGrid()
    {
        decimal[] prices = { -1m, 0m, 1m, 2m, 3m };
        long[] volumes = { -1L, 0L, 100L };

        foreach (decimal o in prices)
        {
            foreach (decimal h in prices)
            {
                foreach (decimal l in prices)
                {
                    foreach (decimal c in prices)
                    {
                        foreach (long v in volumes)
                        {
                            CandleData bar = CandleData.CreateUnchecked(SampleDate, o, h, l, c, v);
                            bool expectedValid = bar.IsValid() && o > 0m && h > 0m && l > 0m && c > 0m;
                            IReadOnlyList<CandleViolationCode> violations = CandleInvariant.Describe(bar);

                            Assert.Equal(expectedValid, CandleInvariant.IsValidBar(bar));
                            Assert.Equal(expectedValid, violations.Count == 0);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void ObservedAuditBarsProduceExactViolationCodesAndMessages()
    {
        CandleData tokyoElectron = CandleData.CreateUnchecked(SampleDate, 3821.333m, 3866.667m, 3770.667m, 3869.333m, 1000L);
        IReadOnlyList<CandleViolationCode> tokyoViolations = CandleInvariant.Describe(tokyoElectron);
        Assert.Equal(new[] { CandleViolationCode.HighBelowClose }, tokyoViolations);
        string tokyoText = CandleInvariant.FormatBarViolation("8035-T", tokyoElectron, tokyoViolations);
        Assert.Contains("8035-T", tokyoText, StringComparison.Ordinal);
        Assert.Contains("2022-05-17", tokyoText, StringComparison.Ordinal);
        Assert.Contains("Close 3869.333 > High 3866.667", tokyoText, StringComparison.Ordinal);

        CandleData mizuho = CandleData.CreateUnchecked(SampleDate, 1500m, 1513.5m, 1487.5m, 1486.5m, 1000L);
        IReadOnlyList<CandleViolationCode> mizuhoViolations = CandleInvariant.Describe(mizuho);
        Assert.Equal(new[] { CandleViolationCode.LowAboveClose }, mizuhoViolations);
        string mizuhoText = CandleInvariant.FormatBarViolation("8411-T", mizuho, mizuhoViolations);
        Assert.Contains("8411-T", mizuhoText, StringComparison.Ordinal);
        Assert.Contains("2022-05-17", mizuhoText, StringComparison.Ordinal);
        Assert.Contains("Close 1486.5 < Low 1487.5", mizuhoText, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeReportsEveryViolatedInvariantInOrder()
    {
        CandleData bar = CandleData.CreateUnchecked(SampleDate, 0m, -2m, 5m, 6m, -10L);

        IReadOnlyList<CandleViolationCode> violations = CandleInvariant.Describe(bar);

        Assert.Equal(
            new[]
            {
                CandleViolationCode.HighBelowLow,
                CandleViolationCode.HighBelowOpen,
                CandleViolationCode.HighBelowClose,
                CandleViolationCode.LowAboveOpen,
                CandleViolationCode.NegativeVolume,
                CandleViolationCode.NonPositivePrice,
            },
            violations);
    }

    [Fact]
    public void TryFindFirstViolationRespectsOptionalDateBounds()
    {
        var bars = new[]
        {
            CandleData.CreateUnchecked(new DateTime(2022, 5, 16, 0, 0, 0, DateTimeKind.Utc), 100m, 110m, 90m, 105m, 100L),
            CandleData.CreateUnchecked(new DateTime(2022, 5, 17, 0, 0, 0, DateTimeKind.Utc), 100m, 105m, 95m, 110m, 100L),
            CandleData.CreateUnchecked(new DateTime(2022, 5, 18, 0, 0, 0, DateTimeKind.Utc), 100m, 110m, 90m, 105m, 100L),
        };

        Assert.True(CandleInvariant.TryFindFirstViolation(bars, out CandleData hit, out IReadOnlyList<CandleViolationCode> codes));
        Assert.Equal(new DateTime(2022, 5, 17, 0, 0, 0, DateTimeKind.Utc), hit.Timestamp);
        Assert.Equal(new[] { CandleViolationCode.HighBelowClose }, codes);

        Assert.False(CandleInvariant.TryFindFirstViolation(
            bars,
            out _,
            out _,
            startDateInclusive: new DateOnly(2022, 5, 18),
            endDateExclusive: new DateOnly(2022, 5, 19)));
    }
}
