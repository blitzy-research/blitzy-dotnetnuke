namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Transport contract for one user's profile, expressed as a definition-keyed set of values
/// rather than as a fixed list of named fields.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the VB.NET class <c>DotNetNuke.Entities.Users.UserProfile</c> in
/// <c>Library/Components/Users/Profile/UserProfile.vb</c>. That class declares nineteen public
/// properties, and this contract deliberately reproduces none of them by name. The reason is
/// the single most important thing to understand about this type, so it is set out in full
/// below rather than left to be rediscovered.
/// </para>
/// <para>
/// Fifteen of the nineteen are convenience accessors over name, address, telephone,
/// online-contact and preference fields, declared between <c>UserProfile.vb:L103</c> and
/// <c>UserProfile.vb:L461</c>. Not one of them is backed by a column. Every one of their
/// getters is a single call of the form <c>Return GetPropertyValue(cSomeName)</c>, and
/// <c>GetPropertyValue</c> at <c>UserProfile.vb:L507-L517</c> resolves that name against a
/// collection of property definitions. The accessor names are therefore *seed data*: they are
/// the property names an out-of-the-box installation happens to be provisioned with, surfaced
/// as typed shortcuts for the convenience of legacy callers.
/// </para>
/// <para>
/// Two measurements make it clear that promoting those names into a wire contract would be a
/// defect rather than a fidelity. First, the seeded vocabulary is larger than the accessor
/// set: <c>UserProfile.vb:L46-L71</c> declares eighteen property-name constants, so three
/// seeded names have no accessor at all and a contract built from the accessors would be an
/// incomplete snapshot even on day one. Second, an administrator may add, rename or remove a
/// profile property at any time through the profile-definition screens, at which point a
/// contract with fixed fields is simply wrong. The values are consequently carried as a
/// collection keyed by property definition, which is what the schema, the legacy screen and
/// the target screen all already do.
/// </para>
/// <para>
/// The legacy screen corroborates this directly. <c>Website/admin/Users/Profile.ascx</c>
/// declares a title row, a single dynamic profile-property editor and a save button, and
/// nothing else: there is not one per-field control in the markup. Its code-behind binds that
/// editor to the definition-keyed collection and never touches an accessor. The target screen
/// is described the same way in the migration plan, which specifies dynamic profile
/// properties rendered from the profile-definition endpoint.
/// </para>
/// <para>
/// Serves the profile sub-resource of the users endpoint, as both the response body of the
/// read operation and the request body of the replace operation. This type is inert: it holds
/// no behaviour, no validation and no persistence concern. Rules that mirror the legacy
/// screens live in <c>DnnMigration.Application.Validation</c>, and projection to and from the
/// domain entity lives in <c>DnnMigration.Application.Mapping.UserMappings</c>.
/// </para>
/// <para>
/// Divergences from the legacy shape, each annotated inline at the point it applies and each
/// reported for consolidation into the repository migration notes:
/// </para>
/// <list type="bullet">
///   <item><description>
///   The fifteen convenience accessors are not reproduced; the values they stood for arrive
///   as the definition-keyed collection on this contract.
///   </description></item>
///   <item><description>
///   The two storage columns behind a profile value are presented as one logical value,
///   because the legacy read path already coalesces them.
///   </description></item>
///   <item><description>
///   Four legacy members are dropped outright: the computed full-name concatenation, the
///   change-tracking flag, the hydration flag, and the pre-generics collection wrapper that
///   the legacy collection member was typed as.
///   </description></item>
///   <item><description>
///   Per-value visibility travels as an <c>int</c> because the domain layer declares no
///   enumeration for it.
///   </description></item>
///   <item><description>
///   Sentinel translation happens here, at the boundary, rather than by weakening the domain
///   model. The empty string and the minimum date are both meaningful legacy values.
///   </description></item>
/// </list>
/// </remarks>
public sealed class UserProfileDto
{
    // MIGRATION: four members of the legacy shape are deliberately absent from this contract,
    // recorded here so that a future reader does not "restore" them believing they were
    // overlooked.
    //
    // The read-only full-name member at UserProfile.vb:L203-L207 concatenated the first and
    // last name accessors on every read. It is dropped for three independent reasons: it is a
    // computed getter, which this layer does not carry; both of its inputs are definition-keyed
    // values that an administrator may remove, so it could silently degrade to a lone space;
    // and the legacy equivalent on the user class is itself marked obsolete in favour of the
    // display name that the user contracts in this folder already publish. Composing a display
    // name is a service concern, and the legacy display-name format is a token-substitution
    // template, not a concatenation.
    //
    // The read-only change-tracking member at UserProfile.vb:L237-L241, and the method that
    // reset it, reported whether the object had been modified since it was loaded. The
    // persistence layer tracks changes itself, so the flag has nothing left to report.
    //
    // The hydration member at UserProfile.vb:L271-L278 recorded whether the object had been
    // populated yet, and every one of the fifteen accessors set it as a side effect of its
    // setter. It existed only to serve the reflection-based row hydrator, which produces no
    // target file at all.
    //
    // The legacy collection member at UserProfile.vb:L329-L336 is dropped as declared, not in
    // purpose. It was a lazy getter returning a pre-generics collection wrapper, and that
    // wrapper is one of the legacy types the migration plan retires without a replacement
    // file. Its purpose is served by the Properties member below, typed with a read-only
    // generic interface.
    //
    // All four belong in the repository migration notes.

