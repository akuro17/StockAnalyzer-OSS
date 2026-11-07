#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Execution;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// Task T1 of the discussion board: the settings, their hard bands and relations, the fixed values, the temp folder, the id helper and the registration. The numbers
/// in this file are copied from the specification on purpose (they are not read from <see cref="DiscussionLimits"/>), so a band that drifts from the specification fails here.
/// </summary>
public sealed class DiscussionSettingsTests
{
    private sealed record BandCase(string Name, long Minimum, long Maximum, Action<DiscussionSettings, long> Set);

    // Spec 4.1: every tunable key with its hard band. A setter that must keep a related key valid does so itself.
    private static readonly BandCase[] Bands =
    {
        new(nameof(DiscussionSettings.MaxActiveSessions), 1, 16, (s, v) => s.MaxActiveSessions = (int)v),
        new(nameof(DiscussionSettings.MaxParticipantsPerSession), 2, 16, (s, v) => s.MaxParticipantsPerSession = (int)v),
        new(nameof(DiscussionSettings.MaxMessagesPerSession), 10, 2000, (s, v) => s.MaxMessagesPerSession = (int)v),
        new(nameof(DiscussionSettings.MaxMessageBytes), 256, 65536, (s, v) => s.MaxMessageBytes = (int)v),
        new(nameof(DiscussionSettings.MaxContextsPerSession), 1, 100, (s, v) => s.MaxContextsPerSession = (int)v),
        new(nameof(DiscussionSettings.MaxContextBytes), 1024, 2097152, (s, v) => s.MaxContextBytes = (int)v),
        new(nameof(DiscussionSettings.MaxTotalBytes), 1048576, 268435456, (s, v) => s.MaxTotalBytes = v),
        new(nameof(DiscussionSettings.IdleTimeoutSeconds), 60, 86400, (s, v) => s.IdleTimeoutSeconds = (int)v),
        new(nameof(DiscussionSettings.MaxSessionAgeSeconds), 300, 86400, (s, v) => s.MaxSessionAgeSeconds = (int)v),
        new(nameof(DiscussionSettings.ClosedRetentionSeconds), 0, 3600, (s, v) => s.ClosedRetentionSeconds = (int)v),
        new(nameof(DiscussionSettings.AwaySeconds), 10, 3600, (s, v) => s.AwaySeconds = (int)v),
        new(nameof(DiscussionSettings.FloorTimeoutSeconds), 10, 600, (s, v) => s.FloorTimeoutSeconds = (int)v),
        new(nameof(DiscussionSettings.MaxWaitSeconds), 0, 30, (s, v) => s.MaxWaitSeconds = (int)v),
        new(nameof(DiscussionSettings.MaxPostsPerMinute), 1, 120, (s, v) => s.MaxPostsPerMinute = (int)v),
        new(nameof(DiscussionSettings.MaxReadLimit), 1, 200, (s, v) => { s.MaxReadLimit = (int)v; s.DefaultReadLimit = (int)Math.Min(s.DefaultReadLimit, v); }),
        new(nameof(DiscussionSettings.AuditCapacity), 100, 10000, (s, v) => s.AuditCapacity = (int)v),
        new(nameof(DiscussionSettings.CommandLogCapacity), 100, 10000, (s, v) => s.CommandLogCapacity = (int)v),
        new(nameof(DiscussionSettings.CommandLogMaxFileBytes), 65536, 16777216, (s, v) => s.CommandLogMaxFileBytes = (int)v),
        new(nameof(DiscussionSettings.CommandLogGenerations), 1, 8, (s, v) => s.CommandLogGenerations = (int)v),
        new(nameof(DiscussionSettings.CommandLogMaxAgeHours), 1, 168, (s, v) => s.CommandLogMaxAgeHours = (int)v),
    };

    public static TheoryData<string> BandNames()
    {
        var data = new TheoryData<string>();
        foreach (BandCase band in Bands)
        {
            data.Add(band.Name);
        }

        return data;
    }

    private static BandCase Case(string name) => Bands.Single(band => band.Name == name);

    private static DiscussionSettings With(BandCase band, long value)
    {
        var settings = new DiscussionSettings();
        band.Set(settings, value);
        return settings;
    }

