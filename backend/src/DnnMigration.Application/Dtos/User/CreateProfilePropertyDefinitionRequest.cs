namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Request contract for <c>POST /api/v1/portals/{portalId}/profile-definitions</c>: the ten values that
/// declare a new profile property for a portal to collect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists separately from the response shape.</b> Both write actions on this resource
/// used to bind <see cref="ProfilePropertyDefinitionDto"/>, the thirteen-member response projection, and
/// each verb honoured a different subset of it. Creation ignored three members - the definition's own
/// identifier, its owning portal and the default visibility - while the update path ignored those three
/// AND the module-definition reference. So one published schema described two endpoints with different
/// effective shapes, and a caller submitting an ignored member was answered <c>201</c> or <c>200</c>
/// without learning the value had been discarded. Declaring one request contract per verb makes the
/// honoured set the published set.
/// </para>
/// <para>
/// <b>The member set is the terminal insert procedure's own, exactly.</b> The authority is the last form
/// of <c>AddPropertyDefinition</c> in the destructive eighty-eight-script chain, at
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.06.00.SqlDataProvider</c> line 1101: it declares
/// eleven parameters - the portal, which arrives in the route here, plus exactly the ten members below -
/// and its <c>INSERT</c> column list names those ten alongside the portal and a literal zero for the
/// soft-delete flag. The legacy editor
/// <c>Website/admin/Users/EditProfileDefinition.ascx</c> reached the same set through a single reflective
/// property editor at L23 rather than through named controls, which is why the procedure rather than the
/// markup is cited as the authority for the count.
/// </para>
/// <para>
/// <b>Three members of the response shape are deliberately absent.</b> The definition's identifier is
/// assigned by the store, which is precisely why the HTTP method rather than a sentinel distinguishes
/// this from an update - the legacy screen overloaded the value minus one as that switch
/// (<c>EditProfileDefinition.ascx.vb</c> L449). The owning portal arrives in the route and is resolved
/// once per request, so accepting it in the body would give the tenant a second, contradictable source
/// of truth. And the default visibility is not a column of this table at all: verified across all
/// eighty-eight scripts, <c>ProfilePropertyDefinition</c> has no <c>Visibility</c> column, and the
/// stored per-account counterpart lives on <c>UserProfile</c> - so the response projection carries it as
/// a portal-level default hint, which is a read-side fact with nothing for a write to do.
/// </para>
/// <para>
/// The type is inert: no behaviour, no derived member, no guard and no constructor. Field rules are
/// declared by <c>Application/Validation/CreateProfilePropertyDefinitionRequestValidator.cs</c>, which
/// is public and is discovered by the assembly scan in <c>Application/DependencyInjection.cs</c>, so the
/// API's validation filter resolves it and reports a breach as an RFC 7807 validation document naming
/// the member. The uniqueness the schema enforces through
/// <c>IX_ProfilePropertyDefinition</c> needs a read, so it is an expected failure raised by
/// <c>Application/Services/UserService.cs</c> and answered as a conflict. Translation onto the persisted
/// model lives in <c>Application/Mapping/UserMappings.cs</c>.
/// </para>
/// </remarks>
// MIGRATION: no inheritance and no shared base type with the sibling update contract, even though nine
// of these ten members also appear there. A base type would have to hold the nine and leave the tenth on
// the derived type, which puts the one member that actually differs between the two verbs in the least
// visible place; the difference is the whole point of splitting them, so each contract states its own
// members outright and the duplication is intentional. Shared RULES are shared instead, through
// Application/Validation/ProfileDefinitionTermsRules.cs.
//
// MIGRATION: the soft-delete flag is absent from both write contracts. The terminal insert procedure
// writes a literal zero into Deleted and its update counterpart never touches the column, so the flag is
// moved by a deletion alone. Publishing it would invite a caller to resurrect or bury a definition
// through an edit, which no legacy screen offered.
public sealed class CreateProfilePropertyDefinitionRequest : IProfilePropertyDefinitionWriteMembers
{
    /// <summary>
    /// Identifier of the module definition that contributes this property, or <see langword="null"/>
    /// when the property belongs to the portal rather than to a module.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@ModuleDefId int</c>
    /// (<c>04.06.00.SqlDataProvider</c> L1103). Column <c>ModuleDefID int NULL</c>, and part of the
    /// unique index over portal, module definition and property name.
    /// <c>ModuleDefinitions.ModuleDefID</c> is declared <c>IDENTITY(1, 1)</c>, so 1 is the lowest real
    /// identifier and no legitimate value can be confused with the legacy -1 marker - which is what
    /// makes a nullable <c>int</c> the honest shape here, where the same choice would be unsafe for a
    /// portal key.
    /// </remarks>
    // MIGRATION: this member is on the CREATE contract and deliberately not on the update one, because
    // the terminal update procedure (04.05.00.SqlDataProvider L1685) declares no such parameter and
    // assigns no such column - a property cannot be moved between a module and its portal after it has
    // been declared. The legacy class initialised the underlying value to the -1 null-integer sentinel to
    // mean "no module definition"; that sentinel is expressed as null here, and no code on this side may
    // compare against -1 or read a non-positive value as absent.
    public int? ModuleDefId { get; set; }

    /// <summary>
    /// Identifier of the data type that governs how the property's value is edited, validated and
    /// displayed.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@DataType int</c>, column <c>DataType int NOT NULL</c>. It is
    /// not an enumeration: it is a foreign key into the shared <c>Lists</c> lookup table, restricted to
    /// the entries whose list name is <c>DataType</c>. The legacy editor bound a drop-down to that
    /// list, which constrained the choice without declaring a validator.
    /// </remarks>
    // MIGRATION: the Lists subsystem is outside this migration's scope, so there is no lookup service to
    // resolve this key against and no enumeration may be invented to stand in for one - inventing a
    // numeric range would refuse whichever identifiers a given installation's list happens to carry. The
    // key travels as the opaque int the schema declares, and whether it names a row is a question about
    // stored state.
    public int DataType { get; set; }

