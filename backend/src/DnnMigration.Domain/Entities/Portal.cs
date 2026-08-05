using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

// MIGRATION: this type models the terminal dbo.Portals BASE TABLE, not the legacy PortalInfo class,
// which was filled from the vw_Portals view. Eight PortalInfo members are therefore absent by design:
// Email, SuperTabId, AdministratorRoleName, RegisteredRoleName and Version came from the view's joins
// and subqueries, and Users, Pages and HomeDirectoryMapPath were getters that performed I/O.

// MIGRATION: a domain entity here carries no attribute of any kind. The legacy type was decorated for
// XML serialisation to drive portal templates; the wire contract now belongs to the Application DTOs,
// column binding to the Infrastructure entity configuration, and validation to the validators.

// MIGRATION: legacy null sentinels are not ported. Nullability is expressed by nullable CLR types and
// nothing else - no sentinel constant, no sentinel comparison, no property initialiser that plants
// one. Sentinel semantics survive only at the DTO and API boundary, where they are observable.

// MIGRATION: -1 and 0 are BOTH real portal keys, because PortalID is declared IDENTITY(-1, 1) while
// the legacy sentinel module also used -1 as its integer null. No member here may read either value as
// absent, unset or unsaved; absence of a portal is expressible only as a nullable key on another
// entity. The PortalId value object keeps that rule visible at Application boundaries.

// MIGRATION: HostFee is decimal rather than the legacy VB Single, because the terminal column is
// money. Binary floating point cannot hold a currency amount exactly, so this is a deliberate
// precision correction, and the Infrastructure configuration must map it with HasColumnType("money").

// MIGRATION: PortalGuid maps the legacy column named GUID and is renamed because GUID is not an
// idiomatic C# member name. It stays a plain Guid so the column binds with no value conversion; the
// PortalGuid value object guards the same handle at Application boundaries, where the all-zero value
// is rejected because the column is NOT NULL DEFAULT newid() and can never hold it.

// MIGRATION: two column names differ from their member names - the offset column is spelled
// TimezoneOffset with a lower-case z, and the identifier column is spelled GUID in full upper case -
// so the Infrastructure configuration must name columns explicitly. A by-convention match would break
// both, and SQL Server's case-insensitive identifier resolution is why the difference went unnoticed.

// MIGRATION: LogoFile and BackgroundFile carry the raw base-table value, which for rows written by
// later DotNetNuke versions is the literal token "fileid=N" rather than a path. The legacy view
// resolved it by joining Files; the Files subsystem is out of scope, so resolving it is an Application
// or Infrastructure projection concern and is deliberately not behaviour on this entity.

// MIGRATION: the legacy ProcessorPassword column stored a payment-gateway credential in clear text.
// The immutable column now carries only an opaque managed-secret reference. Its CLR name states the
// new contract while Infrastructure keeps HasColumnName("ProcessorPassword"); no plaintext credential
// may be assigned, projected, logged or returned.

// MIGRATION: UserRegistration and BannerAdvertising become enumerations over the same persisted
// ordinals, which are live data and must never be renumbered or reordered. Both terminal columns are
// NOT NULL, so there is no absent case and neither enumeration declares an unknown member.

// MIGRATION: a domain entity performs no I/O, so the legacy getters that counted rows or resolved a
// physical path have no counterpart here. Those values are Application-layer projections.

// MIGRATION: the six navigation collections are the inverse ends of six real foreign keys and are
// initialised empty. An empty collection means "this portal has none", never "not loaded" - which end
// was loaded is the repository's decision, so never infer load state from a count here.

/// <summary>
/// A DotNetNuke portal: the tenant container that owns a site's pages, modules, roles and member
/// accounts, modelling the terminal <c>dbo.Portals</c> base table.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a persisted column of that table, in the table's own column order, plus the six
/// navigation ends. The type holds no behaviour: tenant rules, template handling and quota
/// enforcement belong to the Application services, loading and saving to the repository and unit of
/// work, and column and table binding to the Infrastructure entity configuration.
/// </para>
/// <para>
/// The schema is immutable for this migration, so this file describes a table that already exists
/// rather than defining one. Two invariants govern every consumer: neither -1 nor 0 may be read as an
/// absent key, and <see cref="ProcessorCredentialReference"/> is an opaque managed-secret reference,
/// never the referenced credential itself.
/// </para>
/// </remarks>
public sealed class Portal : Entity<int>
{
    /// <summary>The value equality is based on, which is always <see cref="PortalId"/>.</summary>
    /// <remarks>Never mapped to a column; the entity configuration names <see cref="PortalId"/>.</remarks>
    public override int Identity => PortalId;

