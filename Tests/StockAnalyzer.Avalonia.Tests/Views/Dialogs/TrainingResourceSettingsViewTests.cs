using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

public sealed class TrainingResourceSettingsViewTests
{
    [AvaloniaFact]
    public void PageRendersThreeBoundedInputsAndPreviewControls()
    {
        var path = Path.Combine(Path.GetTempPath(), "sa_training_view_" + Guid.NewGuid().ToString("N") + ".json");
        using var vm = new TrainingResourceSettingsViewModel(new TrainingResourceSettings(path));
        var view = Assert.IsType<TrainingResourceSettingsView>(new ViewLocator().Build(vm));
        var window = new Window { Content = view, Width = 800, Height = 700 };
        view.DataContext = vm;

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var inputs = view.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
            Assert.Equal(3, inputs.Length);
            Assert.Equal(new decimal[]
            {
                TrainingResourceOverrides.MaximumChannels,
                TrainingResourceOverrides.MaximumTensorSizeMiB,
                TrainingResourceOverrides.MaximumSamples,
            }, inputs.Select(input => input.Maximum));
            Assert.All(inputs, input =>
            {
                Assert.Equal(TrainingResourceOverrides.Minimum, input.Minimum);
                Assert.True(input.ClipValueToMinMax);
            });
            Assert.All(inputs, input => Assert.False(input.IsEffectivelyEnabled));
            vm.ChannelsEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(inputs[0].IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }
}
