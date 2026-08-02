namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Transport contract for one user's profile, expressed as a definition-keyed set of values
/// rather than as a fixed list of named fields. Serves the profile sub-resource of the users
/// endpoint as both the read response and the replace request body.
/// </summary>
/// <remarks>
/// <para>
/// WHY THERE ARE NO NAMED FIELDS: the nineteen public properties on the legacy
/// <c>DotNetNuke.Entities.Users.UserProfile</c>
/// (<c>Library/Components/Users/Profile/UserProfile.vb</c>) are not columns. Fifteen of them
/// (L103-L461) are one-line accessors of the form <c>Return GetPropertyValue(cSomeName)</c>,
/// and <c>GetPropertyValue</c> (L507-L517) resolves that name against a collection of portal
/// property definitions - so the accessor names are seed data, not schema. Two measurements
/// settle it: the seeded vocabulary is eighteen name constants (L46-L71), so three seeded
/// names have no accessor at all; and an administrator may add, rename or remove a profile
/// property at any time, at which point a fixed-field contract is simply wrong. The legacy
/// screen agrees - <c>Website/admin/Users/Profile.ascx</c> declares a title row, one dynamic
/// property editor and a save button, with no per-field control in the markup.
/// </para>
/// <para>
/// This type is inert: no behaviour, no validation and no persistence concern. Rules that
/// mirror the legacy screens belong to a request validator under
/// <c>Application/Validation/</c>, and projection to and from the domain entity belongs to a
/// hand-written user mapper under <c>Application/Mapping/</c>.
/// </para>
/// </remarks>
public sealed class UserProfileDto
{
    // DELIBERATELY ABSENT, so a later reader does not restore them believing they were
    // overlooked: the computed full-name concatenation (UserProfile.vb:L203-L207), because a
    // data carrier computes nothing and both of its inputs are definition-keyed values an
    // administrator may remove; the change-tracking flag (L237-L241) and the hydration flag
    // (L271-L278), both of which the object-relational materialiser makes redundant; and the
    // pre-generics collection wrapper the legacy collection member was typed as (L329-L336),
    // whose purpose the Properties member below serves through a read-only generic interface.

    /// <summary>
    /// Identifier of the user whose profile this is, from <c>UserProfile.UserID</c>
    /// (<c>int NOT NULL</c>, foreign key to <c>Users(UserID)</c> with cascade delete, so a
    /// profile value cannot outlive the user it describes).
    /// </summary>
    /// <remarks>
    /// <c>Users.UserID</c> is declared <c>IDENTITY(1,1)</c>, so 1 is the lowest real identifier
    /// and no legitimate value collides with the legacy -1 marker. That holds for this column
    /// only and must not be generalised: portal, role and tab identifiers in this schema seed
    /// from -1 or 0 and treat both as genuine addressable values. Absence is expressed as a
    /// null where it must be expressed, never inferred from the sign of an identifier.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// The user's profile values, one entry per profile property, each carrying the definition
    /// that describes it. Never <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordered rather than keyed, deliberately: a dictionary keyed by definition would suit
    /// lookup but would discard the display order the embedded definition's view-order member
    /// establishes, and the dynamic editor this collection feeds groups properties into
    /// sections and presents them in that order. A consumer wanting keyed access can build it
    /// from the definition identifier on each entry.
    /// </para>
    /// <para>
    /// A missing entry and an entry holding an empty value are not the same thing. The legacy
    /// collection is seeded from the portal's property definitions before any stored value is
    /// applied, so a freshly initialised profile holds an entry for every defined property. A
    /// consumer must therefore not read the absence of an entry as an empty value: it means the
    /// property was not part of the set that was projected.
    /// </para>
    /// </remarks>
    public IReadOnlyList<UserProfileValueDto> Properties { get; set; }
        = Array.Empty<UserProfileValueDto>();

