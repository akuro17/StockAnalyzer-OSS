using System.Linq;
using StockAnalyzer.Avalonia.ViewModels;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed class RunModeBadgesTests
{
    private static string Text(string key) => "[" + key + "]";

    [Fact]
    public void NothingIsListedInTheDefaultState()
        => Assert.Empty(RunModeBadges.Build(Text, (false, "A"), (false, "B")));

    [Fact]
    public void OnlyTheStatesThatAreOnAreListed_InTheOrderGiven()
        => Assert.Equal(new[] { "[A]", "[C]" }, RunModeBadges.Build(Text, (true, "A"), (false, "B"), (true, "C")));

    [Fact]
    public void WithoutAnyStateTheListIsEmpty()
        => Assert.Empty(RunModeBadges.Build(Text));

    [Fact]
    public void EveryBadgeKeyExistsInBothLanguages()
    {
        string root = LocalesRoot();
        foreach (string file in new[] { "en.json", "ja.json" })
        {
            string json = System.IO.File.ReadAllText(System.IO.Path.Combine(root, file));
            foreach (string key in new[] { "Rl.Badge.Smoke", "Rl.Badge.ShortHistory", "Rl.Badge.FineTune", "Rl.Badge.EvaluateBase", "Rl.Badge.Continuous", "Rl.Badge.NoHoldout",
                         "TrainingWizard_Badge_Cpcv", "TrainingWizard_Badge_Pretraining" })
                Assert.Contains("\"" + key + "\"", json);
        }
    }

    private static string LocalesRoot()
    {
        string? dir = System.AppContext.BaseDirectory;
        while (dir is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "StockAnalyzer.Avalonia", "Resources", "Locales")))
            dir = System.IO.Path.GetDirectoryName(dir);
        return System.IO.Path.Combine(dir ?? "", "StockAnalyzer.Avalonia", "Resources", "Locales");
    }
}
