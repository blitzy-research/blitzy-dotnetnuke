using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

// =============================================================================================
// MIGRATION PROVENANCE
//
// Legacy inputs, all read-only and none of them modified by this work:
//
//   Library/Components/Portal/PortalInfo.vb            409 lines; 38 private backing fields
//                                                      (L33-L70), one empty constructor
//                                                      (L77-L78) and 39 public properties
//                                                      (L82-L402), every one of them an
//                                                      XML-serialisation-decorated Get/Set block
//   Library/Components/Shared/Null.vb                  the application-encoded null sentinels
//                                                      (L36-L85) and the SetNull translator (L88)
//   Website/Providers/DataProviders/SqlDataProvider/
//     01.00.00.SqlDataProvider                         CREATE TABLE [dbo].[Portals] (L76-L94),
//                                                      PK_Portals (L470-L474)
//     01.00.02.SqlDataProvider                         ADD Description/KeyWords/BackgroundFile
//                                                      (L883-L886); DROP UploadDirectory (L889)
//     01.00.05.SqlDataProvider                         whole-table rebuild via Tmp_Portals
//                                                      (L1364-L1456); HostFee becomes money
//     01.00.06.SqlDataProvider                         ADD PaymentProcessor/ProcessorUserId/
//                                                      ProcessorPassword/SiteLogHistory (L598);
//                                                      DROP PayPalId (L611)
//     02.00.00.SqlDataProvider                         ADD HomeTabId/LoginTabId/UserTabId (L6676)
//     02.02.00.SqlDataProvider                         ADD DefaultLanguage (L146), TimezoneOffset
//                                                      (L150), AdminTabId (L231)
//     02.02.02.SqlDataProvider                         ADD HomeDirectory (L3822); DROP
//                                                      portalalias (L3925, lower-case DDL)
//     03.00.04.SqlDataProvider                         ADD SplashTabId (L552)
//     03.01.01.SqlDataProvider                         re-asserts eight columns NOT NULL
//                                                      (L1116-L1123) and re-adds their defaults
//                                                      (L1125-L1139)
//     04.03.05.SqlDataProvider                         widens DefaultLanguage to 10 (L290)
//     04.04.00.SqlDataProvider                         ADD PageQuota/UserQuota (L14-L16)
//     04.05.00.SqlDataProvider                         terminal vw_Portals (L1526-L1588)
//
// The terminal shape of dbo.Portals was established by replaying the whole ALTER chain across all
// 88 schema scripts, case-insensitively and in all four naming forms the scripts use over their
// lifetime (bare, dbo.-qualified, [dbo].[..]-bracketed, and {databaseOwner}{objectQualifier}
// templated). Exactly 31 columns survive that replay, and this entity declares exactly those 31.
// The baseline CREATE TABLE alone is NOT the schema: three of its columns were later dropped and
// six of them changed type or nullability.
//
// The fourteen notes below record every deliberate divergence from legacy behaviour that this
// entity embodies. They are mirrored by MIGRATION_NOTES.md at the repository root; neither copy is
// a substitute for the other, and neither may be dropped.
// =============================================================================================

// MIGRATION: (1 of 14) This type models the BASE TABLE dbo.Portals, not the legacy PortalInfo
// class and not the vw_Portals view PortalInfo was filled from. PortalInfo declares 39 properties;
// the terminal base table has 31 columns. The eight-member difference is not an omission - it is
// the view's join and subquery output plus two members that performed database reads inside a
// property getter, and it is itemised in note 12. Anyone comparing this file against
// PortalInfo.vb property-for-property will find it "short" by exactly those eight members.

// MIGRATION: (2 of 14) Every piece of XML-serialisation metadata is dropped. The legacy type was
// decorated <XmlRoot("settings", IsNullable:=False)> at PortalInfo.vb L29, 37 of its properties
// carried an <XmlElement("lowercasename")> attribute, and two carried <XmlIgnore()> (L245, L388).
// A domain entity in this solution carries no attribute of any kind: the wire contract belongs to
// the Application-layer data transfer objects, column binding belongs to the Infrastructure entity
// configuration, and validation belongs to the Application-layer validators. That also rules out
// data-annotation, Entity Framework and JSON attributes here, which the Domain project could not
// reference in any case - it declares no package and no project reference.

// MIGRATION: (3 of 14) The 38 private backing fields (PortalInfo.vb L33-L70) and the empty
// constructor (L77-L78) are gone. Each property is an auto-property, so there is no field to keep
// in step with a property, no field initialiser to encode a default that the database already
// declares, and no constructor to keep the object-relational mapper's materialiser away from.

// MIGRATION: (4 of 14) The legacy null sentinels are NOT ported. Null.vb encoded absence as -1 for
// the integral types (L36-L45), 255 for a byte (L46-L50), MinValue for the floating-point and
// decimal types (L51-L65), the earliest representable date (L66-L70), the empty string for text
// (L71-L75) and the empty identifier for a globally unique identifier (L81-L85), and SetNull (L88)
// applied them on every read. Here, SQL nullability is expressed by nullable CLR types and nothing
// else: no sentinel constant is declared, no sentinel value is compared against, and no property
// initialiser plants one. Sentinel semantics are preserved only at the DTO and API boundary, where
// the wire contract is externally observable, which is a decision this file relies on and does not
// implement.

// MIGRATION: (5 of 14) Minus one and zero are BOTH real portal identities. The key column is
// declared IDENTITY(-1, 1) at 01.00.00.SqlDataProvider L77, so the very first portal row is keyed
// -1 and the second is keyed 0, while the legacy sentinel module simultaneously used -1 as its
// integer null (Null.vb L41-L45). Nothing here may treat either value as absent, unset, transient
// or "not yet saved": there is no such helper on this type, none on its base, and none may be
// added. Absence of a portal is representable only through a nullable foreign key on some other
// entity, never through a reserved value of this key. The PortalId value object in
// Domain/ValueObjects exists to keep that rule visible at Application boundaries; it is
// deliberately NOT used as this property's type, because the persistence mapping binds a plain
// int column and a wrapper here would need a value conversion for no gain.

