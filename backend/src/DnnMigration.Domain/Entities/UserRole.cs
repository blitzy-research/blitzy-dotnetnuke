using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

// =====================================================================================
// dbo.UserRoles - the row that grants one account one role, for a bounded stretch of time.
//
// MIGRATION: this replaces DotNetNuke.Entities.Users.UserRoleInfo, and the single most
// important thing it changes is the base class. UserRoleInfo.vb line 43 declares
// "Inherits RoleInfo", so every one of the role definition's own members - PortalID,
// RoleName, Description, ServiceFee, BillingFrequency, BillingPeriod, TrialFrequency,
// TrialPeriod, IsPublic, AutoAssignment, RoleGroupID and the rest - arrived on the
// assignment object as if the assignment owned them. It does not. Relationally this table
// is a link table: three keys, two dates and a flag. The inheritance was a convenience for
// data binding a Web Forms grid, not a statement about the schema, and it is exactly the
// kind of workaround Rule T8 removes rather than translates.
//
// The cost of that flattening is visible at the legacy call sites. RoleController.vb line
// 494 reads "userRole.ServiceFee > 0.0" and line 305 assigns "objUserRole.PortalID", both
// touching members that no column of dbo.UserRoles backs. In the target the same facts are
// reached through the Role navigation below, which is honest about where they live and
// costs one join that the legacy code was performing anyway.
//
// MIGRATION: the terminal table has EXACTLY SIX columns. The chain that produced them,
// verified statement by statement across the 88 scripts in
// Website/Providers/DataProviders/SqlDataProvider:
//
//   01.00.00 lines 238-244  CREATE TABLE [dbo].[UserRoles] with five columns -
//                           UserRoleID int IDENTITY(1,1) NOT NULL, UserID int NOT NULL,
//                           RoleID int NOT NULL, ExpiryDate datetime NULL,
//                           IsTrialUsed bit NULL.
//   01.00.00 lines 441-446  CONSTRAINT PK_UserRoles PRIMARY KEY CLUSTERED (UserRoleID).
//   01.00.00 lines 688-700  FK_UserRoles_Roles (RoleID) and FK_UserRoles_Users (UserID),
//                           both ON DELETE CASCADE NOT FOR REPLICATION.
//   03.02.03 lines 379-380  ALTER TABLE ... ADD EffectiveDate datetime NULL - the sixth and
//                           final column, which is why EffectiveDate is declared last here:
//                           the property order below is the physical column order of the
//                           terminal table, not an alphabetical or aesthetic choice.
//   04.00.04 lines 415-419  the same ADD, wrapped in an fn_GetVersion(3,2,3) guard so an
//                           installation that already ran 03.02.03 does not add it twice.
//                           A second, guarded ADD is not a seventh column.
//
// No later script adds, drops, widens or renames a column of this table. Only the terminal
// state is meaningful (Rule T4), and the terminal state is those six columns.
//
// MIGRATION: three legacy members are deliberately absent, and none of them is a column.
//
//   FullName and Email (UserRoleInfo.vb lines 46-47, 71-87) are projections of the joined
//   dbo.Users row, denormalised onto the assignment so a grid could bind to a flat list.
//   They belong to an Application-layer DTO, which is free to shape a read model however
//   the screen needs. Carrying them here would let a caller mutate a copy of the account's
//   name and believe it had renamed the account.
//
//   Subscribed (UserRoleInfo.vb lines 51, 116-123) is not a column of this table at all: no
//   CREATE TABLE or ALTER TABLE statement anywhere in the chain mentions it. Where the name
//   does appear it is a computed SELECT alias in the role-listing procedures - literally
//   "'Subscribed' = ( select UserRoleId from UserRoles where ... )" at 01.00.08 line 7060,
//   02.00.00 line 2473, 03.00.01 line 1353, 04.05.00 line 33 and 04.06.00 line 1010, which
//   is a correlated existence test restating whether a row like this one exists. Persisting
//   it on the row whose existence it reports would be circular, so the flag is not modelled
//   here; a read model that wants it derives it, exactly as the SQL did. (The unrelated
//   "@Subscribed bit" parameter at 01.00.05 line 664 and 01.00.08 line 5160 belongs to the
//   portal module-definition procedures and has nothing to do with role membership.)
//
// MIGRATION: Infrastructure owns the mapping and must map only those six columns, with the
// idiomatic property names bound to the legacy column names - UserRoleId to UserRoleID,
// UserId to UserID and RoleId to RoleID - preserving PK_UserRoles, both cascading foreign
// keys and the IDENTITY(1,1) seed without altering the schema (Rules T3 and T4). It must
// not read entity inheritance into the Role navigation: there is no discriminator column
// and no table-per-hierarchy or table-per-type arrangement to configure, because dbo.Roles
// and dbo.UserRoles are two tables in a foreign-key relationship and nothing more.
// =====================================================================================

