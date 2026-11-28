using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class PythonDownloadAndSettingsHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa-py-dl-tests", Guid.NewGuid().ToString("N"));

    public PythonDownloadAndSettingsHardeningTests()
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
    }

    [Fact]
    public void PythonSettings_AutoUpdatePackagesOnStartup_DefaultsToFalse()
    {
        var settings = new PythonSettings();
        Assert.False(settings.AutoUpdatePackagesOnStartup);
    }

    [Fact]
    public void StockAnalyzerSettings_PythonAutoUpdatePackagesOnStartup_ReflectsOption()
    {
        IStockAnalyzerSettings CreateSettings(PythonSettings pythonSettings) => new StockAnalyzerSettings(
            new ConfigurationBuilder().Build(),
            Options.Create(pythonSettings),
            Options.Create(new ChartDefaultSettings()),
            Options.Create(new PredictionSettings()),
            Options.Create(new ScreenerSettings()),
            Options.Create(new SmartScreenerSettings()),
            Options.Create(new InfrastructureSettings()),
            Options.Create(new MarketStructureSettings()),
            Options.Create(new PatternRecognitionSettings()),
            Options.Create(new ResilienceSettings()),
            Options.Create(new LocalizationSettings()));

        Assert.False(CreateSettings(new PythonSettings()).PythonAutoUpdatePackagesOnStartup);
        Assert.True(CreateSettings(new PythonSettings { AutoUpdatePackagesOnStartup = true }).PythonAutoUpdatePackagesOnStartup);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void ShouldSkipUpdateCheck_SkipsOnlyWhenInstalledWithoutForceOrStartupUpdate(
        bool isInstalled,
        bool forceUpgrade,
        bool autoUpdateOnStartup,
        bool expectedSkip)
    {
        Assert.Equal(expectedSkip, PythonService.ShouldSkipUpdateCheck(isInstalled, forceUpgrade, autoUpdateOnStartup));
    }

    [Fact]
    public async Task DownloadWithClientAsync_ContentLengthExceedingMax_ThrowsAndDoesNotCreateFile()
    {
        using var handler = new StubHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[1024])
            };
            resp.Content.Headers.ContentLength = 1024;
            return resp;
        });
        using var client = new HttpClient(handler);
        var target = Path.Combine(_root, "too_big.zip");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PythonEmbeddedInstaller.DownloadWithClientAsync(client, "https://example.invalid/a.zip", target, maxBytes: 512, backoffProvider: null, CancellationToken.None));

        Assert.False(File.Exists(target));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DownloadWithClientAsync_StreamExceedingMaxWithoutContentLength_ThrowsAndDeletesPartialFile()
    {
        using var handler = new StubHttpHandler(_ =>
        {
            var stream = new NoLengthStream(new byte[2048]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
        });
        using var client = new HttpClient(handler);
        var target = Path.Combine(_root, "stream_overflow.zip");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PythonEmbeddedInstaller.DownloadWithClientAsync(client, "https://example.invalid/b.zip", target, maxBytes: 1024, backoffProvider: null, CancellationToken.None));

        Assert.False(File.Exists(target));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DownloadWithClientAsync_TransientFailure_RetriesAndSucceeds()
    {
        int calls = 0;
        byte[] payload = new byte[] { 1, 2, 3, 4 };
        using var handler = new StubHttpHandler(_ =>
        {
            calls++;
            if (calls < 3)
            {
                throw new HttpRequestException("transient network error");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        });
        using var client = new HttpClient(handler);
        var target = Path.Combine(_root, "retry_ok.zip");

        await PythonEmbeddedInstaller.DownloadWithClientAsync(
            client,
            "https://example.invalid/c.zip",
            target,
            maxBytes: 1024,
            backoffProvider: _ => TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class NoLengthStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;
    }
}
