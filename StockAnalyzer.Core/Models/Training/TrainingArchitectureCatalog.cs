using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// The architectures offered per training framework and the one each framework starts with. The one definition: the Training Wizard and every other
/// consumer read it here. The lists mirror each trainer's own <c>ARCHES</c> tuple (<c>train_pytorch.py</c> / <c>train_tensorflow.py</c>) or the single
/// GBDT path (<c>train_lightgbm.py</c> has no <c>--arch</c>).
/// </summary>
public static class TrainingArchitectureCatalog
{
    private static readonly IReadOnlyDictionary<TrainingFramework, ReadOnlyCollection<string>> Offered =
        new Dictionary<TrainingFramework, ReadOnlyCollection<string>>
        {
            [TrainingFramework.PyTorch] = Array.AsReadOnly(new[] { "lstm", "cnn", AttentionContract.Architecture }),
            [TrainingFramework.LightGBM] = Array.AsReadOnly(new[] { TrainingEnsembleContract.LightGbmArchitecture }),
            [TrainingFramework.TensorFlow] = Array.AsReadOnly(new[] { "lstm", "gru", "cnn" }),
        };

    // Explicit on purpose: re-ordering the offered lists must not change what a framework starts with.
    private static readonly IReadOnlyDictionary<TrainingFramework, string> Defaults =
        new Dictionary<TrainingFramework, string>
        {
            [TrainingFramework.PyTorch] = "lstm",
            [TrainingFramework.LightGBM] = TrainingEnsembleContract.LightGbmArchitecture,
            [TrainingFramework.TensorFlow] = "lstm",
        };

    /// <summary>The architectures of <paramref name="framework"/> in display order. The same instance on every call.</summary>
    /// <exception cref="InvalidOperationException">The framework has no entry (a value added to <see cref="TrainingFramework"/> without one).</exception>
    public static IReadOnlyList<string> For(TrainingFramework framework)
        => Offered.TryGetValue(framework, out var architectures)
            ? architectures
            : throw new InvalidOperationException($"No architectures are defined for framework '{framework}'.");

    /// <summary>The architecture <paramref name="framework"/> starts with; always one of <see cref="For"/>.</summary>
    /// <exception cref="InvalidOperationException">The framework has no entry.</exception>
    public static string DefaultFor(TrainingFramework framework)
        => Defaults.TryGetValue(framework, out var architecture)
            ? architecture
            : throw new InvalidOperationException($"No default architecture is defined for framework '{framework}'.");
}
