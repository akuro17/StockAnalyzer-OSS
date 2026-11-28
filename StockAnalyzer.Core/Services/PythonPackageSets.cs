namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Package lists shared by the automatic install paths and the manual pip commands. One definition each.
    /// </summary>
    public static class PythonPackageSets
    {
        /// <summary>The interpreter arguments every pip install command line starts with (automatic and manual, including the pip and certifi security upgrade).</summary>
        internal const string PipInstallCommand = "-m pip install";

        /// <summary>Tokenized <see cref="PipInstallCommand"/> for <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.</summary>
        internal static readonly string[] PipInstallCommandTokens = { "-m", "pip", "install" };

        /// <summary>
        /// Start of the install command lines of the packages (automatic and manual). Enforces binary wheels (<c>--only-binary :all:</c>)
        /// so arbitrary source-package <c>setup.py</c> execution cannot occur during installation or upgrade.
        /// </summary>
        internal const string PipInstallPrefix = PipInstallCommand + " --only-binary :all:";

        /// <summary>Tokenized <see cref="PipInstallPrefix"/> for <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.</summary>
        internal static readonly string[] PipInstallPrefixTokens = { "-m", "pip", "install", "--only-binary", ":all:" };

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