    /// <summary>
    /// Identifier of the user whose profile this is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Maps to <c>UserID int NOT NULL</c> on the <c>UserProfile</c> table, constrained by a
    /// foreign key to <c>Users(UserID)</c> with cascade delete, so a profile value cannot
    /// outlive the user it describes.
    /// </para>
    /// <para>
    /// <c>Users.UserID</c> is declared <c>IDENTITY(1,1)</c>, so 1 is the lowest real identifier
    /// and no legitimate value collides with the legacy -1 marker. That is a property of this
    /// column specifically and must not be generalised: sibling tables in this schema seed
    /// their identities differently, and portal and role identifiers in particular treat -1 and
    /// 0 as genuine addressable values. Absence is expressed as a null wherever it has to be
    /// expressed, and is never inferred from the sign or the magnitude of an identifier.
    /// </para>
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// The user's profile values, one entry per profile property, each carrying the definition
    /// that describes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each entry corresponds to one row of the <c>UserProfile</c> table for this user. The
    /// legacy read procedure returns exactly this shape: a flat row set selected by user
    /// identifier, with no fixed field list anywhere in it.
    /// </para>
    /// <para>
    /// Ordered rather than keyed, and deliberately so. A dictionary keyed by property
    /// definition would be a natural fit for lookup, but it would discard the display order
    /// that the embedded definition's view-order member establishes, and the dynamic editor
    /// this collection feeds is order-sensitive: the legacy editor groups properties into
    /// sections and presents them in view order. Preserving a meaningful sequence therefore
    /// takes priority over lookup convenience, and a consumer that wants keyed access can
    /// build it from the definition identifier on each entry.
    /// </para>
    /// <para>
    /// Typed as a read-only interface so that the collection is immutable to its recipient. The
    /// property still exposes a setter, because this contract has to round-trip through a JSON
    /// deserialiser, and it defaults to an empty sequence rather than to
    /// <see langword="null"/>: a user with no stored profile values is an ordinary state, the
    /// legacy representation of it was an empty collection, and no consumer should have to
    /// null-check before iterating.
    /// </para>
    /// <para>
    /// A missing entry and an entry holding an empty value are not the same thing, and the
    /// distinction is load-bearing. The legacy collection is seeded from the portal's property
    /// definitions before any stored value is applied, so a freshly initialised profile
    /// contains an entry for every defined property, each holding its default. A consumer must
    /// therefore not treat the absence of an entry as equivalent to an empty value; it means
    /// the property was not part of the set that was projected.
    /// </para>
    /// </remarks>
    public IReadOnlyList<UserProfileValueDto> Properties { get; set; }
        = Array.Empty<UserProfileValueDto>();

