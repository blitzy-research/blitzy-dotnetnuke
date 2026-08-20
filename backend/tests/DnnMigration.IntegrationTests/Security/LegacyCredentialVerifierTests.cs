using System.Security.Cryptography;
using System.Text;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Pins the bounded legacy-credential migration verifier without committing any deployment key or
/// credential fixture.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LegacyCredentialVerifierTests
{
    private const string Password = "Migr8tion!Pass";
    private const string WrongPassword = "Migr8tion!Miss";

    private const string MeasuredVectorKey = "0123456789ABCDEFFEDCBA98765432100011223344556677";
    private const string MeasuredVectorPassword = "Legacy-Password-1!";
    private const string MeasuredVectorSalt = "AQIDBAUGBwgJCgsMDQ4PEA==";
    private const string MeasuredEncryptedVector =
        "8r7SIDbjbv8Ferz4WFdTfpLBhJ5Xh5IN5tQxLB7z2vjxPGEs7a2irr7mWa76avqHK4nWDpipvvJk+8ecRR/M1g==";

    private static readonly DateTime Now = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A clear legacy representation is compared exactly and reported as legacy.</summary>
    [Fact]
    public void ClearRepresentation_AcceptsOnlyTheExactCredential()
    {
        using LegacyCredentialVerifier verifier = CreateEnabledVerifier(out _);

        LegacyCredentialVerification accepted = verifier.Verify(
            Password,
            Password,
            PasswordFormat.Clear,
            passwordSalt: null);
        LegacyCredentialVerification refused = verifier.Verify(
            WrongPassword,
            Password,
            PasswordFormat.Clear,
            passwordSalt: null);

        accepted.Should().Be(LegacyCredentialVerification.Legacy(isMatch: true));
        refused.Should().Be(LegacyCredentialVerification.Legacy(isMatch: false));
    }

    /// <summary>The legacy SHA-1 membership representation uses salt plus UTF-16LE password bytes.</summary>
    [Fact]
    public void HashedRepresentation_ReproducesTheMembershipProviderByteContract()
    {
        using LegacyCredentialVerifier verifier = CreateEnabledVerifier(out _);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        string encodedSalt = Convert.ToBase64String(salt);
        string storedValue = CreateLegacyHash(Password, salt);

        try
        {
            verifier.Verify(Password, storedValue, PasswordFormat.Hashed, encodedSalt)
                .Should().Be(LegacyCredentialVerification.Legacy(isMatch: true));
            verifier.Verify(WrongPassword, storedValue, PasswordFormat.Hashed, encodedSalt)
                .Should().Be(LegacyCredentialVerification.Legacy(isMatch: false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    /// <summary>
    /// The reversible format can be verified during migration without exposing a plaintext recovery API.
    /// </summary>
    [Fact]
    public void EncryptedRepresentation_UsesTheDiscardedPrefixAndSaltContract()
    {
        using LegacyCredentialVerifier verifier = CreateEnabledVerifier(out string keyHex);
        (string storedValue, string encodedSalt) = CreateEncryptedFixture(Password, keyHex);

        verifier.Verify(Password, storedValue, PasswordFormat.Encrypted, encodedSalt)
            .Should().Be(LegacyCredentialVerification.Legacy(isMatch: true));
        verifier.Verify(WrongPassword, storedValue, PasswordFormat.Encrypted, encodedSalt)
            .Should().Be(LegacyCredentialVerification.Legacy(isMatch: false));
    }

    /// <summary>Disabled migration recognises legacy rows but never accepts one.</summary>
    [Theory]
    [InlineData(PasswordFormat.Clear, "legacy-clear")]
    [InlineData(PasswordFormat.Hashed, "bGVnYWN5LWhhc2g=")]
    [InlineData(PasswordFormat.Encrypted, "bGVnYWN5LWNpcGhlcnRleHQ=")]
    public void DisabledMigration_FailsClosed(PasswordFormat format, string storedValue)
    {
        using LegacyCredentialVerifier verifier = new(
            Options.Create(new LegacyCredentialOptions { Enabled = false }),
            FixedClock());

        LegacyCredentialVerification result = verifier.Verify(
            Password,
            storedValue,
            format,
            passwordSalt: "c2FsdA==");

        result.Should().Be(LegacyCredentialVerification.Legacy(isMatch: false));
    }

    /// <summary>Malformed legacy rows are ordinary refusals and never escape a cryptographic exception.</summary>
    [Theory]
    [InlineData(PasswordFormat.Hashed, "not-base64", "c2FsdA==")]
    [InlineData(PasswordFormat.Hashed, "bGVnYWN5", "not-base64")]
    [InlineData(PasswordFormat.Encrypted, "not-base64", "c2FsdA==")]
    [InlineData(PasswordFormat.Encrypted, "bGVnYWN5", "not-base64")]
    public void MalformedLegacyRepresentation_FailsClosed(
        PasswordFormat format,
        string storedValue,
        string passwordSalt)
    {
        using LegacyCredentialVerifier verifier = CreateEnabledVerifier(out _);

        Action verify = () => verifier.Verify(Password, storedValue, format, passwordSalt)
            .Should().Be(LegacyCredentialVerification.Legacy(isMatch: false));

        verify.Should().NotThrow();
    }

    /// <summary>Current BCrypt values remain exclusively owned by the current password hasher.</summary>
    [Fact]
    public void CurrentBcryptRepresentation_IsNotTreatedAsLegacy()
    {
        using LegacyCredentialVerifier verifier = new(
            Options.Create(new LegacyCredentialOptions { Enabled = false }),
            FixedClock());

        LegacyCredentialVerification result = verifier.Verify(
            Password,
            "$2a$12$synthetic-current-representation",
            PasswordFormat.Hashed,
            passwordSalt: null);

        result.Should().Be(LegacyCredentialVerification.Current);
    }

    /// <summary>
    /// The absolute deadline closes the window, and a restart or a re-read of configuration cannot renew
    /// it.
    /// </summary>
    /// <remarks>
    /// The refusal is reported as a LEGACY non-match rather than as a current representation, which is the
    /// property the sign-in path depends on: a recognised legacy row is paired with the hasher's decoy so
    /// that an unmigrated account costs what a migrated one costs.
    /// </remarks>
    [Fact]
    public void ExpiredWindow_FailsClosed()
    {
        using LegacyCredentialVerifier verifier = CreateEnabledVerifier(
            out _,
            enabledUntilUtc: new DateTimeOffset(Now.AddTicks(-1)));

        LegacyCredentialVerification result = verifier.Verify(
            Password,
            Password,
            PasswordFormat.Clear,
            passwordSalt: null);

        result.Should().Be(LegacyCredentialVerification.Legacy(isMatch: false));
    }

    /// <summary>The final configured instant is inside the window, not after it.</summary>
    [Fact]
    public void DeadlineInstant_RemainsEnabled()
    {
        using LegacyCredentialVerifier verifier = CreateEnabledVerifier(
            out _,
            enabledUntilUtc: new DateTimeOffset(Now));

        LegacyCredentialVerification result = verifier.Verify(
            Password,
            Password,
            PasswordFormat.Clear,
            passwordSalt: null);

        result.Should().Be(LegacyCredentialVerification.Legacy(isMatch: true));
    }

    /// <summary>
    /// An encrypted representation produced by the real legacy provider is accepted only by the credential
    /// that produced it.
    /// </summary>
    /// <remarks>
    /// The hexadecimal key is test-vector material only. It is unrelated to every deployment and is
    /// intentionally obvious; production obtains its value exclusively from the deployment secret store.
    /// </remarks>
    [Fact]
    public void IndependentlyProducedEncryptedVector_AcceptsOnlyTheMatchingCredential()
    {
        using LegacyCredentialVerifier verifier = CreateVerifier(MeasuredVectorKey);

        LegacyCredentialVerification accepted = verifier.Verify(
            MeasuredVectorPassword,
            MeasuredEncryptedVector,
            PasswordFormat.Encrypted,
            MeasuredVectorSalt);
        LegacyCredentialVerification refused = verifier.Verify(
            "wrong-credential",
            MeasuredEncryptedVector,
            PasswordFormat.Encrypted,
            MeasuredVectorSalt);

        accepted.Should().Be(LegacyCredentialVerification.Legacy(isMatch: true));
        refused.Should().Be(LegacyCredentialVerification.Legacy(isMatch: false));
    }

    /// <summary>A discriminator outside the legacy set is not claimed as legacy and never throws.</summary>
    /// <remarks>
    /// The membership column is a plain integer, so a row can hold a value no format names. Claiming such a
    /// row as legacy would route it into the bounded comparison it has no rule for; disclaiming it leaves
    /// it to the current hasher, which refuses it.
    /// </remarks>
    [Fact]
    public void UnsupportedFormatDiscriminator_IsNotTreatedAsLegacy()
    {
        using LegacyCredentialVerifier verifier = CreateEnabledVerifier(out _);

        Func<LegacyCredentialVerification> verify = () => verifier.Verify(
            Password,
            "bGVnYWN5",
            (PasswordFormat)99,
            passwordSalt: "c2FsdA==");

        verify.Should().NotThrow();
        verify().Should().Be(LegacyCredentialVerification.Current);
    }

    /// <summary>
    /// An unusable deployment key is refused while the window is being configured, not silently at the
    /// first sign-in.
    /// </summary>
    /// <remarks>
    /// MIGRATION: a withdrawn parallel implementation answered a malformed key with a quiet refusal on
    /// every verification. Refusing at construction is the stronger behaviour and is what is kept: a
    /// deployment that mistyped its key learns while the application is starting, rather than by way of a
    /// migration window that appears to be open and admits nobody.
    /// </remarks>
    [Fact]
    public void InvalidDeploymentKey_IsRefusedAtConstruction()
    {
        Action construct = () => _ = new LegacyCredentialVerifier(
            Options.Create(new LegacyCredentialOptions
            {
                Enabled = true,
                EnabledUntilUtc = new DateTimeOffset(Now.AddDays(1)),
                DecryptionKey = "not-a-key",
            }),
            FixedClock());

        construct.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().NotContain("not-a-key");
    }

    private static LegacyCredentialVerifier CreateEnabledVerifier(
        out string keyHex,
        DateTimeOffset? enabledUntilUtc = null)
    {
        using TripleDES keySource = TripleDES.Create();
        keySource.GenerateKey();
        keyHex = Convert.ToHexString(keySource.Key);

        return CreateVerifier(keyHex, enabledUntilUtc);
    }

    private static LegacyCredentialVerifier CreateVerifier(
        string keyHex,
        DateTimeOffset? enabledUntilUtc = null)
    {
        return new LegacyCredentialVerifier(
            Options.Create(new LegacyCredentialOptions
            {
                Enabled = true,
                EnabledUntilUtc = enabledUntilUtc ?? new DateTimeOffset(Now.AddDays(1)),
                DecryptionAlgorithm = "3DES",
                DecryptionKey = keyHex,
                ValidationAlgorithm = "SHA1",
            }),
            FixedClock());
    }

    /// <summary>
    /// Returns a clock pinned to <see cref="Now"/> so the migration window's boundary is a fact rather than
    /// a property of the moment the suite happened to run.
    /// </summary>
    /// <returns>A strict clock that answers one instant.</returns>
    private static IClock FixedClock()
    {
        var clock = new Mock<IClock>(MockBehavior.Strict);
        clock.SetupGet(subject => subject.UtcNow).Returns(Now);
        return clock.Object;
    }

    private static string CreateLegacyHash(string password, byte[] salt)
    {
        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
        byte[] combined = new byte[salt.Length + passwordBytes.Length];
        byte[] digest = [];

        try
        {
            salt.CopyTo(combined, 0);
            passwordBytes.CopyTo(combined, salt.Length);
            digest = SHA1.HashData(combined);
            return Convert.ToBase64String(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(combined);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static (string StoredValue, string EncodedSalt) CreateEncryptedFixture(
        string password,
        string keyHex)
    {
        byte[] key = Convert.FromHexString(keyHex);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
        byte[] plaintext = [];
        byte[] ciphertext = [];

        try
        {
            using TripleDES algorithm = TripleDES.Create();
            algorithm.Key = key;
            algorithm.IV = new byte[algorithm.BlockSize / 8];
            algorithm.Mode = CipherMode.CBC;
            algorithm.Padding = PaddingMode.PKCS7;

            int prefixLength = algorithm.BlockSize / 8;
            plaintext = new byte[prefixLength + salt.Length + passwordBytes.Length];
            RandomNumberGenerator.Fill(plaintext.AsSpan(0, prefixLength));
            salt.CopyTo(plaintext, prefixLength);
            passwordBytes.CopyTo(plaintext, prefixLength + salt.Length);

            using ICryptoTransform encryptor = algorithm.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
            return (Convert.ToBase64String(ciphertext), Convert.ToBase64String(salt));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }
}
