using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.ReadOnly;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

public class TemplateFailureRuleTests
{
    [Fact]
    public void FailuresOfOneTemplate_AreIsolated()
    {
        Assert.True(TemplateFailureRule.IsIsolatable(new InvalidOperationException()));
        Assert.True(TemplateFailureRule.IsIsolatable(new IOException()));
        Assert.True(TemplateFailureRule.IsIsolatable(new System.Text.Json.JsonException()));
        Assert.True(TemplateFailureRule.IsIsolatable(new ArgumentNullException("left")));
    }

    [Fact]
    public void Cancellation_IsNeverAbsorbed()
    {
        Assert.False(TemplateFailureRule.IsIsolatable(new OperationCanceledException()));
        Assert.False(TemplateFailureRule.IsIsolatable(new TaskCanceledException()));
    }

    [Fact]
    public void OutOfMemory_IsNeverAbsorbed()
    {
        Assert.False(TemplateFailureRule.IsIsolatable(new OutOfMemoryException()));
    }
}
