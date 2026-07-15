using System.Windows.Media;
using Xunit;
using StockAnalyzer.ViewModels;

namespace StockAnalyzer.Tests.ViewModels
{
    public class IndicatorSettingsHistoryTests
    {
        [Fact]
        public void SaveState_ShouldPushToUndoStack()
        {
            // Arrange
            var history = new IndicatorSettingsHistory();
            var settings = CreateTestSettings();

            // Act
            history.SaveState(settings);

            // Assert
            Assert.True(history.CanUndo);
            Assert.False(history.CanRedo);
        }

        [Fact]
        public void Undo_ShouldRestorePreviousState()
        {
            // Arrange
            var history = new IndicatorSettingsHistory();
            var settings = CreateTestSettings();
            settings.Color = Colors.Red;
            
            history.SaveState(settings);
            settings.Color = Colors.Blue; // Change after save

            // Act
            history.Undo(settings);

            // Assert
            Assert.Equal(Colors.Red, settings.Color);
            Assert.False(history.CanUndo);
            Assert.True(history.CanRedo);
        }

        [Fact]
        public void Redo_ShouldRestoreUndoneState()
        {
            // Arrange
            var history = new IndicatorSettingsHistory();
            var settings = CreateTestSettings();
            settings.Color = Colors.Red;
            
            history.SaveState(settings);
            settings.Color = Colors.Blue;
            history.Undo(settings); // Now settings.Color = Red

            // Act
            history.Redo(settings);

            // Assert
            Assert.Equal(Colors.Blue, settings.Color);
            Assert.True(history.CanUndo);
            Assert.False(history.CanRedo);
        }

        [Fact]
        public void SaveState_ShouldClearRedoStack()
        {
            // Arrange
            var history = new IndicatorSettingsHistory();
            var settings = CreateTestSettings();
            
            history.SaveState(settings);
            settings.Color = Colors.Blue;
            history.Undo(settings);
            Assert.True(history.CanRedo);

            // Act - new action should clear redo
            history.SaveState(settings);

            // Assert
            Assert.False(history.CanRedo);
        }

        [Fact]
        public void SaveState_ShouldLimitHistorySize()
        {
            // Arrange
            var history = new IndicatorSettingsHistory();
            var settings = CreateTestSettings();

            // Act - push more than max (50)
            for (int i = 0; i < 60; i++)
            {
                settings.Offset = i;
                history.SaveState(settings);
            }

            // Assert - should still have undo capability, limited to 50
            Assert.True(history.CanUndo);
        }

        [Fact]
        public void Clear_ShouldResetBothStacks()
        {
            // Arrange
            var history = new IndicatorSettingsHistory();
            var settings = CreateTestSettings();
            
            history.SaveState(settings);
            history.Undo(settings);

            // Act
            history.Clear();

            // Assert
            Assert.False(history.CanUndo);
            Assert.False(history.CanRedo);
        }

        private IndicatorSettings CreateTestSettings()
        {
            return new IndicatorSettings
            {
                Type = "SMA",
                IsEnabled = true,
                Color = Colors.Red,
                Thickness = 1.5,
                Offset = 0,
                UseUpDownColors = false,
                UpColor = Colors.Lime,
                DownColor = Colors.Red
            };
        }
    }

    public class IndicatorSettingsMementoTests
    {
        [Fact]
        public void Constructor_ShouldCaptureAllProperties()
        {
            // Arrange
            var settings = new IndicatorSettings
            {
                Type = "EMA",
                IsEnabled = true,
                Color = Colors.Blue,
                Thickness = 2.0,
                Offset = 5,
                UseUpDownColors = true,
                UpColor = Colors.Green,
                DownColor = Colors.Magenta
            };

            // Act
            var memento = new IndicatorSettingsMemento(settings);

            // Assert
            Assert.Equal("EMA", memento.Type);
            Assert.True(memento.IsEnabled);
            Assert.Equal(Colors.Blue, memento.Color);
            Assert.Equal(2.0, memento.Thickness);
            Assert.Equal(5, memento.Offset);
            Assert.True(memento.UseUpDownColors);
            Assert.Equal(Colors.Green, memento.UpColor);
            Assert.Equal(Colors.Magenta, memento.DownColor);
        }

        [Fact]
        public void RestoreTo_ShouldRestoreAllProperties()
        {
            // Arrange
            var original = new IndicatorSettings
            {
                Type = "RSI",
                IsEnabled = true,
                Color = Colors.Yellow,
                Thickness = 3.0,
                Offset = 10,
                UseUpDownColors = true,
                UpColor = Colors.Cyan,
                DownColor = Colors.Orange
            };
            var memento = new IndicatorSettingsMemento(original);

            var target = new IndicatorSettings
            {
                Type = "RSI",
                IsEnabled = false,
                Color = Colors.Black,
                Thickness = 1.0,
                Offset = 0,
                UseUpDownColors = false,
                UpColor = Colors.White,
                DownColor = Colors.White
            };

            // Act
            memento.RestoreTo(target);

            // Assert
            Assert.True(target.IsEnabled);
            Assert.Equal(Colors.Yellow, target.Color);
            Assert.Equal(3.0, target.Thickness);
            Assert.Equal(10, target.Offset);
            Assert.True(target.UseUpDownColors);
            Assert.Equal(Colors.Cyan, target.UpColor);
            Assert.Equal(Colors.Orange, target.DownColor);
        }
    }
}
