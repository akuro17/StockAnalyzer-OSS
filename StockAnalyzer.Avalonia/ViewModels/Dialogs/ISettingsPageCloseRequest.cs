using System;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Optional capability of a settings page that finishes its job by asking the Settings window to close.
/// The request takes the window's normal close path, so the discard confirmation is not bypassed.
/// </summary>
public interface ISettingsPageCloseRequest
{
    event Action? CloseRequested;
}
