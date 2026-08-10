using Xunit;
using StockAnalyzer.Core.Models.Watchlist;
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
