using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs;

public class FilterTemplatePickerDialogViewModelTests
{
    private static FilterTemplatePickerDialogViewModel CreateViewModel(
        TickerListViewModel owner, FilterNode targetNode, Mock<ITemplateService> templateService)
    {
        var toastService = new Mock<IToastNotificationService>();
        return new FilterTemplatePickerDialogViewModel(owner, targetNode, templateService.Object, toastService.Object);
    }

    [Fact]
    public void Constructor_LoadsExistingFilterTemplates()
    {
        var owner = TickerListViewModelTests.CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var targetNode = new FilterNode(new FilterSettings { Name = "Target" });
            var existing = new FilterTemplate { Name = "Existing" };
            var mockTemplateService = new Mock<ITemplateService>();
            mockTemplateService.Setup(s => s.GetAllAsync<FilterTemplate>(TemplateType.Filter))
                .ReturnsAsync(new List<FilterTemplate> { existing });

            var vm = CreateViewModel(owner, targetNode, mockTemplateService);

            Assert.Single(vm.Templates);
            Assert.Equal("Existing", vm.Templates[0].Name);
            vm.Dispose();
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public async Task SaveTemplateAsync_PersistsCurrentSubtree_AndAddsToTemplatesList()
    {
        var owner = TickerListViewModelTests.CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var targetSettings = new FilterSettings { Name = "Target", Rules = { new FilterRule { Field = "Tag", Value = "Growth" } } };
            var targetNode = new FilterNode(targetSettings);
            var mockTemplateService = new Mock<ITemplateService>();
            mockTemplateService.Setup(s => s.GetAllAsync<FilterTemplate>(TemplateType.Filter))
                .ReturnsAsync(new List<FilterTemplate>());
            mockTemplateService.Setup(s => s.ValidateAsync(It.IsAny<FilterTemplate>()))
                .ReturnsAsync(TemplateValidationResult.Success());
            FilterTemplate? savedTemplate = null;
            mockTemplateService.Setup(s => s.SaveAsync(It.IsAny<FilterTemplate>()))
                .Callback<FilterTemplate>(t => savedTemplate = t)
                .Returns(Task.CompletedTask);

            var vm = CreateViewModel(owner, targetNode, mockTemplateService);
            vm.NewTemplateName = "My Filter Template";

            await vm.SaveTemplateCommand.ExecuteAsync(null);

            Assert.NotNull(savedTemplate);
            Assert.Equal("My Filter Template", savedTemplate!.Name);
            Assert.Single(savedTemplate.RootSettings.Rules);
            Assert.Equal("Growth", savedTemplate.RootSettings.Rules[0].Value);
            Assert.Single(vm.Templates);
            vm.Dispose();
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public async Task LoadTemplateAsync_ReplacesTargetNodeContent_ViaOwner()
    {
        var owner = TickerListViewModelTests.CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var targetSettings = new FilterSettings { Name = "Target", Rules = { new FilterRule { Field = "Tag", Value = "Old" } } };
            var targetNode = new FilterNode(targetSettings);
            var template = new FilterTemplate
            {
                Name = "Replacement",
                RootSettings = new FilterSettings { Rules = { new FilterRule { Field = "Tag", Value = "New" } } }
            };
            var mockTemplateService = new Mock<ITemplateService>();
            mockTemplateService.Setup(s => s.GetAllAsync<FilterTemplate>(TemplateType.Filter))
                .ReturnsAsync(new List<FilterTemplate> { template });
            mockTemplateService.Setup(s => s.ValidateAsync(template))
                .ReturnsAsync(TemplateValidationResult.Success());

            var vm = CreateViewModel(owner, targetNode, mockTemplateService);

            await vm.LoadTemplateCommand.ExecuteAsync(template);

            Assert.Single(targetNode.Settings.Rules);
            Assert.Equal("New", targetNode.Settings.Rules[0].Value);
            vm.Dispose();
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public async Task AppendTemplateAsync_AddsNewChild_ViaOwner()
    {
        var owner = TickerListViewModelTests.CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var targetNode = new FilterNode(new FilterSettings { Name = "Target" });
            var template = new FilterTemplate
            {
                Name = "Appended",
                RootSettings = new FilterSettings { Name = "AppendedRoot" }
            };
            var mockTemplateService = new Mock<ITemplateService>();
            mockTemplateService.Setup(s => s.GetAllAsync<FilterTemplate>(TemplateType.Filter))
                .ReturnsAsync(new List<FilterTemplate> { template });
            mockTemplateService.Setup(s => s.ValidateAsync(template))
                .ReturnsAsync(TemplateValidationResult.Success());

            var vm = CreateViewModel(owner, targetNode, mockTemplateService);

            await vm.AppendTemplateCommand.ExecuteAsync(template);

            Assert.Single(targetNode.Children!);
            Assert.Equal("AppendedRoot", ((FilterNode)targetNode.Children![0]).DisplayName);
            vm.Dispose();
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public async Task DeleteTemplateAsync_RemovesFromTemplatesList_AndCallsTemplateService()
    {
        var owner = TickerListViewModelTests.CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var targetNode = new FilterNode(new FilterSettings { Name = "Target" });
            var template = new FilterTemplate { Name = "ToDelete" };
            var mockTemplateService = new Mock<ITemplateService>();
            mockTemplateService.Setup(s => s.GetAllAsync<FilterTemplate>(TemplateType.Filter))
                .ReturnsAsync(new List<FilterTemplate> { template });
            mockTemplateService.Setup(s => s.DeleteAsync(TemplateType.Filter, template.Id))
                .ReturnsAsync(true);

            var vm = CreateViewModel(owner, targetNode, mockTemplateService);

            await vm.DeleteTemplateCommand.ExecuteAsync(template);

            Assert.Empty(vm.Templates);
            mockTemplateService.Verify(s => s.DeleteAsync(TemplateType.Filter, template.Id), Times.Once);
            vm.Dispose();
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public void Constructor_WithNonFilterNode_EntersCreateMode()
    {
        var owner = TickerListViewModelTests.CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var parentNode = new AllTickersNode("All Tickers");
            var mockTemplateService = new Mock<ITemplateService>();
            mockTemplateService.Setup(s => s.GetAllAsync<FilterTemplate>(TemplateType.Filter))
                .ReturnsAsync(new List<FilterTemplate>());
            var toastService = new Mock<IToastNotificationService>();

            var vm = new FilterTemplatePickerDialogViewModel(owner, parentNode, mockTemplateService.Object, toastService.Object);

            Assert.False(vm.IsExistingNodeMode);
            Assert.Equal("All Tickers", vm.TargetNodeName);
            vm.Dispose();
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public async Task LoadTemplateAsync_InCreateMode_CreatesNewFilterNodeUnderParent()
    {
        var owner = TickerListViewModelTests.CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var parentNode = new AllTickersNode("All Tickers");
            var template = new FilterTemplate
            {
                Name = "FromTemplate",
                RootSettings = new FilterSettings { Name = "TemplateRoot", Rules = { new FilterRule { Field = "Tag", Value = "Growth" } } }
            };
            var mockTemplateService = new Mock<ITemplateService>();
            mockTemplateService.Setup(s => s.GetAllAsync<FilterTemplate>(TemplateType.Filter))
                .ReturnsAsync(new List<FilterTemplate> { template });
            mockTemplateService.Setup(s => s.ValidateAsync(template))
                .ReturnsAsync(TemplateValidationResult.Success());
            var toastService = new Mock<IToastNotificationService>();

            var vm = new FilterTemplatePickerDialogViewModel(owner, parentNode, mockTemplateService.Object, toastService.Object);

            await vm.LoadTemplateCommand.ExecuteAsync(template);

            Assert.Single(parentNode.Children!);
            var newNode = Assert.IsType<FilterNode>(parentNode.Children![0]);
            Assert.Equal("TemplateRoot", newNode.DisplayName);
            vm.Dispose();
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public async Task SaveTemplateAsync_InCreateMode_DoesNothing()
    {
        var owner = TickerListViewModelTests.CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var parentNode = new AllTickersNode("All Tickers");
            var mockTemplateService = new Mock<ITemplateService>();
            mockTemplateService.Setup(s => s.GetAllAsync<FilterTemplate>(TemplateType.Filter))
                .ReturnsAsync(new List<FilterTemplate>());
            var toastService = new Mock<IToastNotificationService>();

            var vm = new FilterTemplatePickerDialogViewModel(owner, parentNode, mockTemplateService.Object, toastService.Object);
            vm.NewTemplateName = "Should Not Save";

            await vm.SaveTemplateCommand.ExecuteAsync(null);

            mockTemplateService.Verify(s => s.SaveAsync(It.IsAny<FilterTemplate>()), Times.Never);
            Assert.Empty(vm.Templates);
            vm.Dispose();
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public async Task AppendTemplateAsync_InCreateMode_DoesNothing()
    {
        var owner = TickerListViewModelTests.CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var parentNode = new AllTickersNode("All Tickers");
            var template = new FilterTemplate { Name = "Ignored", RootSettings = new FilterSettings() };
            var mockTemplateService = new Mock<ITemplateService>();
            mockTemplateService.Setup(s => s.GetAllAsync<FilterTemplate>(TemplateType.Filter))
                .ReturnsAsync(new List<FilterTemplate> { template });
            var toastService = new Mock<IToastNotificationService>();

            var vm = new FilterTemplatePickerDialogViewModel(owner, parentNode, mockTemplateService.Object, toastService.Object);

            await vm.AppendTemplateCommand.ExecuteAsync(template);

            Assert.Empty(parentNode.Children!);
            vm.Dispose();
        }
        finally
        {
            owner.Dispose();
        }
    }
}
