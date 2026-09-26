namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Python trainer a job dispatches to. <c>run_training.py</c> maps each value to an existing
/// trainer script: <c>train_pytorch.py</c>, <c>train_lightgbm.py</c>, <c>train_tensorflow.py</c>.
/// </summary>
/// <remarks>
/// Wire strings (see <c>TrainingConfigJson</c>): <c>pytorch</c> / <c>lightgbm</c> / <c>tensorflow</c>.
/// </remarks>
public enum TrainingFramework
{
    PyTorch = 0,
    LightGBM = 1,
    TensorFlow = 2,
}
