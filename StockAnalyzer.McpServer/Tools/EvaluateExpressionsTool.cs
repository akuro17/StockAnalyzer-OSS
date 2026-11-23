using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_evaluate_expressions: evaluates a sequential batch of deterministic <see cref="decimal"/> arithmetic expressions
/// with named scalar and 1D array variables via <see cref="ExpressionCalculator"/>.
/// </summary>
internal sealed class EvaluateExpressionsTool : IMcpTool
{
    private const int CalculatorVersion = 8;
    private const string ArithmeticName = "System.Decimal";
    private const string VariablesArgument = "variables";
    private const string ArrayVariablesArgument = "arrayVariables";
    private const string ExpressionsArgument = "expressions";
    private const string AllowFunctionNameVariablesArgument = "allowFunctionNameVariables";
    private const string NameProperty = "name";
    private const string ExpressionProperty = "expression";
    private const decimal DecimalScaleStripDivisor = 1.0000000000000000000000000000m;
    private const int MaxInputArrayElements = 2_048;
    private const string DependencyFailedCode = "DEPENDENCY_FAILED";

    private static readonly string MaxTranscendentalMagnitudeText =
        IndicatorDefaultConstants.MaxDoubleToDecimalMagnitude.ToString("0.##e0", CultureInfo.InvariantCulture);

    private static readonly string TranscendentalArithmeticName =
        $"IEEE-754 binary64 narrowed via ToDecimalOrNull (|x| < {MaxTranscendentalMagnitudeText})";

