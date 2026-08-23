using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

// TickerListViewModel and EditTickerNotesDialogViewModel both register as recipients of
// TickerDataRefreshedMessage on the shared static WeakReferenceMessenger.Default, and their
// respective test classes both use overlapping symbol names (e.g. "AAPL"). Under true parallel
// execution, a still-alive ViewModel instance from one class can react to a message sent by the
// other class's test, corrupting Moq call verification / assertions in whichever test happens to
// be mid-flight. Grouping both classes into one non-parallel collection prevents that cross-class
// message collision without affecting any other (unrelated) test class's parallelism.
[CollectionDefinition("MessengerSharedState", DisableParallelization = true)]
public class MessengerSharedStateCollection
{
}
