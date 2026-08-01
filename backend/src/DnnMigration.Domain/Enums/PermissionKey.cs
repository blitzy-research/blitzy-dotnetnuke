namespace DnnMigration.Domain.Enums;

/// <summary>
/// The DotNetNuke permission keys: the discrete access rights that a row in the
/// <c>Permission</c> catalogue table can name. Each member's <i>name</i> is the value
/// persisted in the database and carried over the wire.
/// </summary>
/// <remarks>
/// <para>
/// THE MEMBER NAME IS THE CONTRACT. The legacy schema stores this concept as text
/// rather than as a number: <c>Permission.PermissionKey</c> is declared
/// <c>varchar(20) NOT NULL</c> in
/// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.00.SqlDataProvider:L688</c>,
/// and the <c>AddPermission</c> procedure in that same script (L843-L862) accepts
/// <c>@PermissionKey varchar(20)</c>. The members below are therefore spelled exactly
/// as the legacy literals are spelled, in upper case, so that
/// <c>PermissionKey.VIEW.ToString()</c> and <c>nameof(PermissionKey.VIEW)</c> each
/// yield <c>"VIEW"</c> with no casing step in between. A PascalCase spelling would
/// require callers to remember an upper-casing transform, and forgetting it would
/// produce <c>"View"</c>, which silently fails to match any stored row. These names
/// are load-bearing data that a production database already contains: never rename
/// them, never re-case them.
/// </para>
/// <para>
/// THE ORDINAL IS MEANINGLESS. No number for this concept is stored anywhere, so no
/// member is given an explicit value. The implicit ordinals are incidental artefacts
/// of declaration order: they are never persisted, never serialised, and never sent
/// over the wire. Only the member name is the contract. It follows that the Entity
/// Framework Core mapping in
/// <c>Infrastructure/Persistence/Configurations/PermissionConfiguration.cs</c> must
/// apply a STRING value conversion over the <c>varchar(20)</c> column, converting each
/// member to and from its name, and must never convert to an integer. That conversion
/// is deliberately absent here, because the Domain layer takes no dependency on any
/// persistence, mapping or serialisation technology.
/// </para>
/// <para>
/// THESE FOUR KEYS ARE THE EXHAUSTIVE SET for this DotNetNuke generation. Measured
/// occurrence counts across the legacy tree were EDIT 13, VIEW 12, WRITE 3 and READ 2;
/// independently, a sweep of every value compared or assigned to <c>PermissionKey</c>
/// across all 88 upgrade scripts yields only READ and WRITE literals, and no other key
/// at all. Keys belonging to later DotNetNuke versions or to general permission
/// vocabularies — DEPLOY, ADD, DELETE, MANAGE, FULLCONTROL and their kin — do not
/// exist in this codebase and must not be added. Apparent sightings are something
/// else: in this tree "ADD" is a selection value on an admin screen, while 'Manage'
/// and 'Import' are <c>ModuleControls.ControlKey</c> values.
/// </para>
/// <para>
/// GRANTS ARE ROWS, NOT BITS. A <c>Permission</c> row names exactly one key, and an
/// access grant is a separate <c>ModulePermission</c> or <c>TabPermission</c> row
/// bound to a role. These members are consequently not bit-mask flags and must never
/// be combined or or-ed together; doing so would invent a data model that the schema
/// does not have.
/// </para>
/// <para>
/// THE SIBLING CONCEPT IS NOT ENUMERATED. <c>Permission.PermissionCode</c> scopes a
/// key to a subsystem and carries the values <c>SYSTEM_TAB</c>,
/// <c>SYSTEM_MODULE_DEFINITION</c> and <c>SYSTEM_FOLDER</c>. Declared
/// <c>varchar(50)</c>, it deliberately remains a plain <c>string</c> property on the
/// <c>Permission</c> entity, so an installation carrying a scope this codebase has not
/// seen still round-trips intact.
/// </para>
/// <para>
/// THERE IS NO ABSENT VALUE. The column is <c>NOT NULL</c>, so no "unset" or
/// placeholder member is declared and no member carries a negative value. Where a
/// caller genuinely needs to express absence, it does so with a nullable projection
/// (<c>PermissionKey?</c>) on its own property, never by adding a member here. Note
/// also that the legacy null-sentinel for text was the empty string rather than a
/// null, so an empty key is not modelled either.
/// </para>
/// <para>
/// CLIENT-SIDE GATING IS NEVER SUFFICIENT. The Angular <c>hasPermission</c> directive
/// consumes these exact strings to show or hide affordances, but that is a convenience
/// only. Authorisation is decided on the server by <c>PermissionEvaluator</c> and the
/// permission authorisation policies, and every endpoint enforces it independently of
/// whatever the browser chose to render.
/// </para>
/// </remarks>
// MIGRATION: the legacy representation was bare upper-case string literals — "VIEW",
//   "EDIT", "READ", "WRITE" — scattered across PermissionController.vb,
//   ModulePermissionController.vb, TabPermissionController.vb, PortalSecurity.vb and
//   the call sites cited on each member below. They are centralised here as named
//   members whose NAMES are the persisted and wire values, matching
//   Permission.PermissionKey varchar(20) NOT NULL. Member names must never be renamed
//   or re-cased. The ordinals are incidental: they are never persisted and never
//   serialised, so the Infrastructure mapping must convert by name, not by number.
//   The legacy serialisation decoration on PermissionInfo is intentionally not
//   carried across; the wire shape is owned by the API boundary instead.
public enum PermissionKey
{
    /// <summary>
    /// Permission to view a module or a tab. One of the two keys used by the module
    /// and tab permission triads, and the key whose inheritance is special-cased when
    /// a module defers its view rights to its tab. Legacy call sites:
    /// <c>ModuleController.vb:L134</c> and <c>:L136</c> resolve the authorised view
    /// roles, <c>ModuleController.vb:L1108</c> compares <c>PermissionKey</c> against
    /// the literal directly, and <c>TabController.vb:L110</c> resolves the tab's
    /// authorised roles.
    /// </summary>
    VIEW,

    /// <summary>
    /// Permission to edit a module or a tab, which in DotNetNuke also denotes
    /// administrative control of the item. Legacy call sites:
    /// <c>ModuleController.vb:L130</c> resolves the authorised edit roles,
    /// <c>TabController.vb:L109</c> resolves the tab's administrator roles, and
    /// <c>PortalSecurity.vb:L522</c>, <c>:L618</c>, <c>:L623</c> and <c>:L628</c> test
    /// for it when deciding whether the caller may edit a module.
    /// </summary>
    EDIT,

    /// <summary>
    /// Permission to read a folder. Seeded against the <c>SYSTEM_FOLDER</c> scope by
    /// the upgrade scripts at <c>03.00.11:L15</c>, <c>03.02.04:L28</c>,
    /// <c>03.03.03:L31</c> and <c>04.00.04:L2584</c>. It is not only a folder
    /// artefact: <c>PortalController.vb:L1416</c> compares <c>PermissionKey</c>
    /// against this literal while seeding a new portal's root folder, granting read
    /// access to the all-users role, so the key is reachable from portal creation.
    /// </summary>
    READ,

    /// <summary>
    /// Permission to write to a folder. Seeded against the <c>SYSTEM_FOLDER</c> scope
    /// by the upgrade scripts at <c>03.02.04:L33</c>, <c>03.03.03:L36</c> and
    /// <c>04.00.04:L2589</c>. Retained because the <c>Permission</c> catalogue holds a
    /// row for every scope, so omitting it would leave part of the catalogue
    /// unrepresentable by the read-only permissions endpoint.
    /// </summary>
    WRITE
}
