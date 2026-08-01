// MIGRATION: Portals.GUID. The legacy declaration
//
//     <XmlIgnore()> Public Property GUID() As Guid
//
// at Library/Components/Portal/PortalInfo.vb:L245 becomes this typed wrapper. Six
// facts govern the translation, and each was read from this checkout rather than
// assumed.
//
// 1. THE SERIALISATION ATTRIBUTE IS DROPPED, NOT TRANSLATED. The legacy property
//    carried <XmlIgnore()> on a class that is itself
//    <XmlRoot("settings", IsNullable:=False)> (PortalInfo.vb:L29, enabled by the
//    XML-serialisation import at L26) and whose neighbouring properties carry
//    <XmlElement(...)> throughout - L237 "backgroundfile" and L253
//    "paymentprocessor" sit either side of the GUID property. Per Rule T8 the
//    whole attribute family is deleted rather than converted, and deliberately
//    NOT swapped for a JSON equivalent either: no JsonIgnore, JsonConverter or
//    JsonPropertyName attribute replaces it, because serialisation is performed
//    by the JSON serialiser at the API boundary only and the DTO layer owns the
//    wire contract, so the Domain expresses no serialisation opinion whatsoever.
//    The Domain project declares no package reference, which makes that a
//    compile-time fact rather than a convention.
//
// 2. THE DATABASE NEVER PRODUCES THE ALL-ZERO GUID. The backing column is
//
//        [GUID] [uniqueidentifier] NOT NULL CONSTRAINT DF_Portals_GUID DEFAULT newid()
//
//    at Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L93,
//    inside CREATE TABLE [dbo].[Portals] at L76. The column is NOT NULL and its
//    default is newid(), which cannot emit Guid.Empty, and the shipped _default
//    portal row supplies the concrete value
//    '57ad7180-c5e7-49f5-b282-c6475cdb7ee7' (L7125). Per Rule T4 that column
//    definition is an immutable schema fact; nothing here alters it.
//
// 3. THE ALL-ZERO GUID IS THE LEGACY ABSENCE SENTINEL. Null.NullGuid is defined
//    as Guid.Empty at Library/Components/Shared/Null.vb:L81-L85, and the sentinel
//    is honoured in both directions: SetNull translates DBNull into it on every
//    inbound read (L88 object overload, L108-L109 Guid branch; L119 PropertyInfo
//    overload), GetNull translates it back to DBNull.Value on write (L200-L203),
//    and IsNull tests for it (L229-L230). Combining facts 2 and 3: an all-zero
//    GUID on this column cannot have originated in the database, so it can only
//    have arrived through that legacy sentinel path.
//
// 4. THE DECISION, AND WHY IT MUST NOT BE COPIED TO PortalId. This type therefore
//    REJECTS Guid.Empty at construction - explicitly and with a named reason,
//    rather than letting it pass unremarked. That treatment is legitimate here
//    only because fact 2 removes the collision, and it MUST NOT be generalised to
//    the integer key: [PortalID] is declared
//    [int] IDENTITY (-1, 1) NOT NULL (01.00.00.SqlDataProvider:L77), so -1 is
//    simultaneously Null.NullInteger (Null.vb:L41-L45) and a genuine, addressable
//    row identifier - the first portal allocated is 0 and -1 is a real row.
//    Treating -1 as "absent" would make host-level records unreachable, which is
//    why PortalId must accept it while PortalGuid may refuse Guid.Empty. The two
//    sibling value objects diverge here on purpose.
//
// 5. ABSENCE IS EXPRESSED BY PortalGuid?, AND BY NOTHING ELSE. Per Rule T7 the
//    sentinel survives at the boundary, not in the domain: an optional portal
//    handle is a nullable PortalGuid. This type deliberately exposes no Empty,
//    None, Null, NullValue, Unspecified, NotSet, Invalid, IsEmpty, IsNull,
//    HasValue, IsTransient or IsNew member, and performs no comparison against
//    its own default. Any such member would re-admit the sentinel into the public
//    surface and undo the rejection in fact 4. Where a legacy contract observably
//    carries the all-zero GUID, the DTO layer restores it explicitly so that
//    serialisation never silently converts it.
//
// 6. default(PortalGuid) REMAINS REACHABLE, AND THAT IS STATED RATHER THAN HIDDEN.
//    Every struct has an implicit parameterless constructor that C# does not allow
//    a type to suppress, so default(PortalGuid), a zero-initialised array element
//    and an unassigned field of this type all hold Guid.Empty without passing the
//    validating path. Construction is a boundary, not a proof. Code that must be
//    certain a handle is real inspects Value; a persistence mapping must never
//    round-trip a defaulted instance into the NOT NULL column of fact 2.

