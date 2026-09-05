namespace StockAnalyzer.Avalonia.Common;

public class InfrastructureSettings
{
    public string PipeName { get; set; } = "StockAnalyzerPipe";
    public int PipeConnectionTimeoutMs { get; set; } = 5000;
    public int ScreenerMaxParallelism { get; set; } = 10;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PipeName)) throw new System.InvalidOperationException("InfrastructureSettings: PipeName cannot be empty.");
        if (PipeConnectionTimeoutMs <= 0) throw new System.InvalidOperationException("InfrastructureSettings: PipeConnectionTimeoutMs must be positive.");
        if (ScreenerMaxParallelism <= 0) throw new System.InvalidOperationException("InfrastructureSettings: ScreenerMaxParallelism must be positive.");
    }
}