    // ---- Defaults ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStandardValuesAreThoseOfTheSpecificationAndPassTheValidatorWithoutAnInformationLine()
    {
        var settings = new DiscussionSettings();

        Assert.Equal(4, settings.MaxActiveSessions);
        Assert.Equal(6, settings.MaxParticipantsPerSession);
        Assert.Equal(300, settings.MaxMessagesPerSession);
        Assert.Equal(16384, settings.MaxMessageBytes);
        Assert.Equal(20, settings.MaxContextsPerSession);
        Assert.Equal(524288, settings.MaxContextBytes);
        Assert.Equal(67108864L, settings.MaxTotalBytes);
        Assert.Equal(1800, settings.IdleTimeoutSeconds);
        Assert.Equal(7200, settings.MaxSessionAgeSeconds);
        Assert.Equal(300, settings.ClosedRetentionSeconds);
        Assert.Equal(120, settings.AwaySeconds);
        Assert.Equal(60, settings.FloorTimeoutSeconds);
        Assert.Equal(20, settings.MaxWaitSeconds);
        Assert.Equal(20, settings.MaxPostsPerMinute);
        Assert.Equal(20, settings.DefaultReadLimit);
        Assert.Equal(50, settings.MaxReadLimit);
        Assert.Equal(1000, settings.AuditCapacity);
        Assert.Equal(1000, settings.CommandLogCapacity);
        Assert.False(settings.CommandLogToFile);
        Assert.Equal(2097152, settings.CommandLogMaxFileBytes);
        Assert.Equal(3, settings.CommandLogGenerations);
        Assert.Equal(24, settings.CommandLogMaxAgeHours);
        Assert.Equal("AiIntegration:Discussion", DiscussionSettings.SectionName);
        Assert.Empty(DiscussionSettingsValidator.Validate(settings));
    }

    [Fact]
    public void TheWorstCaseOfTheStandardValuesFitsTheBudgetAsTheSpecificationStates()
    {
        var settings = new DiscussionSettings();

        long worstCase = settings.MaxActiveSessions * (((long)settings.MaxMessagesPerSession * settings.MaxMessageBytes) + ((long)settings.MaxContextsPerSession * settings.MaxContextBytes));

        Assert.Equal(61603840L, worstCase);
        Assert.True(worstCase <= settings.MaxTotalBytes);
    }

