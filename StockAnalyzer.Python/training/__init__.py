"""ONNX training tooling for StockAnalyzer.

Generates and validates ``trend_predictor.onnx`` models that are byte-compatible
with the C# inference engine (``StockAnalyzer.Core.Services.PredictionService`` /
``MLDataProcessor``).

Modules
-------
dataset
    Parquet loading, C#-equivalent feature preprocessing (OhlcvMinMax /
    LogReturn / ZScoreStandardized), 3-class labeling and walk-forward splitting.
"""
