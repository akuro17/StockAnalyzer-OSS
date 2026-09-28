using System;
using System.IO;
using System.Runtime.InteropServices;
using StockAnalyzer.Avalonia.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

public class ExternalFileOpenerTests
{
    [Fact]
    public void CreateFileProcessStartInfo_Windows_UsesDirectShellExecuteWithoutExplorer()
    {
        var filePath = @"C:\Images\chart.png";

        var startInfo = ExternalFileOpener.CreateFileProcessStartInfo(filePath, OSPlatform.Windows);

        Assert.Equal(filePath, startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public void CreateFileProcessStartInfo_OSX_UsesOpenLauncher()
    {
        var filePath = "/Users/test/chart.png";

        var startInfo = ExternalFileOpener.CreateFileProcessStartInfo(filePath, OSPlatform.OSX);

        Assert.Equal("open", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(new[] { filePath }, startInfo.ArgumentList);
    }

    [Fact]
    public void CreateFileProcessStartInfo_Linux_UsesXdgOpenLauncher()
    {
        var filePath = "/home/test/chart.png";

        var startInfo = ExternalFileOpener.CreateFileProcessStartInfo(filePath, OSPlatform.Linux);

        Assert.Equal("xdg-open", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(new[] { filePath }, startInfo.ArgumentList);
    }

    [Fact]
    public void CreateUrlProcessStartInfo_Windows_UsesDirectShellExecute()
    {
        var url = "https://example.com/chart";

        var startInfo = ExternalFileOpener.CreateUrlProcessStartInfo(url, OSPlatform.Windows);

        Assert.Equal(url, startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
    }

    [Theory]
    [InlineData("OSX", "open")]
    [InlineData("Linux", "xdg-open")]
    public void CreateUrlProcessStartInfo_Unix_UsesNativeLauncher(string platformName, string expectedLauncher)
    {
        var url = "https://example.com/chart";
        var platform = platformName switch
        {
            "OSX" => OSPlatform.OSX,
            "Linux" => OSPlatform.Linux,
            _ => throw new ArgumentOutOfRangeException(nameof(platformName))
        };

        var startInfo = ExternalFileOpener.CreateUrlProcessStartInfo(url, platform);

        Assert.Equal(expectedLauncher, startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(new[] { url }, startInfo.ArgumentList);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OpenFile_WithEmptyOrWhitespacePath_ReturnsFalse(string? path)
    {
        var opener = new ExternalFileOpener();

        var result = opener.OpenFile(path!);

        Assert.False(result);
    }

    [Fact]
    public void OpenFile_WithNonExistentFile_ReturnsFalse()
    {
        var opener = new ExternalFileOpener();
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "missing.png");

        var result = opener.OpenFile(nonExistentPath);

        Assert.False(result);
    }

    [Theory]
    [InlineData("malicious.exe")]
    [InlineData("script.bat")]
    [InlineData("command.cmd")]
    [InlineData("powershell.ps1")]
    [InlineData("script.vbs")]
    [InlineData("payload.dll")]
    public void OpenFile_WithUnapprovedExtension_ReturnsFalse(string fileName)
    {
        var opener = new ExternalFileOpener();
        var tempFile = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllText(tempFile, "test");

        try
        {
            var result = opener.OpenFile(tempFile);
            Assert.False(result);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com/file.png")]
    [InlineData("file:///C:/test.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public void OpenUrl_WithInvalidOrNonHttpScheme_ReturnsFalse(string? url)
    {
        var opener = new ExternalFileOpener();

        var result = opener.OpenUrl(url!);

        Assert.False(result);
    }
}
