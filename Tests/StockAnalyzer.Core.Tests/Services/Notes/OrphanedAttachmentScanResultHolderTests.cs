using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class OrphanedAttachmentScanResultHolderTests
{
    [Fact]
    public void LatestReport_BeforeAnyScan_IsNull()
    {
        var holder = new OrphanedAttachmentScanResultHolder();

        Assert.Null(holder.LatestReport);
    }

    [Fact]
    public void SetLatestReport_ThenLatestReport_ReturnsTheSameReport()
    {
        var holder = new OrphanedAttachmentScanResultHolder();
        var report = new OrphanedAttachmentReport(new[] { "orphan1.png", "orphan2.jpg" });

        holder.SetLatestReport(report);

        Assert.Same(report, holder.LatestReport);
    }

    [Fact]
    public void SetLatestReport_CalledTwice_LatestReportReflectsTheMostRecentCall()
    {
        var holder = new OrphanedAttachmentScanResultHolder();
        holder.SetLatestReport(new OrphanedAttachmentReport(new[] { "old.png" }));

        var newer = new OrphanedAttachmentReport(new[] { "new.png" });
        holder.SetLatestReport(newer);

        Assert.Same(newer, holder.LatestReport);
    }
}
