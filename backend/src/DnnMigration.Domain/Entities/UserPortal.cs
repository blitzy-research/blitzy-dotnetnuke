using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// =====================================================================================
// dbo.UserPortals - the row that makes one account a member of one tenant.
//
// MIGRATION: this entity has no legacy counterpart because the legacy model had no join type
// at all. The per-portal facts were flattened onto the user object: UserInfo.vb declared
// "Private _PortalID As Integer" (line 52), seeded it to the Null.NullInteger sentinel in the
// constructor (lines 66-69) and exposed it as a settable property (lines 219-226), which made
// every hydrated user object silently portal-specific and invited authorisation checks against
// ambient state. Rule T8 replaces that projection with this typed link entity: an account is
// one row in dbo.Users, and its membership of each portal is one row here.
//
// MIGRATION: the terminal table has EXACTLY FIVE columns, and the path it took to get there is
// why the shape must never be inferred from any single script. The chain, verified statement by
// statement in Website/Providers/DataProviders/SqlDataProvider:
//
//   01.00.00:153-157   CREATE TABLE [dbo].[UserPortals] ([UserId] int NOT NULL,
//                      [PortalId] int NOT NULL, [Authorized] bit NULL) - American spelling,
//                      and nullable.
//   01.00.00:405-411   CONSTRAINT [PK_UserPortals] PRIMARY KEY CLUSTERED ([UserId],[PortalId]).
//   01.00.00:616-628   FK_UserPortals_Portals (PortalId -> Portals.PortalID, ON DELETE CASCADE)
//                      and FK_UserPortals_Users (UserId -> Users.UserID).
//   01.00.02:254-256   ADD CreatedDate datetime NULL, LastLoginDate datetime NULL.
//   01.00.10:759-773   IX_UserPortals on (PortalId), IX_UserPortals_1 on (UserID).
//   02.00.00:7209-7210 ADD UserPortalId int NOT NULL IDENTITY (1, 1) - a surrogate added
//                      ALONGSIDE the primary key, never promoted to it.
//   02.02.01:54-56     DROP COLUMN Authorized, CreatedDate, LastLoginDate - all three at once.
//   03.00.10:11-16     ADD CreatedDate datetime DEFAULT getDate() NOT NULL, behind an
//                      IF NOT EXISTS column probe.
//   03.00.13:44-53     FK_UserPortals_Users recreated WITH ON DELETE CASCADE, so both foreign
//                      keys cascade in the terminal schema.
//   03.01.01:1325-1338 the auto-named default constraint is dropped through dynamic SQL, the
//                      column is re-asserted NOT NULL, and DF_UserPortals_CreatedDate
//                      DEFAULT (getdate()) is added - the terminal, named form.
//   03.02.03:637-642   ADD Authorised bit NOT NULL CONSTRAINT DF_UserPortals_Authorised
//                      DEFAULT 1 - the British spelling, which is terminal.
//   04.00.04:680-684   the identical guarded re-add, for installations whose lineage skipped
//                      03.02.03. 04.00.04:2358-2366 then back-fills the flag with
//                      "SET UserPortals.Authorised = AM.IsApproved" read out of
//                      aspnet_Membership, and 04.00.04:771-787 rebuilds vw_Users so that
//                      UP.PortalId and UP.Authorised (line 784) reach the user read path
//                      through a LEFT OUTER JOIN onto this table.
//   UnInstall:225,227  names the surviving constraints - PK_UserPortals and
//                      DF_UserPortals_CreatedDate - confirming the terminal set.
//
// Terminal ordinal order is therefore UserId, PortalId, UserPortalId, CreatedDate, Authorised.
// Nothing else survives, and nothing else may be added: Authorized (the American spelling) and
// LastLoginDate were dropped at 02.02.01:54-56, LastAccessDate never existed on this table, and
// a per-portal user name, electronic-mail address or display name never existed either - those
// are columns of dbo.Users, reachable through the User navigation.
//
// MIGRATION: the storage key is NOT the surrogate. Domain identity and database primary key are
// two different things on this table, and each is stated separately below so that neither can be
// mistaken for the other. See the Identity member and the mapping obligations in the class
// remarks.
// =====================================================================================

