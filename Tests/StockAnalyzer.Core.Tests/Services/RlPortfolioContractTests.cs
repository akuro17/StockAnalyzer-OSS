using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>The portfolio contract hash is pinned: a changed constant must be a deliberate, visible decision that invalidates models.</summary>
public sealed class RlPortfolioContractTests
{
    private const string PinnedContractHash = "A9F7C8AC29790AF92714161DF24A39CC49C6BB470C0AD1EBFB38E617F066F00F";

    [Fact]
    public void ContractHashIsPinned() => Assert.Equal(PinnedContractHash, RlPortfolioContract.ContractHash);

    [Fact]
    public void ContractHashCoversTheNumericalContractAndEveryConstant()
    {
        var wire = RlPortfolioContract.ToWireDefaults();
        Assert.Equal(RlContract.ContractHash, wire["NumericalContractHash"]);
        Assert.Equal(RlPortfolioContract.SpecificationId, wire[nameof(RlPortfolioContract.SpecificationId)]);
        Assert.Equal(RlPortfolioContract.WeightDecimals, wire[nameof(RlPortfolioContract.WeightDecimals)]);
        Assert.Equal(RlPortfolioContract.ScalerChannelNames.ToArray(), (string[])wire[nameof(RlPortfolioContract.ScalerChannelNames)]);
        Assert.NotEqual(RlContract.ContractHash, RlPortfolioContract.ContractHash);
        Assert.Equal(64, RlPortfolioContract.ContractHash.Length);
    }

    [Fact]
    public void TheNumericalContractsAreUnchanged()
    {
        Assert.Equal("02434908AD7140E49A80E9FAF0002753A4FC0B25084B68EE29D3E30ABDDA4987", RlContract.ContractHash);
        Assert.Equal("EFD84A0512E779198A303CA9809865A399C12680B3846C667EEF1D8C6D91BAFA", RlContinuousContract.ContractHash);
    }
}
