namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for assigning a user to a security role: the user to assign, the optional dates
/// that bound the membership, and whether the assignment should notify that user.
/// </summary>
/// <remarks>
/// <para>
/// The measured legacy authority is a single statement. <c>Website/admin/Security/SecurityRoles.ascx.vb</c>
/// lines 528 to 542 parse the two date textboxes - substituting the sentinel date whenever a box is
/// blank - and then call
/// <c>RoleController.AddUserRole(User, Role, PortalSettings, datEffectiveDate, datExpiryDate, UserId, chkNotify.Checked)</c>.
/// That seven-argument member is declared at <c>Library/Components/Security/Roles/RoleController.vb</c>
/// line 647, and its own parameter documentation at lines 641 and 642 settles what each argument is
/// for. Three of the seven are not caller input at all: the role comes from the route, the ambient
/// per-request portal composite is replaced by the scoped tenant context, and <c>userId</c> is the
/// <em>assigning administrator</em> recorded for audit at line 656, which this migration reads from
/// the authenticated principal rather than from a request body. Four genuine inputs remain, and they
/// are the four members declared below.
/// </para>
/// <para>
/// <b>This contract deliberately does not project <c>UserRoleInfo</c>.</b> That legacy class is the
/// obvious-looking source and is the wrong one. <c>Library/Components/Users/UserRoleInfo.vb</c> lines
/// 42 and 43 declare <c>Public Class UserRoleInfo</c> / <c>Inherits RoleInfo</c>, so its surface is
/// its own eight properties plus the fifteen it inherits - twenty-three in all. Projecting them would
/// restate every role-definition column that <see cref="CreateRoleRequest"/> and
/// <see cref="UpdateRoleRequest"/> already own, and would let a caller edit the role while merely
/// adding a member to it. It would also carry members that are not input in any sense:
/// <c>FullName</c> and <c>Email</c> are grid display denormalisations, <c>UserRoleID</c> is an
/// identity column, and <c>IsTrialUsed</c> and <c>Subscribed</c> are facts the service records.
/// <c>UserRoleInfo</c> is the shape the legacy screen <em>read</em>; a request is a command, so this
/// contract carries only what the screen <em>posted</em>.
/// </para>
/// <para>
/// The portal and the role are absent because both arrive in the route, which is what keeps them
/// authoritative: a tenant a caller can restate in the body is a tenant a caller can contradict, and
/// the identifier of the role being modified must have exactly one source. <c>UserRoleID</c> is
/// absent because the store assigns it and nothing needs it on the wire - the legacy grid's static
/// <c>datakeyfield="UserRoleID"</c> (<c>securityroles.ascx</c> line 56) is overwritten at runtime with
/// either <c>UserId</c> or <c>RoleId</c> (lines 244 and 251 of the code-behind), so even the legacy
/// delete path identified an assignment by the user-and-role pair rather than by the surrogate key.
/// </para>
/// <para>
/// <b>Every date rule belongs to the service, not here.</b> This type computes nothing. The service
/// reproduces <c>RoleController.vb</c> lines 530 to 554: an effective date already past is cleared and
/// an expiry already past is advanced to the current instant, an absent billing period yields no
/// expiry, the trial terms govern only while the trial is unconsumed, the frequency code then selects
/// the offset, and an existing assignment is revised rather than duplicated. A caller must therefore
/// not assume a subsequent read echoes what it submitted.
/// </para>
/// <para>
/// Both dates are wall-clock and unzoned. The underlying columns are SQL <c>datetime</c>, which stores
/// no offset, and the legacy screen parsed them with a culture-dependent, zone-free
/// <c>Date.Parse</c>, so <c>System.Text.Json</c> serialises these members without an offset and a
/// caller must not read one into them.
/// </para>
/// <para>
/// The type is an inert data carrier validated at the boundary by
/// <c>RoleAssignmentRequestValidator</c>. A body reaching
/// <c>POST roles/{roleId}/users</c> is refused there when its date interval is malformed; stored-state
/// rules remain enforced in <c>Application/Services/RoleService.cs</c>: an unknown user or role answers
/// <c>user.not_found</c> or <c>role.not_found</c>, a protected assignment answers
/// <c>role_assignment.protected</c>, and a malformed value raises <see cref="DnnMigration.Domain.Common.DomainException"/>,
/// which the API translates to a 400. What that costs is worth stating plainly: there is no
/// boundary-level field report for this contract, so a caller receives one reason at a time rather
/// than a per-field list.
/// </para>
/// </remarks>
// MIGRATION: both dates are DateTime? and deliberately not DateTimeOffset?. The columns are SQL
//            datetime, which has no zone component, and the legacy values were produced by a
//            zone-free parse, so an offset type would invent information the schema cannot store and
//            the existing rows do not carry - and would silently re-interpret every legacy row it
//            round-tripped. Widening to an offset type is a schema decision, not a contract one.
// MIGRATION: AAP 0.5.1.4 describes one contract serving both POST and DELETE on the role-members
//            endpoint. The implemented surface diverges, and the divergence is recorded here rather
//            than papered over: Api/Controllers/RolesController.cs line 257 binds this type from the
//            body of POST roles/{roleId}/users, while line 281 exposes removal as
//            DELETE roles/{roleId}/users/{userId}, which carries every identifier
//            in the route and therefore takes no body at all. This type is consequently exercised by
//            the assigning path; the shape
//            is unchanged either way, because the removal path needs no member this type declares.
public sealed class RoleAssignmentRequest
{
    /// <summary>
    /// Identifier of the user to assign to the role. Required.
    /// </summary>
    /// <remarks>
    /// Target column <c>UserRoles.UserID int NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 240, table declared at line 238).
    /// </remarks>
    // MIGRATION: the legacy screen offered two routes to one user and this contract accepts only the
    //            resolved identifier. A free-text box plus a validate button resolved a name through
    //            UserController.GetUserByName (SecurityRoles.ascx.vb lines 104 to 107, and again at
    //            lines 477 to 484), while a query-string identifier took the direct path (lines 417
    //            and 418); both converged on a resolved UserInfo before anything was saved, which is
    //            why line 647 takes an entity rather than a string. Name resolution is a repository
    //            read, so it stays in the service and out of this type (Rule T2, Rule T6): the client
    //            resolves a name through the user lookup endpoint and posts the integer it gets back.
    //            No dual-purpose "identifier or username" string is offered - a single field carrying
    //            two meanings is exactly the untyped contract this migration exists to remove.
    //
    // MIGRATION: non-nullable, because an assignment without a user has no meaning and the column is
    //            NOT NULL. Absence is therefore a validation failure, never a sentinel, and this type
    //            performs no absence test of its own. That restraint is deliberate: the legacy
    //            sentinel for a missing integer is -1 (Library/Components/Shared/Null.vb lines 41 to
    //            45), which the screen itself used as its "no user selected" marker at line 53, yet
    //            -1 and 0 are both real identifiers elsewhere in this very schema - Portals.PortalID
    //            is IDENTITY(-1,1) at 01.00.00 line 77 and Roles.RoleID is IDENTITY(0,1) at line 115.
    //            A "less than or equal to zero means absent" shortcut would therefore be wrong for a
    //            sibling identifier on this endpoint's own route. No range rule is stated in this file
    //            and none is applied at the boundary either, because this contract has no validator:
    //            an identifier matching no stored user is answered by the service with
    //            user.not_found.
    public int UserId { get; set; }

