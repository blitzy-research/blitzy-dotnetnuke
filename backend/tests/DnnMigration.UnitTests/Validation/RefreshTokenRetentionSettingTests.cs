using DnnMigration.Application.Options;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Pins the two settings that turn refresh-record retention from an intention into a behaviour.
/// </summary>
/// <remarks>
/// <para>
/// PRIV-02. <strong>WHY THEY EXIST AT ALL.</strong> Both shipped stores reclaimed a record only when its
/// family's absolute ceiling had elapsed, and only ever as a side effect of issuing or rotating a token. Two
/// consequences followed, and the second is the worse one. An ordinary sign-out left a row naming the account,
/// the tenant and the token digest for the remainder of the refresh lifetime - days, for a session that had
/// already ended. And an installation nobody signed in to again reclaimed nothing at all, indefinitely, which
/// is precisely the installation where no operator is watching.
/// </para>
/// <para>
/// <see cref="RefreshTokenStoreOptions.RevokedRecordRetentionHours"/> is the documented minimum period the
/// finding asked for, and <see cref="RefreshTokenStoreOptions.RetentionSweepMinutes"/> is what makes it real:
/// a retention window that is only applied when traffic happens to arrive is a statement about intent.
/// </para>
/// <para>
/// <strong>WHY THE BOUNDS ARE ASSERTED RATHER THAN THE DEFAULTS ALONE.</strong> Each bound closes a distinct
/// failure. Below the retention floor a revoked record is erased before a replay of its family could be
/// recognised, which discards the one signal that makes a credential stolen before a sign-out visible
/// afterwards. Above its ceiling the record is personal data kept for a signal nobody will read. Below the
/// sweep floor the sweep becomes a busy loop against the store; above its ceiling the sweep runs less often
/// than the retention it is meant to enforce, and the retention setting silently stops describing behaviour.
/// </para>
/// <para>
/// Unit tests, because validation is a pure function of the settings object. The stores' own behaviour under
/// these values is exercised in the integration suite, where there is a store to observe.
/// </para>
/// </remarks>
public sealed class RefreshTokenRetentionSettingTests
{
    /// <summary>A deployment that configures nothing gets a bounded retention and an hourly sweep.</summary>
    /// <remarks>
    /// The defaults matter more than usual here: they are what every deployment that has not read this section
    /// runs, and before these settings existed the effective retention was the whole refresh lifetime and the
    /// effective sweep interval was "whenever somebody signs in".
    /// </remarks>
    [Fact]
    public void TheDefaultsAreOneDayOfRetentionSweptEveryHour()
    {
        RefreshTokenStoreOptions options = new();

        options.RevokedRecordRetentionHours.Should().Be(
            24,
            "a revoked record is kept only for as long as a replay of its family could still arrive");
        options.RetentionSweepMinutes.Should().Be(
            60,
            "reclamation must not depend on sign-in traffic arriving");

        options.Validate().Should().BeEmpty("the shipped defaults are inside their own bounds");
    }

    /// <summary>Both bounds of the revoked-record retention are enforced.</summary>
    /// <param name="hours">The configured retention.</param>
    /// <remarks>
    /// Zero is included deliberately and is the row that matters most: it reads as "erase on revocation", which
    /// would make the record's removal simultaneous with the event that created it and is therefore
    /// indistinguishable from keeping no signal at all.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RefreshTokenStoreOptions.MaximumRevokedRetentionHours + 1)]
    public void ARetentionOutsideItsBoundsIsRefused(int hours)
    {
        RefreshTokenStoreOptions options = new() { RevokedRecordRetentionHours = hours };

        options.Validate().Should().Contain(
            failure => failure.Contains(nameof(RefreshTokenStoreOptions.RevokedRecordRetentionHours), StringComparison.Ordinal),
            "a retention nobody validated is a retention nobody applies");
    }

    /// <summary>Both ends of the permitted retention range are accepted.</summary>
    /// <param name="hours">The configured retention.</param>
    [Theory]
    [InlineData(RefreshTokenStoreOptions.MinimumRevokedRetentionHours)]
    [InlineData(RefreshTokenStoreOptions.MaximumRevokedRetentionHours)]
    public void TheRetentionBoundsThemselvesAreAdmitted(int hours)
    {
        RefreshTokenStoreOptions options = new() { RevokedRecordRetentionHours = hours };

        options.Validate().Should().BeEmpty("a bound is inclusive, or it is a different bound");
    }

    /// <summary>Both bounds of the sweep interval are enforced.</summary>
    /// <param name="minutes">The configured interval.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(RefreshTokenStoreOptions.MaximumRetentionSweepMinutes + 1)]
    public void ASweepIntervalOutsideItsBoundsIsRefused(int minutes)
    {
        RefreshTokenStoreOptions options = new() { RetentionSweepMinutes = minutes };

        options.Validate().Should().Contain(
            failure => failure.Contains(nameof(RefreshTokenStoreOptions.RetentionSweepMinutes), StringComparison.Ordinal));
    }

    /// <summary>
    /// The retention settings are validated for the PROCESS-LOCAL store too, not only for the durable one.
    /// </summary>
    /// <remarks>
    /// PRIV-02. The load-bearing case, and the one an implementation is most likely to get wrong: every other
    /// setting below the provider check describes the shared store and is deliberately skipped for a
    /// process-local deployment. Retention is not one of those - the process-local store honours the same
    /// window and the same sweep - so a deployment running the shipped default must be told when it has
    /// configured an impossible value, rather than having it silently ignored because it is not using SQL
    /// Server.
    /// </remarks>
    [Fact]
    public void TheRetentionSettingsAreJudgedWhateverProviderIsSelected()
    {
        RefreshTokenStoreOptions inProcess = new()
        {
            Provider = RefreshTokenStoreOptions.InProcessProvider,
            RevokedRecordRetentionHours = 0,
            RetentionSweepMinutes = 0,
        };

        IReadOnlyList<string> failures = inProcess.Validate();

        failures.Should().HaveCount(2, "both settings are wrong and both are reported");
        failures.Should().Contain(failure =>
            failure.Contains(nameof(RefreshTokenStoreOptions.RevokedRecordRetentionHours), StringComparison.Ordinal));
        failures.Should().Contain(failure =>
            failure.Contains(nameof(RefreshTokenStoreOptions.RetentionSweepMinutes), StringComparison.Ordinal));
    }

    /// <summary>A refusal explains the consequence rather than merely restating the bound.</summary>
    /// <remarks>
    /// The message is what an operator has to act on at three in the morning, and "must be between 1 and 720"
    /// does not say which direction is dangerous or why. Asserted so that a later edit shortening the message
    /// has to be a deliberate one.
    /// </remarks>
    [Fact]
    public void ARefusalSaysWhatTheValueWouldHaveCost()
    {
        RefreshTokenStoreOptions tooShort = new() { RevokedRecordRetentionHours = 0 };

        tooShort.Validate().Should().Contain(failure =>
            failure.Contains("replay", StringComparison.OrdinalIgnoreCase)
            && failure.Contains("signal", StringComparison.OrdinalIgnoreCase));

        RefreshTokenStoreOptions tooSlow = new() { RetentionSweepMinutes = RefreshTokenStoreOptions.MaximumRetentionSweepMinutes + 1 };

        tooSlow.Validate().Should().Contain(failure =>
            failure.Contains(nameof(RefreshTokenStoreOptions.RevokedRecordRetentionHours), StringComparison.Ordinal),
            "a sweep slower than the retention makes the retention setting describe an intention, and the "
            + "message names the setting that stops being true");
    }
}
