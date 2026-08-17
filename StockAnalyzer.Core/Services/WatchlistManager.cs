using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Implementation of IWatchlistManager that provides thread-safe access to watchlists.
/// </summary>
public class WatchlistManager : IWatchlistManager
{
    private readonly ILogger<WatchlistManager> _logger;
    private readonly object _lock = new();
    private List<WatchlistProfile> _profiles = new();

    public event EventHandler? WatchlistsChanged;

    public WatchlistManager(ILogger<WatchlistManager>? logger = null)
    {
        _logger = logger ?? NullLogger<WatchlistManager>.Instance;
    }

    public void Initialize(IEnumerable<WatchlistProfile> profiles)
    {
        var incoming = profiles.ToList();
        lock (_lock)
        {
            // Optimization: Skip if contents are logically equal to avoid redundant save triggers
            if (_profiles != null && _profiles.SequenceEqual(incoming))
            {
                return;
            }
            _profiles = incoming;
        }
        OnWatchlistsChanged();
    }

    public IReadOnlyList<WatchlistProfile> GetAllProfiles()
    {
        lock (_lock)
        {
            return _profiles.AsReadOnly();
        }
    }

    public WatchlistProfile? GetProfileById(Guid profileId)
    {
        lock (_lock)
        {
            return _profiles.FirstOrDefault(p => p.Id == profileId);
        }
    }

    public WatchlistProfile CreateProfile(string name, IndicatorColor color, bool isPortfolio = false)
    {
        var newProfile = new WatchlistProfile(Guid.NewGuid(), name, color, isPortfolio);
        lock (_lock)
        {
            _profiles.Add(newProfile);
        }
        OnWatchlistsChanged();
        return newProfile;
    }

    public void UpdateProfileName(Guid profileId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        lock (_lock)
        {
            var index = _profiles.FindIndex(p => p.Id == profileId);
            if (index < 0) return;

            var profile = _profiles[index];
            _profiles[index] = profile with { Name = name.Trim() };
        }
        OnWatchlistsChanged();
    }

    public void DeleteProfile(Guid profileId)
    {
        lock (_lock)
        {
            var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile != null)
            {
                _profiles.Remove(profile);
            }
        }
        OnWatchlistsChanged();
    }

    public void AddTickerToProfile(Guid profileId, string ticker) => AddTickersToProfile(profileId, new[] { ticker });

    public void AddTickersToProfile(Guid profileId, IEnumerable<string> tickers)
    {
        if (tickers == null) return;
        var tickerList = tickers.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (tickerList.Count == 0) return;

        lock (_lock)
        {
            var index = _profiles.FindIndex(p => p.Id == profileId);
            if (index < 0) return;

            var profile = _profiles[index];
            var itemsList = profile.Items.ToList();
            bool added = false;

            foreach (var ticker in tickerList)
            {
                string normalizedTicker = ticker.Trim().ToUpperInvariant();
                
                // Deduplication (OrdinalIgnoreCase)
                if (itemsList.Any(i => string.Equals(i.Ticker, normalizedTicker, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var newItem = new WatchlistItem(normalizedTicker, DateTimeOffset.UtcNow.ToUniversalTime());
                itemsList.Add(newItem);
                added = true;
            }

            if (added)
            {
                // Immutable update using 'with' logic
                _profiles[index] = profile with { Items = itemsList.AsReadOnly() };
            }
            else
            {
                return; // Nothing changed
            }
        }
        OnWatchlistsChanged();
    }

    public void RemoveTickerFromProfile(Guid profileId, string ticker) => RemoveTickersFromProfile(profileId, new[] { ticker });

    public void RemoveTickersFromProfile(Guid profileId, IEnumerable<string> tickers)
    {
        if (tickers == null) return;
        var tickerList = tickers.ToList();
        if (tickerList.Count == 0) return;

        lock (_lock)
        {
            var index = _profiles.FindIndex(p => p.Id == profileId);
            if (index < 0) return;

            var profile = _profiles[index];
            var itemsList = profile.Items.ToList();
            bool removed = false;

            foreach (var ticker in tickerList)
            {
                var itemToRemove = itemsList.FirstOrDefault(i => string.Equals(i.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
                if (itemToRemove != null)
                {
                    itemsList.Remove(itemToRemove);
                    removed = true;
                }
            }

            if (removed)
            {
                _profiles[index] = profile with { Items = itemsList.AsReadOnly() };
            }
            else
            {
                return; // Nothing changed
            }
        }
        OnWatchlistsChanged();
    }

    public void RemoveTickersFromAllProfiles(IEnumerable<string> tickers)
    {
        if (tickers == null) return;
        var tickerList = tickers.ToList();
        if (tickerList.Count == 0) return;

        bool overallChanged = false;
        lock (_lock)
        {
            for (int i = 0; i < _profiles.Count; i++)
            {
                var profile = _profiles[i];
                var itemsList = profile.Items.ToList();
                bool removedFromThisProfile = false;

                foreach (var ticker in tickerList)
                {
                    var itemToRemove = itemsList.FirstOrDefault(it => string.Equals(it.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
                    if (itemToRemove != null)
                    {
                        itemsList.Remove(itemToRemove);
                        removedFromThisProfile = true;
                    }
                }

                if (removedFromThisProfile)
                {
                    _profiles[i] = profile with { Items = itemsList.AsReadOnly() };
                    overallChanged = true;
                }
            }
        }

        if (overallChanged)
        {
            OnWatchlistsChanged();
        }
    }

    protected virtual void OnWatchlistsChanged()
    {
        WatchlistsChanged?.Invoke(this, EventArgs.Empty);
        // We could also send a message via IMessenger here if preferred
    }
}
