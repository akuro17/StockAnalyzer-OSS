using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models.GeometricPattern;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// WebAI UI-exposure Task 5: the Data Window rows of <see cref="GeometricPatternObject"/>.
/// </summary>
public class GeometricPatternObjectCalculatedValuesTests
{
    private static readonly DateTime BaseTime = new(2024, 1, 1);

    private static DetectedFormation Formation(
        GeometricFormationType type, int endDay, double confidence, double upperSlope = 0.5, double lowerSlope = -0.25, bool brokenOut = true)
        => new(type, 0, endDay, upperSlope, 110.0, lowerSlope, 100.0, confidence, false, BaseTime, BaseTime.AddDays(endDay))
        {
            IsBrokenOut = brokenOut
        };

    private static GeometricPatternObject Create(params DetectedFormation[] formations)
        => new() { Formations = formations };

    [Fact]
    public void NoFormations_ReturnsNoRows()
    {
        Assert.Empty(new GeometricPatternObject().GetCalculatedValues(BaseTime));
        Assert.Empty(Create().GetCalculatedValues(BaseTime));
    }

    [Fact]
    public void OnlyNotBrokenOutFormations_ReturnsNoRows()
    {
        var obj = Create(Formation(GeometricFormationType.Pennant, 10, 0.9, brokenOut: false));

        Assert.Empty(obj.GetCalculatedValues(BaseTime));
    }

    [Fact]
    public void FormationsHiddenByDisplayFilter_AreNotCounted()
    {
        var obj = Create(
            Formation(GeometricFormationType.AscendingChannel, 10, 0.8),
            Formation(GeometricFormationType.Megaphone, 20, 0.7));
        obj.ShowMegaphone = false;

        var rows = obj.GetCalculatedValues(BaseTime);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1m, rows[0].NumericValue);
        Assert.Equal("Detected Formations", rows[0].Label);
        Assert.Equal("Formation #1 (AscendingChannel)", rows[1].Label);
    }

    [Fact]
    public void AllTypesFiltered_ReturnsNoRows()
    {
        var obj = Create(Formation(GeometricFormationType.Megaphone, 10, 0.8));
        obj.ShowMegaphone = false;

        Assert.Empty(obj.GetCalculatedValues(BaseTime));
    }

    [Fact]
    public void Rows_AreOrderedByEndTimeDescendingThenConfidenceDescending()
    {
        var obj = Create(
            Formation(GeometricFormationType.AscendingChannel, 10, 0.60),
            Formation(GeometricFormationType.DescendingChannel, 30, 0.50),
            Formation(GeometricFormationType.HorizontalBox, 30, 0.90),
            Formation(GeometricFormationType.Pennant, 20, 0.70));

        var rows = obj.GetCalculatedValues(BaseTime);

        Assert.Equal(5, rows.Count);
        Assert.Equal(4m, rows[0].NumericValue);
        Assert.Equal("Formation #1 (HorizontalBox)", rows[1].Label);
        Assert.Equal("Formation #2 (DescendingChannel)", rows[2].Label);
        Assert.Equal("Formation #3 (Pennant)", rows[3].Label);
        Assert.Equal("Formation #4 (AscendingChannel)", rows[4].Label);
    }

    [Fact]
    public void ListedFormations_AreCappedButCountIsComplete()
    {
        var formations = Enumerable.Range(1, GeometricPatternObject.MaxListedFormations + 3)
            .Select(i => Formation(GeometricFormationType.HorizontalBox, i, 0.5))
            .ToArray();

        var rows = Create(formations).GetCalculatedValues(BaseTime);

        Assert.Equal(GeometricPatternObject.MaxListedFormations + 1, rows.Count);
        Assert.Equal((decimal)formations.Length, rows[0].NumericValue);
        Assert.Equal(formations.Length.ToString(), rows[0].FormattedText);
    }

    [Fact]
    public void FormationRow_ExposesConfidencePercentAndSlopes()
    {
        var obj = Create(Formation(GeometricFormationType.RisingWedge, 10, 0.8762, upperSlope: 0.12345, lowerSlope: -0.5));

        var row = obj.GetCalculatedValues(BaseTime)[1];

        Assert.Equal(87.6m, row.NumericValue);
        Assert.Equal("Conf=88%, U-Slope=0.123, L-Slope=-0.500", row.FormattedText);
        Assert.Equal(row.Label, row.Key);
    }

    [Fact]
    public void Rows_UseObjectColourBecauseNoBreakoutDirectionExists()
    {
        var obj = Create(Formation(GeometricFormationType.BullishFlag, 10, 0.8));

        var rows = obj.GetCalculatedValues(BaseTime);

        var expected = new StockAnalyzer.Core.Models.IndicatorColor(obj.Color.A, obj.Color.R, obj.Color.G, obj.Color.B);
        Assert.All(rows, r => Assert.Equal(expected, r.Color));
    }

    [Fact]
    public void FormatsWithEnUsCultureRegardlessOfThreadCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var obj = Create(Formation(GeometricFormationType.RisingWedge, 10, 0.5, upperSlope: 1.5, lowerSlope: 0.25));

            var row = obj.GetCalculatedValues(BaseTime)[1];

            Assert.Equal("Conf=50%, U-Slope=1.500, L-Slope=0.250", row.FormattedText);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ObjectIsRecognisedAsCalculatedValuesProvider()
    {
        Assert.IsAssignableFrom<IDrawingCalculatedValuesProvider>(new GeometricPatternObject());
    }
}
