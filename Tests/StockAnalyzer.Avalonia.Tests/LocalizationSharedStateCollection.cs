using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

// ParameterGroupingTests, ParameterLocalizationTests, and FilterTemplateFormattingTests all mutate
// the shared static LocalizationManager.Instance (via .Initialize(locale)) from their constructors
// or test bodies. Under true parallel execution, one class's "en" reset can race with another
// class's "ja" assertions, producing an intermittent failure in whichever test happens to read the
// singleton mid-flight (observed transiently in both FilterTemplateFormattingTests and
// ParameterLocalizationTests). Grouping all three into one non-parallel collection prevents that
// cross-class locale collision without affecting any other (unrelated) test class's parallelism.
[CollectionDefinition("LocalizationSharedState", DisableParallelization = true)]
public class LocalizationSharedStateCollection
{
}
