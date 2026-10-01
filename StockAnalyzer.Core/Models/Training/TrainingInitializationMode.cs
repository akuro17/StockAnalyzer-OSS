namespace StockAnalyzer.Core.Models.Training;

/// <summary>Fresh run, weights-only transfer into a new run, or exact recovery of the same run.</summary>
public enum TrainingInitializationMode
{
    Fresh,
    FineTune,
    Resume,
}
