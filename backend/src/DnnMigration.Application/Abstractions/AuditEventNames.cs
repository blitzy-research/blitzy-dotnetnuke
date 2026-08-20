namespace DnnMigration.Application.Abstractions;

/// <summary>
/// The stable event names an <see cref="IAuditSink"/> receives, preserved verbatim from the legacy
/// event-log vocabulary.
/// </summary>
public static class AuditEventNames
{
    /// <summary>An account was created. Legacy <c>USER_CREATED</c>.</summary>
    public const string UserCreated = "USER_CREATED";

    /// <summary>An account was removed. Legacy <c>USER_DELETED</c>.</summary>
    public const string UserDeleted = "USER_DELETED";

    /// <summary>A host account signed in. Legacy <c>LOGIN_SUPERUSER</c>.</summary>
    public const string LoginSuperUser = "LOGIN_SUPERUSER";

    /// <summary>An ordinary account signed in. Legacy <c>LOGIN_SUCCESS</c>.</summary>
    public const string LoginSuccess = "LOGIN_SUCCESS";

    /// <summary>A credential was refused. Legacy <c>LOGIN_FAILURE</c>.</summary>
    public const string LoginFailure = "LOGIN_FAILURE";

    /// <summary>A locked account was refused. Legacy <c>LOGIN_USERLOCKEDOUT</c>.</summary>
    public const string LoginUserLockedOut = "LOGIN_USERLOCKEDOUT";

    /// <summary>An unapproved account was refused. Legacy <c>LOGIN_USERNOTAPPROVED</c>.</summary>
    public const string LoginUserNotApproved = "LOGIN_USERNOTAPPROVED";

    /// <summary>A tenant was created. Legacy <c>PORTAL_CREATED</c>.</summary>
    public const string PortalCreated = "PORTAL_CREATED";

    /// <summary>A tenant was removed. Legacy <c>PORTAL_DELETED</c>.</summary>
    public const string PortalDeleted = "PORTAL_DELETED";

    /// <summary>A page was revised. Legacy <c>TAB_UPDATED</c>.</summary>
    public const string TabUpdated = "TAB_UPDATED";

    /// <summary>A page was recycled. Legacy <c>TAB_SENT_TO_RECYCLE_BIN</c>.</summary>
    public const string TabSentToRecycleBin = "TAB_SENT_TO_RECYCLE_BIN";

    /// <summary>A recycled page was restored. Legacy <c>TAB_RESTORED</c>.</summary>
    public const string TabRestored = "TAB_RESTORED";

    /// <summary>An account was granted a role. Legacy <c>USER_ROLE_CREATED</c>.</summary>
    public const string UserRoleCreated = "USER_ROLE_CREATED";

    /// <summary>An existing role membership was renewed or its dates revised.</summary>
    public const string UserRoleUpdated = "USER_ROLE_UPDATED";

    /// <summary>An account's role was withdrawn. Legacy <c>USER_ROLE_DELETED</c>.</summary>
    public const string UserRoleDeleted = "USER_ROLE_DELETED";

    /// <summary>A role was created. Legacy <c>ROLE_CREATED</c>.</summary>
    public const string RoleCreated = "ROLE_CREATED";

    /// <summary>A role was revised. Legacy <c>ROLE_UPDATED</c>.</summary>
    public const string RoleUpdated = "ROLE_UPDATED";

    /// <summary>A role was removed. Legacy <c>ROLE_DELETED</c>.</summary>
    public const string RoleDeleted = "ROLE_DELETED";

    /// <summary>A session was renewed by exchanging a refresh token.</summary>
    /// <remarks>
    /// MIGRATION: NET-NEW, because the mechanism is net-new. The legacy session lived in a
    /// Forms-authentication cookie and was never renewed, so the enumeration has no member for a renewal
    /// and none can be cited.
    /// </remarks>
    public const string SessionRenewed = "SESSION_RENEWED";

    /// <summary>A refresh token was presented for an account that is no longer eligible to hold a session.</summary>
    /// <remarks>
    /// MIGRATION: NET-NEW for the same reason as <see cref="SessionRenewed"/>. It records the security
    /// decision that a session was cut short because the account behind it had been locked, unapproved,
    /// removed from the tenant or deleted since the session began - a condition the legacy cookie could not
    /// detect at all, because nothing re-examined the account until the cookie lapsed.
    /// </remarks>
    public const string SessionRefused = "SESSION_REFUSED";