    private static readonly string IdentifierRegexHint =
        $"[A-Za-z_][A-Za-z0-9_]{{0,{ExpressionCalculator.MaxIdentifierLength - 1}}}";

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
            $"Optional map of scalar variable names to decimal numbers (combined scalar + array variables at most {_limits.MaxCalculatorVariables} entries; names match {IdentifierRegexHint} and cannot equal a function name).",
            required: false)
        .FreeObject(
            ArrayVariablesArgument,
            $"Optional map of 1D array variable names to finite decimal arrays (empty [] allowed; at most {DslArrayArena.MaxArrayLength} items per array, {MaxInputArrayElements} total input elements across arrayVariables, and {DslArrayArena.MaxArenaElements} total elements across batch; combined scalar + array variables at most {_limits.MaxCalculatorVariables} entries).",
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
        .Boolean(
            AllowFunctionNameVariablesArgument,
            "Optional boolean (default false). When true, declared variable and expression names may match built-in function names while 'nan', 'inf', and duplicate names remain rejected.",
            required: false)
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
        bool allowFunctionNameVariables = ToolArgs.Boolean(arguments, AllowFunctionNameVariablesArgument) ?? false;

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

        bool hasArrayVariables = arguments.TryGetProperty(ArrayVariablesArgument, out JsonElement arrayVariablesElement);
        int arrayVariableCount = 0;
        if (hasArrayVariables)
        {
            if (arrayVariablesElement.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Argument '{ArrayVariablesArgument}' must be an object.",
                    ArrayVariablesArgument);
            }

            foreach (JsonProperty _ in arrayVariablesElement.EnumerateObject())
            {
                arrayVariableCount++;
            }

            if (variableCount + arrayVariableCount > _limits.MaxCalculatorVariables)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"At most {_limits.MaxCalculatorVariables} combined scalar and array variables per call.",
                    ArrayVariablesArgument,
                    $"Send {_limits.MaxCalculatorVariables} or fewer total variables, or split the call.");
            }
        }

        var declaredNames = new HashSet<string>(variableCount + arrayVariableCount + expressionCount, StringComparer.Ordinal);
        if (hasVariables)
        {
            foreach (JsonProperty prop in variablesElement.EnumerateObject())
            {
                string varName = prop.Name;
                string varPath = varName.Length == 0 ? VariablesArgument : $"{VariablesArgument}.{varName}";
                ValidateDeclaredName(varName, varPath, declaredNames, allowFunctionNameVariables);
            }
        }

        if (hasArrayVariables)
        {
            foreach (JsonProperty prop in arrayVariablesElement.EnumerateObject())
            {
                string varName = prop.Name;
                string varPath = varName.Length == 0 ? ArrayVariablesArgument : $"{ArrayVariablesArgument}.{varName}";
                ValidateDeclaredName(varName, varPath, declaredNames, allowFunctionNameVariables);
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
            ValidateDeclaredName(exprName, namePath, declaredNames, allowFunctionNameVariables);
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

        var arrayVariables = hasArrayVariables ? new List<(string Name, decimal[] Values)>(arrayVariableCount) : null;
        if (hasArrayVariables)
        {
            int totalArrayElements = 0;
            foreach (JsonProperty prop in arrayVariablesElement.EnumerateObject())
            {
                string arrPath = $"{ArrayVariablesArgument}.{prop.Name}";
                if (prop.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        $"Array variable '{prop.Name}' must be an array of finite decimal numbers.",
                        arrPath);
                }

                int arrLen = prop.Value.GetArrayLength();
                if (arrLen > DslArrayArena.MaxArrayLength)
                {
                    throw new ToolException(
                        ToolErrorCodes.LimitExceeded,
                        $"Array variable '{prop.Name}' exceeds {DslArrayArena.MaxArrayLength} elements.",
                        arrPath,
                        $"Keep each array within {DslArrayArena.MaxArrayLength} elements.");
                }

                totalArrayElements += arrLen;
                if (totalArrayElements > MaxInputArrayElements)
                {
                    throw new ToolException(
                        ToolErrorCodes.LimitExceeded,
                        $"Total array variable elements exceed {MaxInputArrayElements}.",
                        ArrayVariablesArgument,
                        $"Keep combined input array elements within {MaxInputArrayElements}.");
                }

                var elems = new decimal[arrLen];
                int elemIdx = 0;
                foreach (JsonElement elem in prop.Value.EnumerateArray())
                {
                    if (elem.ValueKind != JsonValueKind.Number || !elem.TryGetDecimal(out decimal number))
                    {
                        throw new ToolException(
                            ToolErrorCodes.InvalidArgument,
                            $"Element {elemIdx} of array variable '{prop.Name}' must be a finite decimal number.",
                            $"{arrPath}[{elemIdx}]",
                            "Provide finite JSON numbers that fit System.Decimal.");
                    }

                    elems[elemIdx++] = number;
                }

                arrayVariables!.Add((prop.Name, elems));
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

        return Task.FromResult(EvaluateAndSerialize(variables, arrayVariables, expressions, decimalPlaces, cancellationToken));
    }

    internal static decimal NormalizeDecimal(decimal value)
        => value == 0m ? 0m : value / DecimalScaleStripDivisor;

    private static string EvaluateAndSerialize(
        IReadOnlyList<(string Name, decimal Value)> variables,
        IReadOnlyList<(string Name, decimal[] Values)>? arrayVariables,
        IReadOnlyList<(string Name, string Text)> expressions,
        int? decimalPlaces,
        CancellationToken cancellationToken)
    {
        var scalarDict = new Dictionary<string, decimal>(variables.Count, StringComparer.Ordinal);
        foreach ((string name, decimal value) in variables)
        {
            scalarDict[name] = value;
        }

        Dictionary<string, IReadOnlyList<decimal>>? arrayDict = null;
        if (arrayVariables is not null)
        {
            arrayDict = new Dictionary<string, IReadOnlyList<decimal>>(arrayVariables.Count, StringComparer.Ordinal);
            foreach ((string name, decimal[] values) in arrayVariables)
            {
                arrayDict[name] = values;
            }
        }

        var expressionIndexByName = new Dictionary<string, int>(expressions.Count, StringComparer.Ordinal);
        var batchItems = new ExpressionCalculator.CalculatorBatchItem[expressions.Count];
        for (int i = 0; i < expressions.Count; i++)
        {
            expressionIndexByName[expressions[i].Name] = i;
            batchItems[i] = new ExpressionCalculator.CalculatorBatchItem(expressions[i].Name, expressions[i].Text);
        }

        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> batchResults =
            ExpressionCalculator.EvaluateBatch(scalarDict, arrayDict, batchItems, CalculatorLimits.Default, cancellationToken);

        var failedExpressions = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<ExpressionEvalResult>(expressions.Count);
        int okCount = 0;
        int errorCount = 0;

        for (int i = 0; i < batchResults.Count; i++)
        {
            ExpressionCalculator.CalculatorBatchResult batchItem = batchResults[i];
            if (batchItem.IsOk)
            {
                if (batchItem.IsArray)
                {
                    IReadOnlyList<decimal> rawArr = batchItem.ArrayValues!;
                    var outputArr = new decimal[rawArr.Count];
                    for (int j = 0; j < rawArr.Count; j++)
                    {
                        decimal v = decimalPlaces is { } places
                            ? SeriesOutputOptions.Round(rawArr[j], places)
                            : rawArr[j];
                        outputArr[j] = NormalizeDecimal(v);
                    }

                    results.Add(ExpressionEvalResult.OkArray(batchItem.Name, batchItem.Expression, outputArr));
                }
                else
                {
                    decimal outputValue = decimalPlaces is { } places
                        ? SeriesOutputOptions.Round(batchItem.ScalarValue, places)
                        : batchItem.ScalarValue;
                    outputValue = NormalizeDecimal(outputValue);
                    results.Add(ExpressionEvalResult.Ok(batchItem.Name, batchItem.Expression, outputValue));
                }

                okCount++;
            }
            else
            {
                CalculatorError error = batchItem.Error!;
                failedExpressions.Add(batchItem.Name);
                (string wireCode, string message) = RefineError(
                    batchItem.Name,
                    i,
                    batchItem.Expression,
                    error,
                    expressionIndexByName,
                    failedExpressions);
                results.Add(ExpressionEvalResult.Fail(batchItem.Name, batchItem.Expression, wireCode, error.Position, message));
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

            if (arrayVariables is not null)
            {
                writer.WriteStartArray(ArrayVariablesArgument);
                foreach ((string name, decimal[] values) in arrayVariables)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    writer.WriteStartArray("values");
                    for (int j = 0; j < values.Length; j++)
                    {
                        writer.WriteNumberValue(NormalizeDecimal(values[j]));
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteStartArray("results");
            foreach (ExpressionEvalResult item in results)
            {
                writer.WriteStartObject();
                writer.WriteString("name", item.Name);
                writer.WriteString("expression", item.Expression);
                writer.WriteString("status", item.IsOk ? "Ok" : "Error");
                if (item.IsOk)
                {
                    if (item.IsArray)
                    {
                        writer.WriteString("valueType", "array");
                        writer.WriteStartArray("values");
                        for (int j = 0; j < item.ArrayValues!.Length; j++)
                        {
                            writer.WriteNumberValue(item.ArrayValues[j]);
                        }

                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WriteNumber("value", item.Value);
                    }
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
        string expressionText,
        CalculatorError error,
        IReadOnlyDictionary<string, int> expressionIndexByName,
        IReadOnlySet<string> failedExpressions)
    {
        string wireCode = error.Code.ToWireName();
        string message = error.Message;

        if (error.Code == CalculatorErrorCode.UndefinedName && error.Token is not null)
        {
            if (expressionIndexByName.TryGetValue(error.Token, out int refIndex))
            {
                if (refIndex < currentIndex && failedExpressions.Contains(error.Token))
                {
                    return (DependencyFailedCode, $"'{error.Token}' failed, so '{currentName}' cannot be calculated.");
                }

                if (refIndex == currentIndex)
                {
                    return (wireCode, $"{message} Expression '{currentName}' cannot reference itself.");
                }

                if (refIndex > currentIndex)
                {
                    return (wireCode, $"'{error.Token}' is declared after '{currentName}'; an expression can only use earlier expressions.");
                }
            }
            else if (ExpressionCalculator.IsReservedFunctionName(error.Token))
            {
                bool isArraySubscript = false;
                if (error.Position >= 0
                    && !string.IsNullOrEmpty(error.Token)
                    && error.Position + error.Token.Length <= expressionText.Length)
                {
                    int idx = error.Position + error.Token.Length;
                    while (idx < expressionText.Length && char.IsWhiteSpace(expressionText[idx]))
                    {
                        idx++;
                    }

                    isArraySubscript = idx < expressionText.Length && expressionText[idx] == '[';
                }

                if (!isArraySubscript)
                {
                    return (wireCode, $"{message} To call built-in function '{error.Token}(...)', include parentheses.");
                }
            }
        }

        return (wireCode, message);
    }

    private static void ValidateDeclaredName(
        string name,
        string paramPath,
        HashSet<string> declaredNames,
        bool allowFunctionNameVariables)
    {
        if (!ExpressionCalculator.TryValidateDeclaredIdentifier(
                name,
                allowFunctionNameVariables,
                out ExpressionCalculator.DeclaredIdentifierValidationError error))
        {
            switch (error)
            {
                case ExpressionCalculator.DeclaredIdentifierValidationError.InvalidSyntax:
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        $"Identifier '{name}' is invalid.",
                        paramPath,
                        $"Identifiers must match {IdentifierRegexHint}.");
                case ExpressionCalculator.DeclaredIdentifierValidationError.ReservedFunctionName:
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        $"Identifier '{name}' is a reserved function name.",
                        paramPath,
                        "Function names are reserved.");
                case ExpressionCalculator.DeclaredIdentifierValidationError.NonFiniteLiteral:
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        $"Identifier '{name}' is reserved for non-finite rejection.",
                        paramPath,
                        "Non-finite identifiers 'nan' and 'inf' cannot be declared.");
            }
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
        bool IsArray,
        decimal Value,
        decimal[]? ArrayValues,
        string? ErrorCode,
        int ErrorPosition,
        string? ErrorMessage)
    {
        public static ExpressionEvalResult Ok(string name, string expression, decimal value)
            => new(name, expression, true, false, value, null, null, 0, null);

        public static ExpressionEvalResult OkArray(string name, string expression, decimal[] values)
            => new(name, expression, true, true, 0m, values, null, 0, null);

        public static ExpressionEvalResult Fail(string name, string expression, string errorCode, int errorPosition, string errorMessage)
            => new(name, expression, false, false, 0m, null, errorCode, errorPosition, errorMessage);
    }
}
