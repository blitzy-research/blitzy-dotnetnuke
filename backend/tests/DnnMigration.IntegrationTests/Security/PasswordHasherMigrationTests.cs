using DnnMigration.Domain.Abstractions.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Pins the boundary between the credential upgrade this solution really performs and the legacy credential
/// migration it cannot perform.
/// </summary>
/// <remarks>
/// <para>
/// Prose alone cannot keep that claim from returning, because the next author to write it will be
/// describing an intention that sounds reasonable. These tests make the two operations distinguishable in
/// executable form: what the scheme upgrades, it upgrades; what it cannot verify, it never promises to
/// upgrade.
/// </para>
/// <para>
/// The real registered hasher is exercised rather than a double. The implementation is internal to the
/// infrastructure assembly, but the contract is public domain, so it is resolved from the composed
/// container exactly as the application resolves it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PasswordHasherMigrationTests
{
    /// <summary>A cost below the hasher's own, so a value stored at it is genuinely superseded.</summary>
    private const int SupersededWorkFactor = 10;

    /// <summary>An arbitrary credential; nothing in this suite depends on its content.</summary>
    private const string Password = "Migrat3d!Credential";

    /// <summary>
    /// A stored value shaped like the legacy reversible store: opaque base-64 ciphertext with none of the
    /// structure this scheme's own values carry.
    /// </summary>
    /// <remarks>
    /// The legacy provider was registered with an encrypted password format, so what sits in the column of
    /// an unmigrated installation is ciphertext of exactly this shape. It is not a secret and decrypts to
    /// nothing: the point is only that it is not one of this scheme's digests.
    /// </remarks>
    private const string LegacyStoredValue = "qX8Zt1nD3kLm9pQrS2vW4x==";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PasswordHasherMigrationTests"/> class.</summary>
    /// <param name="fixture">The shared composed host.</param>
    public PasswordHasherMigrationTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A value held under the legacy reversible scheme cannot be verified, whatever is submitted against
    /// it.
    /// </summary>
    /// <remarks>
    /// This is the fact the whole finding turns on. Every lazy-upgrade description begins with a successful
    /// verification against the stored value, so if that verification can never succeed, the upgrade can
    /// never begin - no ordering, no configuration and no additional collaborator changes that.
    /// </remarks>
    [Fact]
    public void Verify_AgainstALegacyStoredValue_NeverSucceeds()
    {
        IPasswordHasher hasher = Hasher();

        hasher.Verify(Password, LegacyStoredValue).Should().BeFalse();
        hasher.Verify(string.Empty, LegacyStoredValue).Should().BeFalse();
        hasher.Verify(LegacyStoredValue, LegacyStoredValue).Should().BeFalse(
            "a stored value is not a password, and offering one as both must not be treated as a match");
    }

    /// <summary>
    /// A legacy stored value is never reported as upgradable, so no caller is promised an upgrade that
    /// cannot be performed.
    /// </summary>
    /// <remarks>
    /// The contract now states this in terms. A <see langword="true"/> answer here would be worse than
    /// useless: it would tell a caller to pair the answer with a successful verification and re-hash the
    /// result, which is precisely the sequence that cannot complete for this input.
    /// </remarks>
    [Fact]
    public void NeedsRehash_ForALegacyStoredValue_DoesNotPromiseAnUpgrade()
    {
        Hasher().NeedsRehash(LegacyStoredValue).Should().BeFalse(
            "the upgrade this member reports is a cost increase on this scheme's own values, not a legacy detector");
    }

    /// <summary>
    /// A value this scheme produced at a superseded cost verifies and is reported as upgradable, which is
    /// the upgrade that genuinely exists.
    /// </summary>
    /// <remarks>
    /// The other side of the boundary. Both halves are asserted in one test on purpose: an upgrade is only
    /// meaningful if the value can also be verified, because the plaintext needed to re-hash it arrives
    /// only with a successful verification.
    /// </remarks>
    [Fact]
    public void ASupersededCostValue_VerifiesAndIsReportedAsUpgradable()
    {
        IPasswordHasher hasher = Hasher();

        // Enhanced and pre-hashed exactly as the production hasher writes, so the only difference from a
        // current credential is its cost. A plain hash would not verify at all and would prove nothing.
        string superseded = BCrypt.Net.BCrypt.EnhancedHashPassword(
            Password,
            SupersededWorkFactor,
            BCrypt.Net.HashType.SHA384);

        hasher.Verify(Password, superseded).Should().BeTrue();
        hasher.NeedsRehash(superseded).Should().BeTrue();
    }

    /// <summary>A value written at the current cost verifies and is reported as needing nothing.</summary>
    /// <remarks>
    /// Without this, the previous test would be satisfied by a member that answered <c>true</c> for every
    /// input, which would re-hash a current credential on every single sign-in.
    /// </remarks>
    [Fact]
    public void ACurrentValue_VerifiesAndNeedsNoUpgrade()
    {
        IPasswordHasher hasher = Hasher();
        string current = hasher.Hash(Password);

        hasher.Verify(Password, current).Should().BeTrue();
        hasher.NeedsRehash(current).Should().BeFalse();
    }

    /// <summary>
    /// The decoy the timing defence publishes is a current-cost value that nothing matches, and it is never
    /// reported as upgradable.
    /// </summary>
    [Fact]
    public void TheDecoy_IsACurrentCostValueThatMatchesNothing()
    {
        IPasswordHasher hasher = Hasher();
        string decoy = hasher.UnmatchableHash;

        decoy.Should().NotBeNullOrWhiteSpace();
        hasher.Verify(Password, decoy).Should().BeFalse();
        hasher.Verify(string.Empty, decoy).Should().BeFalse();
        hasher.NeedsRehash(decoy).Should().BeFalse();
    }

    /// <summary>Resolves the registered hasher from the composed container.</summary>
    /// <returns>The application's own credential hasher.</returns>
    private IPasswordHasher Hasher() => _fixture.Services.GetRequiredService<IPasswordHasher>();
}
