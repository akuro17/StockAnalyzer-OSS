using System;
using System.Collections.Generic;
using System.IO;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

public sealed class TransientFileLockRetryTests
{
    private const int Win32Facility = unchecked((int)0x80070000);
    private static IOException Win32(int code) => new("locked", Win32Facility | code);

    private sealed class DeniedException : UnauthorizedAccessException { public DeniedException(int hresult) : base("denied") => HResult = hresult; }
    private sealed class NotIoException : InvalidOperationException { public NotIoException(int hresult) => HResult = hresult; }
    private static readonly FileReplaceRetrySettings Policy = new() { Attempts = 3, DelayMilliseconds = 7 };

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    public void ATransientLock_IsRetriedWithTheConfiguredPause_AndThenSucceeds(int code)
    {
        if (!OperatingSystem.IsWindows()) return;
        var pauses = new List<TimeSpan>();
        int calls = 0;
        TransientFileLockRetry.Run(() => { if (++calls <= 2) throw Win32(code); }, Policy, sleep: pauses.Add);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(7) }, pauses);
    }

    [Fact]
    public void ExhaustedAttempts_RethrowTheLastError()
    {
        if (!OperatingSystem.IsWindows()) return;
        int calls = 0;
        var pauses = new List<TimeSpan>();
        var error = Assert.Throws<IOException>(() =>
            TransientFileLockRetry.Run(() => { calls++; throw Win32(32); }, Policy, sleep: pauses.Add));
        Assert.Equal(3, calls);
        Assert.Equal(2, pauses.Count);
        Assert.Equal("locked", error.Message);
    }

    [Fact]
    public void AnUnauthorizedAccessLock_IsRetriedToo()
    {
        if (!OperatingSystem.IsWindows()) return;
        int calls = 0;
        TransientFileLockRetry.Run(() => { if (++calls == 1) throw new DeniedException(Win32Facility | 5); },
            Policy, sleep: _ => { });
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(3)]  // path not found
    [InlineData(183)] // already exists
    [InlineData(0)]
    public void AnyOtherFailure_IsFinalAtOnce(int code)
    {
        int calls = 0;
        Assert.Throws<IOException>(() => TransientFileLockRetry.Run(() => { calls++; throw Win32(code); }, Policy, sleep: _ => Assert.Fail("no pause")));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ANonIoException_IsNeverRetried()
    {
        int calls = 0;
        Assert.Throws<NotIoException>(() => TransientFileLockRetry.Run(
            () => { calls++; throw new NotIoException(Win32Facility | 32); }, Policy, sleep: _ => Assert.Fail("no pause")));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AChangedPrecondition_StopsTheRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        int calls = 0;
        Assert.Throws<IOException>(() => TransientFileLockRetry.Run(() => { calls++; throw Win32(32); }, Policy,
            canRetry: () => false, sleep: _ => Assert.Fail("no pause")));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ADefaultPolicy_IsTheApprovedTenAttemptsOfOneHundredMilliseconds()
    {
        var defaults = new FileReplaceRetrySettings();
        Assert.Equal(10, defaults.Attempts);
        Assert.Equal(100, defaults.DelayMilliseconds);
        Assert.Empty(FileReplaceRetrySettingsValidator.Validate(defaults));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(51, 100)]
    [InlineData(10, 0)]
    [InlineData(10, 1001)]
    [InlineData(20, 600)] // each value is inside its hard band, but 12 s of waiting exceeds the contract
    public void AValueOutsideTheHardBandsOrTheTotalWait_IsFatal(int attempts, int delay) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FileReplaceRetrySettingsValidator.Validate(
            new FileReplaceRetrySettings { Attempts = attempts, DelayMilliseconds = delay }));

    [Theory]
    [InlineData(30, 100, "Attempts")]
    [InlineData(10, 5, "DelayMilliseconds")]
    [InlineData(10, 600, "DelayMilliseconds")]
    public void AValueBetweenTheStandardAndTheHardBand_RunsWithOneWarning(int attempts, int delay, string key)
    {
        var warnings = FileReplaceRetrySettingsValidator.Validate(new FileReplaceRetrySettings { Attempts = attempts, DelayMilliseconds = delay });
        var warning = Assert.Single(warnings);
        Assert.Contains($"{FileReplaceRetrySettings.SectionName}:{key}", warning);
    }
}
