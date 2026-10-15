using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>A job lease is a temporary token file: its default location is the OS temp folder, never the Data folder.</summary>
public sealed class TrainingJobGateDefaultRootTests
{
    private static string DataRoot => Path.GetFullPath(PathDiscovery.ResolveDataPath(null, "Data"));

    private static bool IsInside(string parent, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [Fact]
    public void DefaultRoot_IsBelowTheOsTempFolder_AndNeverInsideTheDataRoot()
    {
        string root = TrainingJobGate.DefaultRoot(DataRoot);

        Assert.True(IsInside(Path.Combine(Path.GetTempPath(), PathDiscovery.TempFolderName), root), root);
        Assert.False(IsInside(DataRoot, root), root);
    }

    [Fact]
    public void DefaultRoot_IsKeyedByTheDataRoot_AndStableForTheSameRoot()
    {
        string first = TrainingJobGate.DefaultRoot(@"C:\SA\DataOne");

        Assert.Equal(first, TrainingJobGate.DefaultRoot(@"C:\SA\DataOne"));
        Assert.Equal(first, TrainingJobGate.DefaultRoot(@"C:\SA\DataOne" + Path.DirectorySeparatorChar));
        Assert.NotEqual(first, TrainingJobGate.DefaultRoot(@"C:\SA\DataTwo"));
    }

    [Fact]
    public void DefaultGate_ExcludesTheSameIdentity_UntilTheLeaseIsDisposed_AndWritesNothingToTheDataRoot()
    {
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();
        string lockFile = Path.Combine(TrainingJobGate.DefaultRoot(DataRoot), identity + ".lock");
        var gate = new TrainingJobGate();
        try
        {
            using (gate.AcquireIdentity(identity))
            {
                Assert.True(File.Exists(lockFile));
                Assert.Throws<TrainingJobBusyException>(() => new TrainingJobGate().AcquireIdentity(identity));
            }

            using (gate.AcquireIdentity(identity)) { }
            Assert.False(File.Exists(Path.Combine(DataRoot, "TrainingArtifacts", "job-locks", identity + ".lock")));
        }
        finally
        {
            try { File.Delete(lockFile); } catch (IOException) { /* best-effort cleanup of a test token */ }
        }
    }
}
