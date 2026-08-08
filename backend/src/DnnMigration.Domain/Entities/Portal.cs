using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

// MIGRATION: this type models the dbo.Portals base table, not the legacy PortalInfo class, which was filled
// from the vw_Portals view. Members that came from that view's joins (Email, SuperTabId, the two role names,
// Version) or from getters that performed I/O (Users, Pages, HomeDirectoryMapPath) are therefore absent:
// they are Application-layer projections, not columns.
//
// MIGRATION: HostFee is decimal rather than the legacy single-precision float, because the column is money
// and binary floating point cannot hold a currency amount exactly. Infrastructure maps it with
// HasColumnType("money").
//
// MIGRATION: two columns are named differently from their members - TimezoneOffset spells the z in lower
// case, and PortalGuid maps the column spelled GUID - so the entity configuration names both columns
// explicitly rather than relying on a convention match.
//
// MIGRATION: LogoFile and BackgroundFile carry the raw column value, which in later DotNetNuke data is the
// literal token "fileid=N" rather than a path. The legacy view resolved it by joining Files, a subsystem out
// of scope here, so resolution is a projection concern and not behaviour on this entity.
//
// MIGRATION: the UserRegistration and BannerAdvertising ordinals are live data. Never renumber or reorder
// either enumeration.

/// <summary>
/// A DotNetNuke portal: the tenant container that owns a site's pages, modules, roles and member
/// accounts, modelling the terminal <c>dbo.Portals</c> base table.
/// </summary>
/// <remarks>
/// Every member is a persisted column, in the table's own column order, plus the six navigation
/// ends; the type holds no behaviour. Two invariants govern every consumer: neither -1 nor 0 may be
/// read as an absent key, because <c>PortalID</c> is <c>IDENTITY(-1, 1)</c> and the legacy integer
/// sentinel was itself -1; and <see cref="ProcessorCredentialReference"/> is an opaque
/// managed-secret reference, never the referenced credential.
/// </remarks>
public sealed class Portal : Entity<int>
{
    /// <summary>
    /// Gets the value on which entity equality is based; always <see cref="PortalId"/>.
    /// </summary>
    /// <remarks>
    /// Not a column of its own; the entity configuration maps <see cref="PortalId"/>.
    /// </remarks>
    public override int Identity => PortalId;

    /// <summary>
    /// The <c>PortalID</c> column: <c>int IDENTITY(-1, 1) NOT NULL</c>, the primary key, generated
    /// by the database.
    /// </summary>
    /// <remarks>
    /// Both -1 and 0 key real portals, so no comparison against either may be read as absence.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>The <c>PortalName</c> column: <c>nvarchar(128) NOT NULL</c>, the display name.</summary>
    public string PortalName { get; set; }

    /// <summary>
    /// The <c>LogoFile</c> column: <c>nvarchar(50) NULL</c>, either a file name or the raw
    /// <c>fileid=N</c> token. Unresolved here by design.
    /// </summary>
    public string? LogoFile { get; set; }

    /// <summary>
    /// The <c>FooterText</c> column: <c>nvarchar(100) NULL</c>. A stored empty string means an
    /// empty footer, not an absent one.
    /// </summary>
    public string? FooterText { get; set; }

    /// <summary>
    /// The <c>ExpiryDate</c> column: <c>datetime NULL</c>; null means the hosting arrangement does
    /// not expire.
    /// </summary>
    /// <remarks>
    /// Callers must test for null; the legacy floor date is not used to signal absence.
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
    /// The <c>AdministratorId</c> column: <c>int NULL</c> keying the designated administrator
    /// account; null means none is assigned.
    /// </summary>
    /// <remarks>A null must not be written back as the legacy -1 "none" marker.</remarks>
    public int? AdministratorId { get; set; }

    /// <summary>
    /// The <c>Currency</c> column: <c>char(3) NULL</c>, an ISO code such as <c>USD</c> used for
    /// paid-membership amounts.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// The <c>HostFee</c> column: <c>money NOT NULL</c> defaulting to zero, expressed in
    /// <see cref="Currency"/>.
    /// </summary>
    public decimal HostFee { get; set; }

