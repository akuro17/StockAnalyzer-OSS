using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.Models;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_evaluate_expressions: evaluates a sequential batch of deterministic <see cref="decimal"/> arithmetic expressions
/// with named variables via <see cref="ExpressionCalculator"/>.
/// </summary>
internal sealed class EvaluateExpressionsTool : IMcpTool
{
    private const int CalculatorVersion = 3;
    private const string ArithmeticName = "System.Decimal";
    private const string VariablesArgument = "variables";
    private const string ExpressionsArgument = "expressions";
    private const string NameProperty = "name";
    private const string ExpressionProperty = "expression";
    private const decimal DecimalScaleStripDivisor = 1.0000000000000000000000000000m;
    private const string DependencyFailedCode = "DEPENDENCY_FAILED";

    private static readonly string MaxTranscendentalMagnitudeText =
        IndicatorDefaultConstants.MaxDoubleToDecimalMagnitude.ToString("0.##e0", CultureInfo.InvariantCulture);

    private static readonly string TranscendentalArithmeticName =
        $"IEEE-754 binary64 narrowed via ToDecimalOrNull (|x| < {MaxTranscendentalMagnitudeText})";

    private static readonly string IdentifierRegexHint =
        $"[A-Za-z_][A-Za-z0-9_]{{0,{ExpressionCalculator.MaxIdentifierLength - 1}}}";