    /// <summary>A session was ended by the caller.</summary>
    /// <remarks>
    /// MIGRATION: NET-NEW. The legacy sign-out destroyed cookies and wrote no audit record, so there is no
    /// member to cite; recording it is a deliberate addition rather than a port.
    /// </remarks>
    public const string SessionEnded = "SESSION_ENDED";

    /// <summary>A stored credential could not be re-hashed at the current work factor.</summary>
    /// <remarks>
    /// MIGRATION: NET-NEW, and necessarily so - the legacy password store was reversibly encrypted with a
    /// fixed key, so it had no work factor to fall behind and no upgrade to fail. The name follows the
    /// legacy <c>*_FAILURE</c> convention.
    /// </remarks>
    public const string PasswordRehashFailure = "PASSWORD_REHASH_FAILURE";

    /// <summary>A credential accepted through the bounded legacy verifier was replaced with BCrypt.</summary>
    /// <remarks>
    /// MIGRATION: NET-NEW. The legacy application never changed storage technology during sign-in, so no
    /// historical event name exists. The event records only the tenant, account and former format; it
    /// carries no password, stored representation, salt, replacement hash or deployment key.
    /// </remarks>
    public const string LegacyCredentialMigrated = "LEGACY_CREDENTIAL_MIGRATED";

    /// <summary>A host-level event of note occurred. Legacy <c>HOST_ALERT</c>.</summary>
    /// <remarks>
    /// BOTH names are emitted for that one operation, and the redundancy is the point.
    /// </remarks>
    public const string HostAlert = "HOST_ALERT";

    /// <summary>A module instance or its content was changed. Legacy <c>MODULE_UPDATED</c>.</summary>
    public const string ModuleUpdated = "MODULE_UPDATED";

    /// <summary>A module instance was removed. Legacy <c>MODULE_DELETED</c>.</summary>
    /// <remarks>
    /// NARROWED TO THE MODULE. It once also covered the removal of one PLACEMENT from one page, after which
    /// the module itself still exists and is still placed elsewhere - <see cref="ModulePlacementDeleted"/>
    /// now carries that. It covers a whole-module recycling, which keeps this name because no legacy site
    /// in scope raises MODULE_SENT_TO_RECYCLE_BIN; the <c>Operation</c> fact narrows it.
    /// </remarks>
    public const string ModuleDeleted = "MODULE_DELETED";

    /// <summary>One placement of a module was removed from one page, leaving the module itself in place.</summary>
    /// <remarks>
    /// MIGRATION: NET-NEW, because the distinction is net-new. The legacy recycle bin removed modules, not
    /// placements, so the enumeration has no member for a placement and none can be cited.
    /// </remarks>
    public const string ModulePlacementDeleted = "MODULE_PLACEMENT_DELETED";

    /// <summary>A recycled module was restored. Legacy <c>MODULE_RESTORED</c>.</summary>
    public const string ModuleRestored = "MODULE_RESTORED";

    /// <summary>A module's content was exported.</summary>
    public const string ModuleExported = "MODULE_EXPORTED";

    /// <summary>An invitation code was submitted against an account and did not match any service.</summary>
    /// <remarks>
    /// MIGRATION: NET-NEW, and the enumeration has no member to cite: the legacy handler answered a failed
    /// redemption with an on-screen message and wrote no record at all, so an installation could be guessed
    /// at indefinitely and leave nothing behind.
    /// </remarks>
    /// <summary>An account's personal data was assembled and returned as a portability document.</summary>
    /// <remarks>
    /// MIGRATION: NET-NEW, and the legacy register has nothing to cite. The legacy administration had no
    /// data-portability affordance at all, so there was no operation to record; the name follows the
    /// <c>USER_*</c> convention of the members it sits beside.
    /// </remarks>
    public const string UserDataExported = "USER_DATA_EXPORTED";

    public const string ServiceCodeRedemptionFailure = "SERVICE_CODE_REDEMPTION_FAILURE";

    /// <summary>An invitation code was redeemed and granted at least one service.</summary>
    /// <remarks>
    /// MIGRATION: NET-NEW for the same reason as <see cref="ServiceCodeRedemptionFailure"/> - the legacy
    /// handler recorded nothing on success either, so a role grant obtained by code left no trace of how it
    /// had been obtained.
    /// </remarks>
    public const string ServiceCodeRedeemed = "SERVICE_CODE_REDEEMED";
}
