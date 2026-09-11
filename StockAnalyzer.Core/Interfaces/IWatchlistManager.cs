using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Interfaces;

/// <summary>
/// Provides high-level management for user watchlists, including thread-safe CRUD operations.
/// </summary>
public interface IWatchlistManager
{
    /// <summary>
    /// Gets all registered watchlist profiles.
    /// </summary>
    IReadOnlyList<WatchlistProfile> GetAllProfiles();

    /// <summary>
    /// Gets a specific profile by its ID.
    /// </summary>
    WatchlistProfile? GetProfileById(Guid profileId);

    /// <summary>
    /// Creates a new watchlist profile.
    /// </summary>
    WatchlistProfile CreateProfile(string name, IndicatorColor color, bool isPortfolio = false);

    /// <summary>
    /// Updates the name of a watchlist profile.
    /// </summary>
    void UpdateProfileName(Guid profileId, string name);

    /// <summary>
    /// Deletes an existing watchlist profile.
    /// </summary>
    void DeleteProfile(Guid profileId);

    /// <summary>
    /// Adds a ticker to a specific profile. 
    /// If the ticker already exists in the profile (case-insensitive), it will not be added again.
    /// </summary>
    void AddTickerToProfile(Guid profileId, string ticker);

    /// <summary>
    /// Adds multiple tickers to a specific profile.
    /// </summary>
    void AddTickersToProfile(Guid profileId, IEnumerable<string> tickers);

    /// <summary>
    /// Removes a ticker from a specific profile.
    /// </summary>
    void RemoveTickerFromProfile(Guid profileId, string ticker);

    /// <summary>
    /// Removes multiple tickers from a specific profile.
    /// </summary>
    void RemoveTickersFromProfile(Guid profileId, IEnumerable<string> tickers);

    /// <summary>
    /// Removes multiple tickers from ALL registered profiles.
    /// Used for cascading deletions when a ticker is removed from the master list.
    /// </summary>
    void RemoveTickersFromAllProfiles(IEnumerable<string> tickers);

    /// <summary>
    /// Occurs when any watchlist profile is modified (added, removed, or content changed).
    /// </summary>
    event EventHandler? WatchlistsChanged;

    /// <summary>
    /// Initializes the manager with the provided profiles (usually from persistent storage).
    /// </summary>
    void Initialize(IEnumerable<WatchlistProfile> profiles);
}