/// <summary>
/// One account's membership of one role, optionally bounded by an effective date at the start
/// and an expiry date at the end.
/// </summary>
/// <remarks>
/// <para>
/// A plain persistence entity for the six-column <c>dbo.UserRoles</c> link table. It holds the
/// two foreign keys, the two optional bounds and the trial flag, and it computes nothing except
/// the temporal classification returned by <see cref="GetStatus(DateTime)"/>.
/// </para>
/// <para>
/// Both bounds are nullable, and a null means unbounded on that side rather than absent data: a
/// membership with no effective date is in force immediately, and one with no expiry date never
/// lapses. That is the shape the schema already had - both columns are declared
/// <c>datetime NULL</c> - and it is the shape the terminal <c>GetRolesByUser</c> procedure
/// tests for, admitting a row when the bound is null or when the current instant falls inside
/// it.
/// </para>
/// <para>
/// The type carries no attribute of any kind and takes no dependency outside the Domain layer:
/// no serialisation attribute, no validation attribute, no object-relational annotation. The
/// wire contract belongs to the Application-layer DTOs and the column mapping belongs to the
/// Infrastructure-layer entity configuration, which is what keeps this project free of package
/// and project references altogether (Rule T1).
/// </para>
/// </remarks>
public sealed class UserRole : Entity<int>
{
    /// <summary>
    /// Gets or sets the surrogate key of this membership row.
    /// </summary>
    /// <remarks>
    /// Backing column <c>UserRoleID int IDENTITY(1, 1) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 239), the clustered primary key
    /// <c>PK_UserRoles</c> (lines 441-446). The seed is 1, so unlike
    /// <c>dbo.Roles</c>, <c>dbo.Tabs</c> and <c>dbo.Modules</c> - each seeded at 0 - and
    /// <c>dbo.Portals</c> - seeded at -1 - the default value of this property is not a
    /// persisted key. It is still not read as "unsaved": whether an entity has been written is
    /// declared through <see cref="Entity{TId}.MarkIdentityPersisted"/> by code that already knows it -
    /// nothing declares it automatically - and never deduced from a key value anywhere in this model.
    /// </remarks>
    public int UserRoleId { get; set; }

    /// <inheritdoc />
    public override int Identity => UserRoleId;

    /// <summary>
    /// Gets or sets the account whose membership this row records.
    /// </summary>
    /// <remarks>
    /// Backing column <c>UserID int NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> line 240),
    /// the dependent end of <c>FK_UserRoles_Users</c> (lines 697-700), which cascades on
    /// delete so removing an account withdraws its memberships. The corresponding navigation is
    /// <see cref="User"/>.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// Gets or sets the role being granted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backing column <c>RoleID int NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> line 241), the
    /// dependent end of <c>FK_UserRoles_Roles</c> (lines 689-695), which also cascades on
    /// delete. The corresponding navigation is <see cref="Role"/>.
    /// </para>
    /// <para>
    /// MIGRATION: this property is what replaces the inheritance. <c>UserRoleInfo</c> obtained
    /// its role identifier by inheriting <c>RoleInfo.RoleID</c> rather than by declaring a
    /// foreign key, which is why the legacy type also inherited every other role column. Note
    /// that <c>dbo.Roles.RoleID</c> is declared <c>IDENTITY(0, 1)</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 115), so 0 is a legitimate role identifier here and
    /// must never be treated as "no role selected".
    /// </para>
    /// </remarks>
    public int RoleId { get; set; }