// MIGRATION: (6 of 14) HostFee is corrected from the legacy VB Single (PortalInfo.vb L42 field,
// L157 property) to decimal, because the terminal column is money NOT NULL. It began life as
// nvarchar(10) NULL (01.00.00.SqlDataProvider L89), was rebuilt as money NOT NULL through
// CONVERT(money, HostFee) during the Tmp_Portals swap (01.00.05.SqlDataProvider L1377, the
// conversion at L1414), and was re-asserted money NOT NULL at 03.01.01.SqlDataProvider L1118 with
// a zero default at L1129. Binary floating point cannot represent a currency amount exactly, so
// keeping Single would introduce rounding differences the legacy system did not have. The
// Infrastructure entity configuration must map this property with HasColumnType("money"). This is
// a deliberate precision correction rather than a like-for-like port and requires an append-only
// entry in MIGRATION_NOTES.md, which the owning documentation workflow adds - this file records
// the obligation and does not discharge it.

// MIGRATION: (7 of 14) PortalGuid maps the legacy column named GUID (01.00.00.SqlDataProvider
// L93, re-asserted NOT NULL at 03.01.01.SqlDataProvider L1120, defaulted to newid() at L1133). The
// property is renamed because GUID is not a legal, idiomatic C# member name here, and it is typed
// as a plain Guid on purpose: the legacy <XmlIgnore()> at PortalInfo.vb L245 is dropped with all
// other serialisation metadata, and no value-object wrapper and no value conversion is introduced
// for it in persistence. The database supplies the value by default constraint, so this property
// is never expected to be assigned by the domain.

// MIGRATION: (8 of 14) TimeZoneOffset is spelled with a capital Z here but the SQL identifier is
// not. The column was added as TimezoneOffset - lower-case z - at 02.02.00.SqlDataProvider L151
// and is re-asserted with that exact spelling at 03.01.01.SqlDataProvider L1122, even though its
// default constraint is named DF_..._TimeZoneOffset and the legacy stored procedure assigned
// TimeZoneOffset (02.02.00.SqlDataProvider, UpdatePortalInfo). SQL Server resolves identifiers
// case-insensitively under the default collation, which is why the inconsistency was never
// noticed. The Infrastructure entity configuration must therefore name the column
// "TimezoneOffset" explicitly; relying on a by-convention name match is the one thing that would
// break here.

// MIGRATION: (9 of 14) LogoFile and BackgroundFile carry the RAW base-table value, which for rows
// written by later DotNetNuke versions is the literal token "fileid=N" rather than a path. The
// terminal view resolves that token by joining the Files table
// (04.05.00.SqlDataProvider L1535-L1544 for the logo, L1559-L1568 for the background) and legacy
// callers therefore saw a resolved path. The Files subsystem is out of scope for this migration,
// so the resolution is a projection concern for the Application and Infrastructure layers and is
// deliberately not implemented as behaviour on this entity - a getter that resolved it would have
// to perform a database read, which note 12 rejects on principle.

// MIGRATION: (10 of 14) ProcessorPassword is sensitive legacy data. It is the payment-gateway
// credential held in clear text by the legacy schema (01.00.06.SqlDataProvider L601), and this
// entity treats it as write-through storage and nothing more. There is deliberately no ToString
// override on this type, no equality customisation that could read it, and no logging of any kind:
// identity-based equality is inherited unchanged from the base entity, so no member of this class
// can leak the value through a comparison or a hash. It must never be projected onto a response
// DTO, never written to a log, and never returned by an API endpoint.

// MIGRATION: (11 of 14) UserRegistration and BannerAdvertising become enumerations over the same
// persisted ordinals, never renumbered and never reordered. Both columns began nullable
// (01.00.00.SqlDataProvider L84, L85), were tightened to int NOT NULL in the Tmp_Portals rebuild
// (01.00.05.SqlDataProvider L1372, L1373), and were re-asserted NOT NULL with a zero default at
// 03.01.01.SqlDataProvider L1116/L1125 and L1117/L1127. Because the terminal columns are not
// nullable there is no absent case, which is why these two properties are the only non-nullable
// discriminators here and why neither enumeration declares an "unknown" member. The legacy
// properties were bare Integers (PortalInfo.vb L125, L133).

// MIGRATION: (12 of 14) Eight legacy PortalInfo members are deliberately NOT declared, because
// none of them is a column of dbo.Portals:
//   Email (L285)                    supplied by the view's LEFT OUTER JOIN onto Users on
//                                   AdministratorId (04.05.00.SqlDataProvider L1574, join at
//                                   L1587). It belongs to the administrator account, and no ALTER
//                                   TABLE anywhere in the 88 scripts ever adds an Email column to
//                                   Portals.
//   SuperTabId (L301)               a TOP 1 subquery over Tabs (04.05.00.SqlDataProvider L1583).
//   AdministratorRoleName (L197)    a TOP 1 subquery over Roles (L1584).
//   RegisteredRoleName (L213)       a TOP 1 subquery over Roles (L1585).
//   Version (L395)                  host and application metadata, absent from both the table and
//                                   the view.
//   Users (L309-L319)               its getter called UserController.GetUserCountByPortal (L312) -
//                                   a database read inside a property.
//   Pages (L320-L331)               its getter constructed a TabController and called GetTabCount
//                                   (L324) - likewise a database read inside a property.
//   HomeDirectoryMapPath (L388)     its getter called FolderController.GetMappedDirectory with
//                                   Common.Globals.ApplicationPath (L391), reaching into the
//                                   excluded FileSystem subsystem and the excluded Globals module.
// The two counts and the mapped path are projections or derived values that the Application layer
// computes; a domain entity performs no I/O, so a property that reads the database or the file
// system cannot exist here at all.

// MIGRATION: (13 of 14) Three columns that the baseline CREATE TABLE declared are permanently
// gone and are therefore not declared here: UploadDirectory, dropped at 01.00.02.SqlDataProvider
// L889-L890; PayPalId, whose value was migrated into ProcessorUserId before it was dropped at
// 01.00.06.SqlDataProvider L605-L612; and PortalAlias, dropped at 02.02.02.SqlDataProvider
// L3925-L3926 in lower-case DDL once aliases became their own table. Portal aliases are reached
// through the PortalAliases navigation instead. There is also no audit quartet on this table, so
// this entity derives from the plain identity base rather than from the auditable one.

