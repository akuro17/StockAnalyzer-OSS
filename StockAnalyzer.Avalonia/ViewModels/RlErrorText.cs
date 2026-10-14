using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>Single owner of the exception-to-message mapping shared by the RL dialog and the RL playback page.</summary>
public static class RlErrorText
{
    public static string Describe(Exception error, ILocalizationService text)
    {
        if (error is RlTemporalException temporal)
        {
            string message = text.GetString("Rl.Temporal.Rejection." + temporal.Reason);
            return temporal.Reason == RlTemporalRejection.FutureInformation ? string.Format(CultureInfo.CurrentCulture, message,
                temporal.InformationUpperExclusive?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), temporal.DecisionExchangeDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : message;
        }
        return text.GetString(error switch
        {
            ArgumentException or OverflowException => "Rl.Ui.Status.InvalidInput", FileNotFoundException => "Rl.Ui.Status.DataUnavailable",
            UnauthorizedAccessException => "Rl.Ui.Status.AccessDenied", InvalidDataException when RlSourceContract.IsInsufficientData(error) => "Rl.Ui.Status.InsufficientData",
            InvalidDataException or JsonException => "Rl.Ui.Status.InvalidData",
            IOException => "Rl.Ui.Status.IOFailure", TimeoutException => "Rl.Ui.Status.Timeout", _ => "Rl.Ui.Status.OperationFailed"
        });
    }
}
