using System.Security.Cryptography;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>Proves the package-free invariants of the legacy credential migration options.</summary>
/// <remarks>
/// <para>
/// MIGRATION: THESE FACTS WERE WRITTEN AGAINST A SECOND OPTIONS TYPE AND ARE RE-POINTED AT THE ONE THAT
/// SURVIVED. Two independent revisions each introduced a settings type for the bounded legacy-credential
/// window - one publicly reachable from the Application layer and validated by a host-side
/// <c>IValidateOptions</c> registration, one internal to Infrastructure beside the verifier that consumes
/// it and validated while <c>AddInfrastructure</c> runs. The internal one is kept: the section carries a
/// deployment-supplied decryption key, so the type that holds it should not be reachable by anything that
/// does not need it, and validating it during registration already fails the start-up in the same way a
/// <c>ValidateOnStart</c> binding would. Every requirement the withdrawn type expressed is asserted here
/// against the surviving one, including the two that only it checked - the absolute deadline and the
/// Triple-DES key strength.
/// </para>
/// <para>
/// The surviving validator reports the FIRST failure rather than a collection, so the "enabled needs both"
/// fact is expressed as two facts. Splitting it is not a weakening: each requirement is now pinned
/// independently, and a change that dropped either check would fail its own fact rather than merely
/// reducing a count.
/// </para>
/// <para>
/// The hexadecimal key below is test-vector material only. It is unrelated to every deployment and is
/// intentionally obvious; production obtains its value exclusively from the deployment secret store.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// The key below is a documented weak Triple-DES value: its three sub-keys are identical, which reduces
    /// the cipher to single DES. It passes every shape check - forty-eight hexadecimal characters - so a
    /// validator that stopped at shape would accept it, and the legacy machine-key material this option
    /// carries is exactly the population most likely to contain a hand-written degenerate value.
    /// </remarks>
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
