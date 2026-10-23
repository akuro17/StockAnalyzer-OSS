namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Single definition of the embedded CPython distribution that the app installs on Windows.
    /// Every version, folder name, URL and hash of the interpreter is taken from this class.
    /// </summary>
    public static class PythonEmbeddedDistribution
    {
        /// <summary>Pinned CPython version (security minimum 3.13.17).</summary>
        public const string PythonVersion = "3.13.17";

        /// <summary>Exact folder name under the install root (only this folder is ever used).</summary>
        public const string FolderName = "python-3.13.17-embed-amd64";

        /// <summary>Official python.org embeddable package.</summary>
        public const string ArchiveUrl = "https://www.python.org/ftp/python/3.13.17/python-3.13.17-embed-amd64.zip";

        /// <summary>SHA-256 of <see cref="ArchiveUrl"/> as published by python.org (release_file API, sha256_sum).</summary>
        public const string ArchiveSha256 = "9e35319bffedab8b36ad44d8f377cb4f1733bcec257a0b6d04d94f01486ddb38";

        /// <summary>Unversioned pip bootstrap script. Not hash-checked (owner decision).</summary>
        public const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";

        /// <summary>Interpreter DLL shipped in the archive.</summary>
        public const string PythonDllName = "python313.dll";

        /// <summary>Interpreter executable shipped in the archive (Windows).</summary>
        public const string PythonExecutableName = "python.exe";

        /// <summary>Path file that makes the interpreter ignore the registry PythonPath and PYTHONPATH.</summary>
        public const string PthFileName = "python313._pth";

        /// <summary>Content of <see cref="PthFileName"/>: standard library zip, the folder itself, and site-packages.</summary>
        public const string PthContent = "python313.zip\n.\n" + SitePackagesRelativePath + "\nimport site\n";

        /// <summary>
        /// Site file that puts the script's folder on <c>sys.path</c>, as Python does by default. The path
        /// file <see cref="PthFileName"/> disables that, so the sub-scripts (for example <c>update_daily.py</c>)
        /// could not import their sibling modules.
        /// </summary>
        public const string ScriptPathFileName = "sa_script_path.pth";

        /// <summary>Python tuple items (lower case) of the script extensions that <see cref="ScriptPathContent"/> accepts.</summary>
        public const string ScriptPathExtensions = "'.py', '.pyw'";

        /// <summary>
        /// Content of <see cref="ScriptPathFileName"/>. Executed by <c>site</c>; <c>sys.argv[0]</c> is the script. Only a Python script
        /// (<see cref="ScriptPathExtensions"/>, case-insensitive) puts its folder on <c>sys.path</c>, so an executable run through the interpreter does not.
        /// </summary>
        public const string ScriptPathContent = "import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(sys.argv[0]))) if len(sys.argv) > 0 and os.path.isfile(sys.argv[0]) and os.path.splitext(sys.argv[0])[1].lower() in (" + ScriptPathExtensions + ") else None\n";

        /// <summary>Marker written last, only after the whole install succeeded.</summary>
        public const string StampFileName = ".sa-python-version";

        /// <summary>Suffix of the staging folder and archive while an install runs.</summary>
        public const string PartialSuffix = ".partial";

        /// <summary>Extension of the downloaded archive.</summary>
        public const string ArchiveExtension = ".zip";

        /// <summary>Folder that holds the pip bootstrap packages inside the interpreter.</summary>
        public const string SitePackagesRelativePath = "Lib\\site-packages";
    }
}