// MIGRATION: (14 of 14) The six navigation collections are the inverse ends of six real foreign
// keys onto Portals.PortalID, and nothing more: FK_{objectQualifier}PortalAlias_..._Portals
// (02.02.02.SqlDataProvider L3812), FK_{objectQualifier}Modules_..._Portals
// (03.00.09.SqlDataProvider L296), FK_Tabs_Portals (01.00.05.SqlDataProvider L1541),
// FK_Roles_Portals (L1486), FK_UserPortals_Portals (L1508) and
// FK_{objectQualifier}PortalDesktopModules_..._Portals (02.02.02.SqlDataProvider L3066). They are
// declared as generic collection interfaces, so none of the pre-generics collection wrappers is
// ported and no hydration machinery appears: the legacy reflection-based filler and the
// hand-rolled sentinel-aware reader loops are both replaced by the object-relational mapper's own
// materialiser.

/// <summary>
/// A DotNetNuke portal: the tenant container that owns a site's pages, modules, roles and member
/// accounts. This type is the domain model of the terminal <c>dbo.Portals</c> base table.
/// </summary>
/// <remarks>
/// <para>
/// Every member below is a persisted column of that table, in the table's own column order, and
/// there are exactly 31 of them. The type declares no behaviour: no method, no constructor, no
/// computed member, no validation and no I/O of any kind. Tenant rules, portal creation and
/// template handling belong to <c>DnnMigration.Application.Services.PortalService</c>; loading and
/// saving belong to <c>IPortalRepository</c> and <c>IUnitOfWork</c>; column and table binding
/// belongs to the Infrastructure entity configuration for <c>Portals</c>; the wire shape belongs to
/// the Application-layer portal data transfer objects.
/// </para>
/// <para>
/// Do not add a member that reads the database, the file system, the clock or the current call
/// context. The legacy class this replaces had three such members and they are the main reason it
/// could not be reused: two property getters issued queries to count users and pages, and a third
/// reached into the file-system provider to map a physical path. Migration note 12 lists them.
/// </para>
/// <para>
/// The schema is immutable for this migration. This file describes the table that already exists
/// and never proposes a change to it: the baseline Entity Framework migration is intentionally
/// empty, so applying it against a real DotNetNuke database seeds the migrations-history table
/// without touching a single column. Adding a property here does not add a column; it asserts that
/// a column is already there.
/// </para>
/// <para>
/// Identity semantics deserve particular care and are set out in full in migration note 5: the key
/// column is seeded at minus one and steps by one, so -1 and 0 are both perfectly ordinary portal
/// keys even though the legacy sentinel module used -1 to mean "no value". No member of this type
/// tests the key against a reserved value, and none may be added that does.
/// </para>
/// <para>
/// Nullability is the schema's, not a preference. A property is non-nullable here exactly when its
/// column is <c>NOT NULL</c> in the terminal schema, which for text means
/// <see cref="PortalName"/>, <see cref="DefaultLanguage"/> and <see cref="HomeDirectory"/> and no
/// other string. Every remaining string property is nullable because its column is nullable, and a
/// legacy empty string in one of those columns means an empty string - it is not a second spelling
/// of null (migration note 4).
/// </para>
/// <para>
/// No property carries an initialiser, and the three non-nullable strings deliberately carry none
/// either. That is the solution-wide policy this project's build properties state explicitly: the
/// uninitialised-non-nullable-property warning is suppressed precisely because entities are
/// materialised by the object-relational mapper, so the compiler cannot see the assignment that
/// does happen. Planting a default here would be worse than leaving it out on both counts that
/// matter. It would duplicate, or silently contradict, a default the database already declares -
/// <c>DefaultLanguage</c> defaults to <c>en-US</c> and <c>HomeDirectory</c> to the empty string in
/// the schema itself - and it would turn a caller's failure to supply a required value into a
/// quietly stored empty string instead of the constraint violation that the required-field
/// validators on the legacy screens intended. The six navigation collections are the sole
/// exception, for the reason set out on <see cref="PortalAliases"/>.
/// </para>
/// </remarks>
/// <example>
/// Materialised by the object-relational mapper and read through the repository abstraction:
/// <code>
/// Portal? portal = await portalRepository.GetByIdAsync(portalId, cancellationToken);
/// if (portal?.UserRegistration == UserRegistrationMode.PublicRegistration)
/// {
///     // sign-up is open on this tenant
/// }
/// </code>
/// </example>
public sealed class Portal : Entity<int>
{
    /// <summary>
    /// Gets the value that identifies this portal for the purposes of equality, which is always
    /// <see cref="PortalId"/>.
    /// </summary>
    /// <value>The portal's persisted key, exactly as <see cref="PortalId"/> reports it.</value>
    /// <remarks>
    /// Required by <see cref="Entity{TId}"/>, which supplies identity-based equality and hashing so
    /// that this type needs neither. The member exists for equality alone and is never mapped to a
    /// column: the Infrastructure entity configuration names <see cref="PortalId"/> in its explicit
    /// key declaration instead. Because it merely forwards a field-backed property it performs no
    /// work and can be read freely.
    /// </remarks>
    public override int Identity => PortalId;

