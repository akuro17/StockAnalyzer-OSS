using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// The command-side half of the read-only contract: one small object a view model holds so that every feature that writes below the
/// data folder asks the same question ("may this start?") and shows the same reason. The service-side half is
/// <see cref="DataStorageGuard"/>; both read the one <see cref="IDataStorageState"/> probed at start-up, so they cannot disagree.
/// A null state means "writable" (tests, designer): no view model changes behaviour unless the application proved the folder read-only.
/// </summary>
public sealed class StorageFeatureGate
{
    /// <summary>Locale key of the one sentence shown as the tooltip of every disabled feature.</summary>
    public const string DisabledToolTipKey = "ReadOnlyStorage_FeatureDisabled";

    private readonly IDataStorageState? _state;

    public StorageFeatureGate(IDataStorageState? state)
    {
        _state = state;
    }

    /// <summary>The value to use as <c>CanExecute</c>; fixed for the life of the process.</summary>
    public bool IsAvailable => DataStorageGuard.IsWritable(_state);

    /// <summary>The reason to show while the feature is disabled; null (no tooltip) while it is available.</summary>
    public string? DisabledToolTip => IsAvailable ? null : LocalizationManager.Instance[DisabledToolTipKey];
}
