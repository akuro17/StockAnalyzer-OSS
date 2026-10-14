using System;
using System.IO;
using System.Text.Json;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed class RlErrorTextTests
{
    private static string Describe(Exception error) => RlErrorText.Describe(error, NullLocalizationService.Instance);

    public static TheoryData<Exception, string> Cases => new()
    {
        { new ArgumentException("x"), "Rl.Ui.Status.InvalidInput" },
        { new ArgumentOutOfRangeException("x"), "Rl.Ui.Status.InvalidInput" },
        { new OverflowException("x"), "Rl.Ui.Status.InvalidInput" },
        { new FileNotFoundException("x"), "Rl.Ui.Status.DataUnavailable" },
        { new UnauthorizedAccessException("x"), "Rl.Ui.Status.AccessDenied" },
        { new InvalidDataException(RlSourceContract.InsufficientData("short")), "Rl.Ui.Status.InsufficientData" },
        { new InvalidDataException("other"), "Rl.Ui.Status.InvalidData" },
        { new JsonException("x"), "Rl.Ui.Status.InvalidData" },
        { new IOException("x"), "Rl.Ui.Status.IOFailure" },
        { new TimeoutException("x"), "Rl.Ui.Status.Timeout" },
        { new InvalidOperationException("x"), "Rl.Ui.Status.OperationFailed" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryBranchMapsToItsKey(Exception error, string expectedKey) => Assert.Equal(expectedKey, Describe(error));

    [Fact]
    public void InsufficientDataWinsOverTheGenericInvalidDataCase()
    {
        Assert.Equal("Rl.Ui.Status.InsufficientData", Describe(new InvalidDataException(RlSourceContract.InsufficientData("d"))));
        Assert.Equal("Rl.Ui.Status.InvalidData", Describe(new InvalidDataException("d")));
    }

    [Fact]
    public void TemporalRejectionWinsOverTheIoCaseItDerivesFrom()
    {
        var error = new RlTemporalException(RlTemporalRejection.MissingEvidence, "m");
        Assert.Equal("Rl.Temporal.Rejection.MissingEvidence", Describe(error));
    }

    [Fact]
    public void FutureInformationUsesTheRejectionKeyAsFormat()
    {
        var error = new RlTemporalException(RlTemporalRejection.FutureInformation, "m", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 1));
        Assert.Equal("Rl.Temporal.Rejection.FutureInformation", Describe(error));
    }
}
