using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

// MIGRATION: legacy type DotNetNuke.Security.Permissions.PermissionInfo becomes this entity. The
//   mismatch between the legacy file name and the legacy type name is intentional and is resolved
//   here rather than reproduced: Library/Components/Security/Permissions/Permission.vb line 28
//   declares "Public Class PermissionInfo" inside a #Region of the same name, so the file was
//   already named after the table while the class carried an "...Info" suffix. That suffix existed
//   only to separate a data carrier from its static "...Controller" companion - here
//   Library/Components/Security/Permissions/PermissionController.vb - and the target draws that
//   distinction with the layer boundary instead. Dropping it makes the file and the type agree, and
//   downstream code that goes looking for a "PermissionInfo" will not find one by design.
//
// MIGRATION: the legacy XML-serialisation decoration is deliberately not carried across.
//   Permission.vb decorates PermissionID (line 42), PermissionCode (line 51) and PermissionKey
//   (line 69) with <XmlElement("permissionid")>, <XmlElement("permissioncode")> and
//   <XmlElement("permissionkey")>, and hides ModuleDefID (line 60) and PermissionName (line 78)
//   behind <XmlIgnore()>, because that one class doubled as its own wire format. In the target the
//   wire contract belongs to the DTOs at the API boundary and the storage contract belongs to the
//   Fluent configuration in the Infrastructure layer, so this entity carries no attribute of any
//   kind - no serialisation attribute and no data annotation. Two consequences are worth stating:
//   the lower-case element names disappear with the attributes that carried them, and ModuleDefID
//   and PermissionName are no longer concealed from callers that legitimately need them.
//
// MIGRATION: the five VB Property Get/Set blocks over the five private fields declared at lines 31
//   to 35 become five auto-properties, and the empty "Public Sub New()" at line 38 disappears with
//   them. Rule T8: nothing else about the legacy class survives - there is no reflection hydrator,
//   no sentinel translation and no pre-generics collection wrapper anywhere in this file.
//
// MIGRATION: PermissionKey stops being free text and becomes the closed DnnMigration.Domain.Enums
//   .PermissionKey enumeration. The legacy property is a String (Permission.vb line 69) that the
//   legacy code compared against bare upper-case literals, so a misspelling was a silent
//   no-match; the enumeration makes it a compile error instead. The COLUMN is still text, and the
//   obligations that places on the Infrastructure layer are set out per property below.

