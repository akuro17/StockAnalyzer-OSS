namespace StockAnalyzer.McpServer.Tools;

/// <summary>The words every tool that reads the saved drawing file uses for the same situation (one definition, so no tool refers to another tool for them).</summary>
internal static class SavedDrawingNotices
{
    /// <summary>The drawing file is the old V1 format: it is neither read nor converted.</summary>
    public const string LegacyWarning = "Legacy V1 drawing file detected; open and save in the GUI to migrate to V2.";
}
