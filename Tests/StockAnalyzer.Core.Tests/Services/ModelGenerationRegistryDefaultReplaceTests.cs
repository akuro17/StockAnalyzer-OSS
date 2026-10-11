using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The default <see cref="IModelGenerationRegistry.ReplaceActiveAsync"/> (deactivate, activate, put the expected generation back on failure) used by a registry
/// that cannot replace in one pointer write. An interface has no logger, so a failed restore travels in the <c>Data</c> of the exception that is thrown.
/// </summary>
public sealed class ModelGenerationRegistryDefaultReplaceTests
{
    private const string Active = "active-generation";
    private const string Candidate = "candidate-generation";

    private static Mock<IModelGenerationRegistry> Registry(Func<string, Exception?> activation)
    {
        var mock = new Mock<IModelGenerationRegistry> { CallBase = true };
        mock.SetupGet(r => r.ActiveId).Returns(Active);
        mock.Setup(r => r.DeactivateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mock.Setup(r => r.ActivateAsync(It.IsAny<string>(), true, false, It.IsAny<CancellationToken>()))
            .Returns((string id, bool _, bool _, CancellationToken _) =>
                activation(id) is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(true));
        return mock;
    }

    [Fact]
    public async Task AFailedActivation_PutsTheExpectedGenerationBack_AndThrowsTheActivationFailure()
    {
        var refused = new InvalidDataException("candidate refused");
        var mock = Registry(id => id == Candidate ? refused : null);

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() => mock.Object.ReplaceActiveAsync(Candidate, Active));

        Assert.Same(refused, thrown);
        Assert.False(thrown.Data.Contains(IModelGenerationRegistry.ReplaceRestoreFailureKey));
        mock.Verify(r => r.ActivateAsync(Active, true, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AFailedRestore_IsKeptInTheDataOfTheActivationFailure_NotSwallowed()
    {
        var refused = new InvalidDataException("candidate refused");
        var lost = new IOException("previous generation could not be put back");
        var mock = Registry(id => id == Candidate ? refused : lost);

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() => mock.Object.ReplaceActiveAsync(Candidate, Active));

        Assert.Same(refused, thrown);
        Assert.Same(lost, thrown.Data[IModelGenerationRegistry.ReplaceRestoreFailureKey]);
    }

    [Fact]
    public async Task AReplacementThatSucceeds_ReturnsTrueWithoutARestore()
    {
        var mock = Registry(_ => null);

        Assert.True(await mock.Object.ReplaceActiveAsync(Candidate, Active));

        mock.Verify(r => r.ActivateAsync(Active, true, false, It.IsAny<CancellationToken>()), Times.Never);
    }
}
