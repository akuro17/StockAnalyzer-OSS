#nullable enable
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Regression pins written before the short-history change: the digests were captured from the unchanged code
/// and prove that the standard, smoke and all-block (R06) catalogs and both numerical contract hashes are byte-identical afterwards.
/// </summary>
public sealed class RlShortHistoryCharacterizationTests
{
    private const string StandardContractHash = "02434908AD7140E49A80E9FAF0002753A4FC0B25084B68EE29D3E30ABDDA4987";
    private const string ContinuousContractHash = "EFD84A0512E779198A303CA9809865A399C12680B3846C667EEF1D8C6D91BAFA";

    private static ImmutableArray<CandleData> Bars(int count) => Enumerable.Range(0, count)
        .Select(i => new CandleData(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i), 100m, 101m, 99m, 100m, 1000L)).ToImmutableArray();

    private static string Digest(int count, bool smoke, string? specification)
        => ContentHash.OfText(JsonSerializer.Serialize(RlSourceSnapshot.BuildCatalog(Bars(count), ImmutableArray<RlCorporateAction>.Empty,
            ImmutableArray<DateOnly>.Empty, smoke, specification), RlFiles.Json));

    [Fact]
    public void NumericalContractHashesAreUnchanged()
    {
        Assert.Equal(StandardContractHash, RlContract.ContractHash);
        Assert.Equal(ContinuousContractHash, RlContinuousContract.ContractHash);
    }

    [Theory]
    [InlineData(5118, false, false, "dd298065dd6039faa5f5e054f182db46b9e686ac0faec3adb520d7805e1afd0f")]
    [InlineData(6218, false, false, "bb4089dce2da3731e402ace50fd5022af2a3fc1a2c448d9fe0de366cfc9dfb28")]
    [InlineData(9246, false, false, "6d367e3fc844d804598023db5ef4100a8bc88c5e36d2c0f48764fc6059c2a6e4")]
    [InlineData(1278, false, true, "7c4c29cda2f9d384425a7aa7d365709e160eb20e64dc4ee5dde0a9f491d10820")]
    [InlineData(6218, false, true, "e047666ef67b22829826adac8b5c1015c775b7662db4e3f201606f6938afa821")]
    [InlineData(1278, true, false, "8af6a99cb3a03ca3f6ddc1ac7a0b2d5bbff4a01593c7d378949e266912a75b12")]
    [InlineData(6218, true, false, "ec9c966f0f031252f5caf48a45e1800c554fb7412ea78c4f91bc58781ede10a4")]
    [InlineData(9246, true, false, "7a2458b7293b5d9f8035d7baa5efe5303a1322a4a39c83520680a5627a707a80")]
    [InlineData(1278, true, true, "d2f8d046fe090b663369a8e0aef4924b12f5813fb8e8c0e4290c48146d0c3793")]
    [InlineData(6218, true, true, "2f1f5e44879f9a32fcee22fe1428175bfe30d91202d9f8d48ecaed5259cd7765")]
    public void StandardSmokeAndAllBlockCatalogsAreByteIdentical(int count, bool allBlocks, bool smoke, string expected)
        => Assert.Equal(expected, Digest(count, smoke, allBlocks ? RlAllBlockEvaluationContract.SpecificationId : null));
}
