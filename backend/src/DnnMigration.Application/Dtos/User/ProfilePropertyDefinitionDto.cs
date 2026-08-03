namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Transport contract for a single profile-property definition: the portal-scoped metadata
/// that declares which fields a user profile is composed of, how each field is typed and
/// constrained, and in what order it is presented.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the VB.NET class <c>DotNetNuke.Entities.Profile.ProfilePropertyDefinition</c>
/// in <c>Library/Components/Users/Profile/ProfilePropertyDefinition.vb</c>, which declares
/// fifteen public properties. Thirteen of them survive onto this contract; the two that do
/// not are listed in the divergence register below.
/// </para>
/// <para>
/// Backed by the <c>ProfilePropertyDefinition</c> table, whose shape is the cumulative result
/// of the upgrade-script chain rather than of any single script: the table is created in
/// <c>03.02.03.SqlDataProvider</c> and three of its columns are altered afterwards. Every
/// member below therefore records the column it maps to together with the type that column
/// finally terminates at, so no reader has to trust the creating script on its own.
/// </para>
/// <para>
/// <b>This is a RESPONSE contract and nothing binds it.</b> It is returned by every read on
/// <c>/api/v1/profile-definitions</c> and its portal-nested equivalent, returned as the body of a
/// successful create or update, and embedded by the user-profile contract so that a rendered
/// profile can carry the metadata describing its own fields. It is NOT the payload of any verb:
/// <c>POST</c> binds <see cref="CreateProfilePropertyDefinitionRequest"/> and <c>PUT</c> binds
/// <see cref="UpdateProfilePropertyDefinitionRequest"/>, each carrying only the members the
/// terminal procedure behind that verb actually writes.
/// </para>
/// <para>
/// MIGRATION: both verbs previously bound THIS type, which made the boundary advertise members
/// neither procedure honours. A caller could submit <c>PropertyDefinitionId</c>, <c>PortalId</c> or
/// <c>Visibility</c> and receive a success response in which none of the three had been read - the
/// first two because they arrive from the route and the third because it is not a column on this
/// table. Splitting the write contracts removed those members from the request surface rather than
/// leaving them present and ignored, so the schema a caller reads is now the schema the store
/// honours. The three read-only members below remain on this response for the reason they always
/// existed: a caller needs the assigned key, the owning portal and the resolved default visibility
/// in order to address the definition afterwards.
/// </para>
/// <para>
/// This type is inert: it holds no behaviour, no validation and no persistence concern, and NO
/// FluentValidation validator exists for it, because a response is not something a caller submits.
/// The rules that mirror the legacy screens are declared over the two request contracts by
/// <c>CreateProfilePropertyDefinitionRequestValidator</c> and
/// <c>UpdateProfilePropertyDefinitionRequestValidator</c>, both reading the shared constants on
/// <c>Validation/ProfileDefinitionTermsRules</c>. Projection from the domain entity, and from each
/// request onto it, lives in <c>DnnMigration.Application.Mapping.UserMappings</c>.
/// </para>
/// <para>
/// Divergences from the legacy shape, each annotated inline at the point it applies and each
/// reported for consolidation into the repository migration notes:
/// </para>
/// <list type="bullet">
///   <item><description>
///   The legacy read-only dirty-state flag is not carried across; it is change-tracking state
///   that existed only to serve the reflection-based row hydrator, and both are superseded by
///   the persistence layer's own change tracking.
///   </description></item>
///   <item><description>
///   <c>PropertyValue</c> is not carried across; it is relocated to the user-profile contract,
///   which is where a per-user value belongs.
///   </description></item>
///   <item><description>
///   The table's soft-delete column is not carried across; it is a repository query predicate
///   rather than a wire field.
///   </description></item>
///   <item><description>
///   <c>Visibility</c> is expressed as an <c>int</c> rather than an enumeration, and it is not
///   a column on this table at all.
///   </description></item>
///   <item><description>
///   <c>DataType</c> stays an <c>int</c> foreign key into a lookup subsystem that lies outside
///   the scope of this migration.
///   </description></item>
///   <item><description>
///   Sentinel translation happens here, at the boundary, rather than by weakening the domain
///   model. Three members attach a different and easily confused meaning to the value -1:
///   <c>ModuleDefId</c>, <c>PortalId</c> and <c>ViewOrder</c>.
///   </description></item>
/// </list>
/// </remarks>
public sealed class ProfilePropertyDefinitionDto
{
    // MIGRATION: three members of the legacy shape are deliberately absent from this contract.
    //
    // The legacy read-only change-tracking flag, and the method that reset it, existed only so
    // that the reflection-based row hydrator could distinguish a freshly loaded object from a
    // modified one. That hydrator produces no target file and the persistence layer tracks
    // changes itself, so the flag has nothing left to report and is deleted rather than
    // translated.
    //
    // PropertyValue is relocated rather than dropped. On the legacy definition class it is a
    // scratch slot holding one user's value while a profile is being bound, which conflates a
    // definition with the values keyed to it. The per-user value is carried by the
    // user-profile contract in this same folder, alongside the key-value row set it comes
    // from.
    //
    // The table's Deleted column is a soft-delete predicate that the repository applies when
    // it decides which rows form the live set; the terminal upsert procedure writes a literal
    // zero into it and never reads it back. Publishing it would invite a caller to filter on
    // something the API has already filtered, so it stays behind the repository boundary.
    //
    // All three belong in the repository migration notes.

