using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

// MIGRATION: the core SQL provider (DataProvider.vb, 269 members) contains ZERO Role members and
// SqlDataProvider.vb invokes zero role procedures. The authoritative source is the separate
// membership provider stack, Library/Providers/MembershipProviders/DataProvider/DataProvider.vb
// L91-L115 - 21 members across its three role regions, eight under 'Roles, six under 'RoleGroups and
// seven under 'User Roles - which AspNetMembershipProvider.vb:L59 delegates to through its own
// reflection singleton.
//
// MIGRATION: legacy AddRole (14 positional arguments, membership DataProvider.vb:L95) and UpdateRole
// (13, L97) are replaced by entity-oriented calls.
//
// MIGRATION: the membership provider declares BillingPeriod As String (DataProvider.vb:L95, L97),
// but RoleInfo.vb:L218 declares it As Integer and the terminal schema declares BillingPeriod int
// NULL (01.00.08.SqlDataProvider:L6829). The provider's typing is a measured legacy inconsistency;
// under Rule T4 the schema is authoritative, so the Domain entity uses int? and this contract passes
// the entity. No unrelated legacy behaviour is changed.
//
// MIGRATION: the six billing-frequency codes N, O, D, W, M and Y (RoleController.vb:L540-L547) match
// Roles.BillingFrequency char(1) in the schema and are load-bearing data. They are preserved
// verbatim as an explicitly-valued BillingFrequency enum and are never renamed, re-lettered,
// reordered or collapsed. The legacy DateAdd(DateInterval.D/W/M/Y, ...) arithmetic at
// RoleController.vb:L496 and L540-L547, reached through the single in-scope Imports
// Microsoft.VisualBasic at L25, becomes DateTime.AddDays / AddDays(n * 7) / AddMonths / AddYears in
// the Application layer, not here.
//
// MIGRATION: the six codes are not merely compared against in code, they are persisted rows. The
// baseline installer seeds them into the CodeFrequency lookup at 01.00.08.SqlDataProvider L6840,
// L6849, L6858, L6867, L6876 and L6885 as N=None, O=One-time Fee, D=Day(s), W=Week(s), M=Month(s)
// and Y=Year(s), and the terminal GetPortalRoles at 04.08.00.SqlDataProvider:L38-L39 joins the Lists
// table on BillingFrequency = Value AND ListName='Frequency' to resolve their display text. A stored
// letter is therefore a foreign key in all but name, which is the strongest available reason never to
// re-letter one.
//
// MIGRATION: RoleController.AddRole(objRoleInfo, SynchronizationMode) at L849 and
// GetPortalRoles(PortalId, SynchronizeRoles) at L854 carry role-provider synchronisation flags; the
// provider model is replaced rather than reproduced, so no synchronisation parameter appears on this
// contract.
//
// MIGRATION: membership DataProvider.vb:L70 GetAuthRoles(PortalId, ModuleId) is not surfaced here -
// it sits in the provider's Login/Security section outside the L90-L115 role range and is a
// module-permission projection. Module-permission reads belong to IPermissionRepository and
// evaluation to Infrastructure/Security/PermissionEvaluator.cs.
//
// MIGRATION: every legacy member realised below returned a forward-only reader that the caller then
// turned into objects either by hand, one Null.SetNull assignment per column, or through the
// 729-line reflection binder in CBO.vb. Both mechanisms are deleted rather than translated: the
// persistence layer's own materialiser yields entities directly, so no reader, no row binder, no
// pre-generics collection wrapper and no sentinel-translating helper crosses this boundary. Legacy
// procedure names appear in these notes as provenance only and never as a parameter.
//
// MIGRATION: the legacy Null sentinel table is not reinstated by any signature here. Absence is a
// null of a nullable CLR type, so a role with no group stays distinguishable from a role grouped
// under identity zero, and an assignment with no expiry stays distinguishable from one that expired
// long ago. Two consequences are worth stating because both are easy to get wrong. Null.NullString
// was the EMPTY STRING rather than a null, so an empty RoleName or RSVPCode was legally
// representable in the legacy store and an implementation must not quietly fold one into a null.
// Null.NullDate was Date.MinValue, so the smallest representable date is a legacy "no date" marker
// rather than a real instant; EffectiveDate and ExpiryDate are DateTime? on the entity and an
// implementation must not conflate the two. Where a sentinel is externally observable it is
// reinstated at the DTO and API boundary, which is the only layer whose contract leaves this
// application.