    // MIGRATION: two members a reader may expect to find here are absent because the table
    // does not have them.
    //
    // There is no portal identifier on this contract. The UserProfile table has no PortalID
    // column: profile values are keyed to a user and a property definition, and the portal
    // scope lives on the definition, which carries its own portal identifier. Publishing a
    // portal identifier here would duplicate that scope and invite the two copies to disagree.
    //
    // There is no aggregate row count either. This is one user's complete profile, not a page
    // of a larger set, so the paged envelope that the list-shaped contracts use does not
    // apply. The legacy read procedure likewise returns the whole set in one go.
    //
    // Both belong in the repository migration notes.
}

/// <summary>
/// Transport contract for a single profile value: one user's answer to one profile property,
/// together with the definition that describes the property being answered.
/// </summary>
/// <remarks>
/// <para>
/// Co-located with <see cref="UserProfileDto"/> rather than given a file of its own, because
/// it has no meaning apart from the contract that carries it and is never served on its own.
/// </para>
/// <para>
/// Maps to one row of the <c>UserProfile</c> table, created in
/// <c>Website/Providers/DataProviders/SqlDataProvider/03.02.03.SqlDataProvider</c> at line
/// 1364 in the templated naming form. The terminal shape of that table is:
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
/// Both foreign keys cascade on delete, which is why neither the user identifier nor the
/// definition identifier can ever dangle, and why the embedded definition below is
/// non-nullable.
/// </para>
/// </remarks>
public sealed class UserProfileValueDto
{
    /// <summary>
    /// Identifier of the profile property this value answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Maps to <c>PropertyDefinitionID int NOT NULL</c>, constrained by a foreign key to the
    /// profile-definition table with cascade delete. Together with the user identifier on the
    /// enclosing contract it forms the natural key of a profile value, which is why it is
    /// published here even though the definition is embedded whole: it is the stable key a
    /// caller submits when replacing a value, and it stays meaningful in a request payload
    /// where the embedded definition is redundant.
    /// </para>
    /// <para>
    /// The referenced identity is declared <c>IDENTITY(1,1)</c>, so 1 is the lowest real value
    /// and no legitimate definition collides with the legacy -1 marker.
    /// </para>
    /// </remarks>
    public int PropertyDefinitionId { get; set; }