    /// <summary>
    /// Surrogate key of the definition.
    /// </summary>
    /// <remarks>
    /// Maps to <c>PropertyDefinitionID int IDENTITY(1,1) NOT NULL</c>, the clustered primary
    /// key. Because the identity seed is 1, this column is the one identifier on this contract
    /// that cannot collide with the legacy -1 marker, so a value of zero or below is safe to
    /// read as "not yet persisted" here, and only here. The legacy class initialised its
    /// backing field to -1 for precisely that purpose.
    /// </remarks>
    public int PropertyDefinitionId { get; set; }

    /// <summary>
    /// Identifier of the portal that owns this definition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Maps to <c>PortalID</c>, constrained by a foreign key to <c>Portals(PortalID)</c> with
    /// cascade delete, and part of the unique index over portal, module definition and
    /// property name.
    /// </para>
    /// <para>
    /// Never read -1, 0, or any non-positive value as "no portal". <c>Portals.PortalID</c> is
    /// declared <c>IDENTITY(-1, 1)</c>, so -1 is a genuine addressable portal and 0 is the
    /// first portal an ordinary installation creates. In this table -1 additionally denotes
    /// the host-level definition shared by every portal, which makes it doubly meaningful.
    /// Absence, wherever it has to be expressed, is expressed as a null and is never inferred
    /// from the sign or the magnitude of the value.
    /// </para>
    /// </remarks>
    // MIGRATION: the column starts life as PortalID int NOT NULL, in which -1 denoted the
    // host-level definition. 03.03.03.SqlDataProvider widens it to PortalID int NULL and
    // migrates the existing rows with "SET PortalId = NULL WHERE PortalId = -1", so the
    // terminal schema encodes host-level ownership as a SQL null instead. This contract keeps
    // the non-nullable int and the legacy -1 encoding, because -1 is what the legacy class
    // published and what an existing consumer reads; converting an externally observable
    // sentinel silently into an absent value is exactly what the sentinel-preservation rule
    // forbids at this boundary. Translating between the two encodings is therefore a mapping
    // concern, and the repository predicate must keep matching the terminal
    // "PortalId IS NULL" form. Itemise in the repository migration notes.
    public int PortalId { get; set; }

    /// <summary>
    /// Identifier of the module definition that contributed this property, or <c>null</c> when
    /// the property is not owned by a module.
    /// </summary>
    /// <remarks>
    /// Maps to <c>ModuleDefID int NULL</c>, and part of the unique index over portal, module
    /// definition and property name. <c>ModuleDefinitions.ModuleDefID</c> is declared
    /// <c>IDENTITY(1, 1)</c>, so 1 is the lowest real identifier and no legitimate value can
    /// be confused with the legacy -1 marker. That is what makes a nullable int the honest
    /// shape for this member, where the same choice would be unsafe for <c>PortalId</c>.
    /// </remarks>
    // MIGRATION: the legacy class types this Integer and initialises it to the -1 null-integer
    // sentinel, so -1 meant "no module definition". The sentinel is translated to null when a
    // row is projected onto this contract, and back to -1 only where a legacy consumer
    // demands it. No code on this side may compare against -1 or read a non-positive value as
    // absent. Itemise in the repository migration notes.
    public int? ModuleDefId { get; set; }

