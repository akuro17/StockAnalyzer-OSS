using System;

namespace StockAnalyzer.Avalonia.Models;

public record LogFileInfo(string Name, DateTime LastModified, long SizeBytes, string FullPath);