    /// <summary>
    /// The <c>PortalID</c> column: <c>int IDENTITY(-1, 1) NOT NULL</c>, the primary key, generated by
    /// the database.
    /// </summary>
    /// <remarks>
    /// Both -1 and 0 key real portals, so no comparison against either may be read as absence.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>The <c>PortalName</c> column: <c>nvarchar(128) NOT NULL</c>, the display name.</summary>
    /// <remarks>Non-nullable because the column is, so an empty value is a name of zero length.</remarks>
    public string PortalName { get; set; }

    /// <summary>
    /// The <c>LogoFile</c> column: <c>nvarchar(50) NULL</c>, either a file name or the raw
    /// <c>fileid=N</c> token. Unresolved here by design.
    /// </summary>
    public string? LogoFile { get; set; }

    /// <summary>
    /// The <c>FooterText</c> column: <c>nvarchar(100) NULL</c>. A stored empty string means an empty
    /// footer, not an absent one.
    /// </summary>
    public string? FooterText { get; set; }

    /// <summary>
    /// The <c>ExpiryDate</c> column: <c>datetime NULL</c>; null means the hosting arrangement does not
    /// expire.
    /// </summary>
    /// <remarks>
    /// The legacy property was a non-nullable date, so a SQL null arrived as the earliest representable
    /// date and "never expires" was indistinguishable from "expired in year one". Callers must test for
    /// null rather than for a floor date.
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// The <c>UserRegistration</c> column: <c>int NOT NULL</c> defaulting to zero, projected as
    /// <see cref="UserRegistrationMode"/>.
    /// </summary>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>
    /// The <c>BannerAdvertising</c> column: <c>int NOT NULL</c> defaulting to zero, projected as
    /// <see cref="BannerAdvertisingMode"/>.
    /// </summary>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>
    /// The <c>AdministratorId</c> column: <c>int NULL</c> keying the designated administrator account;
    /// null means none is assigned.
    /// </summary>
    /// <remarks>The legacy property carried -1 for "none"; a null must not be written back as -1.</remarks>
    public int? AdministratorId { get; set; }

    /// <summary>
    /// The <c>Currency</c> column: <c>char(3) NULL</c>, an ISO code such as <c>USD</c> used for
    /// paid-membership amounts.
    /// </summary>
    /// <remarks>Fixed-width in the database, so a shorter code is stored space-padded.</remarks>
    public string? Currency { get; set; }

    /// <summary>
    /// The <c>HostFee</c> column: <c>money NOT NULL</c> defaulting to zero, expressed in
    /// <see cref="Currency"/>.
    /// </summary>
    /// <remarks>
    /// Decimal rather than the legacy single-precision float: the exact type is required to reproduce
    /// stored amounts without rounding drift.
    /// </remarks>
    public decimal HostFee { get; set; }

    /// <summary>
    /// The <c>HostSpace</c> column: <c>int NOT NULL</c> defaulting to zero, a disk allowance in
    /// megabytes where zero conventionally means unlimited.
    /// </summary>
    /// <remarks>Enforcing the allowance is an Application-layer concern, not an invariant of this type.</remarks>
    public int HostSpace { get; set; }

    /// <summary>
    /// The <c>AdministratorRoleId</c> column: <c>int NULL</c> keying the role that confers portal
    /// administration; null means none is assigned.
    /// </summary>
    public int? AdministratorRoleId { get; set; }

    /// <summary>
    /// The <c>RegisteredRoleId</c> column: <c>int NULL</c> keying the role granted to every registered
    /// member; null means none is assigned.
    /// </summary>
    public int? RegisteredRoleId { get; set; }

    /// <summary>The <c>Description</c> column: <c>nvarchar(500) NULL</c>, used as page metadata.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The <c>KeyWords</c> column: <c>nvarchar(500) NULL</c>, a comma-separated list in legacy data,
    /// stored and returned verbatim.
    /// </summary>
    public string? KeyWords { get; set; }

    /// <summary>
    /// The <c>BackgroundFile</c> column: <c>nvarchar(50) NULL</c>, either a file name or the raw
    /// <c>fileid=N</c> token. Unresolved here by design.
    /// </summary>
    public string? BackgroundFile { get; set; }

    /// <summary>
    /// The legacy <c>GUID</c> column: <c>uniqueidentifier NOT NULL</c> defaulting to a newly generated
    /// value, so the database supplies it and the domain does not assign it.
    /// </summary>
    public Guid PortalGuid { get; set; }

    /// <summary>
    /// The <c>PaymentProcessor</c> column: <c>nvarchar(50) NULL</c>, the gateway name; null when no
    /// gateway is configured.
    /// </summary>
    public string? PaymentProcessor { get; set; }

    /// <summary>
    /// The <c>ProcessorUserId</c> column: <c>nvarchar(50) NULL</c>, the account identifier presented to
    /// the gateway.
    /// </summary>
    /// <remarks>
    /// The upgrade chain migrated the earlier PayPal-specific column into this one and dropped it, which
    /// is why no such member appears here.
    /// </remarks>
    public string? ProcessorUserId { get; set; }

