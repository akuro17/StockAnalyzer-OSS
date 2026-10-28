#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The model archive of the RL window: Export Model archives every file of the registered generation directory, and Import Model verifies the
/// archive, hands the extracted directory to the registry and never activates. The registry is a recording fixture; the files, the zips and the
/// hashes are real.
/// </summary>
public sealed partial class RlUiTests
{
    private const string ArchiveInvalidFormatKey = "Rl.Archive.InvalidFormat";
    private const string ArchiveRegisteredKey = "Rl.Archive.GenerationRegistered";
    private static readonly string ArchiveModelId = new('c', 64);
    private static readonly string OtherModelId = new('d', 64);

    private static readonly (string Name, string Text)[] GenerationFiles =
    {
        ("model.onnx", "onnx payload"),
        ("manifest.json", "{\"manifest\":true}"),
        ("job.json", "{\"job\":1}"),
        ("nested/data.bin", "nested payload"),
    };

    private static RlGeneration ArchiveGeneration(string modelId) => new(modelId, "SPY", "rev1", RlContract.ContractHash, "eval", 0.15m, 0.08m,
        new Dictionary<string, string>(), DateTimeOffset.UtcNow);

    /// <summary>A registered generation directory (below <paramref name="root"/>, named by the model id) with real files.</summary>
    private static string WriteGenerationDirectory(string root)
    {
        string directory = Path.Combine(root, ArchiveModelId);
        foreach (var (name, text) in GenerationFiles)
        {
            string path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return directory;
    }

    private static Mock<IDialogService> ArchiveDialogs(string? savePath, string? openPath)
    {
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>()))
            .ReturnsAsync(savePath);
        dialogs.Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string[]?>())).ReturnsAsync(openPath);
        return dialogs;
    }

    private async Task<RlTrainingDialogViewModel> ArchiveWindow(EmptyRegistry registry, IDialogService dialogs)
    {
        var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), Chart(new ImmediateData()),
            StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance, dialogService: dialogs);
        await vm.InitializeAsync();
        return vm;
    }

    private static EmptyRegistry ArchiveRegistry(string? generationDirectory, params RlGeneration[] generations) => new()
    {
        Capabilities = new(Storage: true, Recovery: false, Checkpoints: false),
        Generations = generations,
        LeaseFor = id => id == ArchiveModelId && generationDirectory is not null
            ? new ModelGenerationLease(id, Path.Combine(generationDirectory, "model.onnx"), () => { }) : null,
    };

    /// <summary>What the registry fixture saw when it was asked to register: the directory and the bytes of every file in it at that moment.</summary>
    private sealed class RegistrationRecorder
    {
        public int Calls { get; private set; }
        public string? Directory { get; private set; }
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public Task<RlGeneration> Register(string directory, Func<RlGeneration> result)
        {
            Calls++;
            Directory = directory;
            foreach (string file in System.IO.Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                Files[Path.GetRelativePath(directory, file).Replace('\\', '/')] = File.ReadAllBytes(file);
            return Task.FromResult(result());
        }
    }

    private static string BuildArchive(string path, string kind, Dictionary<string, byte[]> payload, Dictionary<string, string> hashes)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, bytes) in payload)
        {
            using var entry = archive.CreateEntry(name).Open();
            entry.Write(bytes, 0, bytes.Length);
        }
        var manifest = new RlArchiveManifest(RlModelArchiveService.CurrentSchemaVersion, kind, ArchiveModelId, DateTimeOffset.UtcNow,
            new Dictionary<string, string>(), hashes);
        using var manifestEntry = archive.CreateEntry(RlModelArchiveService.ManifestEntryName).Open();
        JsonSerializer.Serialize(manifestEntry, manifest);
        return path;
    }

    private static (Dictionary<string, byte[]> Payload, Dictionary<string, string> Hashes) SamplePayload()
    {
        var payload = GenerationFiles.ToDictionary(f => f.Name, f => Encoding.UTF8.GetBytes(f.Text), StringComparer.Ordinal);
        var hashes = payload.ToDictionary(p => p.Key, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(p.Value)).ToLowerInvariant(), StringComparer.Ordinal);
        return (payload, hashes);
    }

    private static string NewArchiveRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "RlGenerationArchiveUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteQuietly(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task GenerationExport_ArchivesEveryFileOfTheRegisteredDirectoryWithItsHashes()
    {
        string root = NewArchiveRoot();
        try
        {
            string generationDirectory = WriteGenerationDirectory(Path.Combine(root, "registry"));
            string zip = Path.Combine(root, "export.zip");
            var gen = ArchiveGeneration(ArchiveModelId);
            using var vm = await ArchiveWindow(ArchiveRegistry(generationDirectory, gen), ArchiveDialogs(zip, null).Object);
            vm.SelectedGeneration = gen;

            await vm.ExportGenerationCommand.ExecuteAsync(null);

            Assert.Contains("Rl.Archive.ExportSucceeded", vm.Message, StringComparison.Ordinal);
            using var archive = ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName).Where(n => n != RlModelArchiveService.ManifestEntryName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(GenerationFiles.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);
            using var manifestStream = archive.GetEntry(RlModelArchiveService.ManifestEntryName)!.Open();
            var manifest = JsonSerializer.Deserialize<RlArchiveManifest>(manifestStream)!;
            Assert.Equal(RlModelArchiveService.GenerationKind, manifest.ArchiveKind);
            Assert.Equal(ArchiveModelId, manifest.Id);
            foreach (var (name, _) in GenerationFiles)
                Assert.Equal(ContentHash.OfFile(Path.Combine(generationDirectory, name.Replace('/', Path.DirectorySeparatorChar))), manifest.FileHashes[name]);
        }
        finally { DeleteQuietly(root); }
    }

    [Fact]
    public async Task GenerationImport_HandsByteIdenticalFilesToTheRegistryAndRefreshesTheList()
    {
        string root = NewArchiveRoot();
        try
        {
            string generationDirectory = WriteGenerationDirectory(Path.Combine(root, "registry"));
            string zip = Path.Combine(root, "roundtrip.zip");
            var gen = ArchiveGeneration(ArchiveModelId);
            var registry = ArchiveRegistry(generationDirectory, gen);
            var recorder = new RegistrationRecorder();
            var imported = ArchiveGeneration(OtherModelId);
            registry.RegisterHandler = directory => recorder.Register(directory, () => { registry.Generations = new[] { gen, imported }; return imported; });
            using var vm = await ArchiveWindow(registry, ArchiveDialogs(zip, zip).Object);
            vm.SelectedGeneration = gen;
            await vm.ExportGenerationCommand.ExecuteAsync(null);
            Assert.DoesNotContain(vm.Generations, g => g.ModelId == OtherModelId);

            await vm.ImportGenerationCommand.ExecuteAsync(null);

            Assert.Equal(1, recorder.Calls);
            Assert.Contains(ArchiveRegisteredKey, vm.Message, StringComparison.Ordinal);
            Assert.Equal(GenerationFiles.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal), recorder.Files.Keys.OrderBy(n => n, StringComparer.Ordinal));
            foreach (var (name, _) in GenerationFiles)
                Assert.Equal(File.ReadAllBytes(Path.Combine(generationDirectory, name.Replace('/', Path.DirectorySeparatorChar))), recorder.Files[name]);
            Assert.Contains(vm.Generations, g => g.ModelId == OtherModelId);
            Assert.False(Directory.Exists(recorder.Directory), "the extraction folder must not be left behind");
            Assert.False(vm.IsBusy);
        }
        finally { DeleteQuietly(root); }
    }

    [Fact]
    public async Task GenerationImport_RefusesAnArchiveThatIsNotAGenerationAndRegistersNothing()
    {
        string root = NewArchiveRoot();
        try
        {
            var (payload, hashes) = SamplePayload();
            string zip = BuildArchive(Path.Combine(root, "checkpoint.zip"), RlModelArchiveService.CheckpointKind, payload, hashes);
            var registry = ArchiveRegistry(null);
            var recorder = new RegistrationRecorder();
            registry.RegisterHandler = directory => recorder.Register(directory, () => ArchiveGeneration(OtherModelId));
            using var vm = await ArchiveWindow(registry, ArchiveDialogs(null, zip).Object);

            await vm.ImportGenerationCommand.ExecuteAsync(null);

            Assert.Equal(ArchiveInvalidFormatKey, vm.Message);
            Assert.Equal(0, recorder.Calls);
        }
        finally { DeleteQuietly(root); }
    }

    [Fact]
    public async Task GenerationImport_RefusesATamperedArchiveWithTheVerificationErrorAndRegistersNothing()
    {
        string root = NewArchiveRoot();
        try
        {
            var (payload, hashes) = SamplePayload();
            payload["model.onnx"][0] ^= 0x01;
            string zip = BuildArchive(Path.Combine(root, "tampered.zip"), RlModelArchiveService.GenerationKind, payload, hashes);
            var registry = ArchiveRegistry(null);
            var recorder = new RegistrationRecorder();
            registry.RegisterHandler = directory => recorder.Register(directory, () => ArchiveGeneration(OtherModelId));
            using var vm = await ArchiveWindow(registry, ArchiveDialogs(null, zip).Object);

            await vm.ImportGenerationCommand.ExecuteAsync(null);

            Assert.Equal("Rl.Archive.Failure." + RlArchiveImportFailure.HashMismatch, vm.Message);
            Assert.Equal(0, recorder.Calls);
            Assert.False(vm.IsBusy);
        }
        finally { DeleteQuietly(root); }
    }

    [Fact]
    public void EveryArchiveRefusalReasonHasAMessageInBothLanguagesWithTheSamePlaceholders()
    {
        string locales = Path.Combine(TestSolution.Root, "StockAnalyzer.Avalonia", "Resources", "Locales");
        using var en = JsonDocument.Parse(File.ReadAllText(Path.Combine(locales, "en.json")));
        using var ja = JsonDocument.Parse(File.ReadAllText(Path.Combine(locales, "ja.json")));

        foreach (var reason in Enum.GetValues<RlArchiveImportFailure>().Where(r => r is not (RlArchiveImportFailure.None or RlArchiveImportFailure.Other)))
        {
            string key = "Rl.Archive.Failure." + reason;
            Assert.True(en.RootElement.TryGetProperty(key, out var english), "en " + key);
            Assert.True(ja.RootElement.TryGetProperty(key, out var japanese), "ja " + key);
            Assert.Equal(english.GetString()!.Contains("{0}"), japanese.GetString()!.Contains("{0}"));
        }
    }

    [Fact]
    public async Task GenerationImport_ShowsTheRegistryErrorAndDeletesTheExtractionFolderWhenRegistrationFails()
    {
        string root = NewArchiveRoot();
        try
        {
            var (payload, hashes) = SamplePayload();
            string zip = BuildArchive(Path.Combine(root, "rejected.zip"), RlModelArchiveService.GenerationKind, payload, hashes);
            var registry = ArchiveRegistry(null);
            var recorder = new RegistrationRecorder();
            registry.RegisterHandler = directory =>
            {
                _ = recorder.Register(directory, () => ArchiveGeneration(OtherModelId));
                throw new InvalidDataException("registry rejected the generation");
            };
            using var vm = await ArchiveWindow(registry, ArchiveDialogs(null, zip).Object);

            await vm.ImportGenerationCommand.ExecuteAsync(null);

            Assert.Equal(1, recorder.Calls);
            Assert.DoesNotContain(ArchiveRegisteredKey, vm.Message, StringComparison.Ordinal);
            Assert.Contains("registry rejected the generation", vm.TechnicalDetails, StringComparison.Ordinal);
            Assert.False(Directory.Exists(recorder.Directory), "the extraction folder must not be left behind");
            Assert.False(vm.IsBusy);
        }
        finally { DeleteQuietly(root); }
    }

    /// <summary>An archive service whose work is synchronous and blocks until released (what the real one does while it hashes and extracts).</summary>
    private sealed class BlockingArchiveService : IRlModelArchiveService
    {
        private static readonly TimeSpan MaximumBlock = TimeSpan.FromSeconds(5);
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        private void Block() { Entered.Set(); Release.Wait(MaximumBlock); }
        public Task<string> ExportCheckpointArchiveAsync(string checkpointDirectory, string destinationZipPath, System.Threading.CancellationToken ct = default)
        { Block(); return Task.FromResult(destinationZipPath); }
        public Task<string> ExportGenerationArchiveAsync(string generationDirectory, string destinationZipPath, System.Threading.CancellationToken ct = default)
        { Block(); return Task.FromResult(destinationZipPath); }
        public Task<RlArchiveImportResult> ImportArchiveAsync(string sourceZipPath, string destinationDirectory, System.Threading.CancellationToken ct = default)
        { Block(); return Task.FromResult(new RlArchiveImportResult(false, "", "", destinationDirectory, 0, "released")); }
    }

    [Fact]
    public async Task GenerationImportAndExport_HandTheArchiveWorkToAnotherThreadSoTheCallerIsNotBlocked()
    {
        string root = NewArchiveRoot();
        try
        {
            string generationDirectory = WriteGenerationDirectory(Path.Combine(root, "registry"));
            string zip = Path.Combine(root, "blocked.zip");
            File.WriteAllText(zip, "not read by the fake");
            var gen = ArchiveGeneration(ArchiveModelId);
            var service = new BlockingArchiveService();
            var vm = new RlTrainingDialogViewModel(new DelayedTraining(), ArchiveRegistry(generationDirectory, gen), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(),
                Chart(new ImmediateData()), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance, dialogService: ArchiveDialogs(zip, zip).Object, archiveService: service);
            using var _ = vm;
            await vm.InitializeAsync();
            vm.SelectedGeneration = gen;

            // A service that blocks on the calling thread would keep these calls from returning until it is released.
            var export = vm.ExportGenerationCommand.ExecuteAsync(null);
            Assert.False(export.IsCompleted);
            Assert.True(service.Entered.Wait(TimeSpan.FromSeconds(5)));
            service.Release.Set();
            await export;

            service.Entered.Reset(); service.Release.Reset();
            var import = vm.ImportGenerationCommand.ExecuteAsync(null);
            Assert.False(import.IsCompleted);
            Assert.True(service.Entered.Wait(TimeSpan.FromSeconds(5)));
            service.Release.Set();
            await import;
            Assert.Equal("released", vm.Message);
        }
        finally { DeleteQuietly(root); }
    }

    [Fact]
    public async Task GenerationImport_IsDisabledWithoutADialogServiceInsteadOfDoingNothing()
    {
        var registry = ArchiveRegistry(null);
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(),
            Chart(new ImmediateData()), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync();

        Assert.False(vm.CanImportGeneration);
        Assert.False(vm.ImportGenerationCommand.CanExecute(null));
    }

    [Fact]
    public async Task GenerationImport_DoesNothingWhenTheOpenDialogIsCancelled()
    {
        var registry = ArchiveRegistry(null);
        var recorder = new RegistrationRecorder();
        registry.RegisterHandler = directory => recorder.Register(directory, () => ArchiveGeneration(OtherModelId));
        using var vm = await ArchiveWindow(registry, ArchiveDialogs(null, null).Object);

        await vm.ImportGenerationCommand.ExecuteAsync(null);

        Assert.Equal(0, recorder.Calls);
        Assert.Equal("", vm.Message);
        Assert.False(vm.IsBusy);
    }
}