    /// <summary>
    /// Gets or sets the instant at which this membership lapses, or <see langword="null"/> when it
    /// never lapses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backing column <c>ExpiryDate datetime NULL</c> (<c>01.00.00.SqlDataProvider</c> line 242),
    /// present since the baseline. The bound is inclusive: the terminal <c>GetRolesByUser</c>
    /// procedure admits a row while <c>ExpiryDate &gt;= getdate()</c>, so a membership expiring at
    /// this exact instant is still in force.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy property was a non-nullable VB <c>Date</c>
    /// (<c>UserRoleInfo.vb</c> lines 49, 98-105), so it could not represent the nullable column
    /// and carried absence as the <c>Null.NullDate</c> sentinel - <c>Date.MinValue</c>, per
    /// <c>Null.vb</c> lines 66-70 - which <c>RoleController.vb</c> line 76 writes explicitly when
    /// adding an unbounded membership. Under Rule T7 the nullable CLR type replaces the sentinel,
    /// and nothing in this class initialises the property to <c>DateTime.MinValue</c>. Note that
    /// the sentinel could never have reached the column in any case: SQL Server's <c>datetime</c>
    /// range begins at 1753-01-01, which is precisely why <c>Null.GetNull</c> (<c>Null.vb</c>
    /// lines 183-186) converted it to <c>DBNull</c> on the way out.
    /// </para>
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Gets or sets whether this membership has already consumed the role's trial period, or
    /// <see langword="null"/> when nothing has been recorded either way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backing column <c>IsTrialUsed bit NULL</c> (<c>01.00.00.SqlDataProvider</c> line 243).
    /// </para>
    /// <para>
    /// MIGRATION: the column is nullable but the legacy property was a non-nullable VB
    /// <c>Boolean</c> (<c>UserRoleInfo.vb</c> lines 50, 107-114), and <c>Null.NullBoolean</c> is
    /// <see langword="false"/> (<c>Null.vb</c> lines 76-80), so hydration silently collapsed SQL
    /// <c>NULL</c> into "trial not used". <c>RoleController.vb</c> line 494 then branches on the
    /// collapsed value - <c>userRole.ServiceFee &gt; 0.0 AndAlso userRole.IsTrialUsed</c> - and so
    /// cannot distinguish a recorded <see langword="false"/> from no record at all. The nullable
    /// type restores the third state, and the Domain layer must not collapse it: whether an
    /// unrecorded flag should be read as "not used" is a decision for the Application service
    /// that needs an answer, taken where it can be seen, not hidden in a property getter.
    /// </para>
    /// </remarks>
    public bool? IsTrialUsed { get; set; }

    /// <summary>
    /// Gets or sets the instant from which this membership counts, or <see langword="null"/> when
    /// it counts immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backing column <c>EffectiveDate datetime NULL</c>, the sixth and last column of the table,
    /// added by <c>03.02.03.SqlDataProvider</c> lines 379-380 and re-added under a version guard
    /// by <c>04.00.04.SqlDataProvider</c> lines 415-419. Declared last here to match the physical
    /// column order that late arrival produced. The bound is inclusive, mirroring
    /// <c>EffectiveDate &lt;= getdate()</c> in the terminal <c>GetRolesByUser</c> procedure.
    /// </para>
    /// <para>
    /// MIGRATION: nullable for the same reason as <see cref="ExpiryDate"/>, and likewise never
    /// initialised to a sentinel. <c>RoleController.vb</c> line 531 clears a past effective date
    /// back to <c>Null.NullDate</c> on every write; that per-write normalisation is Application
    /// behaviour and is deliberately not reproduced here, because an entity that rewrote its own
    /// stored dates on assignment would make the row disagree with the caller who set it.
    /// </para>
    /// </remarks>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// Gets or sets the account this membership belongs to.
    /// </summary>
    /// <remarks>
    /// The principal end of <c>FK_UserRoles_Users</c>, keyed by <see cref="UserId"/> and cascading
    /// on delete (<c>01.00.00.SqlDataProvider</c> lines 697-700). Its inverse is
    /// <see cref="Entities.User.UserRoles"/>. Declared non-nullable because the foreign-key column
    /// is <c>NOT NULL</c>: every membership row necessarily has an account. That is a statement
    /// about the relationship rather than a promise that the graph is loaded - a read path that
    /// does not include the principal leaves this unset, which is why CS8618 is suppressed
    /// solution-wide for materialised types. Setting this instead of <see cref="UserId"/> is the
    /// natural way to attach a membership to an account the database has not yet keyed.
    /// </remarks>
    public User User { get; set; }

    /// <summary>
    /// Gets or sets the role being granted.
    /// </summary>
    /// <remarks>
    /// The principal end of <c>FK_UserRoles_Roles</c>, keyed by <see cref="RoleId"/> and cascading
    /// on delete (<c>01.00.00.SqlDataProvider</c> lines 689-695). Its inverse is
    /// <see cref="Entities.Role.UserRoles"/>, and non-nullable for the same reason as
    /// <see cref="User"/>. MIGRATION: this navigation, and not a base class, is how the role's own
    /// members are reached. Every property the legacy assignment object inherited from
    /// <c>RoleInfo</c> - the billing frequency and period, the trial frequency and period, the
    /// service fee, the role name and the owning portal - is read from here.
    /// </remarks>
    public Role Role { get; set; }

