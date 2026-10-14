using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Docs;

/// <summary>Machine checks of the public Training / Reinforcement Learning guide and of its release plumbing (PLAN-B2).</summary>
public sealed class TrainingGuidePublicationTests
{
    private const string GuideName = "TRAINING_AND_REINFORCEMENT_LEARNING_GUIDE.md";
    private const string DrawingGuideName = "DRAWING_TOOLS_GUIDE.md";
    private const string SettingsHeader = "| Setting | Where | Default | Allowed values | What it does |";
    private const string KeyHeader = "| Key | Where | Default | Allowed values | What it does |";

    private static readonly string[] ExpectedHeadings =
    {
        "## Table of contents",
        "## 1. Overview: which tool creates which file and where it is used",
        "## 2. Before you start",
        "## 3. Quick start with the default settings",
        "## 4. Training Wizard reference (changing the defaults)",
        "## 5. Reinforcement Learning reference (changing the defaults)",
        "## 6. Portfolio Reinforcement Learning reference (changing the defaults)",
        "## 7. Files and folders",
        "## 8. Limitations and disclaimer",
        "## 9. Troubleshooting",
    };

    // Case-insensitive substrings that must never reach the public guide (internal paths, confidential and internal files, source files).
    private static readonly string[] ProhibitedSubstrings =
    {
        @"i:\", @"y:\", @"c:\users", @"\stock\", "AGENTS.md", "CLAUDE.md", ".agent", "sa_ui_interaction", "sa_architecture",
        "StockAnalyzer.Avalonia/", "localhost",
    };

