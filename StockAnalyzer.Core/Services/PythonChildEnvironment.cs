namespace StockAnalyzer.Core.Services;

/// <summary>The environment variables every Python child process of the application is started with (names and values in one place).</summary>
internal static class PythonChildEnvironment
{
    /// <summary>Makes the child write its standard streams as UTF-8, which is what the readers of the application decode.</summary>
    public const string IoEncodingVariable = "PYTHONIOENCODING";

    public const string IoEncodingValue = "utf-8";

    /// <summary>Stops the interpreter from writing <c>__pycache__</c> folders next to the scripts it imports.</summary>
    public const string NoBytecodeVariable = "PYTHONDONTWRITEBYTECODE";

    public const string NoBytecodeValue = "1";
}
