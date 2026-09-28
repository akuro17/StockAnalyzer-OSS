using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs.Common;

/// <summary>
/// Behavioural coverage for <see cref="TemplateCrudHelper{TTemplate}"/>'s shared control flow,
/// exercised directly (no owning ViewModel). Uses in-file doubles because
/// Tests/StockAnalyzer.Avalonia.Tests has no shared template-service fake for
/// <see cref="FeatureSpecTemplate"/>.
/// </summary>
public class TemplateCrudHelperTests
{
    private sealed class AlwaysValidTemplateService : ITemplateService
    {
        public Task<T?> GetAsync<T>(TemplateType type, Guid id) where T : TemplateBase => Task.FromResult<T?>(null);

        public Task<IReadOnlyList<T>> GetAllAsync<T>(TemplateType type) where T : TemplateBase
            => Task.FromResult((IReadOnlyList<T>)new List<T>());

        public Task SaveAsync<T>(T template) where T : TemplateBase => Task.CompletedTask;

        public Task<bool> DeleteAsync(TemplateType type, Guid id) => Task.FromResult(true);

        public Task<TemplateValidationResult> ValidateAsync<T>(T template) where T : TemplateBase
            => Task.FromResult(TemplateValidationResult.Success());

        public Task EnsureMigratedAsync() => Task.CompletedTask;
    }

    private sealed class RecordingToastService : IToastNotificationService
    {
        public List<string> Messages { get; } = new();
        public string? NotificationMessage { get; private set; }
        public bool IsNotificationVisible { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void ShowNotification(string message)
        {
            Messages.Add(message);
            NotificationMessage = message;
            IsNotificationVisible = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationMessage)));
        }
    }

    private static TemplateCrudHelper<FeatureSpecTemplate> NewHelper(RecordingToastService toast)
        => new(new AlwaysValidTemplateService(), toast, TemplateType.Feature);

    private static string LoadedToast(FeatureSpecTemplate template)
        => string.Format(
            CultureInfo.CurrentCulture,
            LocalizationManager.Instance[TemplateCrudHelper<FeatureSpecTemplate>.MsgLoaded],
            template.Name);

    [Fact]
    public async Task ApplyAsync_HappyPath_RunsOnAppliedThenShowsExactlyOneSuccessToast()
    {
        var toast = new RecordingToastService();
        var helper = NewHelper(toast);
        var template = new FeatureSpecTemplate { Id = Guid.NewGuid(), Name = "T1", Spec = new FeatureSpec { Channels = new List<FeatureChannel>() } };
        var onAppliedRan = false;

        await helper.ApplyAsync(
            template,
            append: false,
            apply: _ => Task.CompletedTask,
            onApplied: () => onAppliedRan = true);

        Assert.True(onAppliedRan);
        Assert.Equal(new[] { LoadedToast(template) }, toast.Messages);
    }

    [Fact]
    public async Task ApplyAsync_WhenOnAppliedThrows_ShowsOnlyErrorToastAndReportsError()
    {
        var toast = new RecordingToastService();
        var helper = NewHelper(toast);
        var template = new FeatureSpecTemplate { Id = Guid.NewGuid(), Name = "T1", Spec = new FeatureSpec { Channels = new List<FeatureChannel>() } };
        Exception? reported = null;

        await helper.ApplyAsync(
            template,
            append: false,
            apply: _ => Task.CompletedTask,
            onError: ex => reported = ex,
            onApplied: () => throw new InvalidOperationException("chart re-apply failed"));

        Assert.IsType<InvalidOperationException>(reported);
        Assert.DoesNotContain(LoadedToast(template), toast.Messages); // no contradictory success toast
        Assert.Equal(
            new[] { LocalizationManager.Instance[TemplateCrudHelper<FeatureSpecTemplate>.MsgLoadError] },
            toast.Messages);
    }
}