/// <summary>
/// One entry in the permission catalogue: a single named action, scoped by a permission code and
/// owned by a module definition, that a grant row is allowed to refer to.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a catalogue, not an access-control list.</b> A row here declares that an action
/// <i>exists</i> - "the VIEW action of a module definition", say. Whether a particular role or
/// account may perform it is a separate row in <c>dbo.ModulePermission</c> or
/// <c>dbo.TabPermission</c> that points back at this one. Nothing on this entity grants anything,
/// which is why it carries no role, no account and no allow-or-deny flag.
/// </para>
/// <para>
/// <b>Mapping brief for the Infrastructure layer.</b> The entity binds to the singular table
/// <c>dbo.Permission</c>, created by
/// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.00.SqlDataProvider</c> at lines 684 to
/// 690 with its primary key <c>PK_Permission</c> over <c>PermissionID</c> added at lines 723 to 728.
/// A sweep of all eighty-eight upgrade scripts, case-insensitively and across all four naming forms
/// the chain uses - bare, <c>dbo.</c>-qualified, <c>[dbo].[…]</c>-bracketed and
/// <c>{databaseOwner}{objectQualifier}</c>-templated - finds exactly two later structural statements
/// against this table, and both are recorded on the properties they affect. Per AAP Rule T4 the
/// TERMINAL state of that chain is what the Fluent configuration must reproduce; the baseline
/// declaration alone is not the schema.
/// </para>
/// <para>
/// <b>Every text column is ANSI, not Unicode.</b> <c>PermissionCode</c>, <c>PermissionKey</c> and
/// <c>PermissionName</c> are each declared <c>varchar(50)</c> in the terminal schema - never
/// <c>nvarchar</c> - so each mapping must say so explicitly with <c>IsUnicode(false)</c>. Letting
/// the provider default to <c>nvarchar</c> would make every parameter a different type from the
/// column it is compared against, which costs the unique index its usefulness for lookup and
/// changes comparison behaviour under a case-sensitive collation.
/// </para>
/// <para>
/// <b>The uniqueness rule spans three columns.</b> <c>IX_Permission</c> is unique over
/// <c>(PermissionCode, ModuleDefID, PermissionKey)</c> - see <see cref="PermissionCode"/> - so the
/// same key may exist once per scope code per definition, and a second row naming the same triple is
/// rejected by the database rather than by any check in this layer.
/// </para>
/// <para>
/// <b>No behaviour lives here.</b> Rule T6: this type performs no I/O, holds no service, reads no
/// clock and validates nothing. Rule T7: it uses honest CLR types and represents no value with a
/// sentinel. Rule T1: it references nothing outside <c>DnnMigration.Domain</c>, which is what keeps
/// the persistence and serialisation decisions described above in the layers that own them.
/// </para>
/// </remarks>
public sealed class Permission : Entity<int>
{
    /// <summary>
    /// Gets or sets the surrogate primary key of the catalogue entry.
    /// </summary>
    /// <value>
    /// The <c>PermissionID</c> column, <c>int IDENTITY(1, 1) NOT NULL</c>
    /// (<c>02.02.00.SqlDataProvider</c> line 685), so the Infrastructure mapping declares it
    /// generated on add with a seed and increment of one.
    /// </value>
    /// <remarks>
    /// MIGRATION: this table is one of the few whose identity seed is 1 rather than 0 or -1, so zero
    /// happens never to identify a real row here. That is emphatically NOT a licence to read zero as
    /// "not saved yet": <see cref="Entity{TId}"/> declares persisted state rather than deducing it,
    /// precisely because the sibling tables seed at 0 and -1, and this entity adds no member that
    /// would deduce it for the one table where the trick would have worked. A local convenience of
    /// that kind is how an inconsistency spreads.
    /// </remarks>
    public int PermissionId { get; set; }

    /// <inheritdoc />
    public override int Identity => PermissionId;