    /// <summary>
    /// Identifier of the data type that governs how the property's value is edited, validated
    /// and displayed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Maps to <c>DataType int NOT NULL</c>. It is not an enumeration. It is a foreign key
    /// into the shared <c>Lists</c> lookup table, restricted to the entries whose list name is
    /// <c>DataType</c>; the legacy class makes that binding explicit by decorating the
    /// property with a list editor bound to the list named <c>DataType</c> by entry
    /// identifier. The administration screens resolve the integer through the list controller
    /// to obtain a display label, so the label is a presentation concern and is deliberately
    /// absent from this contract.
    /// </para>
    /// <para>
    /// One list entry is behaviourally significant rather than merely cosmetic: the entry
    /// whose value is <c>List</c> marks the property as list-backed, which the legacy edit
    /// screen special-cases to offer a nested list selection.
    /// </para>
    /// </remarks>
    // MIGRATION: the Lists subsystem lies outside the scope of this migration, so there is no
    // lookup service to resolve this key against and no enumeration may be invented to stand
    // in for one. The key travels as the opaque int the schema declares. Itemise in the
    // repository migration notes so that the eventual lookup is planned rather than
    // rediscovered.
    public int DataType { get; set; }

    /// <summary>
    /// Value pre-populated into the property when a profile is first presented, or <c>null</c>
    /// when the property has no default.
    /// </summary>
    /// <remarks>
    /// Maps to <c>DefaultValue</c>, nullable. The column is created as <c>nvarchar(50)</c> and
    /// later widened to <c>ntext</c> by <c>04.05.00.SqlDataProvider</c>, so the terminal column
    /// is effectively unbounded and a maximum-length rule of 50 would reject values that an
    /// upgraded database already holds. Any length rule for this field belongs to the
    /// validation layer and must be taken from the terminal column rather than from the
    /// creating script.
    /// </remarks>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Grouping heading under which the property is presented; the "Category" column of the
    /// legacy definition grid.
    /// </summary>
    /// <remarks>
    /// Maps to <c>PropertyCategory nvarchar(50) NOT NULL</c>, and the legacy class marks it
    /// required. Initialised to <c>string.Empty</c> rather than left null, so the non-nullable
    /// annotation holds without a compiler suppression and so a caller that omits the field
    /// yields the same empty string the legacy null-string sentinel yielded rather than a null.
    /// The measured maximum length of 50 is recorded here as the reference for the validation
    /// layer and is deliberately not enforced by an attribute.
    /// </remarks>
    public string PropertyCategory { get; set; } = string.Empty;

    /// <summary>
    /// Stable, machine-readable name of the property; the "Name" column of the legacy
    /// definition grid, and the key by which a stored profile value is matched back to its
    /// definition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Maps to <c>PropertyName nvarchar(50) NOT NULL</c>, and part of the unique index over
    /// portal, module definition and property name. Initialised to <c>string.Empty</c> for the
    /// same reason as the category.
    /// </para>
    /// <para>
    /// The legacy class constrains the name with the regular expression
    /// <c>^[a-zA-Z0-9._%\-+']+$</c>, admitting letters, digits and the punctuation set dot,
    /// underscore, percent, hyphen, plus and apostrophe, and so excluding whitespace. That
    /// exclusion is load-bearing rather than stylistic: a later upgrade script repairs existing
    /// rows by replacing spaces with underscores. The expression is reproduced verbatim here as
    /// the reference for the validation layer, which is the only place it may be enforced.
    /// </para>
    /// <para>
    /// The legacy class marks the name read-only, but that attribute is a hint to the reflective
    /// property editor about whether to render the field as editable - it is NOT a statement that
    /// the store refuses a change. The terminal procedure <c>UpdatePropertyDefinition</c>
    /// (<c>04.05.00:L1685</c>) declares <c>@PropertyName</c> and assigns
    /// <c>PropertyName = @PropertyName</c>, so the stored name genuinely can change, and the
    /// update write contract carries the member for that reason. Whether a submitted name
    /// collides with another definition of the same portal and module is settled by the service
    /// against <c>IX_ProfilePropertyDefinition</c>, with the definition being edited excluded from
    /// the comparison so that an ordinary edit resubmitting the name it read is not a conflict.
    /// </para>
    /// </remarks>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of characters the property's value may hold, or the display width for
    /// the types that use one; zero when unconstrained.
    /// </summary>
    /// <remarks>
    /// Maps to <c>Length int NOT NULL</c>, which carries a database default of 0. Zero is a
    /// legitimate stored value meaning "no explicit length" and not an unset one, so this
    /// member must never be omitted from a serialised payload on the grounds of holding its
    /// default.
    /// </remarks>
    public int Length { get; set; }

