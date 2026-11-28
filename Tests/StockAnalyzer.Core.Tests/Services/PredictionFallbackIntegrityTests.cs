using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("Non-Parallel ONNX Tests")]
public sealed class PredictionFallbackIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa-onnx-verify-tests", Guid.NewGuid().ToString("N"));
    private readonly string _modelsDeployCleanupPrefix = "sa_test_mp3_" + Guid.NewGuid().ToString("N") + ".onnx";

    public PredictionFallbackIntegrityTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort cleanup */ }

        try
        {
            var deployed = PathDiscovery.ResolvePredictionModelPath(_modelsDeployCleanupPrefix);
            BestEffortFile.TryDelete(deployed);
            BestEffortFile.TryDelete(deployed + ".tmp");
            BestEffortFile.TryDelete(deployed + PredictionSettings.Sha256SidecarSuffix);
        }
        catch { /* best-effort cleanup */ }
    }

    private sealed class FallbackTestSettings(
        string modelPath,
        string? expectedSha256 = null,
        bool requireVerified = false) : PredictionServiceTests.TestSettings(modelPath, predictionWindowSize: 10), IStockAnalyzerSettings
    {
        public string? ExpectedSha256Value { get; set; } = expectedSha256;
        public bool RequireVerifiedValue { get; set; } = requireVerified;
        string? IStockAnalyzerSettings.PredictionExpectedModelSha256 => ExpectedSha256Value;
        bool IStockAnalyzerSettings.PredictionRequireVerifiedFallbackModel => RequireVerifiedValue;
    }

    private string CreateDummyModelCopy(string name)
    {
        var dest = Path.Combine(_root, name);
        File.Copy(PredictionServiceTests.ConformantModelPath, dest, overwrite: true);
        return dest;
    }

    [Fact]
    public void PredictionSettings_Validate_RejectsInvalidExpectedModelSha256()
    {
        var invalid = new PredictionSettings { ExpectedModelSha256 = "not-a-sha256" };
        Assert.Throws<InvalidOperationException>(() => invalid.Validate());

        var valid = new PredictionSettings { ExpectedModelSha256 = "  " + new string('A', 64) + "\n" };
        valid.Validate();
    }

    [Fact]
    public void VerifyFallbackModelIntegrity_ValidSidecar_SucceedsAndRequiresExactNoBom64Hex()
    {
        var modelPath = CreateDummyModelCopy("valid_sidecar.onnx");
        var hash = ContentHash.OfFile(modelPath);
        File.WriteAllBytes(modelPath + PredictionSettings.Sha256SidecarSuffix, Encoding.ASCII.GetBytes($"  {hash.ToUpperInvariant()}\r\n"));

        using var service = new PredictionService(new FallbackTestSettings(modelPath, requireVerified: true), new MLDataProcessor());
        service.VerifyFallbackModelIntegrity(modelPath, candidateId: null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void VerifyFallbackModelIntegrity_MalformedSidecar_ThrowsInvalidDataEvenWhenStrictDisabled(string badContent)
    {
        var modelPath = CreateDummyModelCopy("bad_sidecar.onnx");
        File.WriteAllText(modelPath + PredictionSettings.Sha256SidecarSuffix, badContent, new UTF8Encoding(false));

        using var service = new PredictionService(new FallbackTestSettings(modelPath, requireVerified: false), new MLDataProcessor());
        Assert.Throws<InvalidDataException>(() => service.VerifyFallbackModelIntegrity(modelPath, candidateId: null));
    }

    [Fact]
    public void VerifyFallbackModelIntegrity_Utf8BomSidecar_ThrowsInvalidDataEvenWhenStrictDisabled()
    {
        var modelPath = CreateDummyModelCopy("bom_sidecar.onnx");
        var hash = ContentHash.OfFile(modelPath);
        var bomBytes = Encoding.UTF8.GetPreamble();
        var bodyBytes = Encoding.ASCII.GetBytes(hash);
        var combined = new byte[bomBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(bomBytes, 0, combined, 0, bomBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, combined, bomBytes.Length, bodyBytes.Length);
        File.WriteAllBytes(modelPath + PredictionSettings.Sha256SidecarSuffix, combined);

        using var service = new PredictionService(new FallbackTestSettings(modelPath, requireVerified: false), new MLDataProcessor());
        Assert.Throws<InvalidDataException>(() => service.VerifyFallbackModelIntegrity(modelPath, candidateId: null));
    }

    [Fact]
    public void VerifyFallbackModelIntegrity_OversizedSidecar_ThrowsInvalidData()
    {
        var modelPath = CreateDummyModelCopy("oversized_sidecar.onnx");
        File.WriteAllText(modelPath + PredictionSettings.Sha256SidecarSuffix, new string('a', 300), new UTF8Encoding(false));

        using var service = new PredictionService(new FallbackTestSettings(modelPath, requireVerified: false), new MLDataProcessor());
        Assert.Throws<InvalidDataException>(() => service.VerifyFallbackModelIntegrity(modelPath, candidateId: null));
    }

    [Fact]
    public void VerifyFallbackModelIntegrity_ExpectedModelSha256TakesPrecedenceOverSidecar()
    {
        var modelPath = CreateDummyModelCopy("precedence.onnx");
        var validHash = ContentHash.OfFile(modelPath);
        File.WriteAllText(modelPath + PredictionSettings.Sha256SidecarSuffix, new string('0', 64), new UTF8Encoding(false));

        using var service = new PredictionService(
            new FallbackTestSettings(modelPath, expectedSha256: validHash, requireVerified: true),
            new MLDataProcessor());
        service.VerifyFallbackModelIntegrity(modelPath, candidateId: null);
    }

    [Fact]
    public void VerifyFallbackModelIntegrity_MissingVerification_AllowsWhenFalseAndRejectsWhenTrue()
    {
        var modelPath = CreateDummyModelCopy("missing_sidecar.onnx");

        using var permissive = new PredictionService(new FallbackTestSettings(modelPath, requireVerified: false), new MLDataProcessor());
        permissive.VerifyFallbackModelIntegrity(modelPath, candidateId: null);

        using var strict = new PredictionService(new FallbackTestSettings(modelPath, requireVerified: true), new MLDataProcessor());
        Assert.Throws<InvalidDataException>(() => strict.VerifyFallbackModelIntegrity(modelPath, candidateId: null));
    }

    [Fact]
    public void VerifyFallbackModelIntegrity_TamperedModel_ThrowsInvalidData()
    {
        var modelPath = CreateDummyModelCopy("tampered.onnx");
        var wrongHash = new string('f', 64);
        File.WriteAllText(modelPath + PredictionSettings.Sha256SidecarSuffix, wrongHash, new UTF8Encoding(false));

        using var service = new PredictionService(new FallbackTestSettings(modelPath, requireVerified: false), new MLDataProcessor());
        Assert.Throws<InvalidDataException>(() => service.VerifyFallbackModelIntegrity(modelPath, candidateId: null));
    }

    [Fact]
    public async Task DeployAsync_GeneratesBomFree64HexSidecar_AndDeletesStaleSidecarOnWriteFailure()
    {
        var sourceV1 = Path.Combine(_root, _modelsDeployCleanupPrefix);
        await File.WriteAllBytesAsync(sourceV1, new byte[] { 1, 2, 3, 4 });
        var deployer = new ModelDeploymentService();

        var deployedPath = await deployer.DeployAsync(sourceV1);
        var sidecarPath = deployedPath + PredictionSettings.Sha256SidecarSuffix;
        Assert.True(File.Exists(sidecarPath));

        var rawSidecar = await File.ReadAllBytesAsync(sidecarPath);
        Assert.Equal(ContentHash.HexLength, rawSidecar.Length);
        Assert.Equal(ContentHash.OfFile(sourceV1), Encoding.ASCII.GetString(rawSidecar));

        // Simulate re-deploy where .sha256 write fails after .onnx was overwritten: old sidecar must NOT survive.
        await File.WriteAllBytesAsync(sourceV1, new byte[] { 9, 8, 7, 6 });
        deployer.WriteSidecarBytesAsync = (_, _, _) => throw new IOException("Simulated sidecar disk failure");

        await Assert.ThrowsAsync<IOException>(() => deployer.DeployAsync(sourceV1));
        Assert.False(File.Exists(sidecarPath), "Stale .sha256 sidecar must not remain alongside updated .onnx.");
    }
}