    /// <summary>
    /// Gets or sets the code that scopes the key to a subsystem.
    /// </summary>
    /// <value>
    /// The <c>PermissionCode</c> column, <c>varchar(50) NOT NULL</c>
    /// (<c>02.02.00.SqlDataProvider</c> line 686). The shipped vocabulary is
    /// <c>SYSTEM_MODULE_DEFINITION</c>, <c>SYSTEM_TAB</c> and <c>SYSTEM_FOLDER</c>. Defaults to the
    /// empty string so a newly constructed entry is never null.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: deliberately a plain <see cref="string"/> and NOT an enumeration, which is the
    /// opposite of the decision taken for <see cref="PermissionKey"/>. Nothing constrains this
    /// column - no check constraint, no lookup foreign key - so an installation carrying a scope
    /// this migration has never seen must round-trip it intact rather than fail to materialise.
    /// </para>
    /// <para>
    /// MIGRATION: this column leads the unique index <c>IX_Permission</c>, added by
    /// <c>Website/Providers/DataProviders/SqlDataProvider/04.05.02.SqlDataProvider</c> at lines 333
    /// to 341 as <c>ALTER TABLE …Permission ADD CONSTRAINT IX_{objectQualifier}Permission UNIQUE
    /// NONCLUSTERED (PermissionCode, ModuleDefID, PermissionKey)</c>. The Infrastructure
    /// configuration must declare that index over exactly those three columns in exactly that order.
    /// The script removes duplicate triples first (lines 304 to 331), which is direct evidence that
    /// real installations held them before the constraint existed, so the constraint is load-bearing
    /// rather than decorative.
    /// </para>
    /// </remarks>
    public string PermissionCode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the identifier of the module definition that owns this catalogue entry.
    /// </summary>
    /// <value>
    /// The <c>ModuleDefID</c> column, <c>int NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line
    /// 687).
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: renamed from the legacy <c>ModuleDefID</c> to the idiomatic
    /// <c>ModuleDefinitionId</c>, so the Infrastructure configuration MUST map it back with
    /// <c>HasColumnName("ModuleDefID")</c>. Left to convention the provider would look for a
    /// <c>ModuleDefinitionId</c> column, which does not exist, and the mapping would fail at first
    /// use rather than at build time.
    /// </para>
    /// <para>
    /// MIGRATION: required, and a foreign key in intent only - which is why NO relationship is modelled
    /// over it. The upgrade chain declares no <c>FOREIGN KEY</c> on this column anywhere; the only
    /// constraints naming this table point the other way, from the grant tables to
    /// <c>Permission.PermissionID</c> (<c>02.02.00.SqlDataProvider</c> lines 744, 768 and 777, rebuilt
    /// with cascade delete by <c>03.00.09.SqlDataProvider</c> lines 482, 488 and 492). A system-level
    /// entry - one whose <see cref="PermissionCode"/> is <c>SYSTEM_TAB</c>,
    /// <c>SYSTEM_MODULE_DEFINITION</c> or <c>SYSTEM_FOLDER</c>, and which therefore belongs to no
    /// particular definition - carries the value -1 here and has no principal row at all. Because the
    /// column is <c>NOT NULL</c>, any relationship the object-relational mapper could express over it
    /// would be a REQUIRED one, asserting a principal those rows do not have; the omission is explained
    /// in full below the display-name property and mirrored in <c>PermissionConfiguration</c>.
    /// </para>
    /// <para>
    /// MIGRATION: that -1 is <b>real data, not the legacy <c>Null.NullInteger</c> sentinel</b>. Rule T7
    /// keeps sentinels out of the domain, and this is the one place on this entity where the distinction
    /// bites: the column is <c>NOT NULL</c>, so -1 cannot mean "absent" - it means "system level" - and
    /// it must never be converted to a null, mapped to a nullable property, or read as a missing value.
    /// The practical consequence for callers is that no definition can be resolved for such a row,
    /// because there is no definition to resolve; a caller holding a real identifier resolves it through
    /// <c>IModuleDefinitionRepository</c> and handles the -1 case explicitly.
    /// </para>
    /// </remarks>
    public int ModuleDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the action this catalogue entry names.
    /// </summary>
    /// <value>
    /// The <c>PermissionKey</c> column, whose terminal declaration is <c>varchar(50) NOT NULL</c>.
    /// One of the four members of <see cref="Enums.PermissionKey"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: <b>the terminal width is 50, not 20.</b> The baseline declares
    /// <c>[PermissionKey] [varchar] (20) NOT NULL</c>
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/02.02.00.SqlDataProvider</c> line 688),
    /// and <c>04.06.00.SqlDataProvider</c> then widens it under the comment "enlarge permission key
    /// field" at lines 394 to 400 with <c>ALTER TABLE …Permission ALTER COLUMN PermissionKey
    /// varchar(50) not null</c>, recreating <c>AddPermission</c> (line 404) and
    /// <c>UpdatePermission</c> (line 432) with <c>@PermissionKey varchar(50)</c> to match. Nothing
    /// after that touches the column. The Infrastructure configuration must therefore declare
    /// <c>HasMaxLength(50)</c> and <c>IsUnicode(false)</c>, taking its length from the TERMINAL form
    /// as Rule T4 requires; a mapping that copies the baseline 20 would silently truncate or reject
    /// values the live column accepts, and any other statement of this column's width - including one
    /// in a sibling file that cites the baseline script - is superseded by the two lines above.
    /// </para>
    /// <para>
    /// MIGRATION: <b>persist the member identifier, never the ordinal.</b> The column is text, so the
    /// Infrastructure configuration must apply an explicit string value conversion that writes
    /// exactly <c>VIEW</c>, <c>EDIT</c>, <c>READ</c> or <c>WRITE</c> - the enumeration's own member
    /// identifiers, which are what a production database already contains - and reads the same
    /// spellings back. The members carry no explicit numeric values and their ordinals are
    /// incidental, so a default enumeration mapping would store 0, 1, 2 or 3 into a <c>varchar</c>
    /// column and every legacy row and every legacy predicate would stop matching. The house
    /// precedent for exactly this shape is
    /// <c>Infrastructure/Persistence/ValueConverters/BillingFrequencyToStringConverter.cs</c>, bound
    /// by <c>RoleConfiguration</c>. The conversion itself cannot live here: Rule T1 forbids this
    /// layer any persistence dependency, and Rule T3 puts mapping in Infrastructure.
    /// </para>
    /// <para>
    /// MIGRATION: <b>the spellings are a contract in three places at once.</b> They are the stored
    /// values, they are what the API emits and accepts - see
    /// <c>Application/Serialization/PermissionKeyJsonConverter.cs</c>, which formats with
    /// <c>nameof</c> and refuses anything it does not recognise - and the Angular
    /// <c>hasPermission</c> directive compares against the same upper-case strings. A member may
    /// consequently never be renamed and never be case-normalised at any boundary; matching an
    /// inbound or stored spelling case-insensitively is fine, but the value written must be the
    /// canonical identifier. An unrecognised stored value must fail loudly rather than resolve to a
    /// member: silently degrading an unknown key to the zero member would turn it into a VIEW grant,
    /// which is the one failure mode a security vocabulary must not have. The framework's own string
    /// conversion behaves that way already - it resolves a stored spelling by name, case-insensitively,
    /// and refuses anything else with "Cannot convert string value … to any value in the mapped
    /// 'PermissionKey' enum" rather than substituting a member - so the correct mapping needs no
    /// defensive fallback, and adding one would be a regression rather than a safeguard.
    /// </para>
    /// <para>
    /// MIGRATION: <b>a key is not a bit-mask.</b> The enumeration declares no <c>[Flags]</c>
    /// attribute and must never acquire one, and no member may be combined with another. A row here
    /// names exactly one action; holding several actions is several grant rows, which is the model
    /// the schema actually has.
    /// </para>
    /// </remarks>
    public PermissionKey PermissionKey { get; set; }

    /// <summary>
    /// Gets or sets the human-readable name of the action, as the legacy permission grids showed it.
    /// </summary>
    /// <value>
    /// The <c>PermissionName</c> column, <c>varchar(50) NOT NULL</c>
    /// (<c>02.02.00.SqlDataProvider</c> line 689) - for example <c>View Module</c> for the
    /// <see cref="Enums.PermissionKey.VIEW"/> entry. Defaults to the empty string so a newly
    /// constructed entry is never null.
    /// </value>
    /// <remarks>
    /// MIGRATION: display text, and only display text. The legacy class hid it from its own wire
    /// format with <c>&lt;XmlIgnore()&gt;</c> (Permission.vb line 78) while every permission grid
    /// rendered it, so the target exposes it plainly and leaves the decision of whether to project it
    /// to the DTO that answers a particular request. It is never matched on: <see cref="PermissionKey"/>
    /// is the machine-readable identity of the action.
    /// </remarks>
    public string PermissionName { get; set; } = string.Empty;

    // MIGRATION: THERE IS DELIBERATELY NO ModuleDefinition NAVIGATION ON THIS ENTITY, and adding one
    //   would be a defect rather than a convenience. Three facts force the omission and they compound.
    //   First, no physical foreign key from dbo.Permission to dbo.ModuleDefinitions exists anywhere in
    //   the eighty-eight-script chain - PermissionConfiguration records the exhaustive proof of that
    //   absence. Second, the system-level catalogue rows the legacy installer creates carry
    //   ModuleDefID = -1, which matches no ModuleDefinitions row: the value is REAL DATA identifying a
    //   product-wide permission, not an absent reference, and it is exactly why the database never
    //   enforced the reference. Third, and decisively, ModuleDefID is int NOT NULL, so any relationship
    //   Entity Framework Core could express over it is a REQUIRED one - an optional relationship over a
    //   non-nullable foreign key is rejected at model validation, and a nullable navigation cannot
    //   change that. A required relationship asserts every row has a principal, which for the -1 rows is
    //   false; it also makes the navigation permanently unloadable for them and invites an Include that
    //   can never succeed.
    //
    // MIGRATION: the scalar <see cref="ModuleDefinitionId"/> above is the whole of the association and
    //   is mapped to the real ModuleDefID column, so nothing about the persisted shape changes. A caller
    //   that needs the definition behind a real identifier resolves it explicitly through
    //   IModuleDefinitionRepository, which forces the -1 case to be handled deliberately instead of
    //   being hidden behind a navigation that silently yields nothing. ModuleDefinition carries no
    //   inverse collection for the same reason: leaving one in place would let the relationship-discovery
    //   convention rebuild exactly the required relationship this omission exists to prevent.

    /// <summary>
    /// Gets the module-level grants that refer to this catalogue entry.
    /// </summary>
    /// <value>
    /// The <c>dbo.ModulePermission</c> rows whose <c>PermissionID</c> names this entry. Initialised
    /// to an empty collection, so it is never <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// MIGRATION: <see cref="ModulePermission"/> REFERENCES this entity by
    /// <see cref="ModulePermission.PermissionId"/>; it does not and must not derive from it. The
    /// legacy model had <c>ModulePermissionInfo</c> inherit <c>PermissionInfo</c> and flatten the
    /// joined catalogue columns onto the grant, which made a grant indistinguishable from the action
    /// it grants and duplicated <c>PermissionCode</c>, <c>PermissionKey</c> and <c>PermissionName</c>
    /// onto every row. The tables were always separate, related by a foreign key with cascade delete
    /// (<c>03.00.09.SqlDataProvider</c> line 492), and the target models them that way: joined
    /// columns are reassembled by an Application-layer projection when a caller needs them, and
    /// nowhere else. The collection is exposed get-only so that the instance the mapper populates can
    /// never be swapped out from under it.
    /// </remarks>
    public ICollection<ModulePermission> ModulePermissions { get; } = new List<ModulePermission>();

    /// <summary>
    /// Gets the page-level grants that refer to this catalogue entry.
    /// </summary>
    /// <value>
    /// The <c>dbo.TabPermission</c> rows whose <c>PermissionID</c> names this entry. Initialised to
    /// an empty collection, so it is never <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// MIGRATION: the page counterpart of <see cref="ModulePermissions"/>, and the same rule applies
    /// to it. <see cref="TabPermission"/> references this entity by
    /// <see cref="TabPermission.PermissionId"/> and does not derive from it, superseding the legacy
    /// <c>TabPermissionInfo</c> inheritance; its foreign key with cascade delete is at
    /// <c>03.00.09.SqlDataProvider</c> line 488. Two collections rather than one because the two
    /// grant tables are genuinely different scopes: <c>dbo.Modules.ModuleID</c> and
    /// <c>dbo.Tabs.TabID</c> both seed at 0, so an identifier alone does not say what it identifies
    /// and merging the grants would lose the distinction.
    /// </remarks>
    public ICollection<TabPermission> TabPermissions { get; } = new List<TabPermission>();
}
