using System.Text;

namespace DnnMigration.Application.Validation;

// ============================================================================
// MIGRATION AUDIT TRAIL
//
// THIS TYPE EXISTS BECAUSE ONE NUMBER MUST BE SHARED, NOT REPEATED.
// Before it existed, four credential entry points each decided independently how
// long a submitted password was allowed to be, and the four answers disagreed:
// the sign-in validator declared no ceiling at all, the portal-creation validator
// declared none on its administrator password, the user-creation validator argued
// explicitly against having one, and the password-change validator declared a
// character ceiling of its own. A bound that only some entry points apply is not a
// bound - an attacker simply submits through whichever one omits it - so the number,
// the unit it is measured in, the predicate that measures it and the message a
// caller sees are all declared exactly once, here, and every entry point uses them.
//
// The consumers are, exhaustively:
//   Validation/LoginRequestValidator.cs          - LoginRequest.Password
//   Validation/CreateUserRequestValidator.cs     - CreateUserRequest.Password
//   Validation/ChangePasswordRequestValidator.cs - NewPassword and CurrentPassword
//   Validation/CreatePortalRequestValidator.cs   - AdministratorPassword
//   Infrastructure/Security/BcryptPasswordHasher.cs - defence in depth on both
//                                                     Hash and Verify
//
// WHY THE UNIT IS BYTES AND NOT CHARACTERS. The hashing algorithm consumes the
// UTF-8 encoding of the credential, not its UTF-16 characters, so a ceiling
// expressed in characters does not bound what the algorithm is actually handed. The
// two units differ by up to a factor of four: a single emoji is one grapheme, two
// UTF-16 chars and four UTF-8 bytes. Measuring the same quantity the algorithm
// measures is what makes the bound meaningful rather than approximately right.
//
// WHY THE NUMBER IS A CONSTANT AND NOT A CONFIGURATION SETTING. A security bound
// that configuration can raise is not a bound. This is the same reasoning the work
// factor in Infrastructure/Security/BcryptPasswordHasher.cs already applies to
// itself, and it is applied here for the same reason: surfacing the value as a
// bindable option would invite a deployment to weaken it silently, and no
// deployment has a legitimate reason to. Nothing about it needs to vary per
// installation, so nothing about it is made variable.
//
// WHY 256 AND NOT 72. BCrypt itself ignores everything after the first 72 bytes of
// its input, which is precisely the equivalence defect the enhanced hash and verify
// pair in Infrastructure/Security/BcryptPasswordHasher.cs removes: that pair
// digests the credential with SHA-384 first, so the whole of the input contributes
// and two distinct credentials sharing a 72-byte prefix no longer authenticate
// interchangeably. Rejecting anything past 72 bytes would therefore be solving a
// problem that is already solved, at the cost of refusing legitimate passphrases.
// The remaining reason to have a ceiling at all is to bound the work an
// unauthenticated caller can ask the server to do, and 256 bytes does that while
// leaving room for any passphrase a person will actually type. It also matches the
// widest credential-adjacent column in the terminal schema, Users.Username
// nvarchar(256), so the number is recognisable rather than arbitrary.
//
// THIS IS A DIVERGENCE FROM LEGACY BEHAVIOUR AND IS RECORDED AS ONE. DotNetNuke
// 4.9.0 imposed no server-side ceiling on a submitted password; the twenty-character
// limits visible in the legacy markup were client-side attributes of a schema that
// no longer stores the credential. A ceiling is introduced here deliberately, is a
// tightening, and is documented in MIGRATION_NOTES.md together with the enhanced
// hashing decision it accompanies. It is generous enough that no credential a human
// chooses can reach it, so the practical effect on existing accounts is nil.
//
// WHAT THIS TYPE DELIBERATELY DOES NOT DO. It states no minimum. The minimum length
// is legacy policy, is configured (Website/release.config:L242 requires seven), and
// is bound through Options/PasswordPolicyOptions.cs by the validators that are
// entitled to enforce it - which the sign-in validator is NOT, because the shipped
// Host and Administrator accounts carry four- and five-character passwords
// (01.00.00.SqlDataProvider:L7205 and :L7207) and a floor on the sign-in path would
// lock both of them out. A maximum and a minimum are therefore owned in different
// places on purpose, and this type owns only the maximum.
// ============================================================================

/// <summary>
/// The single shared upper bound applied to every clear-text credential this
/// application accepts, together with the predicate that measures it and the message
/// reported when it is exceeded.
/// </summary>
/// <remarks>
/// <para>
/// Every credential entry point applies <see cref="IsWithinMaximumByteLength"/> before
/// a credential can reach
/// <see cref="Domain.Abstractions.Services.IPasswordHasher"/>, and the hasher applies
/// it again as defence in depth. Both sides read the same constant, so the two can
/// never disagree.
/// </para>
/// <para>
/// The bound is expressed in UTF-8 bytes because that is the form the hashing
/// algorithm consumes. It is a compile-time constant rather than a configurable
/// setting, deliberately: a maximum that a deployment can raise provides no
/// guarantee. See the audit trail above for the full reasoning, including why the
/// value is 256 rather than the algorithm's own 72-byte significance limit.
/// </para>
/// <para>
/// This type states no minimum length. That value is legacy policy, varies by
/// configuration, and is bound through
/// <see cref="Options.PasswordPolicyOptions.MinRequiredPasswordLength"/> by the
/// validators entitled to enforce it.
/// </para>
/// </remarks>
public static class CredentialBounds
{
    /// <summary>
    /// The greatest number of UTF-8 bytes a clear-text credential may occupy.
    /// </summary>
    /// <remarks>
    /// Measured in bytes, not characters, because the hashing algorithm consumes the
    /// UTF-8 encoding. Declared <see langword="const"/> so no configuration source can
    /// raise it.
    /// </remarks>
    public const int MaximumByteLength = 256;

    /// <summary>
    /// The message reported when a credential exceeds <see cref="MaximumByteLength"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately identical at every entry point, so a caller cannot infer which
    /// endpoint it reached from the wording, and deliberately free of the submitted
    /// value, so no credential material is ever echoed back or written to a log.
    /// </remarks>
    public const string MaximumByteLengthMessage =
        "The password supplied is too long. A password may be at most 256 bytes when encoded as UTF-8.";

    /// <summary>
    /// Reports whether <paramref name="credential"/> is within
    /// <see cref="MaximumByteLength"/>.
    /// </summary>
    /// <param name="credential">
    /// The candidate clear-text credential. A <see langword="null"/> or empty value is
    /// reported as within the bound.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value occupies no more than
    /// <see cref="MaximumByteLength"/> UTF-8 bytes; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// An absent or empty credential passes this check on purpose. Requiredness is a
    /// separate rule with its own legacy-derived message, and a single omitted value
    /// must produce a single message rather than one complaint per rule that happens to
    /// look at it.
    /// </para>
    /// <para>
    /// The byte count is computed without allocating an encoded copy of the credential,
    /// so no additional copy of the clear text is created in order to measure it.
    /// </para>
    /// </remarks>
    public static bool IsWithinMaximumByteLength(string? credential)
    {
        if (string.IsNullOrEmpty(credential))
        {
            return true;
        }

        return Encoding.UTF8.GetByteCount(credential) <= MaximumByteLength;
    }
}