/// <summary>
/// The persistence contract for the Role aggregate: security roles, the groups that organise them
/// and the assignments that place user accounts into them.
/// </summary>
/// <remarks>
/// <para>
/// One contract deliberately spans three tables. <c>dbo.Roles</c>, <c>dbo.RoleGroups</c> and
/// <c>dbo.UserRoles</c> are a single consistency boundary rather than three independent ones: a
/// group exists only to organise roles, an assignment has no meaning apart from the role it points
/// at, and the legacy membership provider grouped all three under one abstraction for the same
/// reason. There is therefore no separate role-group repository and no separate user-role
/// repository, and neither may be introduced.
/// </para>
/// <para>
/// The members appear below in the legacy provider's own order - roles, then groups, then
/// assignments - so that a reader holding
/// <c>Library/Providers/MembershipProviders/DataProvider/DataProvider.vb</c> open at L90 can walk the
/// two lists side by side. Every member names the provider line it realises.
/// </para>
/// <para>
/// Nothing here decides anything. An implementation reads and writes rows; it does not evaluate a
/// permission, compute an expiry date, classify an assignment's status, notify anybody, cache a
/// result or validate a request. Those belong above this layer, and the split is what keeps the
/// contract testable: the whole surface can be substituted by a fake that only stores objects.
/// </para>
/// <para>
/// <b>Writes are staged, never committed.</b> No member of this contract writes to the database.
/// Each add, update and delete records an intention, and the transaction is closed exactly once by
/// <see cref="IUnitOfWork.SaveChangesAsync"/>. That is what allows one commit to span several
/// aggregates, which the portal-creation sequence requires - see the note on
/// <see cref="AddAsync"/>.
/// </para>
/// <para>
/// <b>Identity keys are opaque.</b> Three different identity seeds meet in this contract and none of
/// the reserved-looking values means "absent": <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>
/// (01.00.00.SqlDataProvider:L115), <c>RoleGroups.RoleGroupID</c> is <c>IDENTITY(0, 1)</c>
/// (04.00.04.SqlDataProvider:L51), <c>UserRoles.UserRoleID</c> is <c>IDENTITY(1, 1)</c>
/// (01.00.00.SqlDataProvider:L239) and <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
/// (01.00.00.SqlDataProvider:L77). Zero is the first role of an installation, and in a freshly
/// provisioned site that is the Administrators role. Minus one is a real portal. No implementation
/// may test an identifier against a reserved value to decide whether it was supplied, and no member
/// of this contract accepts a wildcard identifier.
/// </para>
/// <para>
/// <b>Cancellation is uniform.</b> Every member is asynchronous, is suffixed <c>Async</c> and takes a
/// trailing cancellation token with a default. There is no synchronous counterpart to any of them and
/// none may be added.
/// </para>
/// <para>
/// <b>Outcomes are plain.</b> A read that matches nothing returns a null or an empty list, both of
/// which are ordinary answers rather than failures. The role region of the legacy provider carries no
/// status argument to translate, so no member returns a result wrapper; a genuine fault surfaces as
/// an exception.
/// </para>
/// </remarks>
public interface IRoleRepository
{
    // ---------------------------------------------------------------------------------------------
    // Roles - membership DataProvider.vb L91-L98
    // ---------------------------------------------------------------------------------------------

    // MIGRATION: THE TWO PORTAL-SCOPED READS ARE DELIBERATELY ASYMMETRIC, AND THE ASYMMETRY IS
    // UNOBSERVABLE AGAINST A FAITHFUL INSTALLATION. The terminal GetPortalRoles
    // (04.08.00.SqlDataProvider:L18-L42) filters on ( R.PortalId = @PortalId OR R.PortalId is null )
    // and orders by R.RoleName. The terminal GetRole (04.00.04.SqlDataProvider:L311-L336) filters on
    // RoleId = @RoleId AND PortalId = @PortalId, a strict equality that a null never satisfies. Both
    // predicates are reproduced exactly, because the Minimal Change Clause protects them.
    //
    // What must NOT be inferred from the first one is that a role with no owning portal exists. The
    // terminal Roles.PortalID is int NOT NULL (01.00.05.SqlDataProvider:L2749, corroborated by the
    // fresh-install snapshot at DotNetNuke.Schema.SqlDataProvider:L6209 and recorded in
    // backend/tests/DnnMigration.IntegrationTests/Schema/TerminalSchema.manifest), so no installation
    // can hold such a row and the null branch is unsatisfiable. An earlier note here claimed the
    // difference was observable, and two persistence tests demonstrated it by inserting the row -
    // which passed only because the test schema had drifted to declaring the column nullable. The
    // predicate stays; the claim that anything exercises it does not.