    /// <summary>
    /// The <c>HostSpace</c> column: <c>int NOT NULL</c> defaulting to zero, a disk allowance in
    /// megabytes where zero conventionally means unlimited.
    /// </summary>
    public int HostSpace { get; set; }

    /// <summary>
    /// The <c>AdministratorRoleId</c> column: <c>int NULL</c> keying the role that confers portal
    /// administration; null means none is assigned.
    /// </summary>
    public int? AdministratorRoleId { get; set; }

    /// <summary>
    /// The <c>RegisteredRoleId</c> column: <c>int NULL</c> keying the role granted to every
    /// registered member; null means none is assigned.
    /// </summary>
    public int? RegisteredRoleId { get; set; }

    /// <summary>The <c>Description</c> column: <c>nvarchar(500) NULL</c>, used as page metadata.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The <c>KeyWords</c> column: <c>nvarchar(500) NULL</c>, a comma-separated list in legacy
    /// data, stored and returned verbatim.
    /// </summary>
    public string? KeyWords { get; set; }

    /// <summary>
    /// The <c>BackgroundFile</c> column: <c>nvarchar(50) NULL</c>, either a file name or the raw
    /// <c>fileid=N</c> token. Unresolved here by design.
    /// </summary>
    public string? BackgroundFile { get; set; }

    /// <summary>
    /// The legacy <c>GUID</c> column: <c>uniqueidentifier NOT NULL</c> defaulting to a newly
    /// generated value, so the database supplies it and the domain does not assign it.
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
    public string? ProcessorUserId { get; set; }

    /// <summary>
    /// The legacy <c>ProcessorPassword</c> column: <c>nvarchar(50) NULL</c>, repurposed to hold an
    /// opaque managed-secret reference.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy column held a gateway credential in clear text and is too narrow for
    /// safe ciphertext, so new writes store a bounded <c>secret://</c> reference only. Pre-existing
    /// plaintext requires operator rotation; this value is never logged, projected to a client or
    /// treated as a credential.
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
    public string DefaultLanguage { get; set; }

    /// <summary>
    /// The <c>TimezoneOffset</c> column: <c>int NOT NULL</c> defaulting to -8, an offset from
    /// server time in minutes, where negative values are normal.
    /// </summary>
    /// <remarks>
    /// The SQL identifier spells the z in lower case, so the column is named explicitly.
    /// </remarks>
    public int TimeZoneOffset { get; set; }

    /// <summary>
    /// The <c>AdminTabId</c> column: <c>int NULL</c> rooting this portal's administration area;
    /// only null means none, because zero keys a real page.
    /// </summary>
    public int? AdminTabId { get; set; }

    /// <summary>
    /// The <c>HomeDirectory</c> column: <c>varchar(100) NOT NULL</c> defaulting to the empty
    /// string, a content directory relative to the application root such as <c>Portals/0</c>.
    /// </summary>
    public string HomeDirectory { get; set; }

    /// <summary>
    /// The <c>SplashTabId</c> column: <c>int NULL</c> nominating a splash page shown once before
    /// the home page; only null means none.
    /// </summary>
    public int? SplashTabId { get; set; }

    /// <summary>
    /// The <c>PageQuota</c> column: <c>int NOT NULL</c> defaulting to zero, where zero means no
    /// limit.
    /// </summary>
    public int PageQuota { get; set; }

    /// <summary>
    /// The <c>UserQuota</c> column: <c>int NOT NULL</c> defaulting to zero, where zero means no
    /// limit.
    /// </summary>
    public int UserQuota { get; set; }

    // The inverse ends of the six foreign keys that reference this portal. An empty collection means the
    // portal owns no such row, never "not loaded" - which end a query loaded is the repository's decision,
    // so load state is never inferred from a count here. Host-level modules belong to no portal and so never
    // appear in Modules.
    public ICollection<PortalAlias> PortalAliases { get; } = [];

    public ICollection<Module> Modules { get; } = [];

    public ICollection<Tab> Tabs { get; } = [];

    public ICollection<Role> Roles { get; } = [];

    public ICollection<UserPortal> UserPortals { get; } = [];

    public ICollection<PortalDesktopModule> PortalDesktopModules { get; } = [];
}