    // DELIBERATELY ABSENT: no portal identifier, because the UserProfile table has no PortalID
    // column - values are keyed to a user and a definition, and the portal scope lives on the
    // definition, so publishing it here would invite two copies to disagree. No aggregate row
    // count either: this is one user's complete profile rather than a page of a larger set, and
    // the legacy read procedure likewise returns the whole set in one go.
}

/// <summary>
/// Transport contract for a single profile value: one user's answer to one profile property,
/// together with the definition that describes the property being answered.
/// </summary>
/// <remarks>
/// <para>
/// Co-located with <see cref="UserProfileDto"/> rather than given a file of its own, because it
/// has no meaning apart from the contract that carries it and is never served on its own. Maps
/// to one row of the <c>UserProfile</c> table (created at 03.02.03.SqlDataProvider L1364 in the
/// templated naming form), whose terminal shape is:
/// </para>
/// <code>
/// ProfileID             int IDENTITY(1,1) NOT NULL   -- primary key, non-clustered
/// UserID                int NOT NULL                 -- FK to Users,  ON DELETE CASCADE
/// PropertyDefinitionID  int NOT NULL                 -- FK to definitions, ON DELETE CASCADE
/// PropertyValue         nvarchar(3750) NULL
/// PropertyText          ntext NULL
/// Visibility            int NOT NULL DEFAULT 0
/// LastUpdatedDate       datetime NOT NULL
/// </code>
/// <para>
/// Both foreign keys cascade on delete, which is why neither identifier can dangle and why the
/// embedded definition below is non-nullable.
/// </para>
/// </remarks>
public sealed class UserProfileValueDto
{
    /// <summary>
    /// Identifier of the profile property this value answers, from
    /// <c>PropertyDefinitionID int NOT NULL</c> (foreign key to the profile-definition table
    /// with cascade delete).
    /// </summary>
    /// <remarks>
    /// Published even though the definition is embedded whole, because together with the user
    /// identifier on the enclosing contract it forms the natural key of a profile value: it is
    /// what a caller submits when replacing a value, and it stays meaningful in a request
    /// payload where the embedded definition is redundant.
    /// </remarks>
    public int PropertyDefinitionId { get; set; }

    /// <summary>
    /// The value the user has stored for this property, or the empty string when no value has
    /// been stored. Never <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DO NOT CAP THIS MEMBER AT 3,750 CHARACTERS. That figure is the width of the narrower of
    /// the two storage columns, whereas the legacy write procedure declares its value parameter
    /// as an unbounded text type, so the write path accepted values of any length. A validation
    /// rule capping input at the storage threshold would reject input the legacy application
    /// accepted. Any length rule belongs to the validation layer and must come from the
    /// property's own definition, whose length member is portal-configurable data.
    /// </para>
    /// <para>
    /// No serialisation option that omits empty or default values may be applied to this
    /// contract: it would erase the difference between a property that is present and empty and
    /// one that was never projected, and it would erase a legitimate visibility of zero on the
    /// member below.
    /// </para>
    /// </remarks>
    // MIGRATION: two storage columns, one logical value - and the legacy READ contract already
    // published one. The schema splits a value across PropertyValue (nvarchar(3750)) and
    // PropertyText (ntext); they are mutually exclusive, and the write procedure chooses
    // between them by DATALENGTH(@PropertyValue) > 7500, which is 3,750 UTF-16 characters -
    // exactly the ceiling of the narrower column. The read procedure coalesces them back into
    // one projected column that it names PropertyValue, and the narrower column never appears
    // in the read projection at all. Publishing two members here would therefore be a
    // divergence FROM the legacy behaviour, and would leak a storage-tier decision that
    // belongs to the repository.
    //
    // MIGRATION: the empty-string sentinel is preserved rather than translated to null, because
    // it is externally observable in a legacy rule. Null.vb:L70-L74 defines the null string as
    // "", and ProfileController.vb branches on it to decide whether a required property has
    // been filled in - "If propertyDefinition.Required And propertyDefinition.PropertyValue =
    // Null.NullString". The legacy value getter likewise initialises its result to that
    // sentinel, so an unresolved property yields "" and never a null. Translating it here would
    // silently change the outcome of the required-property check the validation layer must
    // reproduce.
    public string PropertyValue { get; set; } = string.Empty;