    /// <summary>
    /// Classifies this membership at the supplied instant.
    /// </summary>
    /// <param name="asOfUtc">
    /// The instant to classify against, supplied by the caller. Application code passes the value
    /// it read from the injected clock; tests pass a fixed instant.
    /// </param>
    /// <returns>
    /// <see cref="RoleStatus.Expired"/> when the expiry bound is set and already in the past,
    /// <see cref="RoleStatus.Pending"/> when it is not yet expired and the effective bound is set and
    /// still in the future, and <see cref="RoleStatus.Active"/> otherwise - including when neither
    /// bound is set.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Pure and deterministic: it reads no ambient clock, performs no input or output, touches no
    /// service and caches nothing. The instant is a parameter precisely so that the classification
    /// is testable, which the legacy inline <c>Now</c> comparisons at <c>RoleController.vb</c>
    /// lines 530-535 were not (Rule T6). The status is computed on every call and is deliberately
    /// not a property: no column backs it, so exposing it as one would invite a mapping and, with
    /// it, a schema change that Rule T4 forbids.
    /// </para>
    /// <para>
    /// Both bounds are inclusive, which is not an arbitrary choice but the measured behaviour of
    /// the terminal <c>GetRolesByUser</c> procedure at <c>04.00.04.SqlDataProvider</c>
    /// lines 443-444:
    /// </para>
    /// <code>
    /// AND (EffectiveDate &lt;= getdate() or EffectiveDate is null)
    /// AND (ExpiryDate   &gt;= getdate() or ExpiryDate   is null)
    /// </code>
    /// <para>
    /// A membership sitting exactly on either bound is therefore <see cref="RoleStatus.Active"/>,
    /// and this method's active set is the same set that predicate admits. The Infrastructure
    /// repository translates the same test into SQL for server-side filtering, so the two must
    /// agree; only the comparisons that fall strictly outside a set bound yield a non-active
    /// result here.
    /// </para>
    /// <para>
    /// MIGRATION: "NO BOUND" IS ONE VALUE, AND IT IS <see langword="null"/>. Rule T7 keeps sentinel
    /// knowledge at the boundary and out of the Domain, and this method holds none: a bound is set
    /// when it is not null, and that is the whole of the test. An earlier revision additionally
    /// treated <c>DateTime.MinValue</c> - the legacy <c>Null.NullDate</c> marker, which the
    /// non-nullable legacy property used to carry absence - as unbounded, on the reasoning that a
    /// migrated row should not classify differently from its legacy self. That allowance was wrong
    /// on both counts and has been removed.
    /// </para>
    /// <para>
    /// It was unnecessary, because the marker cannot arrive. The legacy write path converted it to
    /// <c>DBNull</c> on every write - <c>Null.GetNull</c> (<c>Null.vb</c> lines 183-186) substitutes
    /// <c>DBNull</c> when the date part equals <c>NullDate.Date</c>, and both members that wrote these
    /// two columns passed both values through it (<c>AddUserRole</c> and <c>UpdateUserRole</c>,
    /// membership <c>DataProvider/SqlDataProvider.vb</c> lines 280-286). Independently of that, both
    /// columns are SQL Server <c>datetime</c>, whose range begins at 1753-01-01: the value 0001-01-01
    /// is not merely absent from them, it is unstorable, and an attempt to write it is refused by the
    /// database. So no stored row can produce it, and the target reproduces <c>GetNull</c> explicitly
    /// one layer higher - <c>RoleService.NormalizeLegacyDateMarker</c>, reached from
    /// <c>DeriveAssignmentDates</c>, reads a submitted marker as absence before either assignment write
    /// is staged. It lives there rather than in the repository because interpreting a bound is a
    /// subscription rule, and one rule with two implementations is worse than either alone.
    /// </para>
    /// <para>
    /// And it was harmful, because the allowance is indistinguishable from a genuine bound in the
    /// only direction that matters. This classification decides whether a membership is in force, and
    /// an administrator role assignment classified <see cref="RoleStatus.Active"/> is what the
    /// tenant-administration policy grants on. A Domain that silently reinterprets one particular
    /// stored instant as "unbounded" is a Domain in which the authorisation answer depends on a
    /// sentinel table, which is exactly the coupling Rule T7 exists to prevent.
    /// </para>
    /// <para>
    /// MIGRATION: expiry is tested before the effective bound, and the reason is worth recording
    /// because the choice is observable. The two bounds really can contradict each other - an expiry
    /// already past while the effective date is still in the future - because the cancellation path
    /// back-dates the expiry by a day to retain the trial-used fact and never touches the effective
    /// date. That path exists in the legacy source at <c>RoleController.vb</c> lines 495-496 and is
    /// carried into the target verbatim, so the state is reachable in current code, not merely in
    /// theory. A membership in it has been cancelled; reporting <see cref="RoleStatus.Pending"/>
    /// would tell an administrator it is about to begin, which is the opposite of the truth.
    /// Expiry is therefore terminal and outranks a start that has not yet arrived.
    /// </para>
    /// <para>
    /// This ordering also keeps the type consistent with the two places the decision is already
    /// written down - the remarks on <see cref="RoleStatus"/>, which state the derivation a
    /// consuming entity is to implement, and the repository's migration notes. Note that the choice
    /// cannot be settled by the legacy SQL: that predicate only ever partitions memberships into
    /// in-force and not-in-force and says nothing about how to label the remainder, so either
    /// ordering reproduces it exactly. Both orderings are also identical for every membership whose
    /// bounds are consistent, which is every membership the ordinary write paths produce.
    /// </para>
    /// </remarks>
    public RoleStatus GetStatus(DateTime asOfUtc)
    {
        // Each test reads "the bound is set, and the instant falls strictly outside it". A bound
        // counts as set when it is not null, and nothing else - no sentinel is recognised here, for
        // the reasons the remarks set out and cite.

        // Set, and strictly before the instant: it has lapsed. Tested FIRST because expiry is
        // terminal - see the ordering paragraph in the remarks, which records why and cites the
        // cancellation path that makes the two bounds observably contradictory.
        if (ExpiryDate is DateTime expiry && expiry < asOfUtc)
        {
            return RoleStatus.Expired;
        }

        // Set, and strictly after the instant: granted, but not yet in force.
        if (EffectiveDate is DateTime effective && effective > asOfUtc)
        {
            return RoleStatus.Pending;
        }

        // In force: inside both bounds, on either bound, or bounded on neither side.
        return RoleStatus.Active;
    }

