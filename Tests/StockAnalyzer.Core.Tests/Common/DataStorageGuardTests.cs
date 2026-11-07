using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using static StockAnalyzer.TestSupport.TestDataStorageState;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// D-2' Run 3 / S4: the service-entry half of the read-only contract. A feature asks the guard before it starts writing below the data
/// folder; no state (tests, tools that do not probe) and a writable state behave exactly as before.
/// </summary>
public class DataStorageGuardTests
{
    [Fact]
    public void IsWritable_NullStateMeansWritable()
    {
        Assert.True(DataStorageGuard.IsWritable(null));
        Assert.True(DataStorageGuard.IsWritable(WritableState()));
        Assert.False(DataStorageGuard.IsWritable(ReadOnlyState()));
    }

    [Fact]
    public void ThrowIfReadOnly_NullOrWritable_DoesNothing()
    {
        DataStorageGuard.ThrowIfReadOnly(null, "op");
        DataStorageGuard.ThrowIfReadOnly(WritableState(), "op");
    }

    [Theory]
    [InlineData(DataStorageReadOnlyReason.DataRootCannotBeCreated)]
    [InlineData(DataStorageReadOnlyReason.DataRootNotWritable)]
    public void ThrowIfReadOnly_ReadOnly_ThrowsAnIoExceptionNamingTheOperationAndReason(DataStorageReadOnlyReason reason)
    {
        var ex = Assert.Throws<DataStorageReadOnlyException>(() => DataStorageGuard.ThrowIfReadOnly(ReadOnlyState(reason), "TrainingStart"));

        Assert.IsAssignableFrom<IOException>(ex); // callers that already handle a failed write handle this too
        Assert.Equal("TrainingStart", ex.Operation);
        Assert.Equal(reason, ex.Reason);
        Assert.Contains("TrainingStart", ex.Message);
        Assert.Contains(reason.ToString(), ex.Message);
    }

    [Fact]
    public void ThrowIfReadOnly_MessageNeverContainsTheFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(profile, "secret-install", "Data");

        var ex = Assert.Throws<DataStorageReadOnlyException>(() => DataStorageGuard.ThrowIfReadOnly(ReadOnlyState(dataRoot: root), "op"));

        Assert.DoesNotContain("secret-install", ex.Message);
        Assert.DoesNotContain(profile, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ThrowIfReadOnly_BlankOperation_IsRejectedEvenWhenWritable(string? operation)
    {
        Assert.Throws<ArgumentException>(() => DataStorageGuard.ThrowIfReadOnly(WritableState(), operation!));
    }

    // ---- the update pipeline (every sync goes through it) --------------------------------------------------------

    private sealed class Settings : PythonIpcTestSettingsBase { }

    [Fact]
    public async Task PythonService_ReadOnly_RefusesBothUpdatePipelineOverloads_BeforeAnyValidationOrProcess()
    {
        await using var service = new PythonService(new Settings(), dataStorageState: ReadOnlyState());
        var config = new SyncSessionConfig(true, true, false, false, 2.0m, 5.0m, 5);

        // An invalid symbol would be an ArgumentException; the guard comes first, so the refusal is the answer and nothing was started.
        var plain = await Assert.ThrowsAsync<DataStorageReadOnlyException>(() => service.RunUpdatePipelineAsync("../evil"));
        var withConfig = await Assert.ThrowsAsync<DataStorageReadOnlyException>(() => service.RunUpdatePipelineAsync("AAPL", config));

        Assert.Equal(PythonService.UpdatePipelineOperation, plain.Operation);
        Assert.Equal(PythonService.UpdatePipelineOperation, withConfig.Operation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PythonService_NoStateOrWritable_ValidatesAsBefore(bool withWritableState)
    {
        await using var service = new PythonService(new Settings(), dataStorageState: withWritableState ? WritableState() : null);
        var config = new SyncSessionConfig(true, true, false, false, 2.0m, 5.0m, 5);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RunUpdatePipelineAsync("../evil"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RunUpdatePipelineAsync("../evil", config));
    }
}
