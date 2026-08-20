namespace DnnMigration.Domain.Enums;

/// <summary>Reports the outcome of an attempt to create a user, or to attach an existing user to a portal.</summary>
/// <remarks>
/// Ported member for member from the legacy <c>DotNetNuke.Security.Membership.UserCreateStatus</c>: every
/// name and ordinal crosses over unchanged, and the ordinals are stated explicitly so that a value the
/// legacy application already persisted, logged or exchanged keeps its original meaning.
/// </remarks>
public enum UserCreateStatus
{
    /// <summary>
    /// The attempt is to create the user account itself, as distinct from attaching an existing account to
    /// a portal. This is an operation marker recording what was attempted - neither an error nor a report
    /// of success - and, holding ordinal zero, it is the value produced by
    /// <c>default(UserCreateStatus)</c>.
    /// </summary>
    AddUser = 0,

    /// <summary>The requested user name is already taken. Reported by the DotNetNuke user-management path.</summary>
    UsernameAlreadyExists = 1,

    /// <summary>
    /// The user account exists and is already registered against the target portal, so there is nothing to
    /// attach.
    /// </summary>
    UserAlreadyRegistered = 2,

    /// <summary>
    /// The supplied email address is already associated with another account and the configured membership
    /// policy forbids reuse.
    /// </summary>
    DuplicateEmail = 3,

    /// <summary>The supplied membership provider user key is already associated with another account.</summary>
    DuplicateProviderUserKey = 4,

    /// <summary>
    /// The requested user name is already taken, as reported by the underlying membership provider.
    /// Retained alongside <see cref="UserCreateStatus.UsernameAlreadyExists"/> rather than merged with it;
    /// see the migration note above that member.
    /// </summary>
    DuplicateUserName = 5,

    /// <summary>
    /// The supplied password-recovery answer is not acceptable, for example because it is empty or breaches
    /// the configured length constraint.
    /// </summary>
    InvalidAnswer = 6,

    /// <summary>The supplied email address is malformed or otherwise fails validation.</summary>
    InvalidEmail = 7,

    /// <summary>
    /// The supplied password fails the configured password policy, for example on minimum length or
    /// required character composition.
    /// </summary>
    InvalidPassword = 8,

    /// <summary>The supplied membership provider user key is malformed or of an unexpected shape.</summary>
    InvalidProviderUserKey = 9,

    /// <summary>
    /// The supplied password-recovery question is not acceptable, for example because it is empty or
    /// breaches the configured length constraint.
    /// </summary>
    InvalidQuestion = 10,

    /// <summary>
    /// The supplied user name is malformed, for example because it is empty or contains characters the
    /// membership configuration disallows.
    /// </summary>
    InvalidUserName = 11,

    /// <summary>The underlying membership provider reported a failure that it did not classify further.</summary>
    ProviderError = 12,

    /// <summary>
    /// The operation completed successfully. This is the only member that denotes success, and it is
    /// deliberately <b>not</b> the zero value of the enumeration.
    /// </summary>
    Success = 13,

    /// <summary>An error occurred that none of the other members describes.</summary>
    UnexpectedError = 14,

    /// <summary>
    /// The account was refused by an application-level rule rather than by validation, for example a
    /// registration restriction on the target portal.
    /// </summary>
    UserRejected = 15,

    /// <summary>The password and its confirmation do not match.</summary>
    PasswordMismatch = 16,

    /// <summary>
    /// The attempt is to attach an already-existing user account to a portal, as distinct from creating the
    /// account. Like <see cref="UserCreateStatus.AddUser"/> this is an operation marker recording what was
    /// attempted, not an error.
    /// </summary>
    AddUserToPortal = 17
}