    /// <summary>
    /// Reports whether any of a set of assignments places its holder in one particular role, in force at one
    /// particular instant.
    /// </summary>
    /// <param name="assignments">The assignments to examine.</param>
    /// <param name="roleId">The role the holder must be in.</param>
    /// <param name="asOfUtc">The instant each assignment's validity window is judged against.</param>
    /// <returns><see langword="true"/> when at least one assignment names that role and is in force.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="assignments"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// WHY THIS IS A DOMAIN MEMBER RATHER THAN A PREDICATE AT EACH CALL SITE. "Is this account in that role
    /// right now" is asked in two layers for two different purposes - by the API layer when it authorises a
    /// request, and by the Application layer when a service needs to know whether its caller holds
    /// administrative authority - and the two must never be able to answer differently. Expressed at each
    /// call site it is a two-line LINQ predicate, which is exactly the kind of expression that gets copied
    /// with one clause subtly altered; expressed here it is one implementation over
    /// <see cref="GetStatus(DateTime)"/>, which is itself the single implementation of the validity window.
    /// </para>
    /// <para>
    /// EVERY ASSIGNMENT IS EXAMINED, not just the first that names the role. Nothing in the schema prevents
    /// two rows pairing the same account with the same role, so stopping at the first match would let a
    /// lapsed duplicate hide a valid grant. The role is compared by KEY and never by name, because role names
    /// are not unique in this schema and a name-based test is satisfied by a role belonging to another tenant.
    /// </para>
    /// <para>
    /// It asks nothing about which tenant the role belongs to; that is the caller's responsibility, and the
    /// callers satisfy it by obtaining the assignments through a portal-scoped read.
    /// </para>
    /// </remarks>
    public static bool AnyActiveInRole(
        IEnumerable<UserRole> assignments,
        int roleId,
        DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        foreach (UserRole assignment in assignments)
        {
            if (assignment.RoleId == roleId && assignment.GetStatus(asOfUtc) == RoleStatus.Active)
            {
                return true;
            }
        }

        return false;
    }
}
