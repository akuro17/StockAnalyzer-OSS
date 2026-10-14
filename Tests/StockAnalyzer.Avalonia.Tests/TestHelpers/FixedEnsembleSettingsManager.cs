using System.Threading.Tasks;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>
/// In-memory <see cref="IEnsembleSettingsManager"/> holding one saved ensemble. Without a <c>loadGate</c> the ensemble is current from the start;
/// with one, nothing is current until <see cref="LoadAsync"/> has run past the gate (a settings file that is read late).
/// </summary>
internal sealed class FixedEnsembleSettingsManager(EnsembleSpec? saved, Task? loadGate = null) : IEnsembleSettingsManager
{
    /// <summary>The two-member equal-weight ensemble that the presentation tests share.</summary>
    public static readonly EnsembleSpec SampleSaved = EnsembleSpec.Equal(new[] { "member-a", "member-b" });

    public EnsembleSpec? Current { get; private set; } = loadGate is null ? saved : null;

    public async Task LoadAsync()
    {
        if (loadGate is null) return;
        await loadGate;
        Current = saved;
    }

    public Task SaveAsync(EnsembleSpec? spec)
    {
        Current = spec;
        return Task.CompletedTask;
    }
}
