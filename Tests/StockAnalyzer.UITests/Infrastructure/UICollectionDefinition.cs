// --------------------------------------------------------------------------------
// File: Infrastructure/UICollectionDefinition.cs
// --------------------------------------------------------------------------------
using Xunit;

namespace StockAnalyzer.UITests.Infrastructure
{
    // Define the test collection to prevent parallel execution of UI tests.
    // Single-window desktop applications require serialized execution to avoid focus stealing.
    [CollectionDefinition("UI Tests", DisableParallelization = true)]
    public class UICollectionDefinition
    {
    }
}