    /// <summary>
    /// Gets or sets the portal's persisted key.
    /// </summary>
    /// <value>
    /// The value of the <c>PortalID</c> column. Both -1 and 0 are legitimate keys of real portals.
    /// </value>
    /// <remarks>
    /// <para>
    /// Column: <c>PortalID int IDENTITY(-1, 1) NOT NULL</c>, the non-clustered primary key
    /// <c>PK_Portals</c> (01.00.00.SqlDataProvider L77 and L470-L474). Legacy origin:
    /// <c>PortalInfo.vb</c> L85.
    /// </para>
    /// <para>
    /// Read migration note 5 before writing any comparison against this value. The seed is minus
    /// one, so a non-positive key is ordinary rather than suspicious, and no code anywhere may read
    /// -1, 0 or any other reserved value as "absent" or "not yet persisted". Absence of a portal is
    /// expressed only by a nullable foreign key on another entity. The value is generated by the
    /// database on insert.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the portal's display name.
    /// </summary>
    /// <value>The value of the <c>PortalName</c> column; never null, though it may be empty.</value>
    /// <remarks>
    /// Column: <c>PortalName nvarchar(128) NOT NULL</c> (01.00.00.SqlDataProvider L79, unchanged by
    /// the rebuild at 01.00.05.SqlDataProvider L1368). Legacy origin: <c>PortalInfo.vb</c> L93.
    /// Non-nullable because the column is, so an empty value is a name of zero length and never a
    /// stand-in for a missing one. The length limit is enforced by the Infrastructure configuration
    /// and by the Application-layer validators, not here. Carries no initialiser, for the reason
    /// given in the type-level remarks.
    /// </remarks>
    public string PortalName { get; set; }

    /// <summary>
    /// Gets or sets the raw stored reference to the portal's logo image.
    /// </summary>
    /// <value>
    /// The value of the <c>LogoFile</c> column: either a file name, or the literal token
    /// <c>fileid=N</c> naming a row of the Files table, or null when no logo is configured.
    /// </value>
    /// <remarks>
    /// Column: <c>LogoFile nvarchar(50) NULL</c> (01.00.00.SqlDataProvider L81). Legacy origin:
    /// <c>PortalInfo.vb</c> L101. This property is deliberately unresolved: see migration note 9.
    /// The terminal view resolved the token by joining the Files table
    /// (04.05.00.SqlDataProvider L1535-L1544), that subsystem is out of scope, and resolving it
    /// here would require the entity to read the database.
    /// </remarks>
    public string? LogoFile { get; set; }

    /// <summary>
    /// Gets or sets the text rendered in the portal's page footer.
    /// </summary>
    /// <value>The value of the <c>FooterText</c> column, or null when none is configured.</value>
    /// <remarks>
    /// Column: <c>FooterText nvarchar(100) NULL</c> (01.00.00.SqlDataProvider L82). Legacy origin:
    /// <c>PortalInfo.vb</c> L109. Nullable because the column is; a stored empty string means an
    /// empty footer rather than an absent one (migration note 4).
    /// </remarks>
    public string? FooterText { get; set; }

    /// <summary>
    /// Gets or sets the moment at which the portal's hosting arrangement expires.
    /// </summary>
    /// <value>
    /// The value of the <c>ExpiryDate</c> column, or null when the portal does not expire.
    /// </value>
    /// <remarks>
    /// <para>
    /// Column: <c>ExpiryDate datetime NULL</c> (01.00.00.SqlDataProvider L83, unchanged by the
    /// rebuild at 01.00.05.SqlDataProvider L1371). Legacy origin: <c>PortalInfo.vb</c> L117.
    /// </para>
    /// <para>
    /// The legacy property was a non-nullable VB date, so a SQL null arrived as the earliest
    /// representable date by way of the sentinel translator (<c>Null.vb</c> L66-L70, L88) and "does
    /// not expire" and "expired in year one" were indistinguishable. Making it nullable restores
    /// that distinction; comparing it against a minimum-value date is therefore wrong, and callers
    /// must test for null instead (migration note 4).
    /// </para>
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Gets or sets the mode by which the portal admits new user accounts.
    /// </summary>
    /// <value>
    /// The value of the <c>UserRegistration</c> column as a <see cref="UserRegistrationMode"/>.
    /// </value>
    /// <remarks>
    /// Column: <c>UserRegistration int NOT NULL</c> defaulting to zero - created nullable at
    /// 01.00.00.SqlDataProvider L84, tightened at 01.00.05.SqlDataProvider L1372 and re-asserted
    /// with its default at 03.01.01.SqlDataProvider L1116 and L1125. Legacy origin:
    /// <c>PortalInfo.vb</c> L125, a bare Integer. Non-nullable, so there is no absent case to
    /// model; the ordinals are live persisted data and must never be renumbered (migration
    /// note 11).
    /// </remarks>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>
    /// Gets or sets the portal's banner-advertising mode.
    /// </summary>
    /// <value>
    /// The value of the <c>BannerAdvertising</c> column as a <see cref="BannerAdvertisingMode"/>.
    /// </value>
    /// <remarks>
    /// Column: <c>BannerAdvertising int NOT NULL</c> defaulting to zero - created nullable at
    /// 01.00.00.SqlDataProvider L85, tightened at 01.00.05.SqlDataProvider L1373 and re-asserted
    /// with its default at 03.01.01.SqlDataProvider L1117 and L1127. Legacy origin:
    /// <c>PortalInfo.vb</c> L133, a bare Integer. Non-nullable, so there is no absent case to
    /// model; the ordinals are live persisted data and must never be renumbered (migration
    /// note 11).
    /// </remarks>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>
    /// Gets or sets the key of the user account designated administrator of this portal.
    /// </summary>
    /// <value>
    /// The value of the <c>AdministratorId</c> column, or null when no administrator is assigned.
    /// </value>
    /// <remarks>
    /// Column: <c>AdministratorId int NULL</c> (01.00.00.SqlDataProvider L86, unchanged by the
    /// rebuild at 01.00.05.SqlDataProvider L1374). Legacy origin: <c>PortalInfo.vb</c> L141, a
    /// non-nullable Integer that carried -1 for "none" through the sentinel translator. Nullable
    /// here, and a null must not be replaced by -1 on the way out (migration note 4). The
    /// administrator's own details, including the address the terminal view exposed as the portal's
    /// e-mail (migration note 12), live on the referenced account.
    /// </remarks>
    public int? AdministratorId { get; set; }

