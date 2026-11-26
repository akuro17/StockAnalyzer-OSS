using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

/// <summary>
/// EditTransactionDialogViewModel remembers the last used currency and exchange rate in process-wide static state
/// (ResetCache clears it). Any test class that saves a dialog, or asserts the pre-filled currency or rate, must opt into
/// this collection so it never runs concurrently with another class that changes that state -- otherwise a save in one
/// class could pre-fill the rate another class expects to be empty, an execution-order-dependent flaky failure.
/// </summary>
[CollectionDefinition("EditTransactionDialogCache State", DisableParallelization = true)]
public class EditTransactionDialogCacheTestCollection
{
}
