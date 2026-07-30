namespace StockAnalyzer.Avalonia.Models;

public enum BulkTagEditAction
{
    Add,
    Delete
}

public record BulkTagEditResult(BulkTagEditAction Action, string Tags);