    /// <summary>
    /// Gets or sets the ISO currency code used for the portal's paid-membership amounts.
    /// </summary>
    /// <value>
    /// The value of the <c>Currency</c> column - a three-character code such as <c>USD</c> - or
    /// null when none is configured.
    /// </value>
    /// <remarks>
    /// Column: <c>Currency char(3) NULL</c> (01.00.00.SqlDataProvider L88, unchanged by the rebuild
    /// at 01.00.05.SqlDataProvider L1376). Legacy origin: <c>PortalInfo.vb</c> L149. The column is
    /// fixed-width, so stored values are space-padded to three characters; whether to trim is a
    /// projection decision for the Application layer, and this property reports exactly what the
    /// column holds.
    /// </remarks>
    public string? Currency { get; set; }


    /// <summary>
    /// Gets or sets the fee the host charges for hosting this portal.
    /// </summary>
    /// <value>
    /// The value of the <c>HostFee</c> column as an exact decimal amount, expressed in
    /// <see cref="Currency"/>. Zero when no fee applies.
    /// </value>
    /// <remarks>
    /// <para>
    /// Column: <c>HostFee money NOT NULL</c> defaulting to zero. It was created as
    /// <c>nvarchar(10) NULL</c> (01.00.00.SqlDataProvider L89), converted to <c>money NOT NULL</c>
    /// during the whole-table rebuild (01.00.05.SqlDataProvider L1377, the value conversion at
    /// L1412, the default at L1396-L1397), and re-asserted <c>money NOT NULL</c> at
    /// 03.01.01.SqlDataProvider L1118 with its default re-added at L1129. Legacy origin:
    /// <c>PortalInfo.vb</c> L157.
    /// </para>
    /// <para>
    /// Typed <see cref="decimal"/> rather than the legacy single-precision floating-point type.
    /// This is the one deliberate precision correction in this entity and it is explained in full
    /// in migration note 6; the Infrastructure entity configuration must map it with the
    /// <c>money</c> column type so that the four-decimal-place scale of the column is honoured.
    /// Non-nullable, so a portal with no fee stores zero rather than null.
    /// </para>
    /// </remarks>
    public decimal HostFee { get; set; }

    /// <summary>
    /// Gets or sets the disk-space allowance for this portal, in megabytes.
    /// </summary>
    /// <value>
    /// The value of the <c>HostSpace</c> column. Zero conventionally means unlimited, which is the
    /// legacy reading and is preserved rather than reinterpreted.
    /// </value>
    /// <remarks>
    /// Column: <c>HostSpace int NOT NULL</c> defaulting to zero - created nullable at
    /// 01.00.00.SqlDataProvider L90, tightened at 01.00.05.SqlDataProvider L1378 after a backfill
    /// of nulls to zero at L1355-L1357, and re-asserted with its default at
    /// 03.01.01.SqlDataProvider L1119 and L1131. Legacy origin: <c>PortalInfo.vb</c> L165.
    /// Non-nullable because the column is; enforcement of the allowance is an Application-layer
    /// concern.
    /// </remarks>
    public int HostSpace { get; set; }

    /// <summary>
    /// Gets or sets the key of the role that confers portal administration rights.
    /// </summary>
    /// <value>
    /// The value of the <c>AdministratorRoleId</c> column, or null when no such role is assigned.
    /// </value>
    /// <remarks>
    /// Column: <c>AdministratorRoleId int NULL</c> (01.00.00.SqlDataProvider L91, unchanged by the
    /// rebuild at 01.00.05.SqlDataProvider L1379). Legacy origin: <c>PortalInfo.vb</c> L189. Role
    /// keys are themselves seeded at zero (01.00.00.SqlDataProvider L114-L115), so a key of zero
    /// names a real role here just as -1 names a real portal; only null means "none". The role's
    /// name is not duplicated onto this entity - the terminal view supplied it as a subquery, which
    /// migration note 12 covers - and is reached through the <see cref="Roles"/> navigation or
    /// through the role repository instead.
    /// </remarks>
    public int? AdministratorRoleId { get; set; }

    /// <summary>
    /// Gets or sets the key of the role granted automatically to every registered member.
    /// </summary>
    /// <value>
    /// The value of the <c>RegisteredRoleId</c> column, or null when no such role is assigned.
    /// </value>
    /// <remarks>
    /// Column: <c>RegisteredRoleId int NULL</c> (01.00.00.SqlDataProvider L92, unchanged by the
    /// rebuild at 01.00.05.SqlDataProvider L1380). Legacy origin: <c>PortalInfo.vb</c> L205. The
    /// same zero-is-real caveat applies as for <see cref="AdministratorRoleId"/>, and the role name
    /// is likewise not duplicated here.
    /// </remarks>
    public int? RegisteredRoleId { get; set; }

    /// <summary>
    /// Gets or sets the portal's descriptive text, used as page metadata.
    /// </summary>
    /// <value>The value of the <c>Description</c> column, or null when none is configured.</value>
    /// <remarks>
    /// Column: <c>Description nvarchar(500) NULL</c>, added at 01.00.02.SqlDataProvider L884 and
    /// carried through the rebuild at 01.00.05.SqlDataProvider L1381. Legacy origin:
    /// <c>PortalInfo.vb</c> L221. Nullable because the column is.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the portal's search keywords, used as page metadata.
    /// </summary>
    /// <value>
    /// The value of the <c>KeyWords</c> column - a comma-separated list in legacy data - or null
    /// when none is configured.
    /// </value>
    /// <remarks>
    /// Column: <c>KeyWords nvarchar(500) NULL</c>, added at 01.00.02.SqlDataProvider L885 and
    /// carried through the rebuild at 01.00.05.SqlDataProvider L1382. Legacy origin:
    /// <c>PortalInfo.vb</c> L229. The capital <c>W</c> is the schema's own spelling and is kept so
    /// that the property and its column read alike. The value is stored and returned verbatim; this
    /// entity does not split, trim or normalise it.
    /// </remarks>
    public string? KeyWords { get; set; }

