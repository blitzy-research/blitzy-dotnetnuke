namespace DnnMigration.Domain.Enums;

/// <summary>
/// Reports the outcome of an attempt to create a user, or to attach an existing user to a portal.
/// </summary>
/// <remarks>
/// <para>
/// Ported member for member from the legacy <c>DotNetNuke.Security.Membership.UserCreateStatus</c>:
/// every name and ordinal crosses over unchanged, and the ordinals are stated explicitly so that a
/// value the legacy application already persisted, logged or exchanged keeps its original meaning.
/// </para>
/// <para>
/// The zero value is not success. <see cref="AddUser"/> occupies ordinal zero, so
/// <c>default(UserCreateStatus)</c> evaluates to <see cref="AddUser"/> and never to
/// <see cref="Success"/> at ordinal thirteen. A caller must compare against <see cref="Success"/>
/// explicitly and must never infer success from a zero or falsiness test.
/// </para>
/// <para>
/// This enumeration is the failure-reason channel of the <c>Result&lt;T&gt;</c> returned by
/// user-creation operations; the legacy <c>ByRef</c> status argument is retired and no <c>out</c> or
/// <c>ref</c> parameter appears in any public API. The members are mutually exclusive outcomes, not
/// combinable bit positions, and must never be combined with a bitwise operator.
/// </para>
/// </remarks>
public enum UserCreateStatus
{
    /// <summary>
    /// The attempt is to create the user account itself, as distinct from attaching an existing
    /// account to a portal. This is an operation marker recording what was attempted - neither an
    /// error nor a report of success - and, holding ordinal zero, it is the value produced by
    /// <c>default(UserCreateStatus)</c>.
    /// </summary>
    AddUser = 0,

    // MIGRATION: UsernameAlreadyExists, DuplicateUserName (ordinal 5) and InvalidUserName (ordinal 11)
    // are three distinct members with overlapping meaning. The overlap is inherited: the
    // Duplicate/Invalid pair mirrors the ASP.NET membership creation-status vocabulary the legacy
    // provider wrapped, while UsernameAlreadyExists is DotNetNuke's own addition. All three are
    // retained as-is and deliberately not consolidated, because collapsing them would silently
    // change the outcome reported to any caller that distinguishes them.
    /// <summary>
    /// The requested user name is already taken. Reported by the DotNetNuke user-management path.
    /// </summary>
    UsernameAlreadyExists = 1,

    /// <summary>
    /// The user account exists and is already registered against the target
    /// portal, so there is nothing to attach.
    /// </summary>
    UserAlreadyRegistered = 2,

    /// <summary>
    /// The supplied email address is already associated with another account and
    /// the configured membership policy forbids reuse.
    /// </summary>
    DuplicateEmail = 3,

    /// <summary>
    /// The supplied membership provider user key is already associated with
    /// another account.
    /// </summary>
    DuplicateProviderUserKey = 4,

    /// <summary>
    /// The requested user name is already taken, as reported by the underlying
    /// membership provider. Retained alongside
    /// <see cref="UserCreateStatus.UsernameAlreadyExists"/> rather than merged
    /// with it; see the migration note above that member.
    /// </summary>
    DuplicateUserName = 5,

    /// <summary>
    /// The supplied password-recovery answer is not acceptable, for example
    /// because it is empty or breaches the configured length constraint.
    /// </summary>
    InvalidAnswer = 6,

    /// <summary>
    /// The supplied email address is malformed or otherwise fails validation.
    /// </summary>
    InvalidEmail = 7,

    /// <summary>
    /// The supplied password fails the configured password policy, for example
    /// on minimum length or required character composition.
    /// </summary>
    InvalidPassword = 8,

    /// <summary>
    /// The supplied membership provider user key is malformed or of an
    /// unexpected shape.
    /// </summary>
    InvalidProviderUserKey = 9,

    /// <summary>
    /// The supplied password-recovery question is not acceptable, for example
    /// because it is empty or breaches the configured length constraint.
    /// </summary>
    InvalidQuestion = 10,

    /// <summary>
    /// The supplied user name is malformed, for example because it is empty or
    /// contains characters the membership configuration disallows. Distinct
    /// from <see cref="UserCreateStatus.UsernameAlreadyExists"/> and
    /// <see cref="UserCreateStatus.DuplicateUserName"/>, which signal
    /// collision rather than malformation; see the migration note above
    /// <see cref="UserCreateStatus.UsernameAlreadyExists"/>.
    /// </summary>
    InvalidUserName = 11,

    /// <summary>
    /// The underlying membership provider reported a failure that it did not
    /// classify further.
    /// </summary>
    ProviderError = 12,

    /// <summary>
    /// The operation completed successfully. This is the only member that
    /// denotes success, and it is deliberately <b>not</b> the zero value of the
    /// enumeration.
    /// </summary>
    Success = 13,

    /// <summary>
    /// An error occurred that none of the other members describes.
    /// </summary>
    UnexpectedError = 14,

    /// <summary>
    /// The account was refused by an application-level rule rather than by
    /// validation, for example a registration restriction on the target portal.
    /// </summary>
    UserRejected = 15,

    /// <summary>
    /// The password and its confirmation do not match.
    /// </summary>
    PasswordMismatch = 16,

    /// <summary>
    /// The attempt is to attach an already-existing user account to a portal,
    /// as distinct from creating the account. Like
    /// <see cref="UserCreateStatus.AddUser"/> this is an operation marker
    /// recording what was attempted, not an error.
    /// </summary>
    AddUserToPortal = 17
}