    /// <summary>
    /// Returns every role visible to one portal, in name order.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L91 GetPortalRoles(PortalId)</c>. The predicate is the
    /// terminal one, which also admits a role with no owning portal - a case the terminal column
    /// forbids, so it never arises; see the migration note above for the evidence and for why this
    /// differs from <see cref="GetByIdAsync"/>.
    /// <para>
    /// The whole set is returned because the legacy procedure returned the whole set: a portal holds
    /// tens of roles, not millions, and no legacy role read was ever paged. Any narrowing a screen
    /// needs - a name filter, a group restriction, a page - is applied by the caller over this
    /// result.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// The portal whose roles are wanted. Minus one and zero are both real portals.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The matching roles, ordered by name; an empty list when the portal owns none and the
    /// installation defines none.
    /// </returns>
    Task<IReadOnlyList<Role>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one page of the roles a portal OWNS, filtered, ordered, counted and windowed by the store.
    /// </summary>
    /// <param name="portalId">
    /// The owning portal, matched by STRICT equality. Minus one and zero are both real portals, and a null
    /// column never equals an identifier - so the installation-wide roles that <see cref="GetByPortalIdAsync"/>
    /// admits are excluded here. That is deliberate and is the behaviour the administration screen this read
    /// serves has always had: it lists the roles the portal itself owns.
    /// </param>
    /// <param name="roleGroupId">
    /// Restrict to one role group, or <see langword="null"/> for no group restriction.
    /// <c>RoleGroupID</c> is <c>IDENTITY(0, 1)</c>, so the PRESENCE of a value selects the filter and its
    /// magnitude never does.
    /// </param>
    /// <param name="ungroupedOnly">
    /// Restrict to the roles belonging to no group at all - the legacy "global roles" selection. Tests for
    /// ABSENCE of a group, because <c>Roles.RoleGroupID</c> is nullable and an ungrouped role stores SQL
    /// null there; it is not a test for any particular number. Ignored when
    /// <paramref name="roleGroupId"/> names a group, since naming one is the narrower instruction.
    /// </param>
    /// <param name="nameQuery">
    /// A fragment the role name must contain, case-insensitively, or <see langword="null"/> for no name
    /// restriction. The value is data and never a pattern.
    /// </param>
    /// <param name="sortBy">The role property to order by, or <see langword="null"/> for the default.</param>
    /// <param name="descending">Whether the ordering runs downwards.</param>
    /// <param name="pageIndex">The page to return, counted from zero.</param>
    /// <param name="pageSize">The page width, or zero for every matching row.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The requested window together with the total number of roles the whole filtered set holds. Each role
    /// carries its group, because the projection reports the group's name.
    /// </returns>
    /// <remarks>
    /// MIGRATION: net-new, and it replaces an Application-layer composition rather than a legacy procedure -
    /// the legacy membership provider's entire role-listing surface was <c>GetPortalRoles(PortalId)</c>
    /// (<c>DataProvider.vb:L91</c>), which returned every row, and the group restriction, the name search and
    /// the page were the admin screen's own work. Reproducing that literally meant materialising the whole
    /// role set on every request and narrowing it in this process, which bounds the RESPONSE by the page
    /// while leaving the read, the sort and the allocation bounded only by the tenant. Every one of those
    /// narrowings is relational, so they belong in the statement; the caller's choice of ordering travels as
    /// data. The reads are two, fixed: one count and one window, or one window alone when every row was
    /// asked for.
    /// </remarks>
    Task<PagedResult<Role>> ListAsync(
        int portalId,
        int? roleGroupId,
        bool ungroupedOnly,
        string? nameQuery,
        string? sortBy,
        bool descending,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every role in the installation, across all portals.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L92 GetRoles()</c>, which took no argument and applied
    /// no filter. This is a host-wide read and crosses tenant boundaries by design, so a caller that
    /// is answering a question about one portal wants <see cref="GetByPortalIdAsync"/> instead.
    /// </remarks>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>Every stored role; an empty list when the installation defines none.</returns>
    Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one role by key within a portal, or <see langword="null"/> when the portal has no such
    /// role.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L93 GetRole(RoleID, PortalID)</c>. Both keys are
    /// required and requiring both is what makes the answer tenant-safe: the portal is not a
    /// redundant hint but a condition, so one tenant cannot read another tenant's role by guessing
    /// its key. The portal condition is a strict equality, exactly as the terminal procedure wrote
    /// it, so an installation-wide role with no owning portal is never returned here even though
    /// <see cref="GetByPortalIdAsync"/> lists it.
    /// </remarks>
    /// <param name="roleId">
    /// The role key. Zero is a real key, so it may not be read as unset - see the identity note on
    /// the interface.
    /// </param>
    /// <param name="portalId">The portal the role must belong to.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The role, or <see langword="null"/> when no stored role has that key within that portal.
    /// Absence is an ordinary answer.
    /// </returns>
    Task<Role?> GetByIdAsync(int roleId, int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one role by name within a portal, or <see langword="null"/> when the portal has no
    /// role of that name.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L94 GetRoleByName(PortalId, RoleName)</c>. A name is
    /// unique only within its portal, so two portals may each own a role called "Administrators" and
    /// the portal argument is what separates them.
    /// <para>
    /// Because the answer is at most one row, this member also settles the uniqueness question a
    /// caller must ask before storing a name: a non-null answer means the name is taken, and an
    /// answer whose key differs from the role being edited means it is taken by somebody else. No
    /// separate existence member is provided, because that is the same query with the row discarded.
    /// </para>
    /// </remarks>
    /// <param name="portalId">The portal to search within.</param>
    /// <param name="roleName">
    /// The name to match. An empty name is a legally representable legacy value rather than a request
    /// for "any name", and it is never treated as absent.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The role, or <see langword="null"/> when that portal has no role of that name.</returns>
    Task<Role?> GetByNameAsync(int portalId, string roleName, CancellationToken cancellationToken = default);

    // MIGRATION: THE STAGING RULING. Every legacy Add member returned Integer because its procedure
    // ended in SCOPE_IDENTITY(), and reproducing that here as a returned key would force this
    // repository to commit in order to have a key to return. That would dissolve the unit-of-work
    // boundary and break the multi-table portal-creation sequence at PortalController.vb:L980, which
    // inserts across Portals, PortalAlias, Roles, Tabs and Modules as one unit - Roles among them,
    // because creating a portal creates its Administrators and Registered Users roles. Every add
    // therefore stages the row and returns a bare task; the generated key appears on the entity
    // after IUnitOfWork.SaveChangesAsync has run.

    /// <summary>
    /// Stages a new role for insertion.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L95 AddRole(...)</c>, whose fourteen positional
    /// arguments are replaced by the entity itself. Nothing is written until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> runs, after which <see cref="Role.RoleId"/> holds
    /// the generated key. The return type is deliberately not the generated key - see the staging
    /// note above.
    /// </remarks>
    /// <param name="role">The role to insert. Its key is assigned by the store, not by the caller.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddAsync(Role role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an existing role's modifications for update.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L97 UpdateRole(...)</c>, whose thirteen positional
    /// arguments are replaced by the entity itself. The legacy procedure rewrote every column it was
    /// given on every call, so a caller had to read the role, change what it meant to change and pass
    /// the rest back unaltered or lose it; passing the whole entity removes that hazard. Nothing is
    /// written until <see cref="IUnitOfWork.SaveChangesAsync"/> runs.
    /// </remarks>
    /// <param name="role">The role whose stored row is to be brought into line with it.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateAsync(Role role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a role for deletion by key.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L96 DeleteRole(RoleId)</c>, which took the key alone
    /// and was not portal-scoped; that shape is preserved, so a caller that must confine a deletion
    /// to one tenant establishes the role's ownership first through <see cref="GetByIdAsync"/>.
    /// Deleting a role that does not exist is not an error, exactly as the legacy procedure's
    /// key-matched delete affected no row and reported nothing. Nothing is written until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> runs.
    /// </remarks>
    /// <param name="roleId">The key of the role to delete. Zero is a real key.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the deletion has been staged.</returns>
    Task DeleteAsync(int roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the roles that one user account holds within one portal.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L98 GetRolesByUser(UserId, PortalId)</c>, and keeps its
    /// argument order. This is the role-shaped view of a membership: it answers "which roles does
    /// this account hold", where <see cref="GetUserRolesAsync"/> answers the assignment-shaped
    /// question and additionally carries each assignment's dates. A caller that needs the validity
    /// window wants the assignment-shaped member.
    /// </remarks>
    /// <param name="userId">The account whose roles are wanted.</param>
    /// <param name="portalId">The portal to confine the answer to.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The roles held within that portal; an empty list when the account holds none there.
    /// </returns>
    Task<IReadOnlyList<Role>> GetRolesByUserIdAsync(int userId, int portalId, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------------------
    // Role groups - membership DataProvider.vb L101-L106
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Stages a new role group for insertion.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L101 AddRoleGroup(PortalId, GroupName, Description)</c>,
    /// whose three positional arguments are replaced by the entity. Nothing is written until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> runs, after which
    /// <see cref="RoleGroup.RoleGroupId"/> holds the generated key - which is seeded at zero, so the
    /// first group of a portal is numbered zero and that is a real key.
    /// </remarks>
    /// <param name="roleGroup">The group to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddRoleGroupAsync(RoleGroup roleGroup, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an existing role group's modifications for update.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L106 UpdateRoleGroup(RoleGroupId, GroupName,
    /// Description)</c>. As with a role, the legacy procedure rewrote both text columns on every call,
    /// so the entity is passed whole rather than the pair of values. Nothing is written until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> runs.
    /// </remarks>
    /// <param name="roleGroup">The group whose stored row is to be brought into line with it.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateRoleGroupAsync(RoleGroup roleGroup, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a role group for deletion by key.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L102 DeleteRoleGroup(RoleGroupId)</c>, which took the
    /// key alone and was not portal-scoped.
    /// <para>
    /// A group cannot be discarded while a role still points at it. <c>FK_Roles_RoleGroups</c> carries
    /// no cascade clause, unlike <c>FK_Roles_Portals</c>, so the store itself refuses the deletion and
    /// the caller is expected to clear or reassign the group's roles first. That constraint is part of
    /// the schema this migration binds to and is deliberately not worked around here. Nothing is
    /// written until <see cref="IUnitOfWork.SaveChangesAsync"/> runs, so the refusal surfaces at the
    /// commit rather than at this call.
    /// </para>
    /// </remarks>
    /// <param name="roleGroupId">The key of the group to delete. Zero is a real key.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the deletion has been staged.</returns>
    Task DeleteRoleGroupAsync(int roleGroupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one role group by key within a portal, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L103 GetRoleGroup(portalId, roleGroupId)</c> and keeps
    /// its argument order, portal first. Both keys are required, and the portal is a condition rather
    /// than a hint: <c>RoleGroups.PortalID</c> is <c>NOT NULL</c>, so every group belongs to exactly
    /// one portal and requiring it is what stops one tenant reading another's group by key.
    /// </remarks>
    /// <param name="portalId">The portal the group must belong to.</param>
    /// <param name="roleGroupId">The group key. Zero is a real key.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The group, or <see langword="null"/> when that portal has no group with that key.
    /// </returns>
    Task<RoleGroup?> GetRoleGroupAsync(int portalId, int roleGroupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every role group belonging to one portal.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L104 GetRoleGroups(portalId)</c>. Groups are purely
    /// organisational - no permission is ever granted to a group - so this read exists to populate a
    /// grouping control and to let a caller settle group-name uniqueness within the portal without a
    /// dedicated existence member.
    /// </remarks>
    /// <param name="portalId">The portal whose groups are wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The portal's groups; an empty list when it defines none, which is the common case.</returns>
    Task<IReadOnlyList<RoleGroup>> GetRoleGroupsAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the roles that belong to one role group within one portal.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L105 GetRolesByGroup(RoleGroupId, PortalId)</c> and
    /// keeps its argument order, group first. The portal is carried as well as the group because the
    /// legacy procedure carried it, and it remains a meaningful condition: it confines the answer to
    /// one tenant even if a group key were supplied that belongs to another.
    /// <para>
    /// This is not the same question as <see cref="GetRoleGroupsAsync"/>. That one lists the groups;
    /// this one lists the roles inside one of them. Roles whose group is absent - the ordinary,
    /// ungrouped case - are not returned by any group, and a caller wanting those uses
    /// <see cref="GetByPortalIdAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="roleGroupId">The group whose roles are wanted. Zero is a real key.</param>
    /// <param name="portalId">The portal to confine the answer to.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The roles in that group; an empty list when the group is empty or unknown.</returns>
    Task<IReadOnlyList<Role>> GetRolesByGroupAsync(int roleGroupId, int portalId, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------------------
    // UserRole assignments - membership DataProvider.vb L109-L115
    // ---------------------------------------------------------------------------------------------

    // MIGRATION: THE USERS-IN-ROLE OWNERSHIP SPLIT. The three reads below return assignment rows and
    // never accounts, even though two of them are keyed by an account. The account-shaped question -
    // membership DataProvider.vb:L85 GetUsersByRolename(PortalID, Rolename), which sits in the
    // provider's Users section rather than its role section - belongs to IUserRepository and is not
    // duplicated here. The division is what keeps the two contracts from overlapping: this one owns
    // dbo.UserRoles, that one owns dbo.Users, and composing the two is the Application layer's work.
    // UserRole.UserId is a mapped integer, so a caller that needs names resolves them there.
    //
    // MIGRATION: dbo.UserRoles CARRIES NO PORTAL COLUMN. Its terminal columns are UserRoleID
    // (IDENTITY(1,1)), UserID, RoleID, ExpiryDate, IsTrialUsed and the later EffectiveDate added at
    // 03.02.03.SqlDataProvider:L380 - and no PortalID among them. Every portal-scoped read below
    // therefore applies its scope THROUGH the role the assignment points at, which is also why
    // assignments to installation-wide roles fall outside a portal-scoped answer: such a role has no
    // owning portal, and a null never equals a portal identifier.
    //
    // MIGRATION: legacy AddUserRole (membership DataProvider.vb:L112) accepted PortalID although the
    // UserRoles table has no PortalID column - role membership is portal-scoped through Roles. The
    // argument served validation only, so it is not part of the persistence contract; the Application
    // RoleService validates portal scope before staging.
    //
    // MIGRATION: no member classifies an assignment. The legacy validity predicate - written
    // ( EffectiveDate <= getdate() or EffectiveDate is null ) and its expiry counterpart, first seen
    // at 03.02.03.SqlDataProvider:L405 - is reproduced by the pure method UserRole.GetStatus, which
    // takes the instant as an argument. Keeping it there rather than adding a status-returning member
    // here means the decision is reproducible, testable at a chosen moment, and reads its clock from
    // the Application layer instead of from a database server whose local time the legacy predicate
    // silently depended on.

    /// <summary>
    /// Returns one user account's assignment to one role within one portal, or
    /// <see langword="null"/> when the account does not hold that role.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L109 GetUserRole(PortalID, UserId, RoleId)</c> and keeps
    /// its argument order. The portal is applied through the assignment's role, since the assignment
    /// row itself has no portal column.
    /// <para>
    /// The returned assignment carries its own validity window. Both bounds are nullable and a null
    /// bound is unbounded in that direction rather than "now"; additionally the smallest representable
    /// date is a legacy no-date marker rather than a real instant. A caller that wants the assignment
    /// classified passes an instant to <see cref="UserRole.GetStatus"/> instead of asking this
    /// contract.
    /// </para>
    /// </remarks>
    /// <param name="portalId">The portal whose scope the assignment's role must fall within.</param>
    /// <param name="userId">The account whose assignment is wanted.</param>
    /// <param name="roleId">The role the assignment must be to. Zero is a real key.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The assignment, or <see langword="null"/> when no stored assignment matches. Absence means the
    /// account does not hold the role and is an ordinary answer, not a failure.
    /// </returns>
    Task<UserRole?> GetUserRoleAsync(int portalId, int userId, int roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every role assignment one user account holds within one portal.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L110 GetUserRoles(PortalID, UserId)</c> and keeps its
    /// argument order. This is the assignment-shaped counterpart of
    /// <see cref="GetRolesByUserIdAsync"/>: the same memberships, but each row carrying its effective
    /// date, expiry date and trial-used flag, which is what a caller needs in order to classify a
    /// membership or to display when it lapses.
    /// </remarks>
    /// <param name="portalId">The portal to confine the answer to.</param>
    /// <param name="userId">The account whose assignments are wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The account's assignments within that portal; an empty list when it holds none there.
    /// </returns>
    Task<IReadOnlyList<UserRole>> GetUserRolesAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns role assignments within one portal, narrowed by account login name, by role name, by
    /// both, or by neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Realises membership <c>DataProvider.vb:L111 GetUserRolesByUsername(PortalID, Username,
    /// Rolename)</c> and keeps its argument order.
    /// </para>
    /// <para>
    /// BOTH name arguments are nullable, and that is measured from the terminal procedure rather than
    /// inferred from the member's name. Its body opens with <c>IF @UserName Is Null</c> and, in that
    /// branch, selects every assignment in the portal filtered by
    /// <c>(R.Rolename = @Rolename or @RoleName is NULL)</c> alone; the else-branch nests the mirror-image
    /// test on the role name. So one procedure serves both directions of the same relation, and the
    /// legacy code reached the role-keyed direction through it: <c>DNNRoleProvider.vb:L520-L522</c>
    /// defines <c>GetUserRolesByRoleName(portalId, roleName)</c> as
    /// <c>GetUserRoles(portalId, Nothing, roleName)</c>, and the provider at
    /// <c>MembershipProviders/DataProvider/SqlDataProvider.vb:L277</c> passes both names through
    /// <c>GetNull</c>. Declaring the login name non-nullable here would therefore have hidden a
    /// direction the legacy application used, which is the direction the role-membership screen needs -
    /// <c>SecurityRoles.ascx.vb</c> binds its grid through <c>GetUserRolesByRoleName</c> at L246 when a
    /// role is selected and through the account-keyed member at L253 when an account is.
    /// </para>
    /// <para>
    /// A null narrows nothing, whereas an empty string is a legally representable legacy name and
    /// therefore narrows to it. That distinction is preserved deliberately for both arguments, because
    /// conflating them would silently change which rows come back.
    /// </para>
    /// <para>
    /// The lookup is by name rather than by key because the legacy member was, and because its callers
    /// held names; the portal is always required, since a login name is unique only within a portal.
    /// </para>
    /// <para>
    /// The account behind each assignment is materialised along with the role, because the terminal
    /// statement projects <c>U.DisplayName As FullName</c> beside the assignment columns - the account
    /// is part of the answer to this question, not a separate one, and composing it here avoids a read
    /// per row at the caller.
    /// </para>
    /// </remarks>
    /// <param name="portalId">The portal to confine the answer to.</param>
    /// <param name="username">
    /// The login name of the account whose assignments are wanted, or <see langword="null"/> for the
    /// assignments of every account in the portal.
    /// </param>
    /// <param name="roleName">
    /// The single role to narrow to, or <see langword="null"/> for every role.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The matching assignments; an empty list when there are none.</returns>
    Task<IReadOnlyList<UserRole>> GetUserRolesByUsernameAsync(int portalId, string? username, string? roleName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one page of one role's assignments, filtered, ordered, counted and windowed by the store.
    /// </summary>
    /// <param name="portalId">The portal to confine the answer to.</param>
    /// <param name="roleName">
    /// The single role whose assignments are wanted, matched case-insensitively. Required here, unlike on
    /// <see cref="GetUserRolesByUsernameAsync"/>: this read serves one role's membership screen, so "every
    /// role" is not a question it is ever asked. An empty name is a legally representable legacy name and
    /// therefore narrows to it rather than widening.
    /// </param>
    /// <param name="accountQuery">
    /// A fragment that the account's display name OR its login name must contain, case-insensitively, or
    /// <see langword="null"/> for no account restriction. Both are considered because the grid shows the
    /// former while a caller who knows the account knows the latter, and honouring only one of the two
    /// would make the same account findable only by luck. The value is data and never a pattern.
    /// </param>
    /// <param name="sortBy">The account property to order by, or <see langword="null"/> for the default.</param>
    /// <param name="descending">Whether the ordering runs downwards.</param>
    /// <param name="pageIndex">The page to return, counted from zero.</param>
    /// <param name="pageSize">The page width, or zero for every matching row.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The requested window together with the total number of assignments the whole filtered set holds.
    /// Each assignment carries its role and its account, because all three records compose the answer -
    /// the two assignment dates the screen renders live on the assignment and exist nowhere else, while the
    /// name it renders lives on the account.
    /// </returns>
    /// <remarks>
    /// MIGRATION: net-new, for the same reason as <see cref="ListAsync"/>. The terminal
    /// <c>GetUserRolesByUsername</c> statement answered a null login name by returning EVERY assignment in
    /// the portal joined to its account and its role, and the account filter, the ordering and the page were
    /// composed above it - so a page of ten memberships materialised every membership of the role, with its
    /// account graph, and sorted the lot. This member asks the same question of the same join and lets the
    /// store settle all four.
    /// </remarks>
    Task<PagedResult<UserRole>> ListRoleMembershipsAsync(
        int portalId,
        string roleName,
        string? accountQuery,
        string? sortBy,
        bool descending,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new role assignment for insertion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Realises membership <c>DataProvider.vb:L112 AddUserRole(PortalID, UserId, RoleId,
    /// EffectiveDate, ExpiryDate)</c>. The portal argument is dropped for the reason given in the
    /// migration note above, and the remaining four values are carried by the entity. Both dates stay
    /// nullable, so an assignment with no expiry is stored as a null rather than as a far-future date.
    /// Nothing is written until <see cref="IUnitOfWork.SaveChangesAsync"/> runs, after which
    /// <see cref="UserRole.UserRoleId"/> holds the generated key.
    /// </para>
    /// <para>
    /// EVERY VALUE ON THE ASSIGNMENT IS STAGED EXACTLY AS SUPPLIED, and an implementer must not amend
    /// one. The caller states the final effective date, the final expiry and the final trial-used flag;
    /// deriving a term from the role's billing or trial columns, clearing an effective date that has
    /// passed, or reinterpreting a particular instant as "no bound" are all subscription rules, and an
    /// implementation that applied them would be a second, invisible copy of rules the Application layer
    /// already owns - two copies that agree until one is amended and then disagree on precisely the case
    /// that prompted the amendment.
    /// </para>
    /// <para>
    /// "No bound" is therefore <see langword="null"/> and nothing else. The legacy absent-date marker
    /// <c>Null.NullDate</c> (<c>DateTime.MinValue</c>) is unstorable in these columns - both are SQL
    /// Server <c>datetime</c>, whose range begins at 1753-01-01 - so a caller translates it to a null
    /// before staging, and an implementer passing it through is refused loudly by the store rather than
    /// quietly reinterpreted.
    /// </para>
    /// </remarks>
    /// <param name="userRole">The assignment to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddUserRoleAsync(UserRole userRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an existing role assignment's modifications for update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Realises membership <c>DataProvider.vb:L113 UpdateUserRole(UserRoleId, EffectiveDate,
    /// ExpiryDate)</c>. The legacy member could rewrite only the two dates; passing the entity also
    /// lets the trial-used flag be persisted, which the legacy cancellation path depended on when it
    /// back-dated an expiry in order to retain the fact that a trial had been consumed. Nothing is
    /// written until <see cref="IUnitOfWork.SaveChangesAsync"/> runs.
    /// </para>
    /// <para>
    /// As on <see cref="AddUserRoleAsync"/>, every value is staged exactly as supplied. An expiry the
    /// caller states is persisted whether it lies in the future or in the past - which is what keeps the
    /// expire-rather-than-delete cancellation described on <see cref="DeleteUserRoleAsync"/> reachable at
    /// all, since an implementer that re-derived the value would silently undo the back-dating the caller
    /// had just computed.
    /// </para>
    /// </remarks>
    /// <param name="userRole">The assignment whose stored row is to be brought into line with it.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateUserRoleAsync(UserRole userRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the removal of one user account's assignment to one role.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L114 DeleteUserRole(UserId, RoleId)</c>, which
    /// identified the row by the pair rather than by its own key and took no portal argument; that
    /// shape is preserved. Removing an assignment that does not exist is not an error, exactly as the
    /// legacy delete affected no row and reported nothing.
    /// <para>
    /// Removal is not the only way a membership ends, and the two are not interchangeable. The legacy
    /// cancellation path deliberately expired an assignment instead of deleting it whenever a trial
    /// had been consumed, so that the consumed-trial fact survived; a caller reproducing that
    /// behaviour uses <see cref="UpdateUserRoleAsync"/>. Nothing is written until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> runs.
    /// </para>
    /// <para>
    /// THE CHOICE BETWEEN THE TWO IS THE CALLER'S, AND THIS MEMBER IS UNCONDITIONAL. An implementer must
    /// not test the role's fee and the assignment's trial-used flag and quietly expire the row instead:
    /// that test is a paid-membership rule, the fee is not even a column on the assignment, and a caller
    /// that asked for removal must not be answered with a retention it cannot observe. A caller reaches
    /// this member only having decided that deletion is what it wants.
    /// </para>
    /// </remarks>
    /// <param name="userId">The account whose assignment is to be removed.</param>
    /// <param name="roleId">The role to remove it from. Zero is a real key.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the removal has been staged.</returns>
    Task DeleteUserRoleAsync(int userId, int roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the portal's publicly available roles - those a user account may subscribe itself to.
    /// </summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L115 GetServices(PortalId, UserId)</c>. The legacy name
    /// is not carried over because it described neither its argument nor its result; the terminal
    /// procedure at <c>04.05.00.SqlDataProvider:L18-L37</c> selects from the roles table
    /// <c>where R.PortalId = @PortalId and R.IsPublic = 1</c>, so what it returns is the portal's
    /// subscribable roles and nothing else. The portal condition is a strict equality, so
    /// installation-wide roles are excluded exactly as they were.
    /// <para>
    /// The account is still an argument because the legacy projection used it: two correlated
    /// subqueries annotated each row with that account's own expiry date and with the key of its
    /// existing assignment, so a subscription page could show which offers had already been taken up.
    /// This contract returns roles rather than that flattened projection, so the caller composes the
    /// same view by pairing this result with <see cref="GetUserRolesAsync"/> - which keeps the
    /// account-specific part of the answer outside the role rows themselves.
    /// </para>
    /// </remarks>
    /// <param name="portalId">The portal whose subscribable roles are wanted.</param>
    /// <param name="userId">
    /// The account the offers are being listed for. It does not widen or narrow the set of roles
    /// returned; it identifies whose subscription state the caller will pair with them.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The portal's public roles; an empty list when it publishes none.
    /// </returns>
    Task<IReadOnlyList<Role>> GetSubscribableRolesAsync(int portalId, int userId, CancellationToken cancellationToken = default);
}