/// <summary>
/// One account's membership of one portal - the <c>dbo.UserPortals</c> row that makes an
/// identity visible to a tenant, records when it joined and whether the tenant admits it.
/// </summary>
/// <remarks>
/// <para>
/// This type is a persistence model and nothing else: five mapped scalars, two navigations, no
/// behaviour. Whether a membership admits its holder is a question about a portal and a point in
/// time, so it is answered by the Application services and the authorisation policies that hold
/// that context, never by a method here.
/// </para>
/// <para>
/// <b>Two notions of identity, and why they must not be conflated.</b> The database primary key
/// of <c>dbo.UserPortals</c> is the composite <c>(UserId, PortalId)</c> declared as the clustered
/// <c>PK_UserPortals</c> in the baseline script (<c>01.00.00.SqlDataProvider</c> lines 405-411)
/// and never replaced since. The <c>UserPortalId</c> identity column arrived two versions later
/// (<c>02.00.00.SqlDataProvider</c> lines 7209-7210) as
/// <c>UserPortalId int NOT NULL IDENTITY (1, 1)</c>, added next to that key rather than in place
/// of it. Both are unique, so both identify a row - but only one of them is the key the schema
/// actually has.
/// </para>
/// <para>
/// <see cref="Identity"/> deliberately returns <see cref="UserPortalId"/>, because
/// <see cref="Entity{TId}"/> compares a single scalar and the surrogate is the only single scalar
/// that distinguishes two memberships of the same account. That choice is an equality concern and
/// carries no mapping authority whatsoever. The Infrastructure entity configuration therefore
/// MUST state the schema as the schema has it:
/// </para>
/// <list type="bullet">
///   <item>
///   <c>HasKey(x =&gt; new { x.UserId, x.PortalId })</c>, named <c>PK_UserPortals</c>, remains the
///   key of the entity type.
///   </item>
///   <item>
///   <see cref="UserPortalId"/> is mapped as a store-generated, NON-key value -
///   <c>ValueGeneratedOnAdd()</c> with the identity seed and increment of one - so that Entity
///   Framework reads it back after an insert.
///   </item>
///   <item>
///   Nothing in the mapping may promote <see cref="UserPortalId"/> to the primary key, drop the
///   composite, or emit any statement that alters the existing key. Rule T4 holds the schema
///   immutable, and the baseline migration for this model is intentionally empty; a "corrected"
///   key here would rewrite the clustered index of a live installation.
///   </item>
/// </list>
/// <para>
/// <b>Approval is context-sensitive, and the context lives outside this entity.</b>
/// <see cref="IsAuthorised"/> answers for an ordinary portal user and for nobody else. The legacy
/// provider is explicit about it: under the guard <c>If Not objUserInfo.IsSuperUser Then</c>, with
/// the comment "For Users the approved/authorised info is stored in UserPortals", it reads
/// <c>isApproved = Convert.ToBoolean(dr("Authorised"))</c>
/// (<c>AspNetMembershipProvider.vb</c> lines 355-361). A super user has no portal to be authorised
/// against, so its approval comes from the external membership snapshot instead - surfaced on the
/// account as <see cref="User.IsApproved"/> and selected by <see cref="User.IsSuperUser"/>. Which
/// store answers is a decision for the services and repositories that know the caller; this row
/// only ever states the ordinary-user answer for one portal.
/// </para>
/// <para>
/// <b>Column history that the mapping must honour.</b> Two of the five columns left and returned,
/// and the terminal form is the only authoritative one.
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Column</term>
///     <description>History and terminal form</description>
///   </listheader>
///   <item>
///     <term><c>UserId</c>, <c>PortalId</c></term>
///     <description>
///     Present from the baseline (<c>01.00.00</c> lines 153-157), both <c>int NOT NULL</c>, and
///     together the primary key. Their foreign keys both cascade in the terminal schema: the
///     portal key from the baseline (lines 616-622) and the user key from its recreation at
///     <c>03.00.13</c> lines 44-53.
///     </description>
///   </item>
///   <item>
///     <term><c>UserPortalId</c></term>
///     <description>
///     Added at <c>02.00.00</c> lines 7209-7210 as <c>IDENTITY (1, 1)</c>. Store-generated, and
///     not the primary key.
///     </description>
///   </item>
///   <item>
///     <term><c>CreatedDate</c></term>
///     <description>
///     Added nullable at <c>01.00.02</c> lines 254-256, dropped outright at <c>02.02.01</c> lines
///     54-56, re-added as <c>datetime DEFAULT getDate() NOT NULL</c> at <c>03.00.10</c> lines
///     11-16, then normalised at <c>03.01.01</c> lines 1325-1338 into the terminal
///     required-with-named-default form. Required is authoritative.
///     </description>
///   </item>
///   <item>
///     <term><c>Authorised</c></term>
///     <description>
///     Began as the nullable American-spelled <c>Authorized</c> (<c>01.00.00</c> lines 153-157),
///     was dropped at <c>02.02.01</c> lines 54-56, and returned British-spelled and required at
///     <c>03.02.03</c> lines 637-642 with <c>DF_UserPortals_Authorised DEFAULT 1</c>. The
///     spelling is load-bearing and must not be "corrected" in the mapping.
///     </description>
///   </item>
/// </list>
/// <para>
/// Nothing here decides whether the row has been written. That declaration is made through
/// <see cref="Entity{TId}.MarkIdentityPersisted"/> by code that already knows the answer, and by
/// nothing automatically, for the reason set out on <see cref="Entity{TId}"/>: this schema seeds real
/// identities at values other codebases reserve for "not saved yet".
/// </para>
/// </remarks>
public sealed class UserPortal : Entity<int>
{
    // =====================================================================================
    // PERSISTED SCALARS (5)
    //
    // One property per terminal column and no property that is not a terminal column. They are
    // declared surrogate first, so that the Identity projection sits immediately beside the value
    // it projects and the two key columns follow together; the table's own ordinal order is the
    // one recorded in the file banner above and is not reproduced here, because column order is a
    // storage detail that no mapping or query depends on.
    //
    // Per Rule T7 every one of these columns is required in the terminal schema, so none is
    // nullable and none carries a legacy sentinel: absence is not representable on this row, and
    // a membership that does not exist is simply a row that is not there.
    // =====================================================================================

