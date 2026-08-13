using System.Security.Cryptography;
using System.Text;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Security;

/// <summary>Configuration for the migration-only legacy membership credential verifier.</summary>
/// <remarks>
/// The decryption key is intentionally absent from every tracked configuration file. A deployment that
/// still has format-2 rows supplies it through an environment-backed secret for the bounded migration
/// window, removes it after all rows have been upgraded or reset, and rotates the legacy machine-key
/// material because its historical disclosure makes it compromised.
/// </remarks>
internal sealed class LegacyCredentialOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "LegacyCredentials";

    /// <summary>Gets or sets a value indicating whether legacy verification is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the instant, expressed in UTC, after which legacy verification refuses every
    /// representation regardless of <see cref="Enabled"/>.
    /// </summary>
    /// <remarks>
    /// THE SWITCH ALONE IS NOT A BOUNDED WINDOW, WHICH IS WHY THIS DEADLINE IS REQUIRED WHENEVER THE SWITCH
    /// IS ON. A deployment that turns compatibility on and forgets it has not migrated its credential store
    /// - it has permanently re-admitted a reversible representation on the ordinary sign-in path, which is
    /// the property the migration exists to remove.
    /// </remarks>
    public DateTimeOffset? EnabledUntilUtc { get; set; }

    /// <summary>Gets or sets the hexadecimal legacy machine-key decryption key.</summary>
    public string DecryptionKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy symmetric algorithm name.</summary>
    public string DecryptionAlgorithm { get; set; } = "3DES";

    /// <summary>Gets or sets the legacy one-way hash algorithm name.</summary>
    public string ValidationAlgorithm { get; set; } = "SHA1";

    /// <summary>Reports whether the named algorithm is the Triple-DES family whose keys can be weak.</summary>
    /// <param name="algorithm">The configured algorithm name.</param>
    /// <returns><see langword="true"/> when the name selects Triple-DES.</returns>
    private static bool RequiresTripleDesStrength(string algorithm) =>
        string.Equals(algorithm, "3DES", StringComparison.OrdinalIgnoreCase)
        || string.Equals(algorithm, "TripleDES", StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates the migration configuration without returning any secret material.</summary>
    /// <returns>An error that names only the invalid setting, or <see langword="null"/> when valid.</returns>
    public string? Validate()
    {
        if (!Enabled)
        {
            return null;
        }

        if (EnabledUntilUtc is null)
        {
            return "LegacyCredentials:EnabledUntilUtc is required while legacy verification is enabled.";
        }

        if (EnabledUntilUtc.Value.Offset != TimeSpan.Zero)
        {
            return "LegacyCredentials:EnabledUntilUtc must be expressed in UTC with a zero offset.";
        }

        if (!string.Equals(ValidationAlgorithm, "SHA1", StringComparison.OrdinalIgnoreCase))
        {
            return "LegacyCredentials:ValidationAlgorithm must be SHA1 for this DotNetNuke credential store.";
        }

        int requiredHexLength;
        if (string.Equals(DecryptionAlgorithm, "3DES", StringComparison.OrdinalIgnoreCase)
            || string.Equals(DecryptionAlgorithm, "TripleDES", StringComparison.OrdinalIgnoreCase))
        {
            requiredHexLength = 48;
        }
        else if (string.Equals(DecryptionAlgorithm, "AES", StringComparison.OrdinalIgnoreCase))
        {
            requiredHexLength = DecryptionKey.Length is 32 or 48 or 64
                ? DecryptionKey.Length
                : 0;
        }
        else
        {
            return "LegacyCredentials:DecryptionAlgorithm must be 3DES or AES.";
        }

        if (requiredHexLength == 0 || DecryptionKey.Length != requiredHexLength)
        {
            return "LegacyCredentials:DecryptionKey has an invalid length for the selected algorithm.";
        }

        byte[] key;
        try
        {
            key = Convert.FromHexString(DecryptionKey);
        }
        catch (FormatException)
        {
            return "LegacyCredentials:DecryptionKey must contain hexadecimal key material.";
        }

        // A KEY OF THE RIGHT LENGTH IS NOT NECESSARILY A KEY. Triple-DES degenerates to single DES when its
        // three sub-keys are not distinct, and the legacy machine-key material this option carries was
        // committed to a configuration file, so the population of keys reaching it is exactly the
        // population most likely to contain a hand-written degenerate value.
        try
        {
            if (RequiresTripleDesStrength(DecryptionAlgorithm)
                && System.Security.Cryptography.TripleDES.IsWeakKey(key))
            {
                return "LegacyCredentials:DecryptionKey is not an acceptable Triple-DES key.";
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return null;
    }
}

/// <summary>
/// Verifies legacy clear, SHA-1 membership-hash and machine-key-encrypted credential values without
/// exposing a plaintext recovery operation.
/// </summary>
internal sealed class LegacyCredentialVerifier : ILegacyCredentialVerifier, IDisposable
{
    private const int MaximumStoredValueLength = 2_048;
    private const int MaximumSaltLength = 512;

    private readonly bool _enabled;
    private readonly DateTime _enabledUntilUtc;
    private readonly IClock _clock;
    private readonly LegacyCipher _cipher;
    private readonly byte[] _decryptionKey;

    private bool _disposed;

    /// <summary>Initialises a new instance of the <see cref="LegacyCredentialVerifier"/> class.</summary>
    /// <param name="options">The migration-only credential options.</param>
    /// <param name="clock">The clock the absolute migration deadline is measured against.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or <paramref name="clock"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OptionsValidationException">Enabled migration options are invalid.</exception>
    public LegacyCredentialVerifier(IOptions<LegacyCredentialOptions> options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        LegacyCredentialOptions configured = options.Value;
        string? error = configured.Validate();
        if (error is not null)
        {
            throw new OptionsValidationException(
                LegacyCredentialOptions.SectionName,
                typeof(LegacyCredentialOptions),
                [error]);
        }

        _clock = clock;
        _enabled = configured.Enabled;

        // Read once and held, because the window must not be re-read from a source that could move it.
        _enabledUntilUtc = configured.Enabled && configured.EnabledUntilUtc is DateTimeOffset deadline
            ? deadline.UtcDateTime
            : DateTime.MinValue;

        _cipher = string.Equals(
            configured.DecryptionAlgorithm,
            "AES",
            StringComparison.OrdinalIgnoreCase)
            ? LegacyCipher.Aes
            : LegacyCipher.TripleDes;
        _decryptionKey = _enabled
            ? Convert.FromHexString(configured.DecryptionKey)
            : [];
    }

    /// <inheritdoc />
    public LegacyCredentialVerification Verify(
        string password,
        string storedValue,
        PasswordFormat format,
        string? passwordSalt)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(storedValue);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LegacyCredentialVerifier));
        }

        bool isCurrentRepresentation = format == PasswordFormat.Hashed
            && storedValue.StartsWith("$2", StringComparison.Ordinal);
        if (isCurrentRepresentation)
        {
            return LegacyCredentialVerification.Current;
        }

        bool isLegacyFormat = format is PasswordFormat.Clear
            or PasswordFormat.Hashed
            or PasswordFormat.Encrypted;
        if (!isLegacyFormat)
        {
            return LegacyCredentialVerification.Current;
        }

        // THE DEADLINE IS PART OF THE SAME FAIL-CLOSED GATE AS THE SWITCH, AND IT IS DELIBERATELY INCLUSIVE
        // OF THE CONFIGURED INSTANT. An operator naming a closing instant means "up to and including then";
        // excluding it would close the window a tick early for no benefit.
        bool withinMigrationWindow = _enabled && _clock.UtcNow <= _enabledUntilUtc;

        if (!withinMigrationWindow
            || storedValue.Length == 0
            || storedValue.Length > MaximumStoredValueLength
            || Encoding.UTF8.GetByteCount(password) > CredentialBounds.MaximumByteLength)
        {
            return LegacyCredentialVerification.Legacy(isMatch: false);
        }

        bool matches = format switch
        {
            PasswordFormat.Clear => VerifyClear(password, storedValue),
            PasswordFormat.Hashed => VerifyHashed(password, storedValue, passwordSalt),
            PasswordFormat.Encrypted => VerifyEncrypted(password, storedValue, passwordSalt),
            _ => false,
        };

        return LegacyCredentialVerification.Legacy(matches);
    }

    /// <summary>Clears retained migration key material when the singleton is disposed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_decryptionKey);
        _disposed = true;
    }

    private static bool VerifyClear(string password, string storedValue)
    {
        byte[] candidate = Encoding.Unicode.GetBytes(password);
        byte[] expected = Encoding.Unicode.GetBytes(storedValue);

        try
        {
            return CryptographicOperations.FixedTimeEquals(candidate, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static bool VerifyHashed(string password, string storedValue, string? passwordSalt)
    {
        if (!TryDecodeSalt(passwordSalt, out byte[] salt)
            || !TryDecodeBase64(storedValue, MaximumStoredValueLength, out byte[] expected))
        {
            CryptographicOperations.ZeroMemory(salt);
            return false;
        }

        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
        byte[] combined = new byte[salt.Length + passwordBytes.Length];
        byte[] actual = [];

        try
        {
            salt.CopyTo(combined, 0);
            passwordBytes.CopyTo(combined, salt.Length);
            actual = SHA1.HashData(combined);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(combined);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private bool VerifyEncrypted(string password, string storedValue, string? passwordSalt)
    {
        if (!TryDecodeSalt(passwordSalt, out byte[] salt)
            || !TryDecodeBase64(storedValue, MaximumStoredValueLength, out byte[] ciphertext))
        {
            CryptographicOperations.ZeroMemory(salt);
            return false;
        }

        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
        byte[] plaintext = [];

        try
        {
            using SymmetricAlgorithm algorithm = CreateAlgorithm();
            algorithm.Key = _decryptionKey;
            algorithm.IV = new byte[algorithm.BlockSize / 8];
            algorithm.Mode = CipherMode.CBC;
            algorithm.Padding = PaddingMode.PKCS7;

            using ICryptoTransform decryptor = algorithm.CreateDecryptor();
            plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

            int randomPrefixLength = algorithm.BlockSize / 8;
            int expectedLength = randomPrefixLength + salt.Length + passwordBytes.Length;
            if (plaintext.Length != expectedLength)
            {
                return false;
            }

            ReadOnlySpan<byte> payload = plaintext.AsSpan(randomPrefixLength);
            return CryptographicOperations.FixedTimeEquals(payload[..salt.Length], salt)
                && CryptographicOperations.FixedTimeEquals(payload[salt.Length..], passwordBytes);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private SymmetricAlgorithm CreateAlgorithm() => _cipher switch
    {
        LegacyCipher.Aes => Aes.Create(),
        _ => TripleDES.Create(),
    };

    private static bool TryDecodeSalt(string? passwordSalt, out byte[] salt)
    {
        if (string.IsNullOrEmpty(passwordSalt)
            || passwordSalt.Length > MaximumSaltLength)
        {
            salt = [];
            return false;
        }

        return TryDecodeBase64(passwordSalt, MaximumSaltLength, out salt);
    }

    private static bool TryDecodeBase64(string value, int maximumLength, out byte[] decoded)
    {
        if (value.Length == 0 || value.Length > maximumLength)
        {
            decoded = [];
            return false;
        }

        try
        {
            decoded = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            decoded = [];
            return false;
        }
    }

    private enum LegacyCipher
    {
        TripleDes,
        Aes,
    }
}
