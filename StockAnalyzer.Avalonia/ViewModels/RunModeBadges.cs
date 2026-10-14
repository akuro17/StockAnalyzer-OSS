using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Interfaces;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// The short state labels the training windows show next to the status while an option that changes what the result means is ON (a trial run registers no
/// model, a short history relaxes the segment count, a fine-tune or base evaluation starts from a stored model, CPCV / pretraining / an ensemble / a continuous
/// policy change what is trained or scored). Pure: the labels come from the states the caller passes; nothing is listed for the default state, and an option that
/// is ON by default is passed as its OFF state (the portfolio symbol holdout), never as its default.
/// </summary>
public static class RunModeBadges
{
    /// <summary>Localized labels of the states that are ON, in the order given.</summary>
    public static IReadOnlyList<string> Build(ILocalizationService localization, params (bool On, string Key)[] states)
        => Build(localization.GetString, states);

    /// <summary>The same with a text lookup (the Training Wizard reads its texts through the application's localization manager).</summary>
    public static IReadOnlyList<string> Build(System.Func<string, string> text, params (bool On, string Key)[] states)
        => states.Where(s => s.On).Select(s => text(s.Key)).ToArray();
}