    /// <summary>
    /// Whether a profile may not be saved while this property is empty.
    /// </summary>
    /// <remarks>
    /// Maps to <c>Required bit NOT NULL</c>, and rendered as an inline check box in the legacy
    /// definition grid. The flag describes a rule the validation layer applies to a profile
    /// submission; it is data on this contract and imposes nothing on the contract itself.
    /// </remarks>
    public bool Required { get; set; }

    /// <summary>
    /// Regular expression a submitted value has to satisfy, or <c>null</c> when the property is
    /// unconstrained beyond its data type.
    /// </summary>
    /// <remarks>
    /// Maps to <c>ValidationExpression</c>, nullable. The column is created as
    /// <c>nvarchar(100)</c> and later widened to <c>nvarchar(2000)</c> by
    /// <c>04.03.05.SqlDataProvider</c>, so 2000 is the length the validation layer has to use: a
    /// limit of 100 would reject expressions that an upgraded database already holds. Note that
    /// this value is itself a rule to be applied to profile input, not a rule applied to this
    /// field.
    /// </remarks>
    public string? ValidationExpression { get; set; }

    /// <summary>
    /// Position of the property within its portal's profile, driving both the display order and
    /// the reorder controls of the legacy definition grid.
    /// </summary>
    /// <remarks>
    /// Maps to <c>ViewOrder int NOT NULL</c>, and the legacy class marks it required.
    /// </remarks>
    // MIGRATION: -1 is not an absence marker on this member, it is an instruction. The terminal
    // upsert procedure branches on "IF @vieworder = -1" and substitutes the current maximum
    // order plus one, so -1 means "append to the end". The value is consequently carried
    // through unchanged and must never be normalised away, clamped to zero or rewritten as
    // null. Itemise in the repository migration notes.
    public int ViewOrder { get; set; }

    /// <summary>
    /// Whether the property is presented at all.
    /// </summary>
    /// <remarks>
    /// Maps to <c>Visible bit NOT NULL</c>, and rendered as an inline check box in the legacy
    /// definition grid. Distinct from <c>Visibility</c>, which answers a different question:
    /// not whether the field is shown, but to whom a stored value is disclosed.
    /// </remarks>
    public bool Visible { get; set; }

    /// <summary>
    /// Default audience permitted to see a stored value of this property: 0 for all users, 1
    /// for authenticated members only, 2 for administrators only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy class types this member as the three-member <c>UserVisibilityMode</c>
    /// enumeration declared in <c>Library/Components/Users/UserVisibilityMode.vb</c>, whose
    /// members are <c>AllUsers = 0</c>, <c>MembersOnly = 1</c> and <c>AdminOnly = 2</c>. It is
    /// expressed as a plain <c>int</c> here because the domain layer deliberately declares no
    /// counterpart, and declaring a competing one in this layer would split ownership of a
    /// single vocabulary across two layers.
    /// </para>
    /// <para>
    /// Zero is a meaningful value and not an unset one, so this member must never be omitted
    /// from a serialised payload on the grounds of holding its default.
    /// </para>
    /// </remarks>
    // MIGRATION: two separate divergences meet on this member.
    //
    // First, there is no Visibility column on the ProfilePropertyDefinition table. Verified
    // across all eighty-eight upgrade scripts, case-insensitively and across the bare,
    // owner-qualified, bracketed and templated naming forms: it appears neither in the creating
    // statement nor in any later column addition. The terminal upsert procedure does not write
    // it and the legacy definition grid does not render it. Its persisted home is
    // UserProfile.Visibility, declared "int NOT NULL DEFAULT 0": one value per user per
    // property, not one per definition. The definition-level member is a default hint. The
    // legacy collection loader assigns it after the reader has finished, from the "User
    // Accounts" module setting Profile_DefaultVisibility, which falls back to
    // administrators-only, that is 2. Note the asymmetry that makes this easy to get wrong. The
    // hint defaults to 2, while the stored per-user column defaults to 0.
    //
    // Second, the value travels as an int because no domain enumeration exists for it. The
    // domain layer declares nine enumerations and this concept is deliberately not among them,
    // so the three meanings are documented above instead of being typed.
    //
    // Both belong in the repository migration notes.
    public int Visibility { get; set; }
}