    // ---- Hard bands (min, min-1, max, max+1) ------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BandNames))]
    public void ABandEdgeIsAcceptedAndOneBeyondItIsRefusedWithTheKeyInTheMessage(string name)
    {
        BandCase band = Case(name);

        DiscussionSettingsValidator.Validate(With(band, band.Minimum));
        DiscussionSettingsValidator.Validate(With(band, band.Maximum));

        foreach (long outside in new[] { band.Minimum - 1, band.Maximum + 1 })
        {
            var error = Assert.Throws<DiscussionLimitsException>(() => DiscussionSettingsValidator.Validate(With(band, outside)));
            Assert.Contains($"AiIntegration:Discussion:{name} ", error.Message, StringComparison.Ordinal);
            Assert.Contains($"{band.Minimum} to {band.Maximum}", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDefaultReadLimitStaysBetweenOneAndTheMaximumReadLimit()
    {
        DiscussionSettingsValidator.Validate(new DiscussionSettings { MaxReadLimit = 30, DefaultReadLimit = 1 });
        DiscussionSettingsValidator.Validate(new DiscussionSettings { MaxReadLimit = 30, DefaultReadLimit = 30 });

        var tooLarge = Assert.Throws<DiscussionLimitsException>(() => DiscussionSettingsValidator.Validate(new DiscussionSettings { MaxReadLimit = 30, DefaultReadLimit = 31 }));
        var zero = Assert.Throws<DiscussionLimitsException>(() => DiscussionSettingsValidator.Validate(new DiscussionSettings { MaxReadLimit = 30, DefaultReadLimit = 0 }));

        Assert.Contains("DefaultReadLimit", tooLarge.Message, StringComparison.Ordinal);
        Assert.Contains("1 to 30", tooLarge.Message, StringComparison.Ordinal);
        Assert.Contains("DefaultReadLimit", zero.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AContextLargerThanTheWholeBudgetIsRefused()
    {
        var edge = new DiscussionSettings { MaxTotalBytes = 1048576, MaxContextBytes = 1048576 };
        var over = new DiscussionSettings { MaxTotalBytes = 1048576, MaxContextBytes = 1048577 };

        DiscussionSettingsValidator.Validate(edge);
        var error = Assert.Throws<DiscussionLimitsException>(() => DiscussionSettingsValidator.Validate(over));

        Assert.Contains("MaxContextBytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("MaxTotalBytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageCanNeverBeLargerThanTheBudgetBecauseTheBandsAreOrdered()
    {
        // The relation "message bytes <= total bytes" is checked, but the hard bands make it unreachable: the largest message is below the smallest budget.
        Assert.True(DiscussionLimits.MessageBytesBand.Maximum <= DiscussionLimits.MinTotalBytes);
    }

    // ---- Non-standard zone ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFullHouseBeyondTheBudgetIsReportedAsInformationAndNeverRefused()
    {
        var settings = new DiscussionSettings { MaxActiveSessions = 16 };

        IReadOnlyList<string> information = DiscussionSettingsValidator.Validate(settings);

        string line = Assert.Single(information);
        Assert.Contains("MaxTotalBytes", line, StringComparison.Ordinal);
        Assert.Contains("16 full sessions", line, StringComparison.Ordinal);
    }

    // ---- Relations with the host ------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFullReadPageMustFitTheHostResponseLimitExactlyAtTheEdge()
    {
        var settings = new DiscussionSettings();
        const int FullPage = (50 * 16384) + 65536; // spec 4.1: 884,736

        DiscussionSettingsValidator.ValidateAgainstHost(settings, 4 * 1024 * 1024, 60);
        DiscussionSettingsValidator.ValidateAgainstHost(settings, FullPage, 60);
        var error = Assert.Throws<DiscussionLimitsException>(() => DiscussionSettingsValidator.ValidateAgainstHost(settings, FullPage - 1, 60));

        Assert.Equal(884736, FullPage);
        Assert.Contains("AiIntegration:Discussion:MaxReadLimit ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLongestWaitMustBeShorterThanTheHostToolTimeLimit()
    {
        var settings = new DiscussionSettings { MaxWaitSeconds = 20 };

        DiscussionSettingsValidator.ValidateAgainstHost(settings, 4 * 1024 * 1024, 21);
        var equal = Assert.Throws<DiscussionLimitsException>(() => DiscussionSettingsValidator.ValidateAgainstHost(settings, 4 * 1024 * 1024, 20));
        var shorter = Assert.Throws<DiscussionLimitsException>(() => DiscussionSettingsValidator.ValidateAgainstHost(settings, 4 * 1024 * 1024, 5));

        Assert.Contains("AiIntegration:Discussion:MaxWaitSeconds ", equal.Message, StringComparison.Ordinal);
        Assert.Contains("MaxWaitSeconds", shorter.Message, StringComparison.Ordinal);
    }

    // ---- Fixed values -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFixedValuesAreThoseOfTheSpecification()
    {
        Assert.Equal(1, DiscussionLimits.WireVersion);
        Assert.Equal(5242880, DiscussionLimits.WireMaxLineBytes);
        Assert.Equal(32, DiscussionLimits.MaxHubConnections);
        Assert.Equal(300, DiscussionLimits.PipeConnectTimeoutMs);
        Assert.Equal(3, DiscussionLimits.ElectionAttempts);
        Assert.Equal(50, DiscussionLimits.ElectionPauseMs);
        Assert.Equal(5000, DiscussionLimits.WireRequestTimeoutMs);
        Assert.Equal(2000, DiscussionLimits.WireDrainTimeoutMs);
        Assert.Equal(20, DiscussionLimits.AcceptRetryMs);
        Assert.Equal(2000, DiscussionLimits.HubShutdownWaitMs);
        Assert.Equal(30, DiscussionLimits.SweepIntervalSeconds);
        Assert.Equal(100, DiscussionLimits.TitleMaxChars);
        Assert.Equal(2000, DiscussionLimits.TopicMaxChars);
        Assert.Equal(64, DiscussionLimits.DisplayNameMaxChars);
        Assert.Equal(64, DiscussionLimits.ClientRequestIdMaxChars);
        Assert.Equal(64, DiscussionLimits.IdempotencyRemembered);
        Assert.Equal(60, DiscussionLimits.PostRateWindowSeconds);
        Assert.Equal(500, DiscussionLimits.NoteMaxChars);
        Assert.Equal(4096, DiscussionLimits.AnalysisArgumentsMaxBytes);
        Assert.Equal(64, DiscussionLimits.ClientLabelMaxChars);
        Assert.Equal(6, DiscussionLimits.EpochTagChars);
        Assert.Equal(16, DiscussionLimits.TokenRandomBytes);
        Assert.Equal(7, DiscussionLimits.SessionIdChars);
        Assert.Equal(8, DiscussionLimits.MaxWaitersPerSession);
        Assert.Equal(16, DiscussionLimits.MaxTotalWaiters);
        Assert.Equal(4, DiscussionLimits.ClosedSessionRetentionMultiplier);
        Assert.Equal("abcdefghijklmnopqrstuvwxyz234567", DiscussionLimits.Base32Alphabet);
        Assert.Equal(32, DiscussionLimits.Base32Alphabet.Length);
    }

    [Theory]
    [InlineData(2, 4)]
    [InlineData(6, 12)]
    [InlineData(16, 32)]
    public void TheJoinCounterLimitIsTwiceTheParticipantLimit(int participants, int expectedJoins)
        => Assert.Equal(expectedJoins, DiscussionLimits.MaxJoinsPerSession(participants));

    // ---- Temp folder ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTempFolderIsASiblingOfTheExecutionSandboxAndNeverBelowIt()
    {
        string expected = Path.Combine(Path.GetTempPath(), "StockAnalyzer", "discussion");

        Assert.Equal(expected, DiscussionTempFolder.Root);
        Assert.Equal("discussion", DiscussionTempFolder.FolderName);
        Assert.NotEqual(EphemeralSandbox.Root, DiscussionTempFolder.Root);
        Assert.False(EphemeralSandbox.IsStrictlyInside(DiscussionTempFolder.Root));
        Assert.Equal(Path.GetDirectoryName(EphemeralSandbox.Root), Path.GetDirectoryName(DiscussionTempFolder.Root));
    }

    // ---- Registration -----------------------------------------------------------------------------------------------------------------

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(pair => $"{DiscussionSettings.SectionName}:{pair.Key}", pair => (string?)pair.Value)).Build();

    /// <summary>The single settings instance the registration added (this project holds the abstractions only, so the descriptor is read instead of building a provider).</summary>
    private static DiscussionSettings Registered(IServiceCollection services)
    {
        ServiceDescriptor descriptor = Assert.Single(services, candidate => candidate.ServiceType == typeof(DiscussionSettings));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        return Assert.IsType<DiscussionSettings>(descriptor.ImplementationInstance);
    }

    [Fact]
    public void TheRegistrationBindsTheSectionAndKeepsTheStandardValuesForTheRest()
    {
        var services = new ServiceCollection();

        services.AddStockAnalyzerDiscussion(Configuration(("MaxActiveSessions", "2"), ("CommandLogToFile", "true")));

        DiscussionSettings settings = Registered(services);
        Assert.Equal(2, settings.MaxActiveSessions);
        Assert.True(settings.CommandLogToFile);
        Assert.Equal(300, settings.MaxMessagesPerSession);
    }

    [Fact]
    public void TheRegistrationWithoutASectionRegistersTheStandardValues()
    {
        var services = new ServiceCollection();

        services.AddStockAnalyzerDiscussion(new ConfigurationBuilder().Build());

        Assert.Equal(4, Registered(services).MaxActiveSessions);
    }

    [Fact]
    public void ABadConfiguredValueStopsTheRegistrationWithTheKeyInTheMessage()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<DiscussionLimitsException>(() => services.AddStockAnalyzerDiscussion(Configuration(("MaxWaitSeconds", "31"))));

        Assert.Contains("AiIntegration:Discussion:MaxWaitSeconds is 31", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonStandardValueReachesTheInformationCallbackAndTheRegistrationStillSucceeds()
    {
        var services = new ServiceCollection();
        var lines = new List<string>();

        services.AddStockAnalyzerDiscussion(Configuration(("MaxActiveSessions", "16")), lines.Add);

        Assert.Single(lines);
        Assert.Contains("MaxTotalBytes", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheRegistrationCreatesNothingOnDisk()
    {
        bool existed = Directory.Exists(DiscussionTempFolder.Root);
        var services = new ServiceCollection();

        services.AddStockAnalyzerDiscussion(Configuration());
        _ = Registered(services);

        Assert.Equal(existed, Directory.Exists(DiscussionTempFolder.Root));
    }
}
