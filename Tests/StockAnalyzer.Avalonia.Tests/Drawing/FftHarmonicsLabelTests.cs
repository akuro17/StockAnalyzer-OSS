using System;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>The FFT header label (invariant culture) is built once per replacement of DominantHarmonics (and not on every read), from the first three harmonics.</summary>
public class FftHarmonicsLabelTests
{
    private static FftHarmonicComponent H(double period) => new(1, 1.0 / period, period, 1.0, 1.0, 0.0);

    [Fact]
    public void Label_ListsTheFirstThreePeriodsWithOneDecimal()
    {
        var obj = new FftProjectionObject { DominantHarmonics = new[] { H(20.04), H(9.087), H(5.882), H(5.0) } };

        Assert.Equal("FFT Harmonics: 20.0b, 9.1b, 5.9b", obj.HarmonicsLabel);
    }

    [Fact]
    public void Label_IsTheSameInstanceUntilTheHarmonicsAreReplaced()
    {
        var obj = new FftProjectionObject { DominantHarmonics = new[] { H(10) } };

        string first = obj.HarmonicsLabel;
        Assert.Same(first, obj.HarmonicsLabel);

        obj.DominantHarmonics = new[] { H(10), H(4) };
        Assert.NotEqual(first, obj.HarmonicsLabel);
        Assert.Contains("4", obj.HarmonicsLabel);
    }

    [Fact]
    public void Label_OfNoHarmonicsIsTheBareHeader()
    {
        Assert.Equal("FFT Harmonics: ", new FftProjectionObject().HarmonicsLabel);
    }

    [Fact]
    public void Label_UsesTheInvariantCulture_WhateverTheCultureOfTheMachine()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var obj = new FftProjectionObject { DominantHarmonics = new[] { H(20.04), H(9.087) } };

            Assert.Equal("FFT Harmonics: 20.0b, 9.1b", obj.HarmonicsLabel);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