    private static readonly IReadOnlySet<string> ReservedFunctionNames =
        ExpressionCalculator.Functions.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> Hints = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["expression"] = "Send an 'expressions' array of {name, expression} items.",
        ["formula"] = "Send an 'expressions' array of {name, expression} items.",
    };

    private readonly McpLimits _limits;

    public EvaluateExpressionsTool(McpLimits limits)
    {
        _limits = limits;
    }

    public string Name => "sa_evaluate_expressions";

    public string Description
    {
        get
        {
            CalculatorLimits coreLimits = CalculatorLimits.Default;
            return
                "Evaluates a sequential batch of named deterministic System.Decimal expressions (and binary64 math functions narrowed via ToDecimalOrNull) with optional variables. " +
                $"Limits: {_limits.MaxCalculatorExpressions} expressions, {_limits.MaxCalculatorVariables} variables, {coreLimits.MaxExpressionChars} chars/expression. " +
                ToolJson.DocLink(Name);
        }
    }

    public string InputSchemaJson => ToolSchema.Object()
        .FreeObject(
            VariablesArgument,
            $"Optional map of variable names to decimal numbers (at most {_limits.MaxCalculatorVariables} entries; names match {IdentifierRegexHint} and cannot equal a function name).",
            required: false)
        .ObjectArray(
            ExpressionsArgument,
            $"Ordered list of expressions to evaluate (1..{_limits.MaxCalculatorExpressions} items). Later expressions may reference earlier ones.",
            ToolSchema.Object()
                .String(NameProperty, $"Unique identifier for this result ({IdentifierRegexHint}, not a reserved function name).", required: true)
                .String(ExpressionProperty, $"Arithmetic expression (1..{CalculatorLimits.Default.MaxExpressionChars} characters).", required: true),
            required: true,
            minItems: 1)
        .Integer(
            SeriesOutputOptions.DecimalPlacesArgument,
            $"Optional rounding applied to expression results on output (0..{SeriesOutputOptions.MaxDecimalPlaces}, MidpointRounding.AwayFromZero). Variables are not rounded.",
            required: false,
            minimum: 0,
            maximum: SeriesOutputOptions.MaxDecimalPlaces)
        .Build();

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"variables\":{\"close\":2950,\"atr\":48.5},\"expressions\":[{\"name\":\"stop\",\"expression\":\"close - 2 * atr\"}]}";

    public IReadOnlyDictionary<string, string>? MisnamedArgumentHints => Hints;

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(ExpressionsArgument, out JsonElement expressionsElement)
            || expressionsElement.ValueKind != JsonValueKind.Array
            || expressionsElement.GetArrayLength() == 0)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Argument '{ExpressionsArgument}' must be a non-empty array.",
                ExpressionsArgument);
        }

        int? decimalPlaces = SeriesOutputOptions.ReadDecimalPlaces(arguments);

        int expressionCount = expressionsElement.GetArrayLength();
        if (expressionCount > _limits.MaxCalculatorExpressions)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {_limits.MaxCalculatorExpressions} expressions per call.",
                ExpressionsArgument,
                $"Send {_limits.MaxCalculatorExpressions} or fewer, or split the call.");
        }

        bool hasVariables = arguments.TryGetProperty(VariablesArgument, out JsonElement variablesElement);
        int variableCount = 0;
        if (hasVariables)
        {
            if (variablesElement.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Argument '{VariablesArgument}' must be an object.",
                    VariablesArgument);
            }

            foreach (JsonProperty _ in variablesElement.EnumerateObject())
            {
                variableCount++;
            }

            if (variableCount > _limits.MaxCalculatorVariables)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"At most {_limits.MaxCalculatorVariables} variables per call.",
                    VariablesArgument,
                    $"Send {_limits.MaxCalculatorVariables} or fewer, or split the call.");
            }
        }

        var declaredNames = new HashSet<string>(variableCount + expressionCount, StringComparer.Ordinal);
        if (hasVariables)
        {
            foreach (JsonProperty prop in variablesElement.EnumerateObject())
            {
                string varName = prop.Name;
                string varPath = varName.Length == 0 ? VariablesArgument : $"{VariablesArgument}.{varName}";
                ValidateDeclaredName(varName, varPath, declaredNames);
            }
        }

        var itemNames = new string[expressionCount];
        int index = 0;
        foreach (JsonElement item in expressionsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Element at index {index} in '{ExpressionsArgument}' must be an object.",
                    $"{ExpressionsArgument}[{index}]");
            }

            string namePath = $"{ExpressionsArgument}[{index}].{NameProperty}";
            if (!item.TryGetProperty(NameProperty, out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Argument '{namePath}' must be a string.",
                    namePath);
            }

            string exprName = nameElement.GetString()!;
            ValidateDeclaredName(exprName, namePath, declaredNames);
            itemNames[index] = exprName;
            index++;
        }

        var variables = new List<(string Name, decimal Value)>(variableCount);
        if (hasVariables)
        {
            foreach (JsonProperty prop in variablesElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDecimal(out decimal number))
                {
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        $"Variable '{prop.Name}' must be a finite decimal number.",
                        $"{VariablesArgument}.{prop.Name}",
                        "Provide a finite JSON number that fits System.Decimal.");
                }

                variables.Add((prop.Name, number));
            }
        }

        var expressions = new List<(string Name, string Text)>(expressionCount);
        int maxExprChars = CalculatorLimits.Default.MaxExpressionChars;
        index = 0;
        foreach (JsonElement item in expressionsElement.EnumerateArray())
        {
            string exprPath = $"{ExpressionsArgument}[{index}].{ExpressionProperty}";
            if (!item.TryGetProperty(ExpressionProperty, out JsonElement exprElement) || exprElement.ValueKind != JsonValueKind.String)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Argument '{exprPath}' must be a string.",
                    exprPath);
            }

            string exprText = exprElement.GetString()!;
            if (exprText.Length == 0)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Expression '{itemNames[index]}' must not be empty.",
                    exprPath);
            }

            if (exprText.Length > maxExprChars)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"Expression '{itemNames[index]}' exceeds {maxExprChars} characters.",
                    exprPath,
                    $"Keep each expression within {maxExprChars} characters.");
            }

            expressions.Add((itemNames[index], exprText));
            index++;
        }

        return Task.FromResult(EvaluateAndSerialize(variables, expressions, decimalPlaces, cancellationToken));
    }

    internal static decimal NormalizeDecimal(decimal value)
        => value == 0m ? 0m : value / DecimalScaleStripDivisor;

    private static string EvaluateAndSerialize(
        IReadOnlyList<(string Name, decimal Value)> variables,
        IReadOnlyList<(string Name, string Text)> expressions,
        int? decimalPlaces,
        CancellationToken cancellationToken)
    {
        var scope = new Dictionary<string, decimal>(variables.Count + expressions.Count, StringComparer.Ordinal);
        foreach ((string name, decimal value) in variables)
        {
            scope[name] = value;
        }

        var expressionIndexByName = new Dictionary<string, int>(expressions.Count, StringComparer.Ordinal);
        for (int i = 0; i < expressions.Count; i++)
        {
            expressionIndexByName[expressions[i].Name] = i;
        }

        var failedExpressions = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<ExpressionEvalResult>(expressions.Count);
        int okCount = 0;
        int errorCount = 0;

        for (int i = 0; i < expressions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string name, string text) = expressions[i];

            CalculatorOutcome outcome = ExpressionCalculator.Evaluate(text, scope, CalculatorLimits.Default);
            if (outcome.Value is { } rawValue)
            {
                scope[name] = rawValue;
                decimal outputValue = decimalPlaces is { } places
                    ? SeriesOutputOptions.Round(rawValue, places)
                    : rawValue;
                outputValue = NormalizeDecimal(outputValue);
                results.Add(ExpressionEvalResult.Ok(name, text, outputValue));
                okCount++;
            }
            else
            {
                CalculatorError error = outcome.Error!;
                failedExpressions.Add(name);
                (string wireCode, string message) = RefineError(name, i, error, expressionIndexByName, failedExpressions);
                results.Add(ExpressionEvalResult.Fail(name, text, wireCode, error.Position, message));
                errorCount++;
            }
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("calculatorVersion", CalculatorVersion);
            writer.WriteString("arithmetic", ArithmeticName);
            writer.WriteString("transcendentalArithmetic", TranscendentalArithmeticName);
            if (decimalPlaces is { } places)
            {
                writer.WriteNumber(SeriesOutputOptions.DecimalPlacesArgument, places);
            }

            writer.WriteStartArray(VariablesArgument);
            foreach ((string name, decimal value) in variables)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteNumber("value", NormalizeDecimal(value));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("results");
            foreach (ExpressionEvalResult item in results)
            {
                writer.WriteStartObject();
                writer.WriteString("name", item.Name);
                writer.WriteString("expression", item.Expression);
                writer.WriteString("status", item.IsOk ? "Ok" : "Error");
                if (item.IsOk)
                {
                    writer.WriteNumber("value", item.Value);
                }
                else
                {
                    writer.WriteString("errorCode", item.ErrorCode);
                    writer.WriteNumber("errorPosition", item.ErrorPosition);
                    writer.WriteString("errorMessage", item.ErrorMessage);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("okCount", okCount);
            writer.WriteNumber("errorCount", errorCount);
            writer.WriteEndObject();
        });
    }

    private static (string WireCode, string Message) RefineError(
        string currentName,
        int currentIndex,
        CalculatorError error,
        IReadOnlyDictionary<string, int> expressionIndexByName,
        IReadOnlySet<string> failedExpressions)
    {
        string wireCode = error.Code.ToWireName();
        string message = error.Message;

        if (error.Code == CalculatorErrorCode.UndefinedName
            && error.Token is not null
            && expressionIndexByName.TryGetValue(error.Token, out int refIndex))
        {
            if (refIndex < currentIndex && failedExpressions.Contains(error.Token))
            {
                return (DependencyFailedCode, $"'{error.Token}' failed, so '{currentName}' cannot be calculated.");
            }

            if (refIndex > currentIndex)
            {
                return (wireCode, $"'{error.Token}' is declared after '{currentName}'; an expression can only use earlier expressions.");
            }
        }

        return (wireCode, message);
    }

    private static void ValidateDeclaredName(string name, string paramPath, HashSet<string> declaredNames)
    {
        if (!ExpressionCalculator.IsValidIdentifier(name))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Identifier '{name}' is invalid.",
                paramPath,
                $"Identifiers must match {IdentifierRegexHint}.");
        }

        if (ReservedFunctionNames.Contains(name))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Identifier '{name}' is a reserved function name.",
                paramPath,
                "Function names are reserved.");
        }

        if (name is "nan" or "inf")
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Identifier '{name}' is reserved for non-finite rejection.",
                paramPath,
                "Non-finite identifiers 'nan' and 'inf' cannot be declared.");
        }

        if (!declaredNames.Add(name))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Duplicate identifier '{name}'.",
                paramPath,
                "Each variable and expression must have a unique name.");
        }
    }

    private readonly record struct ExpressionEvalResult(
        string Name,
        string Expression,
        bool IsOk,
        decimal Value,
        string? ErrorCode,
        int ErrorPosition,
        string? ErrorMessage)
    {
        public static ExpressionEvalResult Ok(string name, string expression, decimal value)
            => new(name, expression, true, value, null, 0, null);

        public static ExpressionEvalResult Fail(string name, string expression, string errorCode, int errorPosition, string errorMessage)
            => new(name, expression, false, 0m, errorCode, errorPosition, errorMessage);
    }
}