    /// <summary>
    /// Audience permitted to see this stored value: 0 for all users, 1 for authenticated
    /// members only, 2 for administrators only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From <c>Visibility int NOT NULL DEFAULT 0</c> - the per-user, per-property choice the
    /// user themselves controls. NOT the same member as the same-named one on the embedded
    /// definition, and the two differ in meaning and in default: the definition's member is a
    /// portal-level default hint, is not a column on the definition table at all, and falls
    /// back to administrators-only (2). Reading the wrong one yields a plausible value with the
    /// wrong meaning, so consult the definition's member only when pre-selecting for a property
    /// that has no stored value yet.
    /// </para>
    /// <para>
    /// Zero is a meaningful value rather than an unset one, so this member must never be
    /// omitted from a payload on the grounds of holding its default. It is advisory to a client
    /// and never the enforcement point: whether a value may be disclosed is decided server-side
    /// before the value is projected onto this contract.
    /// </para>
    /// </remarks>
    // MIGRATION: carried as an int because the domain layer declares no enumeration for it. The
    // legacy concept is the three-member UserVisibilityMode (AllUsers = 0, MembersOnly = 1,
    // AdminOnly = 2) at Library/Components/Users/UserVisibilityMode.vb; declaring a counterpart
    // in this layer would fork a single vocabulary across two layers, so the three meanings are
    // documented above rather than typed.
    public int Visibility { get; set; }

    /// <summary>
    /// When this value was last written, or <see langword="null"/> when it has never been
    /// written.
    /// </summary>
    /// <remarks>
    /// A bare timestamp with no time-zone offset, because the legacy
    /// <c>LastUpdatedDate datetime NOT NULL</c> column was written from the application
    /// server's local clock; attaching an offset here would assert a precision the stored data
    /// does not carry.
    /// </remarks>
    // MIGRATION: nullable here despite the column being NOT NULL, because the legacy null
    // contract does not use SQL nulls for dates - Null.vb defines its null date as
    // DateTime.MinValue, so a row that was never really stamped carries 0001-01-01. Publishing
    // that verbatim would give a client a value that looks like a real timestamp and sorts
    // before every genuine one. The sentinel is translated to null on the way out and back on
    // the way in, and that translation belongs to the mapper: no code on this side may compare
    // this member against the minimum date. Note the deliberate asymmetry with the value member
    // above, which keeps its empty-string sentinel - that sentinel is observable in a legacy
    // rule, whereas the minimum date is observable only as a rendering artefact.
    public DateTime? LastUpdatedDate { get; set; }

    /// <summary>
    /// The definition of the property this value answers: its name, category, data type,
    /// length, whether it is required, its validation expression and its display order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Embedded rather than referenced by identifier alone, so a dynamic profile screen can
    /// render a label, choose an editor, apply a validation rule and place the field in order
    /// from a single response instead of a second call plus a client-side join.
    /// </para>
    /// <para>
    /// Non-nullable, and defaulted to an empty instance. That is schema-faithful rather than
    /// merely convenient: the definition foreign key is <c>NOT NULL</c> with cascade delete, so
    /// a stored profile value cannot exist without its definition. Read-only from this
    /// contract's point of view - definitions are portal-scoped metadata administered through
    /// their own endpoint, so a value submitted here does not update the definition it carries,
    /// and the definition identifier above is what a write keys on. The required flag and
    /// validation expression carried here are data rather than constraints on this contract,
    /// which is what lets a portal change a rule without a redeployment.
    /// </para>
    /// </remarks>
    // MIGRATION: the legacy definition class carried a per-user value slot of its own, which
    // conflated a portal-scoped definition with one user's answer to it. That slot is relocated
    // to the value member above, so expect no value member on the embedded definition.
    public ProfilePropertyDefinitionDto Definition { get; set; } = new();
}
