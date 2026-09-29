using Xunit;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using System;

namespace StockAnalyzer.Avalonia.Tests.ViewModels
{
    public class WatchlistItemViewModelTests
    {
        private WatchlistItemViewModel CreateViewModel()
        {
            return new WatchlistItemViewModel(
                "AAPL", "Apple Inc.", "Technology", "Consumer Electronics",
                150.0m, 155.0m, 149.0m, 153.0m, 1000000, 2.0, 3.0m);
        }

        [Fact]
        public void DisplayNotes_WhenEmpty_ShowsPlaceholder()
        {
            var vm = CreateViewModel();
            Assert.Equal("-", vm.DisplayNotes);
        }

        [Fact]
        public void DisplayNotes_ReplacesNewlinesWithSpacesForSingleLineCellDisplay()
        {
            var vm = CreateViewModel();
            vm.Notes = "line one\r\nline two\nline three";

            Assert.Equal("line one line two line three", vm.DisplayNotes);
            // The raw, unconverted value (bound to the cell's tooltip) must keep real newlines.
            Assert.Equal("line one\r\nline two\nline three", vm.Notes);
        }

        [Fact]
        public void RefreshNotes_PicksUpAnExternallyUpdatedCacheEntry_AndRaisesDisplayNotesChanged()
        {
            // Regression test (sa_minimal_fix): previously a Notes-tab-driven cache update to an
            // already-displayed row's ticker was never reflected until an app restart, because
            // nothing told the row's WatchlistItemViewModel to re-read UserStrategyMetadataRepository.
            var ticker = "REFRESH_NOTES_TEST_" + Guid.NewGuid().ToString("N");
            var vm = new WatchlistItemViewModel(
                ticker, "Test Co.", "Technology", "Consumer Electronics",
                150.0m, 155.0m, 149.0m, 153.0m, 1000000, 2.0, 3.0m);
            Assert.Equal("-", vm.DisplayNotes);

            // Simulate the Notes-tab cache synchronizer updating the repository out-of-band.
            UserStrategyMetadataRepository.Instance.SaveStrategy(ticker, null, null, null, null, null, null, "latest article preview");

            var raisedProperties = new System.Collections.Generic.List<string?>();
            vm.PropertyChanged += (s, e) => raisedProperties.Add(e.PropertyName);

            vm.RefreshNotes();

            Assert.Equal("latest article preview", vm.Notes);
            Assert.Equal("latest article preview", vm.DisplayNotes);
            Assert.Contains(nameof(WatchlistItemViewModel.DisplayNotes), raisedProperties);
        }

        [Fact]
        public void DisplayReminder_WhenEmpty_ShowsPlaceholder()
        {
            var vm = CreateViewModel();
            Assert.Equal("-", vm.DisplayReminder);
        }

        [Fact]
        public void DisplayReminder_WhenSet_ReturnsRawValue()
        {
            var vm = CreateViewModel();
            vm.Reminder = "Check earnings date";

            Assert.Equal("Check earnings date", vm.DisplayReminder);
        }

        [Fact]
        public void InitialStatus_ShouldBePending()
        {
            var vm = CreateViewModel();
            Assert.Equal(LoadStatus.Pending, vm.Status);
        }

        [Fact]
        public void Transition_PendingToLoading_ShouldSucceed()
        {
            var vm = CreateViewModel();
            vm.MarkLoading();
            Assert.Equal(LoadStatus.Loading, vm.Status);
        }

        [Fact]
        public void Transition_LoadingToSuccess_ShouldSucceed()
        {
            var vm = CreateViewModel();
            vm.MarkLoading();
            vm.MarkSuccess();
            Assert.Equal(LoadStatus.Success, vm.Status);
            Assert.NotNull(vm.LastUpdatedUtc);
        }

        [Fact]
        public void Transition_LoadingToFailed_ShouldSucceed()
        {
            var vm = CreateViewModel();
            vm.MarkLoading();
            vm.MarkFailed("ERROR_001", "Something went wrong");
            Assert.Equal(LoadStatus.Failed, vm.Status);
            Assert.Equal("ERROR_001", vm.ErrorCode);
            Assert.Equal("Something went wrong", vm.ErrorMessage);
            Assert.NotNull(vm.LastUpdatedUtc);
        }

        [Fact]
        public void IllegalTransition_PendingToSuccess_ShouldThrow()
        {
            var vm = CreateViewModel();
            Assert.Throws<InvalidOperationException>(() => vm.MarkSuccess());
        }

        [Fact]
        public void Transition_FailedToPending_ShouldSucceed()
        {
            var vm = CreateViewModel();
            vm.MarkLoading();
            vm.MarkFailed("ERR", "Msg");
            vm.ResetToPending();
            Assert.Equal(LoadStatus.Pending, vm.Status);
        }

        [Fact]
        public void PropertyChange_ReturnOnEquity_ShouldNotifyDisplayReturnOnEquity()
        {
            var vm = CreateViewModel();
            string? changedProp = null;
            vm.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(vm.DisplayReturnOnEquity))
                {
                    changedProp = e.PropertyName;
                }
            };
            vm.ReturnOnEquity = 15m;
            Assert.Equal("DisplayReturnOnEquity", changedProp);
            Assert.Equal("15.00%", vm.DisplayReturnOnEquity);
        }
    }
}
