using System;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services;

public interface ILayoutSaveScheduler : IAsyncDisposable
{
    void RequestSave(LayoutChangeReason reason);
    Task ForceSaveImmediateAsync();
    void RegisterSaveAction(Func<Task> saveAction);
    void Cancel();
}
