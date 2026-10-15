using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// An exception that leaves an <c>async void</c> method is raised on the UI synchronization context and ends the process; no caller can catch it.
/// The scan reads the sources of StockAnalyzer.Avalonia and requires every <c>async void</c> method to enclose its first <c>await</c> in a
/// <c>try</c> block that has a <c>catch</c>. Early guard returns before the <c>try</c> are allowed because they cannot throw.
/// </summary>
public class AsyncVoidHandlerGuardTests
{
    private const string AvaloniaProjectDirectory = "StockAnalyzer.Avalonia";
    private static readonly Regex AsyncVoidMethod = new(@"\basync\s+void\s+(\w+)\s*\([^)]*\)\s*\{", RegexOptions.Compiled);
    private static readonly Regex TryBlock = new(@"\btry\s*\{", RegexOptions.Compiled);

    private static bool IsBuildOutput(string relativePath)
        => relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string WithoutLineComments(string source)
        => string.Join('\n', source.Split('\n').Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line));

    /// <summary>Index of the brace that closes the one opened at <paramref name="openIndex"/>, or -1 when the source ends first.</summary>
    private static int FindClosingBrace(string source, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return i;
        }

        return -1;
    }

    /// <summary>Every <c>async void</c> method of the project as (relative file, method name, whether its first await is inside try/catch).</summary>
    private static IReadOnlyList<(string File, string Method, bool Guarded)> ScanAsyncVoidMethods()
    {
        string projectDirectory = Path.Combine(TestRepositoryRoot.Find(), AvaloniaProjectDirectory);
        Assert.True(Directory.Exists(projectDirectory), $"Project directory not found: {projectDirectory}");

        var result = new List<(string, string, bool)>();
        foreach (string file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(projectDirectory, file);
            if (IsBuildOutput(relative)) continue;

            string source = WithoutLineComments(File.ReadAllText(file));
            foreach (Match method in AsyncVoidMethod.Matches(source))
            {
                int bodyOpen = method.Index + method.Length - 1;
                int bodyClose = FindClosingBrace(source, bodyOpen);
                Assert.True(bodyClose > bodyOpen, $"Unbalanced braces in {relative} after {method.Groups[1].Value}");

                string body = source.Substring(bodyOpen, bodyClose - bodyOpen + 1);
                int firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
                bool guarded = firstAwait < 0 || TryBlock.Matches(body).Any(tryStart =>
                {
                    int tryOpen = tryStart.Index + tryStart.Length - 1;
                    int tryClose = FindClosingBrace(body, tryOpen);
                    return tryStart.Index < firstAwait && tryClose > firstAwait
                        && Regex.IsMatch(body.Substring(tryClose + 1), @"^\s*catch\b");
                });
                result.Add((relative, method.Groups[1].Value, guarded));
            }
        }

        return result;
    }

    [Fact]
    public void EveryAsyncVoidMethodEnclosesItsFirstAwaitInTryCatch()
    {
        var unguarded = ScanAsyncVoidMethods().Where(m => !m.Guarded).Select(m => $"{m.File}::{m.Method}").ToList();

        Assert.True(unguarded.Count == 0, "async void methods whose first await is not inside try/catch: " + string.Join(", ", unguarded));
    }

    [Fact]
    public void TheScanFindsTheKnownAsyncVoidHandlers()
    {
        // Guards the guard: a wrong path or pattern would find nothing and pass without checking anything.
        var names = ScanAsyncVoidMethods().Select(m => m.Method).ToHashSet();

        Assert.Contains("OnBrowseManifestClick", names);
        Assert.Contains("AttachImageButton_Click", names);
    }
}
