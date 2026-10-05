#nullable enable
using System;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>The single definitions shared by several monitoring components (one file lock predicate, one device name policy, one timeframe set, one folder derivation).</summary>
public sealed class MonitoringSharedDefinitionsTests
{
    private const char SuperscriptOne = (char)0xB9, SuperscriptTwo = (char)0xB2, SuperscriptThree = (char)0xB3;

    [Theory]
    [InlineData(32, true)]                    // ERROR_SHARING_VIOLATION
    [InlineData(33, true)]                    // ERROR_LOCK_VIOLATION
    [InlineData(11, true)]                    // EAGAIN
    [InlineData(unchecked((int)0x80070020), true)]   // the same code as an HRESULT
    [InlineData(5, false)]                    // access denied is not "held by another owner"
    [InlineData(0, false)]
    public void FileLockViolation_RecognizesTheHeldFileCodes_AsTheOneSharedPredicate(int hresult, bool held) =>
        Assert.Equal(held, FileLockViolation.IsHeldByAnotherOwner(new IOException("x", hresult)));

    [Theory]
    [InlineData("CON", true)] [InlineData("con", true)] [InlineData("PRN", true)] [InlineData("AUX", true)] [InlineData("NUL", true)]
    [InlineData("COM1", true)] [InlineData("com9", true)] [InlineData("LPT1", true)] [InlineData("lpt9", true)]
    [InlineData("COM0", false)] [InlineData("COM10", false)] [InlineData("CONSOLE", false)] [InlineData("COM", false)] [InlineData("", false)]
    public void WindowsDeviceFileName_ReservesTheDocumentedNames(string name, bool reserved) =>
        Assert.Equal(reserved, WindowsDeviceFileName.IsReserved(name));

    [Fact]
    public void WindowsDeviceFileName_ReservesTheSuperscriptDigitVariants()
    {
        foreach (var digit in new[] { SuperscriptOne, SuperscriptTwo, SuperscriptThree })
        {
            Assert.True(WindowsDeviceFileName.IsReserved("COM" + digit));
            Assert.True(WindowsDeviceFileName.IsReserved("lpt" + digit));
            Assert.False(WindowsDeviceFileName.IsReserved("CON" + digit));
        }
    }

    [Fact]
    public void MonitoringTimeframes_AreExactlyDailyWeeklyAndMonthly_ForBothEnums()
    {
        var supported = Enum.GetValues<TimeframeType>().Where(MonitoringTimeframes.IsSupported).ToArray();
        Assert.Equal([TimeframeType.Daily, TimeframeType.Weekly, TimeframeType.Monthly], supported);
        foreach (var frame in Enum.GetValues<TimeFrame>())
        {
            var mapped = MonitoringTimeframes.TryFrom(frame, out var timeframe);
            Assert.Equal(frame is TimeFrame.D1 or TimeFrame.W1 or TimeFrame.MN1, mapped);
            if (mapped) Assert.True(MonitoringTimeframes.IsSupported(timeframe));
        }
        Assert.True(MonitoringTimeframes.TryFrom(TimeFrame.W1, out var weekly) && weekly == TimeframeType.Weekly);
    }

    [Fact]
    public void MonitoringFolders_AreDerivedOnceUnderDataPredictionMonitoring()
    {
        var root = PredictionMonitoringPaths.DefaultRoot();
        Assert.Equal("PredictionMonitoring", Path.GetFileName(root));
        Assert.Equal("Data", Path.GetFileName(Path.GetDirectoryName(root)));
        var copies = PredictionMonitoringPaths.AttestedCopyRoot(root);
        Assert.Equal(root, Path.GetDirectoryName(copies));
        Assert.Equal("attested", Path.GetFileName(copies));
    }
}