    /// <summary>
    /// Gets or sets the raw stored reference to the portal's background image.
    /// </summary>
    /// <value>
    /// The value of the <c>BackgroundFile</c> column: either a file name, or the literal token
    /// <c>fileid=N</c> naming a row of the Files table, or null when no background is configured.
    /// </value>
    /// <remarks>
    /// Column: <c>BackgroundFile nvarchar(50) NULL</c>, added at 01.00.02.SqlDataProvider L886 and
    /// carried through the rebuild at 01.00.05.SqlDataProvider L1383. Legacy origin:
    /// <c>PortalInfo.vb</c> L237. Unresolved for exactly the same reason as
    /// <see cref="LogoFile"/>; the terminal view resolved it at 04.05.00.SqlDataProvider
    /// L1559-L1568 (migration note 9).
    /// </remarks>
    public string? BackgroundFile { get; set; }

    /// <summary>
    /// Gets or sets the portal's stable globally unique identifier.
    /// </summary>
    /// <value>
    /// The value of the legacy <c>GUID</c> column. Never null; the database generates it.
    /// </value>
    /// <remarks>
    /// Column: <c>GUID uniqueidentifier NOT NULL</c> defaulting to a newly generated value
    /// (01.00.00.SqlDataProvider L93, the default dropped and re-created through the rebuild at
    /// 01.00.05.SqlDataProvider L1384 and L1404, re-asserted NOT NULL at 03.01.01.SqlDataProvider
    /// L1120 with its default re-added at L1133). Legacy origin: <c>PortalInfo.vb</c> L245. The
    /// property is renamed and typed as a plain <see cref="Guid"/> for the reasons in migration
    /// note 7: the legacy serialisation-ignore decoration is dropped with all other attributes, no
    /// value-object wrapper is introduced, and the empty identifier is not a sentinel for absence
    /// here.
    /// </remarks>
    public Guid PortalGuid { get; set; }

    /// <summary>
    /// Gets or sets the name of the payment gateway used for paid memberships.
    /// </summary>
    /// <value>
    /// The value of the <c>PaymentProcessor</c> column - legacy installations hold <c>PayPal</c> -
    /// or null when no gateway is configured.
    /// </value>
    /// <remarks>
    /// Column: <c>PaymentProcessor nvarchar(50) NULL</c>, added at 01.00.06.SqlDataProvider L599 and
    /// backfilled to <c>PayPal</c> for existing rows at L605-L606. Legacy origin:
    /// <c>PortalInfo.vb</c> L253. Nullable because the column is.
    /// </remarks>
    public string? PaymentProcessor { get; set; }

    /// <summary>
    /// Gets or sets the account identifier presented to the payment gateway.
    /// </summary>
    /// <value>
    /// The value of the <c>ProcessorUserId</c> column, or null when no gateway is configured.
    /// </value>
    /// <remarks>
    /// Column: <c>ProcessorUserId nvarchar(50) NULL</c>, added at 01.00.06.SqlDataProvider L600.
    /// Legacy origin: <c>PortalInfo.vb</c> L269. The same script migrated the value of the
    /// then-existing <c>PayPalId</c> column into this one (L605-L607) before dropping it (L611-L612),
    /// which is why no <c>PayPalId</c> member appears on this entity (migration note 13).
    /// </remarks>
    public string? ProcessorUserId { get; set; }

    /// <summary>
    /// Gets or sets the credential presented to the payment gateway.
    /// </summary>
    /// <value>
    /// The value of the <c>ProcessorPassword</c> column, or null when no gateway is configured.
    /// </value>
    /// <remarks>
    /// <para>
    /// Column: <c>ProcessorPassword nvarchar(50) NULL</c>, added at 01.00.06.SqlDataProvider L601.
    /// Legacy origin: <c>PortalInfo.vb</c> L261.
    /// </para>
    /// <para>
    /// SENSITIVE. Read migration note 10 before using this property. The legacy schema holds this
    /// gateway credential in clear text and this entity preserves that storage without extending its
    /// reach: the value must never be projected onto a response data transfer object, never written
    /// to a log or a diagnostic string, and never compared as part of any equality or hashing.
    /// Identity-based equality is inherited unchanged from <see cref="Entity{TId}"/> and this type
    /// declares no string conversion, so no member here can expose it.
    /// </para>
    /// </remarks>
    public string? ProcessorPassword { get; set; }

    /// <summary>
    /// Gets or sets the number of days of site-log history the portal retains.
    /// </summary>
    /// <value>
    /// The value of the <c>SiteLogHistory</c> column, or null when no retention is configured.
    /// </value>
    /// <remarks>
    /// Column: <c>SiteLogHistory int NULL</c>, added at 01.00.06.SqlDataProvider L602 and backfilled
    /// to 60 for existing rows at L605-L608. Legacy origin: <c>PortalInfo.vb</c> L277. Retained as
    /// persisted portal configuration even though site logging itself is out of scope for this
    /// migration: the column exists, so dropping the property would misrepresent the table and the
    /// value would be lost on the first update that round-trips a portal.
    /// </remarks>
    public int? SiteLogHistory { get; set; }

    /// <summary>
    /// Gets or sets the key of the page that serves as the portal's home page.
    /// </summary>
    /// <value>
    /// The value of the <c>HomeTabId</c> column, or null when no home page is nominated.
    /// </value>
    /// <remarks>
    /// Column: <c>HomeTabId int NULL</c>, added at 02.00.00.SqlDataProvider L6677. Legacy origin:
    /// <c>PortalInfo.vb</c> L340. Page keys are seeded at zero (01.00.00.SqlDataProvider
    /// L139-L140), so zero nominates a real page and only null means "none" (migration note 5).
    /// This is one of the five nominated-page keys on the table; each is an ordinary nullable
    /// reference to a row of <c>Tabs</c> and none is enforced by a foreign key in the legacy schema.
    /// </remarks>
    public int? HomeTabId { get; set; }

    /// <summary>
    /// Gets or sets the key of the page that hosts the portal's sign-in experience.
    /// </summary>
    /// <value>
    /// The value of the <c>LoginTabId</c> column, or null when the default sign-in page is used.
    /// </value>
    /// <remarks>
    /// Column: <c>LoginTabId int NULL</c>, added at 02.00.00.SqlDataProvider L6678. Legacy origin:
    /// <c>PortalInfo.vb</c> L348. Null means the portal has nominated no dedicated sign-in page, not
    /// that sign-in is unavailable; zero is a real page key (migration note 5).
    /// </remarks>
    public int? LoginTabId { get; set; }

