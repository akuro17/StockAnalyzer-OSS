using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

// DrawingObjectDisplayNameLocalizationTests and ScreenerViewModelTests both mutate the shared
// static LocalizationManager.Instance (via .Initialize(locale)) from their test bodies or
// constructor. Under true parallel execution, one class's locale switch can race with the other's
// assertions, producing an intermittent failure in whichever test happens to read the singleton
// mid-flight. Grouping both into one non-parallel collection prevents that cross-class locale
// collision without affecting any other (unrelated) test class's parallelism.
[CollectionDefinition("LocalizationSharedState", DisableParallelization = true)]
public class LocalizationSharedStateCollection
{
}
