namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Package lists shared by the automatic install paths and the manual pip commands. One definition each.
    /// </summary>
    public static class PythonPackageSets
    {
        /// <summary>The interpreter arguments every pip install command line starts with (automatic and manual, including the pip and certifi security upgrade).</summary>
        internal const string PipInstallCommand = "-m pip install";

        /// <summary>
        /// Start of the install command lines of the packages (automatic and manual). Build isolation is bypassed because the embedded environment often
        /// fails to create a clean isolated environment with all necessary build tools (like setuptools). The pip and certifi security upgrade uses
        /// <see cref="PipInstallCommand"/> only: both are wheels, so there is nothing to build.
        /// </summary>
        internal const string PipInstallPrefix = PipInstallCommand + " --no-build-isolation";

        /// <summary>CPU-only PyTorch index (torch is installed from here on Windows).</summary>
        public const string TorchIndexUrl = "https://download.pytorch.org/whl/cpu";

        /// <summary>Package that needs <see cref="TorchIndexUrl"/>.</summary>
        public const string TorchPackage = "torch";

        /// <summary>Packages of the optional ONNX training tools (ONNX Environment &amp; Dependencies).</summary>
        public static readonly string[] OnnxTraining =
        {
            "numpy",
            "torch",
            "onnx",
            "onnxruntime",
            "onnxscript",
            "tensorflow",
            "tf2onnx",
            "lightgbm",
            "scikit-learn",
            "skl2onnx",
            "onnxmltools"
        };

        /// <summary>Essential packages that the manual update upgrades to the latest version.</summary>
        public static readonly string[] Update =
        {
            "yfinance",
            "pandas",
            "polars",
            "pyarrow"
        };
    }
}
