using System.Security.Cryptography;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Validation;

/// <summary>Proves the package-free invariants of the legacy credential migration options.</summary>
/// <remarks>
/// <para>
/// THESE FACTS WERE WRITTEN AGAINST A SECOND OPTIONS TYPE AND ARE RE-POINTED AT THE ONE THAT SURVIVED. Two
/// independent revisions each introduced a settings type for the bounded legacy-credential window - one
/// publicly reachable from the Application layer and validated by a host-side <c>IValidateOptions</c>
/// registration, one internal to Infrastructure beside the verifier that consumes it and validated while
/// <c>AddInfrastructure</c> runs.
/// </para>
/// <para>
/// The surviving validator reports the FIRST failure rather than a collection, so the "enabled needs both"
/// fact is expressed as two facts. Splitting it is not a weakening: each requirement is now pinned
/// independently, and a change that dropped either check would fail its own fact rather than merely
/// reducing a count.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class LegacyCredentialOptionsTests
{
    private const string TestKey = "0123456789ABCDEFFEDCBA98765432100011223344556677";

    /// <summary>The shipped disabled state requires neither a key nor a deadline.</summary>
    [Fact]
    public void DisabledState_IsValidWithoutSecretMaterial()
    {
        var options = new LegacyCredentialOptions();

        options.Validate().Should().BeNull();
    }

    /// <summary>Enabling the window requires its absolute deadline.</summary>
    [Fact]
    public void EnabledState_RequiresTheAbsoluteDeadline()
    {
        var options = new LegacyCredentialOptions
        {
            Enabled = true,
            DecryptionKey = TestKey,
        };

        options.Validate().Should().NotBeNull()
            .And.Contain(nameof(LegacyCredentialOptions.EnabledUntilUtc));
    }

    /// <summary>Enabling the window requires its deployment key.</summary>
    [Fact]
    public void EnabledState_RequiresTheDeploymentKey()
    {
        var options = new LegacyCredentialOptions
        {
            Enabled = true,
            EnabledUntilUtc = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero),
        };

        options.Validate().Should().NotBeNull()
            .And.Contain(nameof(LegacyCredentialOptions.DecryptionKey));
    }

    /// <summary>A complete zero-offset configuration is valid.</summary>
    [Fact]
    public void EnabledState_AcceptsACompleteUtcConfiguration()
    {
        var options = new LegacyCredentialOptions
        {
            Enabled = true,
            EnabledUntilUtc = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero),
            DecryptionKey = TestKey,
        };

        options.Validate().Should().BeNull();
    }

    /// <summary>The setting named UTC refuses an offset-bearing deadline.</summary>
    [Fact]
    public void EnabledState_RefusesANonUtcDeadline()
    {
        var options = new LegacyCredentialOptions
        {
            Enabled = true,
            EnabledUntilUtc = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(1)),
            DecryptionKey = TestKey,
        };

        options.Validate().Should().NotBeNull()
            .And.Contain(nameof(LegacyCredentialOptions.EnabledUntilUtc));
    }

    /// <summary>Key length and alphabet are both closed rather than interpreted leniently.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("0123")]
    [InlineData("Z123456789ABCDEFFEDCBA98765432100011223344556677")]
    public void EnabledState_RefusesMalformedKeyText(string key)
    {
        var options = new LegacyCredentialOptions
        {
            Enabled = true,
            EnabledUntilUtc = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero),
            DecryptionKey = key,
        };

        options.Validate().Should().NotBeNull()
            .And.Contain(nameof(LegacyCredentialOptions.DecryptionKey));
    }

    /// <summary>A degenerate Triple-DES key is refused even though its length and alphabet are correct.</summary>
    [Fact]
    public void EnabledState_RefusesADegenerateTripleDesKey()
    {
        byte[] repeated = Convert.FromHexString("0123456789ABCDEF");
        string weakKey = Convert.ToHexString([.. repeated, .. repeated, .. repeated]);
        TripleDES.IsWeakKey(Convert.FromHexString(weakKey)).Should().BeTrue(
            "the fixture is only meaningful while the platform still classifies this key as weak");

        var options = new LegacyCredentialOptions
        {
            Enabled = true,
            EnabledUntilUtc = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero),
            DecryptionKey = weakKey,
        };

        options.Validate().Should().NotBeNull()
            .And.Contain(nameof(LegacyCredentialOptions.DecryptionKey))
            .And.NotContain(weakKey, "a validation message must never carry the key material");
    }
}