    /// <summary>
    /// Gets or sets the key of the page that hosts the portal's user-account experience.
    /// </summary>
    /// <value>
    /// The value of the <c>UserTabId</c> column, or null when the default account page is used.
    /// </value>
    /// <remarks>
    /// Column: <c>UserTabId int NULL</c>, added at 02.00.00.SqlDataProvider L6679. Legacy origin:
    /// <c>PortalInfo.vb</c> L356. Null means no dedicated page is nominated; zero is a real page key
    /// (migration note 5).
    /// </remarks>
    public int? UserTabId { get; set; }


    /// <summary>
    /// Gets or sets the portal's default culture code.
    /// </summary>
    /// <value>
    /// The value of the <c>DefaultLanguage</c> column, such as <c>en-US</c>. Never null; the
    /// database defaults it.
    /// </value>
    /// <remarks>
    /// Column: <c>DefaultLanguage nvarchar(10) NOT NULL</c> defaulting to <c>en-US</c>. It was added
    /// as <c>nvarchar(6)</c> with that default at 02.02.00.SqlDataProvider L146-L147, re-asserted at
    /// 03.01.01.SqlDataProvider L1121 with the default re-added at L1135, and widened to
    /// <c>nvarchar(10)</c> at 04.03.05.SqlDataProvider L290-L291 to accommodate longer culture
    /// codes. Legacy origin: <c>PortalInfo.vb</c> L364. Non-nullable, and carries no initialiser: the
    /// default belongs to the database, and duplicating it here would let the two drift apart. The
    /// legacy resource-based localisation mechanism is not ported, so this value is consumed as a
    /// culture code by the Application layer and the client rather than by a resource provider.
    /// </remarks>
    public string DefaultLanguage { get; set; }

    /// <summary>
    /// Gets or sets the portal's time-zone offset from the server's time, in minutes.
    /// </summary>
    /// <value>
    /// The value of the <c>TimezoneOffset</c> column. Negative values are normal - the schema
    /// default is -8.
    /// </value>
    /// <remarks>
    /// <para>
    /// Column: <c>TimezoneOffset int NOT NULL</c> defaulting to -8, added at
    /// 02.02.00.SqlDataProvider L150-L151 and re-asserted at 03.01.01.SqlDataProvider L1122 with the
    /// default re-added at L1137. Legacy origin: <c>PortalInfo.vb</c> L372.
    /// </para>
    /// <para>
    /// Note the spelling difference: the property capitalises the <c>Z</c> and the SQL column does
    /// not. That is not a typographical slip in either direction, and the Infrastructure entity
    /// configuration must name the column explicitly rather than rely on a by-convention match. See
    /// migration note 8 for the evidence, including the legacy default constraint whose own name
    /// spells it the other way. Because the column is not nullable, a portal that has never been
    /// configured carries the schema default rather than an absent value.
    /// </para>
    /// </remarks>
    public int TimeZoneOffset { get; set; }

    /// <summary>
    /// Gets or sets the key of the page that roots the portal's administration area.
    /// </summary>
    /// <value>
    /// The value of the <c>AdminTabId</c> column, or null when the portal has no administration
    /// page.
    /// </value>
    /// <remarks>
    /// Column: <c>AdminTabId int NULL</c>, added at 02.02.00.SqlDataProvider L231-L232 and
    /// backfilled from the page named <c>Admin</c> for existing rows at L235-L236. Legacy origin:
    /// <c>PortalInfo.vb</c> L293. Zero is a real page key, so only null means "none" (migration
    /// note 5). This is a portal-scoped page and must not be confused with the host-scoped page that
    /// the terminal view exposed as a subquery, which is not a column of this table (migration
    /// note 12).
    /// </remarks>
    public int? AdminTabId { get; set; }