    /// <summary>
    /// The value the user has stored for this property, or the empty string when no value has
    /// been stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One logical value, deliberately, even though two columns stand behind it. See the
    /// migration note below for the measured behaviour that makes a single member the faithful
    /// choice rather than a lossy one.
    /// </para>
    /// <para>
    /// Never <see langword="null"/>. The legacy contract for an unset profile value is the
    /// empty string, and this member preserves that exactly. Consequently no serialisation
    /// option that omits empty or default values may be applied to this contract: doing so
    /// would erase the difference between a property that is present and empty and one that
    /// was never projected, and it would also erase a legitimate visibility of zero on the
    /// member below.
    /// </para>
    /// <para>
    /// Do not derive a maximum length from the narrower of the two storage columns. The legacy
    /// write procedure declares its value parameter as an unbounded text type, so the write
    /// path accepts values of any length and the 3,750-character figure is a storage threshold
    /// rather than an input limit. A validation rule capping this member at 3,750 characters
    /// would reject input the legacy application accepted, which is a regression. Any length
    /// rule belongs to the validation layer and must be taken from the property's own
    /// definition, whose length member is portal-configurable data.
    /// </para>
    /// </remarks>
    // MIGRATION: the two storage columns are presented as one logical value.
    //
    // The legacy schema splits a profile value across PropertyValue, which is
    // nvarchar(3750), and PropertyText, which is ntext. The split is a storage optimisation
    // and the two are mutually exclusive: exactly one of them holds the data and the other is
    // null. The write procedure sets both in the same statement, in its update branch and
    // again in its insert branch,
    //
    //     PropertyValue = case when (DATALENGTH(@PropertyValue) > 7500) then NULL
    //                          else @PropertyValue end,
    //     PropertyText  = case when (DATALENGTH(@PropertyValue) > 7500) then @PropertyValue
    //                          else NULL end
    //
    // where 7,500 bytes is 3,750 UTF-16 characters, exactly the ceiling of the narrower
    // column. The read procedure then coalesces them back into one projected column that it
    // names PropertyValue,
    //
    //     'PropertyValue' = case when (PropertyValue Is Null) then PropertyText
    //                            else PropertyValue end
    //
    // and the search predicate matches either column against the same term. The narrower
    // column never appears in the read projection at all.
    //
    // So the legacy read contract already published exactly one value, and publishing two
    // here would be a divergence FROM the legacy behaviour rather than fidelity to it, while
    // also leaking a storage-tier decision that belongs to the repository. Choosing which
    // column to write is the repository's concern; this contract carries the value.
    //
    // Second, and separately: this member is a non-nullable string defaulting to the empty
    // string, not a nullable one. That is a deliberate reading of the legacy null contract
    // rather than a convenience. The legacy sentinel table at
    // Library/Components/Shared/Null.vb:L70-L74 defines its null string as the empty string,
    // literally returning "", and the profile validator at
    // Library/Components/Users/Profile/ProfileController.vb compares a required property
    // against that sentinel to decide whether it has been filled in:
    //
    //     If propertyDefinition.Required And propertyDefinition.PropertyValue = Null.NullString
    //
    // The accessor path agrees: the legacy value getter initialises its result to the same
    // empty-string sentinel before attempting a lookup, so an unresolved property yields ""
    // and never a null. An unset profile value therefore *is* the empty string in the legacy
    // contract, and translating it to null at this boundary would silently change the outcome
    // of the required-property check that the validation layer has to reproduce.
    //
    // Both belong in the repository migration notes.
    public string PropertyValue { get; set; } = string.Empty;

    /// <summary>
    /// Audience permitted to see this stored value: 0 for all users, 1 for authenticated
    /// members only, 2 for administrators only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Maps to <c>Visibility int NOT NULL DEFAULT 0</c>. This is the value the user themselves
    /// controls: the legacy profile editor renders a per-property visibility selector beside
    /// each field, gated by a portal setting that decides whether the selector is shown at all,
    /// and the read-only profile view honours the stored choice by hiding a property whose
    /// value is administrators-only from an ordinary viewer and a members-only property from an
    /// anonymous one.
    /// </para>
    /// <para>
    /// Not to be confused with the same-named member on the embedded definition, and the
    /// distinction matters because the two differ in both meaning and default. The member here
    /// is the stored, per-user, per-property choice and its column defaults to 0. The member on
    /// the definition is a portal-level default hint, is not a column on the definition table at
    /// all, and is populated from a module setting that falls back to administrators-only, that
    /// is 2. A caller reading the wrong one gets a plausible value with the wrong meaning, so
    /// read the definition's member only when deciding what to pre-select for a property that
    /// has no stored value yet.
    /// </para>
    /// <para>
    /// Zero is a meaningful value and not an unset one, so this member must never be omitted
    /// from a serialised payload on the grounds of holding its default.
    /// </para>
    /// <para>
    /// This member is advisory to a client and is never the enforcement point. Whether a value
    /// may be disclosed is decided server-side before the value is projected onto this
    /// contract.
    /// </para>
    /// </remarks>
    // MIGRATION: the value travels as an int because no domain enumeration exists for it.
    //
    // The legacy concept is a three-member enumeration declared at
    // Library/Components/Users/UserVisibilityMode.vb, whose members are AllUsers = 0,
    // MembersOnly = 1 and AdminOnly = 2. The domain layer declares nine enumerations and this
    // concept is deliberately not among them, so the three meanings are documented above
    // rather than typed. Declaring a counterpart in this layer instead would split ownership
    // of a single vocabulary across two layers and fork it from the domain, so the gap is
    // reported rather than filled locally. Two sibling contracts in this folder record the
    // same gap against the same enumeration.
    //
    // Belongs in the repository migration notes.
    public int Visibility { get; set; }