    /// <summary>
    /// The legacy <c>ProcessorPassword</c> column: <c>nvarchar(50) NULL</c>, repurposed to hold an
    /// opaque managed-secret reference.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the column name cannot change under Rule T4 and is too narrow for safe envelope
    /// ciphertext. New writes therefore store a bounded <c>secret://</c> reference only. Existing
    /// plaintext values require operator rotation and replacement; application code never treats them
    /// as credentials or echoes them to a client.
    /// </remarks>
    public string? ProcessorCredentialReference { get; set; }

    /// <summary>
    /// The <c>SiteLogHistory</c> column: <c>int NULL</c>, days of site-log retention; null when none is
    /// configured.
    /// </summary>
    public int? SiteLogHistory { get; set; }

    /// <summary>
    /// The <c>HomeTabId</c> column: <c>int NULL</c> nominating the home page; only null means none,
    /// because zero keys a real page.
    /// </summary>
    public int? HomeTabId { get; set; }

    /// <summary>
    /// The <c>LoginTabId</c> column: <c>int NULL</c> nominating the sign-in page; null selects the
    /// default page rather than disabling sign-in.
    /// </summary>
    public int? LoginTabId { get; set; }

    /// <summary>
    /// The <c>UserTabId</c> column: <c>int NULL</c> nominating the user-account page; null selects the
    /// default page.
    /// </summary>
    public int? UserTabId { get; set; }

    /// <summary>
    /// The <c>DefaultLanguage</c> column: <c>nvarchar(10) NOT NULL</c> defaulting to <c>en-US</c>, a
    /// culture code.
    /// </summary>
    /// <remarks>No property initialiser: the database default is the single source of that value.</remarks>
    public string DefaultLanguage { get; set; }

    /// <summary>
    /// The <c>TimezoneOffset</c> column: <c>int NOT NULL</c> defaulting to -8, an offset from server
    /// time in minutes, where negative values are normal.
    /// </summary>
    /// <remarks>
    /// The SQL identifier spells the z in lower case, so the entity configuration must name the column
    /// explicitly instead of relying on a convention match.
    /// </remarks>
    public int TimeZoneOffset { get; set; }

    /// <summary>
    /// The <c>AdminTabId</c> column: <c>int NULL</c> rooting this portal's administration area; only
    /// null means none, because zero keys a real page.
    /// </summary>
    public int? AdminTabId { get; set; }

    /// <summary>
    /// The <c>HomeDirectory</c> column: <c>varchar(100) NOT NULL</c> defaulting to the empty string, a
    /// content directory relative to the application root such as <c>Portals/0</c>.
    /// </summary>
    /// <remarks>Resolving it to a physical location is an Application-layer concern.</remarks>
    public string HomeDirectory { get; set; }

    /// <summary>
    /// The <c>SplashTabId</c> column: <c>int NULL</c> nominating a splash page shown once before the
    /// home page; only null means none.
    /// </summary>
    public int? SplashTabId { get; set; }

    /// <summary>
    /// The <c>PageQuota</c> column: <c>int NOT NULL</c> defaulting to zero, where zero means no limit.
    /// </summary>
    public int PageQuota { get; set; }

    /// <summary>
    /// The <c>UserQuota</c> column: <c>int NOT NULL</c> defaulting to zero, where zero means no limit.
    /// </summary>
    public int UserQuota { get; set; }

    /// <summary>
    /// The <c>PortalAlias</c> rows that reference this portal - the host names and paths it answers on.
    /// Empty means none.
    /// </summary>
    /// <remarks>
    /// Aliases were a single column on this table until the upgrade chain moved them to their own table,
    /// which is why no alias column appears here.
    /// </remarks>
    public ICollection<PortalAlias> PortalAliases { get; } = [];

    /// <summary>
    /// The <c>Modules</c> rows that reference this portal. Empty means none; host-level modules belong
    /// to no portal and never appear here.
    /// </summary>
    public ICollection<Module> Modules { get; } = [];

    /// <summary>
    /// The <c>Tabs</c> rows that reference this portal, at every level of the page hierarchy. Empty
    /// means none.
    /// </summary>
    public ICollection<Tab> Tabs { get; } = [];

    /// <summary>The <c>Roles</c> rows that reference this portal. Empty means none.</summary>
    public ICollection<Role> Roles { get; } = [];

    /// <summary>
    /// The <c>UserPortals</c> rows that reference this portal - the per-portal membership record of each
    /// account. Empty means none.
    /// </summary>
    public ICollection<UserPortal> UserPortals { get; } = [];

    /// <summary>
    /// The <c>PortalDesktopModules</c> rows that reference this portal - which module types are
    /// available to it, as distinct from the instances actually created. Empty means none.
    /// </summary>
    public ICollection<PortalDesktopModule> PortalDesktopModules { get; } = [];
}