    /// <summary>
    /// Gets or sets the portal's content directory, relative to the application root.
    /// </summary>
    /// <value>
    /// The value of the <c>HomeDirectory</c> column, such as <c>Portals/0</c>. Never null; the
    /// database defaults it to the empty string.
    /// </value>
    /// <remarks>
    /// Column: <c>HomeDirectory varchar(100) NOT NULL</c> defaulting to the empty string, added at
    /// 02.02.02.SqlDataProvider L3822-L3823 and backfilled to <c>Portals/</c> followed by the portal
    /// key at L3825-L3826, then re-asserted at 03.01.01.SqlDataProvider L1123 with its default
    /// re-added at L1139. Legacy origin: <c>PortalInfo.vb</c> L380. This is the stored relative path
    /// and nothing more. The legacy class also exposed a resolved physical path, which is
    /// deliberately absent here because computing it called into the excluded file-system provider
    /// and the excluded globals module (migration note 12); resolving a physical location is a
    /// concern of the hosting environment, reached through the Infrastructure layer.
    /// </remarks>
    public string HomeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the key of the page shown once as a splash screen before the home page.
    /// </summary>
    /// <value>
    /// The value of the <c>SplashTabId</c> column, or null when the portal has no splash page.
    /// </value>
    /// <remarks>
    /// Column: <c>SplashTabId int NULL</c>, added at 03.00.04.SqlDataProvider L552-L553. Legacy
    /// origin: <c>PortalInfo.vb</c> L332. Zero is a real page key, so only null means "none"
    /// (migration note 5).
    /// </remarks>
    public int? SplashTabId { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of pages this portal may contain.
    /// </summary>
    /// <value>
    /// The value of the <c>PageQuota</c> column. Zero means no limit, which is the legacy reading
    /// and is preserved rather than reinterpreted.
    /// </value>
    /// <remarks>
    /// Column: <c>PageQuota int NOT NULL</c> defaulting to zero, added at 04.04.00.SqlDataProvider
    /// L14-L15. Legacy origin: <c>PortalInfo.vb</c> L173. Non-nullable because the column is. The
    /// quota is a limit, not a count: the current number of pages is not stored on this table, and
    /// the legacy member that reported it queried the database from inside a property getter and is
    /// therefore not carried forward (migration note 12).
    /// </remarks>
    public int PageQuota { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of user accounts this portal may contain.
    /// </summary>
    /// <value>
    /// The value of the <c>UserQuota</c> column. Zero means no limit, which is the legacy reading
    /// and is preserved rather than reinterpreted.
    /// </value>
    /// <remarks>
    /// Column: <c>UserQuota int NOT NULL</c> defaulting to zero, added at 04.04.00.SqlDataProvider
    /// L14 and L16. Legacy origin: <c>PortalInfo.vb</c> L181. Non-nullable because the column is,
    /// and a limit rather than a count for the same reason given on <see cref="PageQuota"/>.
    /// </remarks>
    public int UserQuota { get; set; }

    /// <summary>
    /// Gets the host names and paths through which this portal is reached.
    /// </summary>
    /// <value>
    /// The rows of <c>PortalAlias</c> that reference this portal. Empty rather than null when the
    /// portal has no alias or when the collection has not been loaded.
    /// </value>
    /// <remarks>
    /// <para>
    /// Inverse end of <c>FK_{objectQualifier}PortalAlias_{objectQualifier}Portals</c>
    /// (02.02.02.SqlDataProvider L3812). Aliases used to be a single column on this very table until
    /// it was dropped at 02.02.02.SqlDataProvider L3925-L3926, which is why no scalar alias property
    /// appears above (migration note 13).
    /// </para>
    /// <para>
    /// This and the five navigations that follow are the only members of this type that carry an
    /// initialiser, and the only ones without a setter. Both choices serve the same requirement:
    /// a collection expression guarantees the collection exists from the moment the object does, and
    /// omitting the setter guarantees no caller can ever put null back. The object-relational mapper
    /// populates a collection navigation by adding to the instance the getter returns rather than by
    /// assigning a new one, so a get-only collection is fully materialisable. An empty collection
    /// therefore means either "no related rows" or "not loaded", and says nothing on its own about
    /// which - ask the persistence layer, never this property.
    /// </para>
    /// </remarks>
    public ICollection<PortalAlias> PortalAliases { get; } = [];

    /// <summary>
    /// Gets the module instances that belong to this portal.
    /// </summary>
    /// <value>
    /// The rows of <c>Modules</c> that reference this portal. Empty rather than null.
    /// </value>
    /// <remarks>
    /// Inverse end of <c>FK_{objectQualifier}Modules_{objectQualifier}Portals</c>
    /// (03.00.09.SqlDataProvider L296, declared not for replication). The referencing column is
    /// nullable in the legacy schema, so host-level modules that belong to no portal simply do not
    /// appear in any portal's collection. Placement of a module on a page is a separate relationship
    /// carried by the page-module join entity, not by this collection.
    /// </remarks>
    public ICollection<Module> Modules { get; } = [];

    /// <summary>
    /// Gets the pages that belong to this portal.
    /// </summary>
    /// <value>
    /// The rows of <c>Tabs</c> that reference this portal, at every level of the page hierarchy.
    /// Empty rather than null.
    /// </value>
    /// <remarks>
    /// Inverse end of <c>FK_Tabs_Portals</c> (01.00.05.SqlDataProvider L1541, re-created there after
    /// the whole-table rebuild dropped it at L1418-L1419). The referencing column is nullable
    /// (01.00.00.SqlDataProvider L139-L141 and the page table's own portal column), which is how the
    /// legacy schema models host-level pages: they belong to no portal and appear in no portal's
    /// collection. This collection is flat - parent and child pages are related to each other
    /// through the page entity's own hierarchy, not through this navigation.
    /// </remarks>
    public ICollection<Tab> Tabs { get; } = [];

    /// <summary>
    /// Gets the security roles defined by this portal.
    /// </summary>
    /// <value>
    /// The rows of <c>Roles</c> that reference this portal. Empty rather than null.
    /// </value>
    /// <remarks>
    /// Inverse end of <c>FK_Roles_Portals</c> (01.00.05.SqlDataProvider L1486). Includes the roles
    /// nominated by <see cref="AdministratorRoleId"/> and <see cref="RegisteredRoleId"/>; those two
    /// scalars name a role by key and this collection is where the role itself is reached, which is
    /// why the role names the terminal view supplied as subqueries are not duplicated as properties
    /// (migration note 12).
    /// </remarks>
    public ICollection<Role> Roles { get; } = [];

    /// <summary>
    /// Gets the per-portal membership records of the accounts that belong to this portal.
    /// </summary>
    /// <value>
    /// The rows of <c>UserPortals</c> that reference this portal. Empty rather than null.
    /// </value>
    /// <remarks>
    /// Inverse end of <c>FK_UserPortals_Portals</c> (01.00.05.SqlDataProvider L1508). An account is
    /// a single row of the user table shared across portals, and its membership of a particular
    /// portal is the join row reached here - which is why this navigation is typed as the join entity
    /// rather than as the account. The number of such rows is not stored on this table; the legacy
    /// member that counted them queried the database from inside a property getter and is not carried
    /// forward (migration note 12).
    /// </remarks>
    public ICollection<UserPortal> UserPortals { get; } = [];

    /// <summary>
    /// Gets the desktop-module registrations that make module types available to this portal.
    /// </summary>
    /// <value>
    /// The rows of <c>PortalDesktopModules</c> that reference this portal. Empty rather than null.
    /// </value>
    /// <remarks>
    /// Inverse end of <c>FK_{objectQualifier}PortalDesktopModules_{objectQualifier}Portals</c>
    /// (02.02.02.SqlDataProvider L3066). This is the registration relationship - which module types
    /// a portal administrator may add - and is distinct from <see cref="Modules"/>, which holds the
    /// instances actually created. Module registration and lifecycle are preserved by this migration
    /// as a domain concern even though the Web Forms control-loading mechanism that once consumed
    /// them is not.
    /// </remarks>
    public ICollection<PortalDesktopModule> PortalDesktopModules { get; } = [];
}
