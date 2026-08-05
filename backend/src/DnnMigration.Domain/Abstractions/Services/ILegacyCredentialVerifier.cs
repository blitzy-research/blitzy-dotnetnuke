using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Verifies a bounded credential against a legacy ASP.NET membership representation during the
/// migration window.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this narrowly scoped verifier exists only to satisfy the frozen cut-over contract:
/// an account whose credential is still stored in a legacy format may authenticate once, after
/// which the caller immediately replaces the value with the current one-way BCrypt representation.
/// It is not a password-retrieval API and exposes no decryption operation or plaintext result.
/// </para>
/// <para>
/// Implementations must fail closed for disabled migration, malformed input, unsupported formats,
/// invalid key material and cryptographic failures. They must never log, return or retain the
/// submitted password, the stored representation, the salt or any migration key.
/// </para>
/// </remarks>
public interface ILegacyCredentialVerifier
{
    /// <summary>
    /// Determines whether a submitted credential matches a stored legacy representation.
    /// </summary>
    /// <param name="password">The submitted plaintext credential.</param>
    /// <param name="storedValue">The representation stored by the legacy membership provider.</param>
    /// <param name="format">The persisted legacy format discriminator.</param>
    /// <param name="passwordSalt">The base-64 salt stored alongside the representation.</param>
    /// <returns>
    /// A result that distinguishes a current representation from a recognised legacy one and, for
    /// the latter, reports whether the submitted credential matched.
    /// </returns>
    LegacyCredentialVerification Verify(
        string password,
        string storedValue,
        PasswordFormat format,
        string? passwordSalt);
}

/// <summary>
/// The non-secret outcome of examining one stored credential representation.
/// </summary>
/// <param name="IsLegacyCredential">
/// Whether the representation belongs to a legacy format rather than to the current hasher.
/// </param>
/// <param name="IsMatch">
/// Whether the submitted credential matched. Always <see langword="false"/> when
/// <paramref name="IsLegacyCredential"/> is <see langword="false"/>.
/// </param>
public readonly record struct LegacyCredentialVerification(
    bool IsLegacyCredential,
    bool IsMatch)
{
    /// <summary>Gets the outcome for a representation owned by the current hasher.</summary>
    public static LegacyCredentialVerification Current { get; } = new(
        IsLegacyCredential: false,
        IsMatch: false);

    /// <summary>Creates an outcome for a recognised legacy representation.</summary>
    /// <param name="isMatch">Whether the submitted credential matched.</param>
    /// <returns>The legacy-verification outcome.</returns>
    public static LegacyCredentialVerification Legacy(bool isMatch) => new(
        IsLegacyCredential: true,
        IsMatch: isMatch);
}
