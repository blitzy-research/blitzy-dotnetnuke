namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Transport contract for one user's profile, expressed as a definition-keyed set of values rather than as
/// a fixed list of named fields. Serves the profile sub-resource of the users endpoint as both the read
/// response and the replace request body.
/// </summary>
public sealed class UserProfileDto
{
    // DELIBERATELY ABSENT, so a later reader does not restore them believing they were overlooked: the
    // computed full-name concatenation, because a data carrier computes nothing and both of its inputs are
    // definition-keyed values an administrator may remove; the change-tracking and hydration flags, which
    // the object-relational materialiser makes redundant; and the pre-generics collection wrapper, whose
    // purpose the Properties member below serves through a read-only generic interface.

    /// <summary>
    /// Identifier of the user whose profile this is, from <c>UserProfile.UserID</c> (<c>int NOT NULL</c>,
    /// foreign key to <c>Users(UserID)</c> with cascade delete, so a profile value cannot outlive the user
    /// it describes).
    /// </summary>
    /// <remarks>
    /// <c>Users.UserID</c> is declared <c>IDENTITY(1,1)</c>, so 1 is the lowest real identifier and no
    /// legitimate value collides with the legacy -1 marker. That holds for this column only and must not be
    /// generalised: portal, role and tab identifiers in this schema seed from -1 or 0 and treat both as
    /// genuine addressable values.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// The user's profile values, one entry per profile property, each carrying the definition that
    /// describes it. Never <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Ordered rather than keyed, deliberately: a dictionary keyed by definition would suit lookup but
    /// would discard the display order the embedded definition's view-order member establishes, and the
    /// dynamic editor this collection feeds groups properties into sections and presents them in that
    /// order. A consumer wanting keyed access can build it from the definition identifier on each entry.
    /// </remarks>
    public IReadOnlyList<UserProfileValueDto> Properties { get; set; }
        = Array.Empty<UserProfileValueDto>();

    /// <summary>
    /// Whether the tenant lets an account holder choose who may see each of their own profile values, from
    /// the <c>Profile_DisplayVisibility</c> tenant setting. Defaults to <see langword="true"/> when the
    /// tenant has stored nothing, which is the legacy default.
    /// </summary>
    /// <remarks>
    /// ⚠ THE SECOND HALF OF THE LEGACY RULE IS NOT ENCODED HERE AND MUST NOT BE. This member reports the
    /// TENANT'S POLICY only.
    /// </remarks>
    public bool DisplayVisibilityEnabled { get; set; } = true;

    // DELIBERATELY ABSENT: no portal identifier, because the UserProfile table has no PortalID column -
    // values are keyed to a user and a definition, and the portal scope lives on the definition, so
    // publishing it here would invite two copies to disagree.
}

/// <summary>
/// Transport contract for a single profile value: one user's answer to one profile property, together with
/// the definition that describes the property being answered.
/// </summary>
/// <remarks>
/// Co-located with <see cref="UserProfileDto"/> rather than given a file of its own, because it has no
/// meaning apart from the contract that carries it and is never served alone. Maps to one row of the
/// <c>UserProfile</c> table (created at 03.02.03.SqlDataProvider L1364 in the templated naming form), whose
/// terminal shape the members below reproduce.
/// </remarks>
public sealed class UserProfileValueDto
{
    /// <summary>
    /// Identifier of the profile property this value answers, from <c>PropertyDefinitionID int NOT NULL</c>
    /// (foreign key to the profile-definition table with cascade delete).
    /// </summary>
    public int PropertyDefinitionId { get; set; }

    /// <summary>
    /// The value the user has stored for this property, or the empty string when no value has been stored.
    /// Never <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// DO NOT CAP THIS MEMBER AT 3,750 CHARACTERS. That figure is the width of the narrower of the two
    /// storage columns, whereas the legacy write procedure declares its value parameter as an unbounded
    /// text type, so the write path accepted values of any length and a rule capping input at the storage
    /// threshold would reject input the legacy application accepted.
    /// </remarks>
    // Two storage columns, one logical value - and the legacy READ contract already published one.
    public string PropertyValue { get; set; } = string.Empty;

    /// <summary>
    /// Audience permitted to see this stored value: 0 for all users, 1 for authenticated members only, 2
    /// for administrators only.
    /// </summary>
    /// <remarks>
    /// From <c>Visibility int NOT NULL DEFAULT 0</c> - the per-user, per-property choice the user
    /// themselves controls. NOT the same member as the same-named one on the embedded definition, and the
    /// two differ in meaning and in default: the definition's member is a portal-level default hint, is not
    /// a column on the definition table at all, and falls back to administrators-only (2).
    /// </remarks>
    public int Visibility { get; set; }

    /// <summary>When this value was last written, or <see langword="null"/> when it has never been written.</summary>
    /// <remarks>
    /// A bare timestamp with no time-zone offset, because the legacy <c>LastUpdatedDate datetime NOT
    /// NULL</c> column was written from the application server's local clock; attaching an offset here
    /// would assert a precision the stored data does not carry.
    /// </remarks>
    public DateTime? LastUpdatedDate { get; set; }

    /// <summary>
    /// The definition of the property this value answers: its name, category, data type, length, whether it
    /// is required, its validation expression and its display order.
    /// </summary>
    /// <remarks>
    /// Embedded rather than referenced by identifier alone, so a dynamic profile screen can render a label,
    /// choose an editor, apply a validation rule and place the field in order from a single response
    /// instead of a second call plus a client-side join.
    /// </remarks>
    public ProfilePropertyDefinitionDto Definition { get; set; } = new();
}
