using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class LogViewerViewModel : ViewModelBase
{
    private readonly ILogService _logService;
    private readonly IDialogService _dialogService;
    private readonly Core.Services.IClipboardService _clipboardService;

    [ObservableProperty]
    private ObservableCollection<LogFileInfo> _logFiles = new();

    [ObservableProperty]
    private LogFileInfo? _selectedLogFile;

    [ObservableProperty]
    private ObservableCollection<LogEntry> _logs = new();

    [ObservableProperty]
    private bool _isErrorOnly;

    [ObservableProperty]
    private bool _isLoggingEnabled;

    [ObservableProperty]
    private string _searchText = string.Empty;

    private IEnumerable<LogEntry> _allLogs = Enumerable.Empty<LogEntry>();

    public LogViewerViewModel(ILogService logService, IDialogService dialogService, Core.Services.IClipboardService clipboardService)
    {
        _logService = logService;
        _dialogService = dialogService;
        _clipboardService = clipboardService;
        _isLoggingEnabled = _logService.IsLoggingEnabled;
        LoadLogFiles();
    }

    partial void OnIsLoggingEnabledChanged(bool value)
    {
        _logService.IsLoggingEnabled = value;
    }

    private void LoadLogFiles()
    {
        LogFiles = new ObservableCollection<LogFileInfo>(_logService.GetLogFiles());
        SelectedLogFile = LogFiles.FirstOrDefault();
    }

    partial void OnSelectedLogFileChanged(LogFileInfo? value)
    {
        if (value != null)
        {
            _ = LoadLogsAsync(value.Name);
        }
        else
        {
            Logs.Clear();
            _allLogs = Enumerable.Empty<LogEntry>();
        }
    }

    private async Task LoadLogsAsync(string fileName)
    {
        _allLogs = await _logService.GetLogsAsync(fileName);
        ApplyFilter();
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        var filtered = _allLogs;

        if (IsErrorOnly)
        {
            filtered = filtered.Where(x => x.Level == LogLevel.Error || x.Level == LogLevel.Fatal);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = filtered.Where(x => x.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || 
                                           (x.Exception?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        Logs = new ObservableCollection<LogEntry>(filtered);
    }

    [RelayCommand]
    private async Task ExportLogAsync(LogFileInfo? fileInfo)
    {
        var targetFile = fileInfo ?? SelectedLogFile;
        if (targetFile == null) return;
        
        var targetPath = await _dialogService.ShowSaveFileDialogAsync(
            LocalizationManager.Instance["LogViewer_Dialog_ExportTitle"],
            "log",
            targetFile.Name,
            new[] { "log", "txt" }
        );

        if (!string.IsNullOrEmpty(targetPath))
        {
            try
            {
                await _logService.ExportLogAsync(targetFile.Name, targetPath);
                await _dialogService.ShowAlertAsync(
                    LocalizationManager.Instance["LogViewer_Dialog_Success"], 
                    string.Format(LocalizationManager.Instance["LogViewer_Dialog_ExportSuccess"], targetPath));
            }
            catch (Exception ex)
            {
                await _dialogService.ShowAlertAsync(
                    LocalizationManager.Instance["LogViewer_Dialog_Error"], 
                    string.Format(LocalizationManager.Instance["LogViewer_Dialog_ExportError"], ex.Message));
            }
        }
    }

    [RelayCommand]
    private async Task CopyToClipboardAsync()
    {
        if (Logs.Count == 0) return;

        var text = string.Join(Environment.NewLine, Logs.Select(x => 
            $"{x.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{x.Level}] {x.Message}{(string.IsNullOrEmpty(x.Exception) ? "" : "\n" + x.Exception)}"));
        
        await SetClipboardTextAsync(text, LocalizationManager.Instance["LogViewer_Dialog_ClipboardContent"]);
    }

    [RelayCommand]
    private async Task CopyPathToClipboardAsync(LogFileInfo? fileInfo)
    {
        var targetFile = fileInfo ?? SelectedLogFile;
        if (targetFile == null) return;

        await SetClipboardTextAsync(targetFile.FullPath, LocalizationManager.Instance["LogViewer_Dialog_ClipboardPath"]);
    }

    [RelayCommand]
    private async Task CopyContentToClipboardAsync(LogFileInfo? fileInfo)
    {
        var targetFile = fileInfo ?? SelectedLogFile;
        if (targetFile == null) return;

        try
        {
            var content = await _logService.GetRawLogContentAsync(targetFile.Name);
            await SetClipboardTextAsync(content, LocalizationManager.Instance["LogViewer_Dialog_ClipboardRaw"]);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowAlertAsync(
                LocalizationManager.Instance["LogViewer_Dialog_Error"], 
                string.Format(LocalizationManager.Instance["LogViewer_Dialog_CopyContentError"], ex.Message));
        }
    }

    [RelayCommand]
    private async Task RenameLogAsync(LogFileInfo? fileInfo)
    {
        var targetFile = fileInfo ?? SelectedLogFile;
        if (targetFile == null) return;

        var newName = await _dialogService.ShowInputAsync(
            LocalizationManager.Instance["LogViewer_Dialog_RenameTitle"], 
            LocalizationManager.Instance["LogViewer_Dialog_RenameLabel"], 
            targetFile.Name);
            
        if (!string.IsNullOrEmpty(newName) && newName != targetFile.Name)
        {
            try
            {
                await _logService.RenameLogAsync(targetFile.Name, newName);
                LoadLogFiles();
                await _dialogService.ShowAlertAsync(
                    LocalizationManager.Instance["LogViewer_Dialog_Success"], 
                    LocalizationManager.Instance["LogViewer_Dialog_RenameSuccess"]);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowAlertAsync(
                    LocalizationManager.Instance["LogViewer_Dialog_Error"], 
                    string.Format(LocalizationManager.Instance["LogViewer_Dialog_RenameError"], ex.Message));
            }
        }
    }

    [RelayCommand]
    private async Task DeleteLogAsync(LogFileInfo? fileInfo)
    {
        var targetFile = fileInfo ?? SelectedLogFile;
        if (targetFile == null) return;

        bool confirm = await _dialogService.ShowConfirmationAsync(
            LocalizationManager.Instance["LogViewer_Dialog_DeleteTitle"], 
            string.Format(LocalizationManager.Instance["LogViewer_Dialog_DeleteConfirm"], targetFile.Name));
            
        if (confirm)
        {
            try
            {
                await _logService.DeleteLogAsync(targetFile.Name);
                if (SelectedLogFile?.Name == targetFile.Name)
                {
                    SelectedLogFile = null;
                }
                LoadLogFiles();
                await _dialogService.ShowAlertAsync(
                    LocalizationManager.Instance["LogViewer_Dialog_Success"], 
                    LocalizationManager.Instance["LogViewer_Dialog_DeleteSuccess"]);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowAlertAsync(
                    LocalizationManager.Instance["LogViewer_Dialog_Error"], 
                    string.Format(LocalizationManager.Instance["LogViewer_Dialog_DeleteError"], ex.Message));
            }
        }
    }

    private async Task SetClipboardTextAsync(string text, string successMessage)
    {
        await _clipboardService.SetTextAsync(text);
        await _dialogService.ShowAlertAsync(
            LocalizationManager.Instance["LogViewer_Dialog_ClipboardTitle"], 
            successMessage);
    }
}