    /// <summary>
    /// Gets or sets the store-generated surrogate key of this membership row
    /// (<c>UserPortalId</c>).
    /// </summary>
    /// <remarks>
    /// Mapped to <c>UserPortalId int NOT NULL IDENTITY (1, 1)</c>, added at
    /// <c>02.00.00.SqlDataProvider</c> lines 7209-7210. It is unique, and it is <b>not</b> the
    /// primary key - see the class remarks. Value-generated on add, so it holds zero until the
    /// database assigns it and the persistence layer reads it back.
    /// </remarks>
    public int UserPortalId { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see cref="UserPortalId"/>. The surrogate is used rather than either key column
    /// because <see cref="Entity{TId}"/> compares one scalar, and neither <see cref="UserId"/>
    /// nor <see cref="PortalId"/> identifies a membership on its own - an account with two
    /// tenancies would otherwise report both rows as the same entity. This is equality only: the
    /// database key stays the composite <c>(UserId, PortalId)</c>, and the mapping must say so.
    /// </remarks>
    public override int Identity => UserPortalId;

    /// <summary>
    /// Gets or sets the identifier of the account this membership belongs to (<c>UserId</c>).
    /// </summary>
    /// <remarks>
    /// Mapped to <c>UserId int NOT NULL</c> from the baseline table
    /// (<c>01.00.00.SqlDataProvider</c> lines 153-157). It is simultaneously the first component
    /// of the composite primary key <c>PK_UserPortals</c> (lines 405-411) and the foreign key
    /// <c>FK_UserPortals_Users</c> to <c>dbo.Users</c>, which the <c>03.00.13</c> upgrade
    /// recreated with <c>ON DELETE CASCADE</c> (lines 44-53) - so deleting an account removes its
    /// memberships.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal this membership is scoped to
    /// (<c>PortalId</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mapped to <c>PortalId int NOT NULL</c> from the baseline table
    /// (<c>01.00.00.SqlDataProvider</c> lines 153-157). It is the second component of
    /// <c>PK_UserPortals</c> (lines 405-411), the foreign key <c>FK_UserPortals_Portals</c> to
    /// <c>dbo.Portals</c> with <c>ON DELETE CASCADE</c> (lines 616-622), and the column the
    /// non-clustered <c>IX_UserPortals</c> covers (<c>01.00.10.SqlDataProvider</c> lines
    /// 759-765).
    /// </para>
    /// <para>
    /// MIGRATION: zero is a real tenant here, not a missing one. <c>dbo.Portals.PortalID</c> is
    /// declared <c>IDENTITY(-1, 1)</c>, so -1 is its seed and first generated value while the shipped
    /// default portal row carries an explicit zero - both are real keys - and -1
    /// is also the value the legacy <c>Null.NullInteger</c> sentinel used for "no value". No code
    /// may read either value as absence, and because this property is part of the key and of a
    /// foreign key at once, the mapping supplies a sentinel outside the range of real identifiers
    /// so that a genuine tenant zero can still be recorded.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this portal admits the account - the ordinary
    /// user's approval flag (<c>Authorised</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mapped to the British-spelled column <c>Authorised</c>, and only to that name. The
    /// terminal declaration is <c>Authorised bit NOT NULL CONSTRAINT DF_UserPortals_Authorised
    /// DEFAULT 1</c> (<c>03.02.03.SqlDataProvider</c> lines 637-642, re-asserted for skipped
    /// lineages at <c>04.00.04.SqlDataProvider</c> lines 680-684). The mapping keeps the spelling
    /// exactly, keeps the column required, and preserves its admit-by-default meaning; binding
    /// the dropped American-spelled <c>Authorized</c> instead would fail against every
    /// installation in the field.
    /// </para>
    /// <para>
    /// MIGRATION: the initialiser is deliberate and is what preserves the default-true semantics
    /// of the schema in the object model. Three sources agree on it - the store default
    /// <c>DEFAULT 1</c> above; the legacy field, declared
    /// <c>Private _Approved As Boolean = True</c> at <c>UserMembership.vb</c> line 45; and the
    /// 4.0 data migration, which back-filled existing rows from the membership store with
    /// <c>SET UserPortals.Authorised = AM.IsApproved</c> (<c>04.00.04.SqlDataProvider</c> lines
    /// 2358-2366). The store default is intentionally not configured in the mapping, because a
    /// configured default that differs from the CLR default would silently reverse a request to
    /// create an unauthorised membership; the initialiser here carries it instead, and a caller
    /// that means <see langword="false"/> still gets <see langword="false"/>.
    /// </para>
    /// <para>
    /// MIGRATION: this flag speaks for ordinary portal users only. The legacy read is guarded by
    /// <c>If Not objUserInfo.IsSuperUser Then</c> (<c>AspNetMembershipProvider.vb</c> lines
    /// 355-361); for a super user the answer comes from the external membership snapshot rather
    /// than from any row in this table, and reaches the model as <see cref="User.IsApproved"/>.
    /// Services, repositories and authorisation policies apply that rule - it is not, and must
    /// not become, a computation on this entity.
    /// </para>
    /// </remarks>
    public bool IsAuthorised { get; set; } = true;

    /// <summary>
    /// Gets or sets the instant at which the account joined this portal (<c>CreatedDate</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mapped to <c>CreatedDate datetime NOT NULL</c> with the store default
    /// <c>DF_UserPortals_CreatedDate DEFAULT (getdate())</c>. This is a genuine column of the
    /// legacy table, which is precisely why this entity derives from <see cref="Entity{TId}"/>
    /// and not from the auditing base type: the value is per-portal membership data that the
    /// legacy application read and displayed, not an audit stamp the target added.
    /// </para>
    /// <para>
    /// MIGRATION: the column left and came back, and only its terminal form is authoritative. It
    /// was added nullable together with <c>LastLoginDate</c> at <c>01.00.02.SqlDataProvider</c>
    /// lines 254-256, dropped outright by <c>02.02.01.SqlDataProvider</c> lines 54-56, re-added
    /// as <c>datetime DEFAULT getDate() NOT NULL</c> behind a column probe at
    /// <c>03.00.10.SqlDataProvider</c> lines 11-16, and finally normalised at
    /// <c>03.01.01.SqlDataProvider</c> lines 1325-1338, where the auto-named default constraint
    /// is dropped through dynamic SQL, the column is re-asserted <c>NOT NULL</c> and the named
    /// <c>DF_UserPortals_CreatedDate</c> default is added. Required, therefore, and never
    /// nullable - the intermediate nullable form is history, not contract.
    /// </para>
    /// <para>
    /// The value is supplied by the caller from the injected clock. Per Rule T6 this entity reads
    /// no ambient time of its own, so a service that creates a membership states the instant
    /// explicitly and a test can pin it.
    /// </para>
    /// </remarks>
    public DateTime CreatedDate { get; set; }

    // =====================================================================================
    // NAVIGATIONS (2)
    //
    // Both are required: the relationship each one represents is required in the schema, because
    // its foreign-key column is NOT NULL and cascades. They are declared non-nullable to say so
    // in the type system.
    //
    // A navigation being non-nullable is a statement about the relationship, not a promise that
    // the graph is loaded. A read path that does not include one leaves it unset, which is why
    // CS8618 is suppressed solution-wide for materialised types - code that needs the principal
    // must include it rather than assume it.
    //
    // MIGRATION: neither replaces a legacy member. UserInfo.vb had no navigation to a portal or
    // to a membership row at all - it carried a bare portal identifier (lines 219-226) and the
    // provider set it per read (AspNetMembershipProvider.vb line 330). The inverse ends of both
    // relationships are the UserPortals collections on the two aggregates.
    // =====================================================================================

    /// <summary>
    /// Gets or sets the account this membership belongs to.
    /// </summary>
    /// <remarks>
    /// The principal end of <c>FK_UserPortals_Users</c>, keyed by <see cref="UserId"/> and
    /// cascading on delete (<c>03.00.13.SqlDataProvider</c> lines 44-53). Its inverse is
    /// <see cref="User.UserPortals"/>. Setting this instead of <see cref="UserId"/> is the
    /// natural way to attach a membership to an account that has not yet been written, since the
    /// database assigns the account key on insert.
    /// </remarks>
    public User User { get; set; }

    /// <summary>
    /// Gets or sets the portal this membership is scoped to.
    /// </summary>
    /// <remarks>
    /// The principal end of <c>FK_UserPortals_Portals</c>, keyed by <see cref="PortalId"/> and
    /// cascading on delete (<c>01.00.00.SqlDataProvider</c> lines 616-622). Its inverse is
    /// <see cref="Portal.UserPortals"/>. Setting this rather than <see cref="PortalId"/> also
    /// sidesteps the identity-seed collision described on that property, because the key travels
    /// with the tracked principal instead of being written by hand.
    /// </remarks>
    public Portal Portal { get; set; }
}