using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.ValueObjects;

/// <summary>
/// The stable, globally unique handle of a portal: a typed wrapper over the
/// <c>Portals.GUID</c> column that is distinct from the portal's integer identity.
/// </summary>
/// <remarks>
/// <para>
/// A DotNetNuke portal carries two independent identifiers. The integer key is a
/// database-allocated identity that is meaningful only within one installation, whereas
/// this GUID is generated once, never reassigned, and remains valid across export,
/// re-import and installation boundaries. Wrapping it removes a whole class of defect
/// that a bare <see cref="Guid"/> permits: a portal handle can no longer be passed where
/// a module, tab, role or user GUID is expected, because the compiler rejects it.
/// </para>
/// <para>
/// The type is a <c>readonly record struct</c>, matching the idiom already established by
/// <c>Result</c> in the <c>Common</c> folder. That choice has three consequences worth
/// stating. It costs no allocation, so wrapping is free at the scale a repository query
/// operates. Value equality, <see cref="GetHashCode"/> and the <c>==</c> and <c>!=</c>
/// operators are synthesized by the compiler over the single <see cref="Value"/> field, so
/// there is no hand-written equality to keep in step with the state. And it satisfies
/// <see cref="IEquatable{T}"/> for itself, which is listed explicitly on the declaration
/// because <c>Entity&lt;TId&gt;</c> compares identities through
/// <see cref="EqualityComparer{T}.Default"/>: that resolves to the non-boxing
/// <see cref="IEquatable{T}.Equals(T)"/> path only when the interface is implemented.
/// </para>
/// <para>
/// <strong>Absence is modelled as a nullable <c>PortalGuid</c>, never as a value.</strong>
/// The all-zero GUID was the legacy absence marker - <c>Null.NullGuid</c> is
/// <c>Guid.Empty</c> - and it is rejected here rather than re-exposed, so this type
/// deliberately offers no <c>Empty</c>, <c>None</c> or <c>IsNull</c> member and no test
/// against its own default. The rejection is safe because the backing column is
/// <c>NOT NULL</c> with a <c>newid()</c> default and so never produces the all-zero value.
/// The <c>// MIGRATION:</c> block at the head of this file records the measured evidence,
/// including why the sibling <c>PortalId</c> must reach the opposite conclusion about its
/// own sentinel.
/// </para>
/// <para>
/// Instances are immutable and hold no reference state, so the type is thread-safe and may
/// be shared freely. One honest caveat applies: because a struct always has an implicit
/// parameterless constructor, <c>default(PortalGuid)</c> is reachable and carries
/// <c>Guid.Empty</c> without passing the validation below. Construction is therefore a
/// boundary rather than a guarantee.
/// </para>
/// </remarks>
/// <example>
/// Wrapping the handle of the shipped <c>_default</c> portal, then unwrapping it for
/// persistence:
/// <code>
/// PortalGuid handle = PortalGuid.Parse("57ad7180-c5e7-49f5-b282-c6475cdb7ee7");
/// Guid forTheDatabase = handle.Value;
///
/// // An optional handle is a nullable PortalGuid - never a sentinel value.
/// PortalGuid? unknown = null;
/// </code>
/// </example>
public readonly record struct PortalGuid : IEquatable<PortalGuid>
{
    /// <summary>
    /// Initialises a new <see cref="PortalGuid"/> over the supplied handle, rejecting the
    /// legacy absence sentinel.
    /// </summary>
    /// <param name="value">
    /// The portal handle as stored in the <c>Portals.GUID</c> column. It must not be
    /// <c>Guid.Empty</c>: that value is the legacy <c>Null.NullGuid</c> marker rather than a
    /// handle, and the column's <c>NOT NULL DEFAULT newid()</c> definition means the database
    /// cannot produce it.
    /// </param>
    /// <exception cref="DomainException">
    /// <paramref name="value"/> is <c>Guid.Empty</c>.
    /// </exception>
    /// <remarks>
    /// This constructor is the single place the invariant is enforced. <see cref="From"/>,
    /// <see cref="Parse"/>, <see cref="TryParse"/> and the explicit conversion from
    /// <see cref="Guid"/> all reach the value through it, so there is exactly one definition
    /// of what a valid handle is. Note the caveat in the type's remarks: the implicit
    /// parameterless constructor that every struct carries bypasses this path, so
    /// <c>default(PortalGuid)</c> is reachable and holds <c>Guid.Empty</c>.
    /// </remarks>
    public PortalGuid(Guid value)
    {
        if (DenotesLegacySentinel(value))
        {
            throw new DomainException(
                "A PortalGuid cannot be the all-zero GUID. That value is the legacy " +
                "Null.NullGuid absence sentinel (Library/Components/Shared/Null.vb:L81-L85), " +
                "not a portal handle: the Portals.GUID column is declared NOT NULL with " +
                "DEFAULT newid() (01.00.00.SqlDataProvider:L93), so the database never " +
                "produces it. Model an absent handle as a nullable PortalGuid instead.");
        }

        Value = value;
    }

    /// <summary>
    /// Gets the underlying handle.
    /// </summary>
    /// <value>
    /// The <see cref="Guid"/> this instance wraps. Except for the <c>default</c> caveat
    /// described in the type's remarks, it is never <c>Guid.Empty</c>.
    /// </value>
    /// <remarks>
    /// This is the unwrap point for persistence. The Infrastructure layer's entity
    /// configuration converts between <see cref="PortalGuid"/> and the column's
    /// <c>uniqueidentifier</c> type through this property and <see cref="From"/>; that
    /// conversion is declared there rather than here, because the Domain project takes no
    /// dependency on any persistence technology.
    /// </remarks>
    public Guid Value { get; }

    /// <summary>
    /// Creates a <see cref="PortalGuid"/> from an existing handle.
    /// </summary>
    /// <param name="value">
    /// The portal handle to wrap. It must not be <c>Guid.Empty</c>.
    /// </param>
    /// <returns>A <see cref="PortalGuid"/> wrapping <paramref name="value"/>.</returns>
    /// <exception cref="DomainException">
    /// <paramref name="value"/> is <c>Guid.Empty</c>.
    /// </exception>
    /// <remarks>
    /// A named counterpart to the explicit conversion from <see cref="Guid"/>, provided so
    /// that call sites and expression trees that cannot express a cast operator - notably a
    /// persistence value conversion - have a first-class factory to use. It delegates to the
    /// validating constructor and adds no rule of its own.
    /// </remarks>
    public static PortalGuid From(Guid value)
    {
        return new PortalGuid(value);
    }

    /// <summary>
    /// Converts the canonical text form of a handle into a <see cref="PortalGuid"/>.
    /// </summary>
    /// <param name="value">
    /// The text to convert. Every format <see cref="Guid.TryParse(string, out Guid)"/>
    /// accepts is accepted here.
    /// </param>
    /// <returns>The parsed <see cref="PortalGuid"/>.</returns>
    /// <exception cref="DomainException">
    /// <paramref name="value"/> is not well-formed GUID text, or it denotes the all-zero
    /// GUID. A <see langword="null"/> reference is treated as malformed text and reported the
    /// same way, so this method never raises a null-reference or argument exception.
    /// </exception>
    /// <remarks>
    /// Deliberately implemented on top of <see cref="TryParse"/> rather than beside it, which
    /// is the relationship the framework itself uses. There is consequently one parsing
    /// implementation and one definition of validity, and the throwing overload contributes
    /// only the diagnostic message.
    /// </remarks>
    public static PortalGuid Parse(string value)
    {
        if (TryParse(value, out PortalGuid parsed))
        {
            return parsed;
        }

        throw new DomainException(
            $"'{value}' is not a valid PortalGuid. The text must be a well-formed GUID and " +
            "must not be the all-zero GUID, which is the legacy Null.NullGuid absence " +
            "sentinel (Library/Components/Shared/Null.vb:L81-L85) rather than a portal " +
            "handle. Model an absent handle as a nullable PortalGuid instead.");
    }

    /// <summary>
    /// Attempts to convert the canonical text form of a handle into a
    /// <see cref="PortalGuid"/> without throwing.
    /// </summary>
    /// <param name="value">
    /// The text to convert. <see langword="null"/>, empty and white-space input are treated
    /// as malformed and reported through the return value.
    /// </param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, the parsed handle; when it returns
    /// <see langword="false"/>, <c>default</c>. The parameter is always assigned before the
    /// method returns.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="value"/> is well-formed GUID text denoting
    /// a handle other than the all-zero GUID; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the only member in the file with an <see langword="out"/> parameter, and the
    /// exception is deliberate. The pattern this refactor removes is the legacy
    /// mutate-and-return-status signature - <c>ByRef</c> arguments used to smuggle an updated
    /// aggregate and a status code back from a service call - which becomes a returned result
    /// instead. The framework's own non-throwing parse idiom is a different construct: it
    /// allocates nothing on the failure path, it is what <see cref="Guid.TryParse(string, out Guid)"/>
    /// itself exposes, and departing from its shape would surprise every caller. Following it
    /// exactly is therefore the correct choice, not an exception to the rule.
    /// </para>
    /// <para>
    /// On the failure path <paramref name="result"/> is assigned <c>default</c>, which carries
    /// <c>Guid.Empty</c>. That is the framework's convention for a failed parse and is the one
    /// place the all-zero GUID legitimately appears in an output; a caller that ignores the
    /// <see langword="false"/> return and reads the handle anyway is reading a value the type
    /// has already reported as invalid.
    /// </para>
    /// </remarks>
    public static bool TryParse(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value,
        out PortalGuid result)
    {
        if (Guid.TryParse(value, out Guid candidate) && !DenotesLegacySentinel(candidate))
        {
            result = new PortalGuid(candidate);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Converts a <see cref="Guid"/> into a <see cref="PortalGuid"/>.
    /// </summary>
    /// <param name="value">The portal handle to wrap. It must not be <c>Guid.Empty</c>.</param>
    /// <returns>A <see cref="PortalGuid"/> wrapping <paramref name="value"/>.</returns>
    /// <exception cref="DomainException">
    /// <paramref name="value"/> is <c>Guid.Empty</c>.
    /// </exception>
    /// <remarks>
    /// The conversion is explicit rather than implicit on purpose. An implicit conversion
    /// would let any <see cref="Guid"/> in scope become a portal handle silently - reinstating
    /// exactly the substitution error this type exists to prevent - and would hide a
    /// throwing call behind an invisible coercion. Requiring a cast keeps both the intent and
    /// the validation visible at the call site.
    /// </remarks>
    public static explicit operator PortalGuid(Guid value)
    {
        return new PortalGuid(value);
    }

    /// <summary>
    /// Converts a <see cref="PortalGuid"/> into the <see cref="Guid"/> it wraps.
    /// </summary>
    /// <param name="portalGuid">The handle to unwrap.</param>
    /// <returns>The underlying <see cref="Guid"/>.</returns>
    /// <remarks>
    /// Explicit for symmetry with the inbound conversion, and because discarding the type's
    /// guarantee should be a visible act. <see cref="Value"/> is the equivalent named form and
    /// is preferred in ordinary code; this operator exists for the generic conversion sites
    /// that can only express a cast. Unlike the inbound direction this cannot fail, since
    /// every constructed instance already satisfies the invariant.
    /// </remarks>
    public static explicit operator Guid(PortalGuid portalGuid)
    {
        return portalGuid.Value;
    }

    /// <summary>
    /// Returns the handle in the canonical hyphenated GUID form.
    /// </summary>
    /// <returns>
    /// The underlying handle formatted as 32 hexadecimal digits in five hyphen-separated
    /// groups, for example <c>57ad7180-c5e7-49f5-b282-c6475cdb7ee7</c>.
    /// </returns>
    /// <remarks>
    /// This replaces the record-synthesized rendering, which would have emitted the type name
    /// and property name around the value, so that the text is the handle itself and round
    /// trips through <see cref="Parse"/>. The output is deliberately the raw value in every
    /// case: no substitute token such as "(none)" is ever produced, because this type admits
    /// no absent state to describe - absence is expressed by a nullable
    /// <see cref="PortalGuid"/>, whose text form is the caller's concern. The format is
    /// composed of invariant characters, so the result does not vary with culture.
    /// </remarks>
    public override string ToString()
    {
        return Value.ToString("D");
    }

    /// <summary>
    /// Determines whether a candidate handle is the legacy absence sentinel rather than a
    /// real portal handle.
    /// </summary>
    /// <param name="candidate">The handle to test.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="candidate"/> is the all-zero GUID; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Deliberately private, and named for the legacy concept it detects rather than for the
    /// mechanical comparison it performs, so that both call sites read as a statement about
    /// the migration instead of an unexplained equality test. Keeping it in one place means
    /// the validating constructor and <see cref="TryParse"/> cannot drift apart. It is not
    /// exposed: a public predicate of this shape would hand the sentinel back to callers as a
    /// supported concept, which is precisely what item 5 of the migration note above rules
    /// out.
    /// </remarks>
    private static bool DenotesLegacySentinel(Guid candidate)
    {
        return candidate == Guid.Empty;
    }
}