    /// <summary>
    /// Value pre-populated into the property when a profile is first presented, or
    /// <see langword="null"/> when the property has no default.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@DefaultValue ntext</c>, column <c>DefaultValue</c>, nullable.
    /// Created as <c>nvarchar(50)</c> at <c>03.02.03.SqlDataProvider</c> L1069 and widened to
    /// <c>ntext</c> by <c>04.05.00.SqlDataProvider</c> L1593, so the terminal column is effectively
    /// unbounded and carries no length rule: a limit of 50 would refuse values an upgraded database
    /// already holds.
    /// </remarks>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Grouping heading under which the property is presented; the "Category" column of the legacy
    /// definition grid. Required.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@PropertyCategory nvarchar(50)</c>, column
    /// <c>PropertyCategory nvarchar(50) NOT NULL</c> (<c>03.02.03.SqlDataProvider</c> L1070, never
    /// altered). The legacy class marks it required through
    /// <c>&lt;Required(True), SortOrder(2)&gt;</c> at <c>ProfilePropertyDefinition.vb</c> L193.
    /// </remarks>
    // MIGRATION: non-nullable and initialised to the empty string rather than to a null-forgiving
    // default, because the column is NOT NULL and an omitted category is a MISSING required field rather
    // than a null one.
    public string PropertyCategory { get; set; } = string.Empty;

    /// <summary>
    /// Stable, machine-readable name of the property, and the key by which a stored profile value is
    /// matched back to its definition. Required, and unique within the portal and module definition.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@PropertyName nvarchar(50)</c>, column
    /// <c>PropertyName nvarchar(50) NOT NULL</c> (<c>03.02.03.SqlDataProvider</c> L1071, never
    /// altered), and part of the unique index over portal, module definition and property name. The
    /// legacy class marks it required and constrains it with the regular expression
    /// <c>^[a-zA-Z0-9._%\-+']+$</c> at <c>ProfilePropertyDefinition.vb</c> L228 - a pattern that
    /// excludes whitespace, which is load-bearing rather than stylistic because a later upgrade script
    /// repairs existing rows by replacing spaces with underscores.
    /// </remarks>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of characters the property's value may hold, or the display width for the types
    /// that use one; zero when unconstrained.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@Length int</c>, column <c>Length int NOT NULL</c> with a store
    /// default of zero.
    /// </remarks>
    // MIGRATION: zero legitimately means "no explicit length" and is not an unset value, so this member
    // must never be omitted from a payload on the grounds of holding its default. A negative width is
    // meaningless and is floored to zero by the mapper, which is the behaviour the legacy profile editor
    // relied on; that floor applies to a COLUMN WIDTH and never to an identifier.
    public int Length { get; set; }

    /// <summary>
    /// Whether a profile may not be saved while this property is empty.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@Required bit</c>, column <c>Required bit NOT NULL</c>, rendered
    /// as an inline check box in the legacy definition grid (<c>ProfileDefinitions.ascx</c> L32).
    /// </remarks>
    // MIGRATION: the member describes a rule the validation layer applies to a PROFILE SUBMISSION; it is
    // data on this contract and imposes nothing on the contract itself. A boolean is its own constraint,
    // so it carries no rule.
    public bool Required { get; set; }

    /// <summary>
    /// Regular expression a submitted value has to satisfy, or <see langword="null"/> when the property
    /// is unconstrained beyond its data type.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@ValidationExpression nvarchar(2000)</c>, column nullable.
    /// Created as <c>nvarchar(100)</c> at <c>03.02.03.SqlDataProvider</c> L1074 and widened to
    /// <c>nvarchar(2000)</c> by <c>04.03.05.SqlDataProvider</c> L17, so 2000 is the terminal width the
    /// validator uses: a limit of 100 would refuse expressions an upgraded database already holds.
    /// </remarks>
    // MIGRATION: this value is itself a rule to be applied to profile input, not a rule applied to this
    // field. It is stored verbatim and is never compiled or executed by this layer.
    public string? ValidationExpression { get; set; }

    /// <summary>
    /// Position of the property within its portal's profile, or -1 to append it after the last existing
    /// property.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@ViewOrder int</c>, column <c>ViewOrder int NOT NULL</c>. The
    /// legacy class marks it required through <c>&lt;Required(True), SortOrder(8)&gt;</c> at
    /// <c>ProfilePropertyDefinition.vb</c> L300.
    /// </remarks>
    // MIGRATION: -1 IS AN INSTRUCTION HERE, NOT AN ABSENCE MARKER, and no lower bound may be placed on
    // this member. The terminal insert procedure branches on "IF @vieworder=-1" and substitutes the
    // current maximum order plus one (04.06.00.SqlDataProvider L1122-L1126), so -1 means "append to the
    // end". Refusing it, normalising it away, clamping it to zero or rewriting it as null would each
    // remove the only way a caller can say that.
    public int ViewOrder { get; set; }

    /// <summary>
    /// Whether the property is presented at all.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@Visible bit</c>, column <c>Visible bit NOT NULL</c>, rendered
    /// as an inline check box in the legacy definition grid (<c>ProfileDefinitions.ascx</c> L33).
    /// Distinct from the response projection's default-visibility member, which answers a different
    /// question: not whether the field is shown, but to whom a stored value is disclosed.
    /// </remarks>
    public bool Visible { get; set; }
}
