using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;

// This contract replaces the whole public surface of
// Library/Components/Security/Roles/RoleController.vb, measured at 892 lines and
// 42 public members. Fourteen of those 42 were Shared (static) and are reborn here
// as instance members on an injected abstraction, per AAP 0.4.3. The annotations
// below record every place the migrated contract deliberately departs from the
// legacy behaviour, as AAP Rule T5 requires; each is also carried in
// MIGRATION_NOTES.md at the repository root.
//
// MIGRATION: `Imports Microsoft.VisualBasic` at RoleController.vb:L25 is removed. It
// MIGRATION: is the ONLY occurrence in the entire in-scope legacy surface, and the
// MIGRATION: DateAdd(DateInterval.Day, n, d), DateAdd(DateInterval.Month, n, d) and
// MIGRATION: DateAdd(DateInterval.Year, n, d) calls it supplied become d.AddDays(n),
// MIGRATION: d.AddDays(n * 7), d.AddMonths(n) and d.AddYears(n) inside
//            Services/RoleService.cs, selected by the frequency enumeration that the
//            domain layer owns. No date arithmetic is exposed on this contract, so no
//            caller above the application layer can perform it (AAP Rule T2).
//
// MIGRATION: the frequency code set has SIX measured members, not the four the plan
// MIGRATION: cites. RoleController.vb:L541-L546 reads: 'N' assigns Null.NullDate, so
// MIGRATION: never expires; 'O' assigns the far-future sentinel 9999-12-31, so
// MIGRATION: perpetual; 'D' is DateAdd by day and period; 'W' is DateAdd by day and
// MIGRATION: period times seven; 'M' is DateAdd by month; 'Y' is DateAdd by year. The
// MIGRATION: whole table is short-circuited at L537, where a period equal to
// MIGRATION: Null.NullInteger yields no expiry before the code is even examined. The
//            codes are load-bearing single-character data - the columns are declared
//            char(1) NULL at 01.00.00.SqlDataProvider:L120 and L122 - and are never
//            renamed. This is REPORTED as a refinement of the plan, not a correction
//            to it, and the enumeration itself is declared by the domain layer.
//
// MIGRATION: the trial-versus-billing selection at RoleController.vb:L521 is preserved
//            exactly: the trial terms govern only when the trial has not already been
//            consumed AND the trial frequency is not the 'N' code; otherwise the
//            billing terms govern. The 'N' code therefore does double duty as the
//            no-trial guard, which is why it may never be treated as an unset marker.
//
// MIGRATION: RoleController.vb:L530 and L533 normalise both dates before any offset is
// MIGRATION: applied, and both rules are preserved. An effective date already in the
// MIGRATION: past is reset to Null.NullDate - no start gate at all - and an expiry date
//            already in the past is reset to the current instant so that the offset
//            always runs forward from now rather than from a stale value. The current
//            instant is read from the clock abstraction the domain layer owns, never
//            from an ambient one, so the arithmetic is unit-testable.
//
// MIGRATION: RoleController.vb:L495-L496 does NOT delete a cancelled assignment when
//            the role carries a service fee and its trial has been used. It back-dates
//            the expiry to yesterday instead, commented in the source as retaining the
//            trial-used data. Preserved, and reported through an informational reason on
//            a SUCCESSFUL outcome so a caller can distinguish an expiry from a deletion.
//
// MIGRATION: the legacy removal reported success when the portal or the assignment could
//            not be found - L330-L347 leaves its flag true and silently does nothing - so
//            a caller could not tell a real removal from a no-op. This surface reports a
//            distinct failure reason instead. Documented behavioural difference.
//
// MIGRATION: nine legacy parameters typed PortalSettings (L647, L677, L695, L714, L741)
// MIGRATION: or PortalInfo (L764) are removed with the ambient per-request composite they
//            carried. Every member below takes its portal identifier explicitly; the
//            immutable, scoped tenant context that the domain layer owns supplies the
//            remaining per-request facts. No member infers a tenant from ambient state.
//
// MIGRATION: twelve legacy members took or returned RoleInfo, RoleGroupInfo,
// MIGRATION: UserRoleInfo or UserInfo. None of those types, and no domain entity,
//            appears here: requests and responses are data transfer objects and plain
//            identifiers, so no entity crosses the boundary in either direction.
//
// MIGRATION: the notifyUser flag on the four shared members (L647, L677, L695, L714) is
//            dropped. It sent an e-mail through a mail subsystem this migration excludes,
//            so no equivalent exists to delegate to. A deliberate functional reduction,
//            recorded rather than silently absorbed.
//
// MIGRATION: the SynchronizationMode flag (L849) and the SynchronizeRoles flag (L854) are
//            dropped. Both toggled the legacy membership and role provider
//            synchronisation model, which this migration replaces wholesale rather than
//            reproduces, so neither flag has anything left to switch.
//
// MIGRATION: the legacy removability predicate at L741 and L764 - two duplicated bodies
//            the source itself flags as a hack - is NOT reproduced as a public predicate.
//            Its measured rule blocks exactly two cases: stripping the portal's
//            designated administrator of that portal's administrator role, and removing
//            ANY user from that portal's registered-users role. Both are enforced inside
//            the removal member and reported as role_assignment.protected, so no caller
//            above the application layer can settle the question for itself (Rule T2).
//
// MIGRATION: the three legacy members returning a raw single-dimension array of names
//            (L194, L240, L859) and every legacy untyped, non-generic collection return
//            become read-only generic sequences. Neither a raw array nor a non-generic
//            collection appears anywhere on this surface (AAP 0.5.1.10).
//
// MIGRATION: duplicate and overload families are collapsed. The two portal role listings
//            (L146, L854) join the group-filtered listing (L224) as one member; the two
//            byte-identical listings of a role's users (L457, L874) become one; four
//            removal overloads (L330, L677, L695, L714) become one; three assignment
//            overloads (L277, L295, L647) become one; and three listings of a user's
//            assignments (L376, L392, L408) join L240, L425 and L859 as one.
//
// MIGRATION: the paid-services wrappers (L864, L869, L879, L884) are not reproduced.
//            Measured, each is a thin alias of a role or assignment member above, and the
//            member-services screen they served folds into the user feature's membership
//            settings screen, so re-declaring them under service names would fork one
//            behaviour across two contracts.
//
// MIGRATION: the all-portals role read (L208) is not exposed. Measured, it reaches the
// MIGRATION: data layer with the portal identifier set to Null.NullInteger, returning
//            every tenant's roles in a single answer. Tenant isolation is a preservation
//            requirement of this migration, so every member below demands an explicit
//            portal identifier and none offers an unscoped variant.
//
// MIGRATION: no numeric or date sentinel survives on this surface. Null.NullInteger is
// MIGRATION: minus one and Null.NullDate is DateTime.MinValue (Null.vb:L41-L45 and
// MIGRATION: L66-L70), yet Roles.RoleID is IDENTITY(0,1) at
// MIGRATION: 01.00.00.SqlDataProvider:L115 - so 0 is a real role identifier - and
// MIGRATION: Portals.PortalID is IDENTITY(-1,1), making minus one simultaneously the
// MIGRATION: legacy absence marker and the first real portal. RoleGroupID additionally
// MIGRATION: uses Null.NullInteger to mean "no group", and L224 documents minus one as
//            "all roles for the portal". Absence is therefore carried by nullable types
//            alone: an absent role group is a null int, an absent date a null DateTime.
//            An implementation must never coalesce minus one or 0 to null, and must never
//            read either value as meaning absent (AAP Rule T7, 0.7.2).
//
// MIGRATION: every projection named on this contract is one AAP 0.4.1.1 itemises, and
// MIGRATION: that constraint is deliberate rather than stylistic. A projection the plan
// MIGRATION: does not name has no authoring owner in this migration, so naming one here
// MIGRATION: would leave the backend permanently uncompilable however faithful its shape
//            was. Listing a role's members therefore projects the user list item the plan
//            names, and listing a user's roles projects the role list item, which is the
//            faithful shape in any case: the legacy assignment record inherits the role
// MIGRATION: record outright, declaring eight properties of its own over fifteen inherited
// MIGRATION: (UserRoleInfo.vb:L38-L128 over RoleInfo.vb).
//
// MIGRATION: consequently the effective and expiry dates are NOT carried on either
// MIGRATION: listing, and that is a functional reduction against the legacy grid, which
// MIGRATION: bound five columns at securityroles.ascx:L68-L84 - user identifier, role
// MIGRATION: identifier, display name, effective date and expiry date. The two dates
//            travel on the write path instead, on the assignment request, which is where
//            AAP 0.5.1.8 requires them. Restoring them to the read path requires the plan
//            to name an assignment projection; until it does, inventing one here would
//            trade a documented reduction for a broken build.
//
// MIGRATION: the single-assignment reader at RoleController.vb:L362 is not surfaced. Its
// MIGRATION: only measured in-scope consumer is the date-priming routine spanning
// MIGRATION: SecurityRoles.ascx.vb:L273-L303, which reads an existing assignment purely to
// MIGRATION: pre-fill two inputs and otherwise projects a proposed expiry from the role's
// MIGRATION: billing terms with a reduced four-code switch at L295-L301 - a presentation
//            layer recomputation of arithmetic that AAP Rule T2 places squarely inside the
//            application layer, and the likely origin of the four-code reading the plan
//            cites. Exposing the reader would invite that recomputation back above the
//            boundary; the authoritative expiry is computed once, on assignment, from the
//            full six-code table.

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Application-layer contract for the role aggregate - DotNetNuke's permission
/// grouping - together with role groups and user-to-role assignment.
/// </summary>
/// <remarks>
/// <para>
/// Scope. Three closely bound concerns share this one contract: the role itself,
/// including its paid-membership terms; the role group that classifies roles for
/// presentation; and the assignment that joins a user to a role for a period. They
/// are not separable, because the legacy screens treat them as one workflow and
/// because a role group cannot be deleted while it still classifies roles. There is
/// deliberately no separate role-group contract; the role-group endpoints are served
/// from here.
/// </para>
/// <para>
/// Not in this contract. Role <em>permissions</em> belong to the permission contract:
/// a role is the <em>subject</em> of a permission grant, never its store, so no
/// member here reads or writes a module or page permission. Caching is an
/// implementation concern behind the cache abstraction the domain layer owns, so no
/// member exposes a cache, a clear or a synchronise switch. Data access is reached
/// only through the repository abstractions the domain layer declares (AAP Rule T3),
/// which is why nothing on this surface names a persistence type.
/// </para>
/// <para>
/// Every member is asynchronous, carries the <c>Async</c> suffix and accepts a
/// trailing cancellation token, because every one of them performs input or output
/// (AAP Rule T6). No member declares an out-parameter or a by-reference parameter:
/// the thirty measured legacy signatures that mutated an argument while returning a
/// status are replaced by <see cref="Result"/> and <see cref="Result{T}"/>, which
/// carry the value and the reason together (AAP 0.7.4).
/// </para>
/// <para>
/// Reading an outcome. An <em>expected</em> failure - a missing role, a duplicate
/// name, a protected assignment - is returned as a failed result carrying a stable
/// reason code, never thrown; an unexpected exception is left to surface and is
/// translated once at the outermost boundary. On a single-item lookup a
/// <em>successful</em> result whose value is <see langword="null"/> means the item is
/// <em>absent</em>, which is a different answer from a failed lookup and must not be
/// collapsed into one (AAP Rule T7). A successful result may additionally carry an
/// informational reason, which is how the cancel-a-paid-assignment path reports that
/// it expired the assignment rather than deleting it.
/// </para>
/// <para>
/// Reason codes used by this contract, all stable and all lower-case:
/// <c>portal.not_found</c>, <c>role.not_found</c>, <c>role.name_duplicate</c>,
/// <c>role.create_failed</c>, <c>role_group.not_found</c>,
/// <c>role_group.name_duplicate</c>, <c>role_group.in_use</c>, <c>user.not_found</c>,
/// <c>role_assignment.not_found</c>, <c>role_assignment.protected</c> and the
/// informational <c>role_assignment.expired_not_removed</c>.
/// </para>
/// <para>
/// Tenancy. Every member takes an explicit portal identifier and scopes every read
/// and write to it. A role, a role group or an assignment that exists in a different
/// portal is reported as not found rather than returned, which reproduces the legacy
/// screens' treatment of a cross-tenant identifier as a security violation. Where a
/// request object also carries a portal identifier, the parameter is authoritative
/// and a disagreement is a request-shape validation failure at the boundary, not a
/// reason code here.
/// </para>
/// <para>
/// Registration. Implemented by <c>Services/RoleService.cs</c> and registered by
/// <c>AddApplication()</c> as one of its seven scoped services. A scoped lifetime is
/// required: the implementation composes with the scoped repositories, the scoped
/// unit of work and the scoped tenant context, so a singleton registration would
/// capture one request's state and serve it to every later request.
/// </para>
/// </remarks>
public interface IRoleService
{
    /// <summary>
    /// Lists one page of the roles defined in a portal, optionally narrowed to a
    /// single role group.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal whose roles are listed. Authoritative for tenant
    /// scoping: no role belonging to any other portal may appear in the answer.
    /// </param>
    /// <param name="request">
    /// Paging coordinates and the optional free-text filter for the listing. The
    /// page-index base is fixed and documented by the paging envelope this member
    /// returns, and is deliberately not restated here.
    /// </param>
    /// <param name="roleGroupId">
    /// Identifier of the role group to restrict the listing to, or
    /// <see langword="null"/> for every role in the portal irrespective of group.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying one page of roles, empty when the portal has
    /// none or when the requested page lies past the end of the set; a failed
    /// outcome carrying <c>portal.not_found</c> when no such portal exists, or
    /// <c>role_group.not_found</c> when <paramref name="roleGroupId"/> is supplied
    /// but names no group in that portal.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Consolidates three measured legacy members: the portal role listing at
    /// RoleController.vb:L146, its synchronisation-flagged twin at L854, and the
    /// group-filtered listing at L224. The legacy group filter used the integer
    /// absence sentinel to mean "no filter" - its own documentation at L224 reads
    /// "If -1 all roles for the portal are retrieved" - which is exactly why the
    /// filter is a nullable integer here.
    /// </para>
    /// <para>
    /// The projected item carries the columns the legacy grid bound, measured in
    /// <c>Website/admin/Security/roles.ascx</c>: name, description, service fee,
    /// billing frequency and period, trial fee, trial frequency and period, and the
    /// public and auto-assignment flags. Preserving the paid-membership fields is a
    /// functional-parity requirement, not an optional extra.
    /// </para>
    /// </remarks>
    Task<Result<PagedResult<RoleListItemDto>>> ListRolesAsync(
        int portalId,
        PagedRequest request,
        int? roleGroupId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one role of a portal in full, including its paid-membership terms.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the role. Authoritative for tenant
    /// scoping.
    /// </param>
    /// <param name="roleId">
    /// Identifier of the role to read. Any non-negative value is legitimate,
    /// including 0.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome whose value is the role, or a successful outcome whose
    /// value is <see langword="null"/> when the portal has no such role - absent is
    /// not the same answer as failed; or a failed outcome carrying
    /// <c>portal.not_found</c> when no such portal exists.
    /// </returns>
    /// <remarks>
    /// Replaces the identifier lookup at RoleController.vb:L163 and the by-name
    /// lookup at L179. The by-name form is not surfaced separately because its only
    /// measured use is the duplicate-name guard at
    /// <c>Website/admin/Security/EditRoles.ascx.vb:L252</c>, and that guard is a
    /// business rule enforced inside <see cref="CreateRoleAsync"/> and
    /// <see cref="UpdateRoleAsync"/> rather than a question a caller asks first
    /// (AAP Rule T2).
    /// </remarks>
    Task<Result<RoleDetailDto?>> GetRoleAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a role in a portal, including its paid-membership terms, and applies
    /// the legacy auto-assignment rule.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal that will own the role. Authoritative for tenant
    /// scoping; any portal identifier carried by
    /// <paramref name="request"/> is subordinate to it.
    /// </param>
    /// <param name="request">
    /// The role to create. Carries the paid-membership terms - billing frequency and
    /// period, service fee, trial frequency, period and fee - plus the public flag,
    /// the auto-assignment flag and the optional role-group identifier, which is
    /// nullable because "no group" is a real state.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying the created role, with its server-assigned
    /// identifier, so the caller can answer a create request with 201 and a
    /// location; or a failed outcome carrying <c>portal.not_found</c> when no such
    /// portal exists, <c>role_group.not_found</c> when a role group is named but
    /// does not exist in that portal, <c>role.name_duplicate</c> when the portal
    /// already has a role of that name, or <c>role.create_failed</c> when the store
    /// declines the insert.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces the two measured creation members, RoleController.vb:L100 and its
    /// synchronisation-flagged twin at L849. The legacy member returned the integer
    /// absence sentinel to signal failure; that channel is replaced by a failed
    /// outcome, so a real identifier can never be confused with a failure - which
    /// matters here because the identity column seeds at 0.
    /// </para>
    /// <para>
    /// The auto-assignment rule is preserved: L100 calls its private auto-assign
    /// helper immediately after a successful insert, so creating a role whose
    /// auto-assignment flag is set also enrols the portal's existing users in it.
    /// That work happens inside the implementation and is not a separate call.
    /// </para>
    /// <para>
    /// The duplicate-name rule is measured at
    /// <c>Website/admin/Security/EditRoles.ascx.vb:L252-L253</c>, where the legacy
    /// screen looks the name up and inserts only when nothing comes back. It is
    /// enforced here rather than left to the caller, and it is an expected failure,
    /// so it is reported as a reason code and never thrown.
    /// </para>
    /// </remarks>
    Task<Result<RoleDetailDto>> CreateRoleAsync(
        int portalId,
        CreateRoleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing role of a portal, including its paid-membership terms.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the role. Authoritative for tenant
    /// scoping.
    /// </param>
    /// <param name="roleId">
    /// Identifier of the role to update. Authoritative; any identifier carried by
    /// <paramref name="request"/> must agree with it, and a disagreement is a
    /// request-shape validation failure at the boundary rather than a reason code.
    /// </param>
    /// <param name="request">The replacement state for the role.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying the updated role, so the caller can answer an
    /// update request with 200 and the new state; or a failed outcome carrying
    /// <c>portal.not_found</c>, <c>role.not_found</c> when the portal has no such
    /// role, <c>role_group.not_found</c> when a role group is named but does not
    /// exist in that portal, or <c>role.name_duplicate</c> when the new name is
    /// already taken by a different role in the same portal.
    /// </returns>
    /// <remarks>
    /// Replaces RoleController.vb:L254, which accepted a whole legacy entity and
    /// returned nothing at all, so a caller could not tell an applied update from a
    /// silently discarded one. Changing the billing or trial terms here does not
    /// retrospectively re-compute the expiry of assignments already in force; the
    /// terms are re-read on the next assignment or renewal, which is the legacy
    /// behaviour and is preserved deliberately.
    /// </remarks>
    Task<Result<RoleDetailDto>> UpdateRoleAsync(
        int portalId,
        int roleId,
        UpdateRoleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a role from a portal together with its user assignments.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the role. Authoritative for tenant
    /// scoping, so a role belonging to another portal is reported as missing rather
    /// than deleted.
    /// </param>
    /// <param name="roleId">Identifier of the role to delete.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome when the role no longer exists, so the caller can answer
    /// a delete request with 204; or a failed outcome carrying
    /// <c>portal.not_found</c>, or <c>role.not_found</c> when the portal has no such
    /// role.
    /// </returns>
    /// <remarks>
    /// Replaces RoleController.vb:L125. Deleting a role necessarily discards its
    /// assignments and its permission grants; those cascades are the implementation's
    /// responsibility and are committed as one unit of work, because the legacy
    /// sequence performed them as separate, non-transactional statements.
    /// </remarks>
    Task<Result> DeleteRoleAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists one page of the users assigned to a role.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the role. Authoritative for tenant
    /// scoping.
    /// </param>
    /// <param name="roleId">Identifier of the role whose members are listed.</param>
    /// <param name="request">Paging coordinates and the optional free-text filter.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying one page of members, empty when the role has none;
    /// or a failed outcome carrying <c>portal.not_found</c>, or <c>role.not_found</c>
    /// when the portal has no such role.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Serves the role side of the legacy security-roles screen and collapses three
    /// measured members: the byte-identical listings at RoleController.vb:L457 and
    /// L874, and the by-role-name listing at L441. The legacy members were keyed by
    /// role <em>name</em>; this member is keyed by identifier and resolves the name
    /// internally, so a rename cannot silently change which members are returned.
    /// </para>
    /// <para>
    /// The legacy grid bound five columns, measured at
    /// <c>Website/admin/Security/securityroles.ascx:L68-L84</c>: the user identifier,
    /// the role identifier, the display name, the effective date and the expiry date.
    /// The first three are carried by the projected item; the two assignment dates are
    /// not, because no projection named by AAP 0.4.1.1 carries them and this contract
    /// may not invent one. The dates therefore travel on the write path only, on the
    /// assignment request, which is where AAP 0.5.1.8 requires them. The reduction is
    /// annotated at the head of this file.
    /// </para>
    /// </remarks>
    Task<Result<PagedResult<UserListItemDto>>> ListRoleUsersAsync(
        int portalId,
        int roleId,
        PagedRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every role a user holds in a portal.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal the assignments belong to. Authoritative for tenant
    /// scoping.
    /// </param>
    /// <param name="userId">Identifier of the user whose assignments are listed.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying every role the user holds in that portal, empty
    /// when the user holds none; or a failed outcome carrying
    /// <c>portal.not_found</c>, or <c>user.not_found</c> when the portal has no such
    /// user.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Serves the user side of the legacy security-roles screen, measured at
    /// <c>Website/admin/Security/SecurityRoles.ascx.vb:L246</c> and L253. The legacy
    /// projection was the assignment record, which inherits the role record outright -
    /// <c>UserRoleInfo</c> declares eight properties of its own and inherits fifteen
    /// from <c>RoleInfo</c> - so the role projection is the faithful shape for the
    /// roles a user holds, and it is the shape AAP 0.4.1.1 names.
    /// </para>
    /// <para>
    /// Collapses six measured members: the three assignment listings at
    /// RoleController.vb:L376, L392 and L408, the by-user-name listing at L425, and
    /// the two name-only readers at L240 and L859. The visibility toggle carried by
    /// L408 is not reproduced: a boolean that silently changes which rows come back
    /// makes the answer unreadable at the call site, and the projection instead
    /// carries the public flag so a caller filters explicitly on data it can see.
    /// </para>
    /// <para>
    /// Unpaged by design. The answer is bounded by the number of roles a portal
    /// defines, which the legacy screen also rendered whole, so paging would add a
    /// coordinate with nothing to coordinate.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<RoleListItemDto>>> ListUserRolesAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns a user to a role, or revises the dates of an assignment the user
    /// already holds, computing the expiry from the role's trial and billing terms
    /// when the caller does not state one.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal the assignment belongs to. Authoritative for tenant
    /// scoping.
    /// </param>
    /// <param name="roleId">
    /// Identifier of the role to assign. Authoritative; any identifier carried by
    /// <paramref name="request"/> must agree with it.
    /// </param>
    /// <param name="request">
    /// The user to assign, together with the optional effective and expiry dates.
    /// Both dates are nullable: a null effective date means the assignment is in
    /// force immediately, and a null expiry date means the service derives one from
    /// the role's terms.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome, which the caller answers with 201 because the assignment
    /// now exists at <c>/api/v1/roles/{roleId}/users/{userId}</c>; or a failed outcome
    /// carrying <c>portal.not_found</c>, <c>role.not_found</c> or
    /// <c>user.not_found</c>. No payload is returned: the stored dates are readable
    /// from the two listing members, and returning a projection here would require a
    /// type AAP 0.4.1.1 does not name.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Collapses the three measured assignment members at RoleController.vb:L277,
    /// L295 and L647, and the assigning half of the two revision members at L472 and
    /// L489. The measured behaviour at L295-L315 is an upsert - it inserts when the
    /// user does not yet hold the role and otherwise revises the two dates - so this
    /// member is deliberately idempotent in the same way.
    /// </para>
    /// <para>
    /// Expiry derivation, reproducing RoleController.vb:L503-L558 exactly and
    /// entirely inside the implementation, in this order. First, an absent period
    /// yields no expiry at all, short-circuiting everything that follows. Second, an
    /// effective date already in the past is cleared, so the assignment carries no
    /// start gate, and an expiry date already in the past is advanced to the current
    /// instant so that the offset runs forward from now. Third, the trial terms
    /// govern only when the trial has not already been consumed and the trial
    /// frequency is not the never code; otherwise the billing terms govern. Fourth,
    /// the frequency code selects the offset: never yields no expiry, one-off yields
    /// the perpetual far-future date, and the day, week, month and year codes offset
    /// by the period, by seven times the period in days, by months and by years
    /// respectively. The current instant comes from the injected clock, never from an
    /// ambient reading, so the arithmetic is pinned by unit tests.
    /// </para>
    /// <para>
    /// Sentinel dates at the boundary. The legacy store represents "never expires"
    /// with the minimum date value and "perpetual" with the explicit far-future date
    /// 9999-12-31. The first is absence and is carried as a null date here; the
    /// second is a real, externally observable value and is carried through
    /// unchanged, because a legacy consumer reading the same row expects to see it.
    /// Neither is silently converted into the other (AAP Rule T7).
    /// </para>
    /// <para>
    /// The notification switch the legacy shared member carried is not reproduced,
    /// and neither is its dependency on the ambient per-request composite; see the
    /// annotations at the head of this file.
    /// </para>
    /// </remarks>
    Task<Result> AssignUserToRoleAsync(
        int portalId,
        int roleId,
        RoleAssignmentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a user from a role, enforcing the protected-assignment rule, and
    /// expiring rather than deleting an assignment whose paid trial has been used.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal the assignment belongs to. Authoritative for tenant
    /// scoping, and the source of the two protected identifiers the rule consults.
    /// </param>
    /// <param name="roleId">Identifier of the role to remove the user from.</param>
    /// <param name="userId">Identifier of the user to remove.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome when the user no longer holds the role, so the caller can
    /// answer a delete request with 204. The success carries the informational reason
    /// <c>role_assignment.expired_not_removed</c> when the assignment was expired
    /// instead of deleted, so a caller that must report the difference can. A failed
    /// outcome carries <c>portal.not_found</c>, <c>role.not_found</c>,
    /// <c>user.not_found</c>, <c>role_assignment.not_found</c> when the user does not
    /// hold the role, or <c>role_assignment.protected</c> when the rule refuses the
    /// removal.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Collapses the four measured removal members at RoleController.vb:L330, L677,
    /// L695 and L714, and the cancelling half of L489.
    /// </para>
    /// <para>
    /// The protected-assignment rule, measured at RoleController.vb:L741 and again in
    /// its duplicated twin at L764, refuses exactly two cases: removing the portal's
    /// designated administrator from that portal's administrator role, and removing
    /// any user at all from that portal's registered-users role. It is enforced here,
    /// inside the operation, and never exposed as a question a caller may ask and
    /// then ignore (AAP Rule T2). A client that wants to hide a delete affordance
    /// uses the permission directive on the client side, exactly as the legacy screen
    /// used the rule for button visibility at
    /// <c>Website/admin/Security/SecurityRoles.ascx.vb:L362</c>; the authoritative
    /// enforcement is here.
    /// </para>
    /// <para>
    /// The expire-rather-than-delete rule, measured at RoleController.vb:L495-L496,
    /// is preserved verbatim: when the role carries a service fee and the trial has
    /// been used, the assignment's expiry is back-dated by one day instead of the row
    /// being deleted, so the trial-used fact survives and a cancelled subscriber
    /// cannot restart a trial. The row therefore remains readable afterwards, in the
    /// expired state, and the informational reason on the successful outcome is the
    /// only way a caller learns which of the two effects occurred.
    /// </para>
    /// </remarks>
    Task<Result> RemoveUserFromRoleAsync(
        int portalId,
        int roleId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every role group defined in a portal.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal whose role groups are listed. Authoritative for
    /// tenant scoping.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying every role group in the portal, empty when it
    /// has none; or a failed outcome carrying <c>portal.not_found</c>.
    /// </returns>
    /// <remarks>
    /// Replaces RoleController.vb:L825. Unpaged by design, because both measured
    /// consumers bind the whole answer to a selector rather than a grid: the group
    /// filter at <c>Website/admin/Security/Roles.ascx.vb:L108</c> and the group
    /// selector at <c>Website/admin/Security/EditRoles.ascx.vb:L73</c>.
    /// </remarks>
    Task<Result<IReadOnlyList<RoleGroupDto>>> ListRoleGroupsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one role group of a portal.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the role group. Authoritative for tenant
    /// scoping.
    /// </param>
    /// <param name="roleGroupId">Identifier of the role group to read.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome whose value is the role group, or a successful outcome
    /// whose value is <see langword="null"/> when the portal has no such group; or a
    /// failed outcome carrying <c>portal.not_found</c>.
    /// </returns>
    /// <remarks>
    /// Replaces RoleController.vb:L811. The legacy screen treated an empty answer as
    /// an attempt to reach an item belonging to another portal and redirected away -
    /// measured at <c>Website/admin/Security/EditGroups.ascx.vb:L80-L82</c> - so the
    /// absent case is a genuine, expected answer here rather than an error, and the
    /// portal scoping that makes it meaningful is enforced by this member.
    /// </remarks>
    Task<Result<RoleGroupDto?>> GetRoleGroupAsync(
        int portalId,
        int roleGroupId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a role group in a portal.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal that will own the role group. Authoritative for
    /// tenant scoping.
    /// </param>
    /// <param name="request">
    /// The role group to create. Its own identifier is ignored - the store assigns
    /// one - and its portal identifier is subordinate to
    /// <paramref name="portalId"/>. The same projection type serves as both the
    /// request and the response here, mirroring the legacy members at L626 and L838,
    /// which both accepted the one type, and reflecting that a role group is four
    /// fields with no asymmetry between what is sent and what is returned.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying the created role group with its server-assigned
    /// identifier, so the caller can answer with 201 and a location; or a failed
    /// outcome carrying <c>portal.not_found</c>, or <c>role_group.name_duplicate</c>
    /// when the portal already has a group of that name.
    /// </returns>
    /// <remarks>
    /// Replaces RoleController.vb:L626. The duplicate-name case is measured at
    /// <c>Website/admin/Security/EditGroups.ascx.vb:L114-L120</c>, where the legacy
    /// screen wraps the call and maps any thrown error onto a localised duplicate
    /// message. A duplicate name is an expected outcome, so it is reported as a
    /// reason code rather than thrown (AAP 0.4.3), and the caller is no longer
    /// obliged to interpret an exception to discover it.
    /// </remarks>
    Task<Result<RoleGroupDto>> CreateRoleGroupAsync(
        int portalId,
        RoleGroupDto request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing role group of a portal.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the role group. Authoritative for tenant
    /// scoping.
    /// </param>
    /// <param name="roleGroupId">
    /// Identifier of the role group to update. Authoritative; an identifier carried
    /// by <paramref name="request"/> must agree with it, and a disagreement is a
    /// request-shape validation failure at the boundary rather than a reason code.
    /// </param>
    /// <param name="request">The replacement state for the role group.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying the updated role group, so the caller can answer
    /// with 200 and the new state; or a failed outcome carrying
    /// <c>portal.not_found</c>, <c>role_group.not_found</c>, or
    /// <c>role_group.name_duplicate</c> when the new name is already taken by a
    /// different group in the same portal.
    /// </returns>
    /// <remarks>
    /// Replaces RoleController.vb:L838, which returned nothing and so left a caller
    /// unable to distinguish an applied update from a discarded one. Re-classifying a
    /// group does not move any role between groups; a role's group membership is a
    /// field of the role and is changed through <see cref="UpdateRoleAsync"/>.
    /// </remarks>
    Task<Result<RoleGroupDto>> UpdateRoleGroupAsync(
        int portalId,
        int roleGroupId,
        RoleGroupDto request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a role group from a portal, refusing while it still classifies roles.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the role group. Authoritative for tenant
    /// scoping.
    /// </param>
    /// <param name="roleGroupId">Identifier of the role group to delete.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome when the role group no longer exists, so the caller can
    /// answer with 204; or a failed outcome carrying <c>portal.not_found</c>,
    /// <c>role_group.not_found</c>, or <c>role_group.in_use</c> when the group still
    /// classifies at least one role.
    /// </returns>
    /// <remarks>
    /// Collapses the two measured members at RoleController.vb:L779 and L794. The
    /// in-use rule is measured at
    /// <c>Website/admin/Security/EditGroups.ascx.vb:L75-L79</c>, where the legacy
    /// screen counts the group's roles and hides the delete affordance when the count
    /// is positive. Hiding a button is not enforcement, so the rule is enforced here,
    /// inside the operation, and reported as a reason code - which closes the gap a
    /// direct request could otherwise walk through.
    /// </remarks>
    Task<Result> DeleteRoleGroupAsync(
        int portalId,
        int roleGroupId,
        CancellationToken cancellationToken = default);
}
