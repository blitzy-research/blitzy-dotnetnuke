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
// subsystem; ADMIN_ALERT was raised by host administration, which AAP 0.2.2.4 excludes.
// Publishing a name nothing can raise would be a placeholder, so none is published.
//
// MIGRATION: CORRECTION. An earlier revision of the paragraph above also listed HOST_ALERT as belonging
// to excluded host administration. THAT WAS WRONG, and it cost the migration an audit record: the single
// legacy site that raises HOST_ALERT is the TENANT INSTALLATION at PortalController.vb:L1140-L1141, which
// is squarely in scope and is implemented by PortalService.CreatePortalAsync. The claim is corrected
// rather than softened, and HostAlert is published below.
//
// MIGRATION: CORRECTION, AND A LARGER ONE. An earlier revision listed FIVE members as unraisable by this
// migration - TAB_CREATED, TAB_DELETED, TAB_SENT_TO_RECYCLE_BIN, TAB_RESTORED and MODULE_RESTORED -
// reasoning that none had a committed boundary because AAP 0.5.1.4 makes the page surface "deliberately
// narrow - GET /api/v1/portals/{id}/tabs and GET/PUT /api/v1/tabs/{id} only" and AAP 0.2.2.2 excludes the
// recycle-bin pages the events were observed on. THREE OF THE FIVE WERE WRONG, and the error cost the
// migration three legacy events.
//
// The mistake was to locate an operation by the legacy PAGE that performed it rather than by the state
// TRANSITION it made. Recycling and restoring are not separate operations in this solution: both are
// carried on the update request as the IsDeleted flag, which TabMappings.ApplyUpdate assigns and
// ModuleMappings.ApplyUpdate assigns, so the narrow PUT surface IS the committed boundary for all three.
// TAB_SENT_TO_RECYCLE_BIN (TabController.vb:L840 and L952), TAB_RESTORED (RecycleBin.ascx.vb:L280) and
// MODULE_RESTORED (RecycleBin.ascx.vb:L392) are therefore published below and raised on the transition.
// Until they were, a recycling and a restoration were both recorded as a plain update - so the two
// questions an operator most often brings to a page trail, "who took this down" and "who put it back",
// could not be answered from it at all.
//
// TWO OF THE FIVE WERE RIGHT and stay unpublished, for the reason originally given rather than a softened
// one: TAB_CREATED (ManageTabs.ascx.vb:L315) and TAB_DELETED (RecycleBin.ascx.vb:L205) describe CREATION
// and PERMANENT PURGE, and the narrow surface AAP 0.5.1.4 specifies has no POST and no DELETE for a page,
// so neither transition can occur. Publishing a name nothing can raise would be a placeholder - the same
// reason the paragraph above gives for the other omissions - and each becomes raisable in the same change
// that adds the operation it describes. Both decisions are recorded in MIGRATION_NOTES.md as well as here.
//
// MIGRATION: MODULE_SENT_TO_RECYCLE_BIN is a member of the legacy enumeration and is deliberately NOT
// published, on the strength of a measurement rather than an assumption: no legacy site in scope raises it.
// The legacy recycle bin audited a module's soft-deletion as MODULE_DELETED (RecycleBin.ascx.vb:L156) and
// reserved MODULE_SENT_TO_RECYCLE_BIN for a producer that does not appear in the surveyed trees. Recycling
// a whole module therefore keeps MODULE_DELETED, exactly as the legacy code did, narrowed by its Operation
// fact - and the asymmetry with the page vocabulary above is the legacy behaviour rather than an oversight.

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
    /// <remarks>
    /// MIGRATION: NARROWED. This name once covered every page change, including a recycling and a
    /// restoration; those now have <see cref="TabSentToRecycleBin"/> and <see cref="TabRestored"/>, so this
    /// one means a REVISION - a change to the page's own properties or its place in the tree. A request that
    /// repeats the delete flag a page already carries changes no state and is still a revision.
    /// </remarks>
    public const string TabUpdated = "TAB_UPDATED";

    /// <summary>A page was recycled. Legacy <c>TAB_SENT_TO_RECYCLE_BIN</c>.</summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: a verbatim member of the legacy enumeration, raised by <c>TabController.vb</c> at
    /// <c>L840</c> and <c>L952</c>. It is raised here on the TRANSITION from present to recycled, which the
    /// page update carries as its delete flag - so the committed boundary is the narrow <c>PUT</c> the page
    /// surface exposes, not the excluded recycle-bin page the legacy call site happened to sit behind.
    /// </para>
    /// <para>
    /// It exists because the alternative was a false economy. Recording a recycling as
    /// <see cref="TabUpdated"/> is not untrue, but it is useless: the one question a page trail is asked
    /// most - who took this page down, and when - cannot be answered by filtering a stream in which every
    /// title change looks the same as a removal.
    /// </para>
    /// </remarks>
    public const string TabSentToRecycleBin = "TAB_SENT_TO_RECYCLE_BIN";

    /// <summary>A recycled page was restored. Legacy <c>TAB_RESTORED</c>.</summary>
    /// <remarks>
    /// MIGRATION: a verbatim member of the legacy enumeration, raised by <c>RecycleBin.ascx.vb:L280</c>, and
    /// the counterpart of <see cref="TabSentToRecycleBin"/>. Raised on the transition from recycled to
    /// present. Restoring a page makes it reachable again, which is a visibility change worth its own name
    /// for the same reason the recycling is.
    /// </remarks>
    public const string TabRestored = "TAB_RESTORED";

    /// <summary>An account was granted a role. Legacy <c>USER_ROLE_CREATED</c>.</summary>
    /// <remarks>
    /// MIGRATION: NARROWED to an actual GRANT - a membership row that did not exist and now does. It once
    /// also covered a renewal or a date revision of an existing membership, which
    /// <see cref="UserRoleUpdated"/> now carries.
    /// </remarks>
    public const string UserRoleCreated = "USER_ROLE_CREATED";

    /// <summary>An existing role membership was renewed or its dates revised.</summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: NET-NEW, and the enumeration has no member to cite: the legacy enumeration declares
    /// USER_ROLE_CREATED and USER_ROLE_DELETED and nothing between them, because the legacy assignment
    /// screen recorded a renewal under the creation name (<c>RoleController.vb</c>). The name follows the
    /// legacy register so it reads alongside its two siblings.
    /// </para>
    /// <para>
    /// It exists because the shared name asserted something false. A renewal is not a grant: the member
    /// already held the role, and recording it as a creation makes a trail in which the same account appears
    /// to have been granted the same role repeatedly - so counting grants over-counts them, and finding when
    /// access was FIRST given becomes impossible. The <c>Renewed</c> fact remains on the record, so a reader
    /// filtering on either the name or the fact sees the same thing.
    /// </para>
    /// </remarks>
    public const string UserRoleUpdated = "USER_ROLE_UPDATED";

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

    /// <summary>
    /// A credential accepted through the bounded legacy verifier was replaced with BCrypt.
    /// </summary>
    /// <remarks>
    /// MIGRATION: NET-NEW. The legacy application never changed storage technology during sign-in, so no
    /// historical event name exists. The event records only the tenant, account and former format; it carries
    /// no password, stored representation, salt, replacement hash or deployment key.
    /// </remarks>
    public const string LegacyCredentialMigrated = "LEGACY_CREDENTIAL_MIGRATED";


    /// <summary>
    /// A host-level event of note occurred. Legacy <c>HOST_ALERT</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: raised for a TENANT INSTALLATION, alongside <see cref="PortalCreated"/>, because the
    /// legacy installation raised exactly this type and nothing else:
    /// <c>PortalController.vb:L1140-L1141</c> assigns
    /// <c>objEventLogInfo.LogTypeKey = ...EventLogType.HOST_ALERT.ToString</c> before the record is
    /// written at <c>L1157</c>.
    /// </para>
    /// <para>
    /// BOTH names are emitted for that one operation, and the redundancy is the point. An operator with an
    /// existing saved search or alert on <c>HOST_ALERT</c> keeps matching the event they have always
    /// matched, which is the whole reason this vocabulary is preserved verbatim; and a reader who wants to
    /// know specifically that a tenant appeared gets the enumeration's own accurate member,
    /// <see cref="PortalCreated"/>, rather than having to infer it from a coarse alert. Emitting only one
    /// of the two silently breaks one of those two readers, and which one depends on a judgement about
    /// legacy intent that this migration is not entitled to make on their behalf.
    /// </para>
    /// <para>
    /// Nothing else in this solution raises it. The other legacy uses of this type belong to host
    /// administration, which AAP 0.2.2.4 excludes, so a second producer would be inventing an event rather
    /// than porting one.
    /// </para>
    /// </remarks>
    public const string HostAlert = "HOST_ALERT";

    /// <summary>A module instance or its content was changed. Legacy <c>MODULE_UPDATED</c>.</summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: a verbatim member of the legacy enumeration, at
    /// <c>EventLogController.vb:L38-L77</c>. This is a CORRECTION of provenance as well as an addition of
    /// a name: the module service previously declared this string as a private constant of its own and
    /// cited <c>EventMessageProcessor.vb:L69</c> for it - a file AAP 0.2.2.1 excludes - which made an
    /// in-scope legacy event look like a borrowing from out-of-scope code. The name was always the
    /// enumeration's, and it is published here so that every producer draws it from one catalogue.
    /// </para>
    /// <para>
    /// It covers a change to the module's placement or settings and a change to its content by import.
    /// It deliberately does NOT cover an export, which changes nothing and has its own name, nor a
    /// deletion, which has <see cref="ModuleDeleted"/>. The <c>Operation</c> fact on the record narrows it
    /// further.
    /// </para>
    /// </remarks>
    public const string ModuleUpdated = "MODULE_UPDATED";

    /// <summary>A module instance was removed. Legacy <c>MODULE_DELETED</c>.</summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: a verbatim member of the legacy enumeration, raised by the legacy recycle bin at
    /// <c>RecycleBin.ascx.vb:L156</c>. The recycle-bin surface itself is excluded, but the DELETION it
    /// audited is not: this solution removes a module through its own committed boundary, so the event has
    /// a real producer and is published.
    /// </para>
    /// <para>
    /// MIGRATION: NARROWED TO THE MODULE. It once also covered the removal of one PLACEMENT from one page,
    /// after which the module itself still exists and is still placed elsewhere -
    /// <see cref="ModulePlacementDeleted"/> now carries that. It covers a whole-module recycling, which
    /// keeps this name because no legacy site in scope raises MODULE_SENT_TO_RECYCLE_BIN; the
    /// <c>Operation</c> fact narrows it.
    /// </para>
    /// </remarks>
    public const string ModuleDeleted = "MODULE_DELETED";

    /// <summary>One placement of a module was removed from one page, leaving the module itself in place.</summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: NET-NEW, because the distinction is net-new. The legacy recycle bin removed modules, not
    /// placements, so the enumeration has no member for a placement and none can be cited. The name follows
    /// the legacy register so it reads alongside the other <c>MODULE_*</c> members.
    /// </para>
    /// <para>
    /// It exists because <see cref="ModuleDeleted"/> asserted something false. Removing a named placement
    /// hard-deletes one <c>TabModules</c> row; the module survives, its content survives, and every other
    /// placement of it survives - so a trail claiming the module was deleted sent anyone investigating to
    /// look for a module that is still there, and made a genuine module deletion indistinguishable from the
    /// far more common act of taking a module off one page.
    /// </para>
    /// </remarks>
    public const string ModulePlacementDeleted = "MODULE_PLACEMENT_DELETED";

    /// <summary>A recycled module was restored. Legacy <c>MODULE_RESTORED</c>.</summary>
    /// <remarks>
    /// MIGRATION: a verbatim member of the legacy enumeration, raised by <c>RecycleBin.ascx.vb:L392</c>, and
    /// the counterpart of a recycling recorded as <see cref="ModuleDeleted"/>. Raised on the TRANSITION from
    /// recycled to present, which the module update carries as its delete flag - so the committed boundary is
    /// the module <c>PUT</c>, not the excluded recycle-bin page the legacy call site sat behind. Without it, a
    /// restoration was recorded as a plain update and a module reappearing on a live page left no trace of who
    /// had brought it back.
    /// </remarks>
    public const string ModuleRestored = "MODULE_RESTORED";

    /// <summary>A module's content was exported.</summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: NET-NEW, and the enumeration has no member to cite because the legacy export page wrote
    /// no audit record at all - <c>Website/admin/Modules/Export.ascx.vb</c> contains no <c>AddLog</c>
    /// call. The name follows the legacy register so it reads alongside the other <c>MODULE_*</c> members.
    /// </para>
    /// <para>
    /// It exists because the alternative was worse. An export was previously recorded as
    /// <see cref="ModuleUpdated"/>, which states that a module changed when nothing changed - a false
    /// record in a trail whose value is that it is believed. Reading a module's content out of the system
    /// is worth auditing on its own terms, so the operation keeps its record and gets an honest name.
    /// </para>
    /// </remarks>
    public const string ModuleExported = "MODULE_EXPORTED";
}
