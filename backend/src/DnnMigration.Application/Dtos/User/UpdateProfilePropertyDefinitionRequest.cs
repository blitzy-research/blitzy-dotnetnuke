namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Request contract for <c>PUT /api/v1/portals/{portalId}/profile-definitions/{propertyDefinitionId}</c>:
/// the writable state of an existing profile property declaration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists separately from the response shape.</b> Both write actions on this resource
/// used to bind <see cref="ProfilePropertyDefinitionDto"/>, the thirteen-member response projection, and
/// each verb honoured a different subset of it - this one ignored FOUR members: the definition's own
/// identifier, its owning portal, the default visibility, and the module-definition reference that the
/// creation path does honour. One published schema therefore described two endpoints with different
/// effective shapes, and a caller submitting an ignored member was answered <c>200</c> without learning
/// the value had been discarded. Declaring one request contract per verb makes the honoured set the
/// published set, and makes the one member that genuinely differs between the two verbs visible.
/// </para>
/// <para>
/// <b>The member set is the terminal update procedure's own, exactly.</b> The authority is the last form
/// of <c>UpdatePropertyDefinition</c> in the destructive eighty-eight-script chain, at
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.05.00.SqlDataProvider</c> line 1685: it declares
/// ten parameters - the definition identifier, which arrives in the route here, plus exactly the nine
/// members below - and its <c>UPDATE ... SET</c> list names those nine columns, one per member.
/// </para>
/// <para>
/// <b>The module-definition reference is deliberately absent, and its absence is the difference from the
/// creation contract.</b> The terminal update procedure declares no such parameter and assigns no such
/// column, so a property cannot be moved between a module and its portal once it has been declared. The
/// definition's identifier arrives in the route, the owning portal is resolved once per request, and the
/// default visibility is not a column of this table at all - verified across all eighty-eight scripts,
/// <c>ProfilePropertyDefinition</c> has no <c>Visibility</c> column, and the stored per-account
/// counterpart lives on <c>UserProfile</c>.
/// </para>
/// <para>
/// <b>This is a full replacement, not a partial edit.</b> Every member is applied as supplied, so
/// omitting a nullable member clears the stored value and omitting a non-nullable flag clears it to
/// false. A caller amending one field reads the definition first and resubmits the rest - which is also
/// how a reorder is expressed: the legacy grid's Up and Dn commands swapped the display order of two
/// adjacent definitions and persisted each through this same operation
/// (<c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> L295), so moving a property is a
/// <c>PUT</c> carrying a new display order.
/// </para>
/// <para>
/// The type is inert: no behaviour, no derived member, no guard and no constructor. Field rules are
/// declared by <c>Application/Validation/UpdateProfilePropertyDefinitionRequestValidator.cs</c>, which is
/// public and is discovered by the assembly scan in <c>Application/DependencyInjection.cs</c>, and which
/// shares every rule with the creation validator through
/// <c>Application/Validation/ProfileDefinitionTermsRules.cs</c> so the two verbs cannot diverge. The
/// uniqueness the schema enforces through <c>IX_ProfilePropertyDefinition</c> needs a read, so it is an
/// expected failure raised by <c>Application/Services/UserService.cs</c>, which excludes the definition
/// being edited from the comparison and answers a collision as a conflict. Translation onto the persisted
/// model lives in <c>Application/Mapping/UserMappings.cs</c>.
/// </para>
/// </remarks>
// MIGRATION: no inheritance and no shared base type with the sibling creation contract, for the reason
// recorded on that contract: the one member that differs between the two verbs must not be the one a
// reader has to go looking for. Shared RULES are shared instead.
//
// MIGRATION: the soft-delete flag is absent from both write contracts. The terminal update procedure
// never touches the Deleted column and the terminal insert writes a literal zero into it, so the flag is
// moved by a deletion alone. Publishing it would invite a caller to resurrect or bury a definition
// through an edit, which no legacy screen offered.
//
// MIGRATION: the property NAME is present and writable, and that is measured rather than assumed. The
// legacy class marks the member read-only for its reflective editor
// (ProfilePropertyDefinition.vb L228), but the terminal update procedure assigns
// "PropertyName = @PropertyName" (04.05.00.SqlDataProvider L1702), so the stored name genuinely could
// change. Because it can, the uniqueness comparison in the service excludes the definition being edited -
// otherwise every ordinary edit, which resubmits the name it read, would collide with itself.
public sealed class UpdateProfilePropertyDefinitionRequest : IProfilePropertyDefinitionWriteMembers
{
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
    /// Replacement value pre-populated into the property when a profile is first presented, or
    /// <see langword="null"/> when the property has no default.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@DefaultValue nvarchar(50)</c>, column <c>DefaultValue</c>,
    /// nullable. The parameter narrowed while the column widened, which is why the COLUMN decides:
    /// it was created as <c>nvarchar(50)</c> at <c>03.02.03.SqlDataProvider</c> L1069 and widened to
    /// <c>ntext</c> by <c>04.05.00.SqlDataProvider</c> L1593, so the terminal column is effectively
    /// unbounded and carries no length rule. A limit of 50 taken from either the creating script or this
    /// procedure's parameter would refuse values an upgraded database already holds.
    /// </remarks>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Replacement grouping heading under which the property is presented; the "Category" column of the legacy
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
    /// Replacement name of the property, which is also the key by which a stored profile value is
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
    /// Replacement maximum number of characters the property's value may hold, or the display width for the types
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
    /// Terminal procedure parameter <c>@ValidationExpression nvarchar(100)</c>, column nullable - and
    /// again the COLUMN decides rather than the parameter:
    /// it was created as <c>nvarchar(100)</c> at <c>03.02.03.SqlDataProvider</c> L1074 and widened to
    /// <c>nvarchar(2000)</c> by <c>04.03.05.SqlDataProvider</c> L17, so 2000 is the terminal width the
    /// validator uses: a limit of 100 would refuse expressions an upgraded database already holds.
    /// </remarks>
    // MIGRATION: this value is itself a rule to be applied to profile input, not a rule applied to this
    // field. It is stored verbatim and is never compiled or executed by this layer.
    public string? ValidationExpression { get; set; }

    /// <summary>
    /// Replacement position of the property within its portal's profile.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@ViewOrder int</c>, column <c>ViewOrder int NOT NULL</c>. The
    /// legacy class marks it required through <c>&lt;Required(True), SortOrder(8)&gt;</c> at
    /// <c>ProfilePropertyDefinition.vb</c> L300.
    /// </remarks>
    // MIGRATION: -1 IS AN INSTRUCTION RATHER THAN AN ABSENCE MARKER on the sibling creation path,
    // where the terminal insert procedure branches on "IF @vieworder=-1" and substitutes the current
    // maximum order plus one (04.06.00.SqlDataProvider L1122-L1126). The terminal UPDATE procedure has no
    // such branch, so on this path the value is stored as submitted. No lower bound may be placed on the
    // member on either path: refusing -1 would refuse a value one verb interprets and the other stores.
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