    /// <summary>
    /// When this value was last written, or <see langword="null"/> when it has never been
    /// written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Maps to <c>LastUpdatedDate datetime NOT NULL</c>. Nullable here despite the column being
    /// non-nullable, for the reason given in the migration note below.
    /// </para>
    /// <para>
    /// A timestamp only, with no time-zone offset, because the legacy column is a bare
    /// <c>datetime</c> written from the application server's local clock. Attaching an offset
    /// at this boundary would assert a precision the stored data does not carry. Interpreting
    /// the value is the caller's concern, and the target reads clocks through an injected
    /// abstraction so that this is testable rather than ambient.
    /// </para>
    /// </remarks>
    // MIGRATION: the minimum date is translated to null at this boundary.
    //
    // The column is declared NOT NULL, so at first sight a non-nullable timestamp looks
    // correct. It is not, because the legacy null contract does not use SQL nulls for dates:
    // the sentinel table at Library/Components/Shared/Null.vb defines its null date as the
    // minimum date value, so a row that has never really been stamped carries 0001-01-01
    // rather than a null. Publishing that verbatim would present a client with a date that
    // looks like a real timestamp and sorts before every genuine one, and every consumer would
    // have to know the sentinel to filter it out.
    //
    // The sentinel is therefore translated to null on the way out and back on the way in, and
    // the translation lives in the mapper. No code on this side may compare this member
    // against the minimum date, and no consumer should ever be shown 0001-01-01 as though it
    // were a real timestamp.
    //
    // Note the asymmetry with the value member above, which keeps its empty-string sentinel
    // rather than translating it. The two are treated differently on purpose: the empty string
    // is externally observable in a legacy contract, because the required-property check
    // compares against it, whereas the minimum date is observable only as a rendering
    // artefact and no legacy rule branches on it.
    //
    // Belongs in the repository migration notes.
    public DateTime? LastUpdatedDate { get; set; }

    /// <summary>
    /// The definition of the property this value answers: its name, category, data type,
    /// length, whether it is required, its validation expression and its display order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Embedded rather than referenced by identifier alone, so that a dynamic profile screen
    /// can render a label, choose an editor, apply a validation rule and place the field in
    /// order from a single response. Without it, rendering one profile would require a second
    /// call to the profile-definition endpoint and a client-side join, for data the server has
    /// already loaded in order to project this collection.
    /// </para>
    /// <para>
    /// Non-nullable, and defaulted to an empty instance so that it is never
    /// <see langword="null"/>. This is schema-faithful rather than merely convenient: the
    /// definition foreign key is declared <c>NOT NULL</c> with cascade delete, so a stored
    /// profile value cannot exist without its definition and deleting a definition removes
    /// every value keyed to it. Modelling it as optional would invite a null check that the
    /// schema makes unreachable, and it lets the front-end model mirror this member as
    /// non-optional.
    /// </para>
    /// <para>
    /// Read-only from this contract's point of view. Definitions are portal-scoped metadata
    /// administered through their own endpoint, so a value submitted against this contract does
    /// not update the definition it carries; the definition identifier above is what a write
    /// operation keys on.
    /// </para>
    /// <para>
    /// The required flag and validation expression carried here are data, not constraints on
    /// this contract. They are evaluated by <c>DnnMigration.Application.Validation</c> and by
    /// the client form, which is what allows a portal to change a rule without a redeployment.
    /// </para>
    /// </remarks>
    // MIGRATION: the legacy definition class carried a per-user value slot of its own, which
    // conflated a portal-scoped definition with one user's answer to it. That slot is not
    // reproduced on the definition contract; it is relocated to the value member above, which
    // is where a per-user value belongs. The two concerns are separated here and joined only
    // by this embedding, so a reader should expect no value member on the embedded definition.
    //
    // Belongs in the repository migration notes.
    public ProfilePropertyDefinitionDto Definition { get; set; } = new();
}