    /// <summary>
    /// Instant from which the membership takes effect, or <see langword="null"/> for immediately.
    /// </summary>
    /// <remarks>
    /// Target column <c>UserRoles.EffectiveDate datetime NULL</c>. The column is not part of the
    /// original baseline: it is added by a templated <c>ALTER TABLE ... ADD</c> at
    /// <c>03.02.03.SqlDataProvider</c> line 380 and re-added under a version guard at
    /// <c>04.00.04.SqlDataProvider</c> line 418, with matching stored-procedure parameters defaulting
    /// to <c>null</c> at lines 463 and 569 of the former and 501 and 607 of the latter.
    /// </remarks>
    // MIGRATION: only the terminal schema counts (Rule T4). EffectiveDate has zero occurrences in
    //            01.00.00.SqlDataProvider - the baseline UserRoles table declares five columns and
    //            this is not one of them - so a search confined to the baseline would have concluded
    //            the column does not exist and dropped a real member from this contract.
    //
    // MIGRATION: null carries the legacy blank-textbox meaning. Lines 528 to 533 of the screen
    //            substituted the sentinel date precisely when the box was empty, and the reciprocal
    //            read at line 281 rendered the box blank again whenever the value was sentinel, so
    //            null and the sentinel are the same state observed from two directions. The stored
    //            null is also load-bearing in SQL: the membership window is
    //            (EffectiveDate <= getdate() or EffectiveDate is null), measured at
    //            03.02.03.SqlDataProvider line 405 and 04.00.04.SqlDataProvider line 443, so an
    //            absent value means "already in force" rather than "unknown".
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// Instant at which the membership lapses, or <see langword="null"/> to let the service derive one
    /// from the role's trial and billing terms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Target column <c>UserRoles.ExpiryDate datetime NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 242).
    /// </para>
    /// <para>
    /// The legacy store encodes "no expiry" two different ways, and both survive this boundary
    /// unchanged. The sentinel minimum date is absence and is carried as <see langword="null"/>. The
    /// literal <c>9999-12-31</c> is not absence: it is an ordinary, in-range, externally observable
    /// <c>datetime</c> that a legacy consumer reading the same row expects to see, and it round-trips
    /// verbatim. Neither is ever converted into the other.
    /// </para>
    /// </remarks>
    // MIGRATION: the two no-expiry encodings come from the six-code expiry switch at
    //            RoleController.vb lines 540 to 547. Code "N" assigns the sentinel minimum date at
    //            line 541, which maps faithfully to null. Code "O" assigns
    //            New System.DateTime(9999, 12, 31) at line 542, which must NOT be normalised to null,
    //            to DateTime.MaxValue, or to any notion of "unbounded" - it is a stored value and
    //            changing it changes what a legacy reader sees. The legacy code agrees: Null.IsNull
    //            compares only the date component against the sentinel (Null.vb lines 222 to 224), so
    //            9999-12-31 reads as present, while the sentinel reads as absent.
    //
    // MIGRATION: AAP 0.5.1.2 and 0.7.4 name four billing codes, D, W, M and Y. The measured switch
    //            carries six: N and O precede them and are the two that matter for this member.
    //            Reported rather than quietly reconciled. No frequency member appears on this type -
    //            the codes belong to the role definition, so this file imports nothing from
    //            DnnMigration.Domain.Enums and names no billing or trial field.
    //
    // MIGRATION: null on this request means "derive an expiry from the role's terms", which is the
    //            one place a null here reads differently from the stored column's null. That is the
    //            measured legacy behaviour - a blank box got the role's own terms applied - and the
    //            derivation is the service's, never this type's. Nothing in this file computes a date:
    //            no offset arithmetic, no clock reading, and none of the legacy DateAdd calls at lines
    //            543 to 546, whose Microsoft.VisualBasic import at line 25 is removed outright.
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Whether the assigned user should be notified of the membership. Not persisted: no column backs
    /// this member.
    /// </summary>
    /// <remarks>
    /// A transient instruction rather than stored state. It reproduces the "Send Notification?"
    /// checkbox the legacy screen presented, and whether it can be acted upon is a property of the
    /// service, not of this contract - see the annotations below and on
    /// <c>Application/Abstractions/IRoleService.cs</c>.
    /// </remarks>
    // MIGRATION: retained as a caller instruction because it is measurably one. chkNotify.Checked is
    //            the seventh argument of the assignment call at SecurityRoles.ascx.vb line 542 and is
    //            passed again on both removal calls at lines 569 and 574; it is a declared parameter,
    //            notifyUser, at RoleController.vb line 647, documented at line 642, and consumed at
    //            lines 659 and 660. The legacy markup even pre-selects it - securityroles.ascx line 49
    //            declares the checkbox Checked="True" - so notifying was the legacy default rather
    //            than an edge case, which is why the switch is preserved rather than absorbed.
    //
    // MIGRATION: preserved as an instruction, NOT as a promise. Outbound mail is out of scope for this
    //            migration - Library/Components/Mail is an excluded subsystem per AAP 0.2.2.2 - so the
    //            application layer has no notifier to delegate to and does not currently act on this
    //            flag. A successful response must therefore never be read as evidence that a
    //            notification was sent. The two states are deliberately not conflated: declaring the
    //            member keeps the legacy affordance expressible and keeps adding the behaviour a
    //            purely additive change, while dropping it would delete a user-facing choice from the
    //            contract and make restoring it a breaking one. The reduction is recorded here and in
    //            MIGRATION_NOTES.md rather than silently absorbed (Minimal Change Clause items 4
    //            and 6, Rule T5).
    //
    // MIGRATION: bool, not bool?, because a checkbox always posts a definite value and line 647
    //            declares a non-nullable Boolean. No initialiser is given, so an omitted member is
    //            false: the legacy Checked="True" is a presentation default that now belongs to the
    //            Angular role-assignment screen, exactly as it belonged to the legacy markup, and the
    //            fail-safe wire default is not to notify anyone the caller did not ask to notify.
    public bool NotifyUser { get; set; }
}