    private static readonly Regex SourceFileName = new(@"\.cs\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EmailAddress = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.CultureInvariant);
    private static readonly Regex AdvancedVocabulary = new(@"\b(?:hyperparameter\w*|cpcv|calibrat\w*|stacking|bagging|pretrain\w*|fine-tune\w*|z-score|lags?|clip\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Lazy<string> RepoRoot = new(FindRepositoryRoot);
    private static readonly Lazy<string> Guide = new(() => ReadRepoFile("docs", GuideName));
    private static readonly Lazy<HashSet<string>> EnglishValues = new(() =>
    {
        using var json = JsonDocument.Parse(ReadRepoFile("StockAnalyzer.Avalonia", "Resources", "Locales", "en.json"));
        return json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
    });

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("StockAnalyzer.sln was not found above the test output.");
    }

    private static string ReadRepoFile(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot.Value }.Concat(parts).ToArray()));

    private static string Chapter(string number)
    {
        var text = Guide.Value;
        int start = text.IndexOf("\n## " + number + ". ", StringComparison.Ordinal);
        Assert.True(start >= 0, "Chapter " + number + " is missing.");
        int end = text.IndexOf("\n## ", start + 4, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static IEnumerable<string[]> TableRows(string text, string header)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i] != header) continue;
            for (int j = i + 2; j < lines.Length && lines[j].StartsWith('|'); j++)
                yield return Regex.Split(lines[j].Trim().Trim('|'), @"(?<!\\)\|").Select(c => c.Trim().Replace("\\|", "|")).ToArray();
        }
    }

    // ---- the inventory procedure of PLAN-B2 3.3 (distinct bound input controls of an AXAML file) ----

    private static readonly string[] InputControls =
        { "TextBox", "NumericUpDown", "ComboBox", "CheckBox", "CalendarDatePicker", "ToggleSwitch", "Slider", "RadioButton", "AutoCompleteBox" };
    private static readonly Regex ControlStart = new(@"<(" + string.Join("|", InputControls) + @")(?=[\s>/])", RegexOptions.CultureInvariant);
    private static readonly Regex ValueBinding = new(@"\b(?:Text|Value|IsChecked|SelectedItem|SelectedDate|SelectedValue)=""\{Binding ([^,}""]+)", RegexOptions.CultureInvariant);

    /// <summary>
    /// One entry per distinct (control type, bound value path); read-only (OneWay) controls are skipped. A control without a value binding is not an input of the window.
    /// <paramref name="excluded"/> names binding paths that a window renders another way (the hyperparameter editor is generated from the catalog).
    /// </summary>
    private static int CountInputs(string relativeAxaml, params string[] excluded)
    {
        string text = ReadRepoFile(relativeAxaml.Split('/'));
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match start in ControlStart.Matches(text))
        {
            int end = start.Index + start.Length; bool quoted = false;
            while (end < text.Length && (quoted || text[end] != '>')) { if (text[end] == '"') quoted = !quoted; end++; }
            string tag = text[start.Index..Math.Min(end + 1, text.Length)];
            var binding = ValueBinding.Match(tag);
            if (!binding.Success || tag.Contains("Mode=OneWay", StringComparison.Ordinal) || tag.Contains("IsReadOnly=\"True\"", StringComparison.Ordinal)) continue;
            if (excluded.Contains(binding.Groups[1].Value)) continue;
            found.Add(start.Groups[1].Value + "|" + binding.Groups[1].Value);
        }
        return found.Count;
    }

    private static int SettingRows(string chapter) => TableRows(chapter, SettingsHeader).Count();

    [Fact]
    public void TheGuideHasExactlyTheDocumentedHeadingsInOrder()
    {
        var headings = Guide.Value.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("## ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(ExpectedHeadings, headings);
        Assert.StartsWith("# Training Wizard, Reinforcement Learning and Portfolio Reinforcement Learning Guide", Guide.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", Guide.Value);
        Assert.False(Guide.Value.StartsWith('\uFEFF'), "The guide is UTF-8 without a byte order mark.");
    }

    [Fact]
    public void TheGuideContainsNoInternalPathsConfidentialNamesSourceFilesOrAddresses()
    {
        foreach (string prohibited in ProhibitedSubstrings)
            Assert.False(Guide.Value.Contains(prohibited, StringComparison.OrdinalIgnoreCase), "The guide contains: " + prohibited);
        Assert.False(SourceFileName.IsMatch(Guide.Value), "The guide names a source file.");
        Assert.False(EmailAddress.IsMatch(Guide.Value), "The guide contains an e-mail address.");
    }

    [Fact]
    public void EveryRelativeLinkResolvesToAnExistingFile()
    {
        foreach (Match link in Regex.Matches(Guide.Value, @"\]\(([^)\s]+)\)"))
        {
            string target = link.Groups[1].Value;
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) || target.StartsWith('#')) continue;
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(RepoRoot.Value, "docs", target.Split('#')[0]))), "Broken link: " + target);
        }
    }

    [Fact]
    public void TheReleasePlumbingNamesTheGuide()
    {
        string files = ReadRepoFile(".agent", "skills", "sa_release_file", "scripts", "prepare_oss_files.ps1");
        string patch = ReadRepoFile(".agent", "skills", "sa_release_patch_file", "scripts", "prepare_oss_patch.ps1");
        string skill = ReadRepoFile(".agent", "skills", "sa_release_file", "SKILL.md");
        Assert.Contains(GuideName, files); Assert.Contains(GuideName, patch); Assert.Contains(GuideName, skill);
        Assert.Contains(GuideName, ReadRepoFile("README.md"));

        var expected = new[] { DrawingGuideName, GuideName }.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, NamesIn(files, "$PublicDocsToCopy"));
        Assert.Equal(expected, NamesIn(files, "$AllowedPublicDocs"));
        Assert.Equal(expected, NamesIn(patch, "$AllowedPublicDocs"));

        static string[] NamesIn(string script, string variable)
        {
            var match = Regex.Match(script, Regex.Escape(variable) + @"\s*=\s*@\((?<body>[^)]*)\)", RegexOptions.CultureInvariant);
            Assert.True(match.Success, variable + " was not found.");
            return Regex.Matches(match.Groups["body"].Value, "\"([^\"]+)\"").Select(m => Path.GetFileName(m.Groups[1].Value.Replace('\\', '/'))).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }
    }

    [Fact]
    public void EachWindowTableHasOneRowPerInputControlOfItsWindows()
    {
        // The hyperparameter editor (key list + value box) is generated from the catalog; its rows are checked separately.
        int trainingWizard = CountInputs("StockAnalyzer.Avalonia/Views/Dialogs/TrainingWizardWindow.axaml", "SelectedOption", "Value")
            + CountInputs("StockAnalyzer.Avalonia/Views/Dialogs/FeatureChannelPickerView.axaml")
            + CountInputs("StockAnalyzer.Avalonia/Views/Dialogs/TrainingResourceSettingsView.axaml");
        int reinforcement = CountInputs("StockAnalyzer.Avalonia/Views/RlTrainingWindow.axaml") + CountInputs("StockAnalyzer.Avalonia/Views/Dialogs/RlPlaybackSettingsView.axaml");
        int portfolio = CountInputs("StockAnalyzer.Avalonia/Views/PortfolioRlWindow.axaml");

        Assert.Equal(trainingWizard, SettingRows(Chapter("4")));
        Assert.Equal(reinforcement, SettingRows(Chapter("5")));
        Assert.Equal(portfolio, SettingRows(Chapter("6")));
    }

    [Fact]
    public void EverySettingNameIsALabelOfTheApplication()
    {
        foreach (string chapter in new[] { "4", "5", "6" })
            foreach (var row in TableRows(Chapter(chapter), SettingsHeader))
            {
                Assert.Equal(5, row.Length);
                string name = row[0];
                string withoutSuffix = Regex.Replace(name, @" \([^()]*\)$", "");
                Assert.True(EnglishValues.Value.Contains(name) || EnglishValues.Value.Contains(withoutSuffix), "Not an application label: " + name);
                Assert.All(row, c => Assert.False(string.IsNullOrWhiteSpace(c), "Empty cell in the row of " + name));
            }
    }

    [Fact]
    public void TheHyperparameterTableMatchesTheCatalogKeysAndDefaults()
    {
        var rows = TableRows(Chapter("4"), KeyHeader).ToArray();
        Assert.Equal(TrainingHyperparameterCatalog.All.Select(d => d.Key), rows.Select(r => r[0]));
        foreach (var definition in TrainingHyperparameterCatalog.All)
            Assert.Equal(definition.DefaultValue, rows.Single(r => r[0] == definition.Key)[2]);
    }

    [Fact]
    public void TheQuickStartHasNoTableNoAdvancedVocabularyAndOnlyApplicationLabelsInBold()
    {
        string quickStart = Chapter("3");
        Assert.DoesNotContain(quickStart.Split('\n'), line => line.TrimStart().StartsWith('|'));
        var advanced = AdvancedVocabulary.Match(quickStart);
        Assert.False(advanced.Success, "Advanced vocabulary in the quick start: " + advanced.Value);
        var bold = Regex.Matches(quickStart, @"\*\*(.+?)\*\*").Select(m => m.Groups[1].Value).ToArray();
        Assert.NotEmpty(bold);
        Assert.All(bold, label => Assert.True(EnglishValues.Value.Contains(label), "Not an application label: " + label));
    }

    [Fact]
    public void TheRemovedScopeBannerKeysExistNowhere()
    {
        string[] keys = { "PortfolioRl_Banner_ScopeNotice", "RlTraining_Banner_ScopeNotice" };
        var files = new List<string>
        {
            Path.Combine(RepoRoot.Value, "StockAnalyzer.Avalonia", "Resources", "Locales", "en.json"),
            Path.Combine(RepoRoot.Value, "StockAnalyzer.Avalonia", "Resources", "Locales", "ja.json"),
        };
        foreach (string project in new[] { "StockAnalyzer.Avalonia", "StockAnalyzer.Core" })
            files.AddRange(Directory.EnumerateFiles(Path.Combine(RepoRoot.Value, project), "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                    && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (string key in keys) Assert.False(text.Contains(key, StringComparison.Ordinal), key + " is still used in " + Path.GetFileName(file));
        }
    }
}
