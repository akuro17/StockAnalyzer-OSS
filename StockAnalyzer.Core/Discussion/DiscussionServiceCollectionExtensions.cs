using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// Registers the discussion board and the router that decides whether it is held here or reached over a pipe. A separate call from the read-only composition, which stays as it is: only a host that was started with the discussion
/// option calls it. Registering creates nothing and writes nothing: the board, its gateway and the command log are built when something first asks for them
/// (the command log opens its file only when the first record is written, and only when the file switch is on).
/// </summary>
public static class DiscussionServiceCollectionExtensions
{
    /// <summary>Binds <c>AiIntegration:Discussion</c>, checks it against its hard bands and registers the settings, the clock, the command log, the board and its gateway.</summary>
    /// <param name="information">Receives one line per value that is inside its band but beyond the standard zone (the host writes it to its log); null drops them.</param>
    /// <param name="report">Receives the one line written when the command log file is turned off after an I/O failure; null drops it.</param>
    /// <param name="protocolSuffix">Appended to the protocol text a joining participant receives (the research disclaimer of the MCP tools).</param>
    /// <exception cref="DiscussionLimitsException">A configured discussion setting is outside its band.</exception>
    public static IServiceCollection AddStockAnalyzerDiscussion(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<string>? information = null,
        Action<string>? report = null,
        string? protocolSuffix = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        DiscussionSettings settings = configuration.GetSection(DiscussionSettings.SectionName).Get<DiscussionSettings>() ?? new DiscussionSettings();
        foreach (string line in DiscussionSettingsValidator.Validate(settings))
        {
            information?.Invoke(line);
        }

        services.TryAddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICommandLog>(sp => new CommandLog(settings, sp.GetRequiredService<TimeProvider>(), report: report));
        services.TryAddSingleton<IDiscussionGateway>(sp => new DiscussionGatewayRouter(
            settings,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ICommandLog>(),
            protocolSuffix,
            DiscussionSharingKey.ResolveDataFolder(sp.GetService<IOptions<MarketDataSettings>>()?.Value.DailyDataPath)));
        return services;
    }
}
