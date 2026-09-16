using System;
using System.Collections;
using System.Collections.Generic;

namespace StockAnalyzer.Core.MathUtils;

public static class PrimeNumberHelper
{
    public const int MaxCachedPrime = 2_000_000; // Supports stock prices up to $200,000 * 10 multiplier (~600KB static cache)
    private static readonly int[] _primes;

    static PrimeNumberHelper()
    {
        _primes = GeneratePrimesSieve(MaxCachedPrime);
    }

    public static IReadOnlyList<int> Primes => _primes;

    /// <summary>
    /// Performs a fast binary search retrieval of the nearest lower and upper primes for a given value.
    /// </summary>
    public static (int LowerPrime, int UpperPrime) FindNearestPrimes(int value)
    {
        if (value <= 2) return (2, 2);
        if (value >= _primes[^1]) return (_primes[^1], _primes[^1]);

        int idx = Array.BinarySearch(_primes, value);
        if (idx >= 0) return (_primes[idx], _primes[idx]); // Exact prime match

        int nextIdx = ~idx;
        int prevIdx = nextIdx - 1;
        return (_primes[prevIdx], _primes[nextIdx]);
    }

    private static int[] GeneratePrimesSieve(int max)
    {
        var isPrime = new BitArray(max + 1, true);
        isPrime[0] = isPrime[1] = false;
        for (int p = 2; p * p <= max; p++)
        {
            if (isPrime[p])
            {
                for (int i = p * p; i <= max; i += p)
                    isPrime[i] = false;
            }
        }
        var list = new List<int>(150_000);
        for (int i = 2; i <= max; i++)
        {
            if (isPrime[i]) list.Add(i);
        }
        return list.ToArray();
    }
}
