// MIGRATION: every name published below is the VERBATIM member name of the legacy
// DotNetNuke.Services.Log.EventLog.EventLogController.EventLogType enumeration, measured at
// Library/Components/Providers/Logging/Event Logging/EventLogController.vb:L38-L77. The names are
// reproduced character for character, in upper snake case, because they are the stable identifiers an
// existing operator already greps for and already has saved searches and alerts against. Renaming them
// to a modern register - "portal.created", say - would be a silent, breaking change to the one part of
// the audit trail that outlives the mechanism carrying it, so the wording is preserved even though the
// transport is not.
//
// MIGRATION: the legacy transport is NOT preserved and is not reproducible. The legacy path constructed a
// LogInfo, populated it with the portal identifier, portal name, filtered user name and user identifier,
// set LogTypeKey to one of these members, and handed the whole thing to
// EventLogController.AddLog, which persisted it through the logging PROVIDER FAMILY that
// AAP 0.2.2.2 places out of scope. What survives is the vocabulary and the facts; what changes is that
// the record is emitted as a structured log event rather than written to an EventLog table.
//
// MIGRATION: the enumeration declares forty-three members and only the subset this migration can
// legitimately raise is published here. The omissions are deliberate rather than incidental: the eight
// SCHEDULER_* and APPLICATION_* members belong to the excluded scheduling subsystem and the application
// lifetime of a Web Forms host; CACHE_REFRESHED belonged to the excluded cache provider family;
// PASSWORD_SENT_SUCCESS, PASSWORD_SENT_FAILURE and LOG_NOTIFICATION_FAILURE belonged to the excluded mail
// subsystem; ADMIN_ALERT and HOST_ALERT were raised by host administration, which AAP 0.2.2.4 excludes.
// Publishing a name nothing can raise would be a placeholder, so none is published.

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// The stable event names an <see cref="IAuditSink"/> receives, preserved verbatim from the legacy
/// event-log vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// These are identifiers, not prose. They are never localised, never lower-cased and never
/// reformatted, because their whole purpose is that a search which matched a legacy audit record still
/// matches its replacement. A new name is added here only when the legacy enumeration has no member for
/// the fact being recorded, and every such addition is called out individually below.
/// </para>
/// <para>
/// A caller passes one of these to <see cref="AuditEvent"/>. Nothing derives a name at run time - no
/// string concatenation, no enum <c>ToString</c>, no interpolation - so the set of names this
/// application can emit is closed and greppable.
/// </para>
/// </remarks>
public static class AuditEventNames
{
    /// <summary>An account was created. Legacy <c>USER_CREATED</c>.</summary>
    public const string UserCreated = "USER_CREATED";

    /// <summary>An account was removed. Legacy <c>USER_DELETED</c>.</summary>
    /// <remarks>
    /// MIGRATION: the legacy site raised this from <c>UserController.vb:L240</c>, passing the account
    /// name as the log key together with the account identifier and the ambient tenant composite.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: the legacy site raised this from <c>PortalController.vb:L1157</c> with a property list
    /// naming the alias, the template file, the home directory and the child-portal flag.
    /// </remarks>
    public const string PortalCreated = "PORTAL_CREATED";

    /// <summary>A tenant was removed. Legacy <c>PORTAL_DELETED</c>.</summary>
    public const string PortalDeleted = "PORTAL_DELETED";

    /// <summary>A page was revised. Legacy <c>TAB_UPDATED</c>.</summary>
    public const string TabUpdated = "TAB_UPDATED";

    /// <summary>An account was granted a role. Legacy <c>USER_ROLE_CREATED</c>.</summary>
    public const string UserRoleCreated = "USER_ROLE_CREATED";

    /// <summary>An account's role was withdrawn. Legacy <c>USER_ROLE_DELETED</c>.</summary>
    public const string UserRoleDeleted = "USER_ROLE_DELETED";

    /// <summary>A role was created. Legacy <c>ROLE_CREATED</c>.</summary>
    public const string RoleCreated = "ROLE_CREATED";

    /// <summary>A role was revised. Legacy <c>ROLE_UPDATED</c>.</summary>
    public const string RoleUpdated = "ROLE_UPDATED";

    /// <summary>A role was removed. Legacy <c>ROLE_DELETED</c>.</summary>
    public const string RoleDeleted = "ROLE_DELETED";

    /// <summary>
    /// A session was renewed by exchanging a refresh token.
    /// </summary>
    /// <remarks>
    /// MIGRATION: NET-NEW, because the mechanism is net-new. The legacy session lived in a
    /// Forms-authentication cookie and was never renewed, so the enumeration has no member for a renewal
    /// and none can be cited. The name follows the legacy register exactly so that it sits alongside the
    /// <c>LOGIN_*</c> family a reader is already scanning.
    /// </remarks>
    public const string SessionRenewed = "SESSION_RENEWED";

    /// <summary>
    /// A refresh token was presented for an account that is no longer eligible to hold a session.
    /// </summary>
    /// <remarks>
    /// MIGRATION: NET-NEW for the same reason as <see cref="SessionRenewed"/>. It records the security
    /// decision that a session was cut short because the account behind it had been locked, unapproved,
    /// removed from the tenant or deleted since the session began - a condition the legacy cookie could
    /// not detect at all, because nothing re-examined the account until the cookie lapsed.
    /// </remarks>
    public const string SessionRefused = "SESSION_REFUSED";

    /// <summary>A session was ended by the caller.</summary>
    /// <remarks>
    /// MIGRATION: NET-NEW. The legacy sign-out destroyed cookies and wrote no audit record, so there is
    /// no member to cite; recording it is a deliberate addition rather than a port.
    /// </remarks>
    public const string SessionEnded = "SESSION_ENDED";

    /// <summary>
    /// A stored credential could not be re-hashed at the current work factor.
    /// </summary>
    /// <remarks>
    /// MIGRATION: NET-NEW, and necessarily so - the legacy password store was reversibly encrypted with
    /// a fixed key, so it had no work factor to fall behind and no upgrade to fail. The name follows the
    /// legacy <c>*_FAILURE</c> convention. This event carries no credential material of any kind: not the
    /// password, not the old hash, not the new one.
    /// </remarks>
    public const string PasswordRehashFailure = "PASSWORD_REHASH_FAILURE";
}
