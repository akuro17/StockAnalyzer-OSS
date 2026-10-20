using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Analysis;

public class PatternConstantsCharacterizationTests
{
    private static string ComputeCharacterizationHash()
    {
        var sb = new StringBuilder();

        for (int seed = 1; seed <= 5; seed++)
        {
            var bars = SyntheticBars.SeededRandomWalk(seed, 2000, 100m);

            // CandlePatternDetector on prefixes of length 20..2000 step 7
            for (int len = 20; len <= 2000; len += 7)
            {
                var prefix = new List<CandleData>(len);
                for (int i = 0; i < len; i++) prefix.Add(bars[i]);

                var pattern = CandlePatternDetector.DetectPattern(prefix);
                sb.Append($"{seed}:{len}:{pattern};");
            }
            // GeometricPatternDetector on 5 windows
            int[] windows = { 50, 100, 200, 500, 1000 };
            foreach (var win in windows)
            {
                var winBars = new List<CandleData>(win);
                for (int i = 0; i < win; i++) winBars.Add(bars[i]);

                var formations = GeometricPatternDetector.Detect(winBars);
                foreach (var f in formations)
                {
                    sb.Append($"{seed}:{win}:{f.Type}:{f.StartIndex}:{f.EndIndex}:{f.UpperSlope:F4}:{f.LowerSlope:F4};");
                }
            }
        }

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes);
    }

    [Fact]
    public void CharacterizationHash_MatchesGoldenValue()
    {
        string actualHash = ComputeCharacterizationHash();

        Assert.Equal("4AE49822CE4538080E4A9C2A4581984F31A53DFF50C09F6DC2FF71D403A8B8F2", actualHash);
    }
}
