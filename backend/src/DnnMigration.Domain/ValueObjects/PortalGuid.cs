// MIGRATION: Portals.GUID. The legacy declaration
//
//     <XmlIgnore()> Public Property GUID() As Guid
//
// at Library/Components/Portal/PortalInfo.vb:L245 becomes this typed wrapper. Six
// facts govern the translation, and each was read from this checkout rather than
// assumed; a seventh note records how a rejection is reported, because that is a
// decision rather than a measurement.
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
//    [int] IDENTITY (-1, 1) NOT NULL (01.00.00.SqlDataProvider:L77), so the seed -
//    the first value the column generates - is -1, which is simultaneously
//    Null.NullInteger (Null.vb:L41-L45) and a genuine, addressable row identifier.
//    The shipped default portal row is a separate matter: it is inserted with an
//    explicit PortalID of 0 (01.00.00.SqlDataProvider:L7125), so 0 is a real key
//    too, and neither value may be read as absence. Treating -1 as "absent" would
//    make host-level records unreachable, which is why PortalId must accept it
//    while PortalGuid may refuse Guid.Empty. The two sibling value objects diverge
//    here on purpose.
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
// 6. default(PortalGuid) REMAINS REACHABLE, SO THE INVARIANT IS ENFORCED ON THE WAY
//    OUT AS WELL AS ON THE WAY IN. Every struct has an implicit parameterless
//    constructor that C# does not allow a type to suppress, so default(PortalGuid),
//    a zero-initialised array element and an unassigned field of this type all hold
//    Guid.Empty without passing the validating path. The type cannot stop such an
//    instance existing; what it can and does do is guarantee that no such instance
//    ever yields a handle. Value and the explicit conversion to Guid - the only two
//    routes by which a Guid a caller could persist leaves this type - re-check the
//    invariant and throw DomainException instead of surrendering Guid.Empty, so a
//    defaulted wrapper cannot round-trip into the NOT NULL column of fact 2 by way
//    of a cast or an unwrap. Equality, hashing and ToString stay total on purpose:
//    they read the private field directly, none of them produces a value a
//    persistence layer can store, and a rendering path that threw would break
//    logging and debugger display at the worst possible moment. Construction is
//    therefore the first gate and unwrapping the second, rather than construction
//    being a boundary that callers are merely warned about.
//
// 7. REJECTIONS REPORT FIXED TEXT, NEVER THE REJECTED INPUT. Parse is the only
//    member here that accepts caller-supplied text, and its DomainException message
//    is fixed wording plus the input's character count - the input itself is never
//    interpolated into it. An exception message travels to every log sink and error
//    surface that reports it, so echoing untrusted text would carry attacker-chosen
//    characters, control characters and line breaks among them, into records that
//    are read as trustworthy. The rejected value is not needed to explain the
//    fault: what is wrong with it is that it is not a GUID. Callers who genuinely
//    need to correlate one rejected input with one report use TryParse, which never
//    throws and leaves them in control of what is recorded and how it is escaped.

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
/// be shared freely. One structural caveat applies and is closed rather than merely admitted:
/// because a struct always has an implicit parameterless constructor that C# does not permit a
/// type to suppress, <c>default(PortalGuid)</c> is reachable and holds <c>Guid.Empty</c>
/// without passing the constructor's validation. The invariant is therefore enforced on the
/// way back as well as on the way in - <see cref="Value"/> and the explicit conversion to
/// <see cref="Guid"/> both refuse to surrender the all-zero GUID - so although a defaulted
/// instance can exist, it can never be mistaken for a real handle by anything that unwraps it.
/// Equality, hashing and <see cref="ToString"/> stay total, because none of them yields a
/// value a persistence layer could store.
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
    /// The wrapped handle, held in an explicit field rather than an auto-property so that the
    /// reads which must enforce the invariant and the reads which must not can be told apart.
    /// </summary>
    /// <remarks>
    /// <see cref="Value"/> and the explicit conversion to <see cref="Guid"/> reach the handle
    /// through the invariant check, so neither can hand the all-zero GUID to a caller.
    /// <see cref="ToString"/> reads this field directly, because rendering must never throw.
    /// The compiler-synthesized equality and hashing also operate on the field, which is what
    /// keeps a zero-initialised instance comparable and hashable instead of unusable.
    /// </remarks>
    private readonly Guid _value;

    /// <summary>
    /// Explanation given when a handle is rejected for being the legacy absence sentinel.
    /// </summary>
    /// <remarks>
    /// Declared once and shared by the two members that can reach that conclusion - the
    /// validating constructor and <see cref="DescribeViolation(string?)"/> - for the same
    /// reason <see cref="DenotesLegacySentinel(Guid)"/> is shared by the two members that
    /// detect it: a caller who supplied the all-zero GUID as text and one who supplied it as a
    /// value have made the identical mistake and must be told the identical thing. Two copies
    /// of this sentence would be free to drift apart.
    /// </remarks>
    private const string SentinelRejectionDetail =
        "A PortalGuid cannot be the all-zero GUID. That value is the legacy Null.NullGuid " +
        "absence sentinel (Library/Components/Shared/Null.vb:L81-L85), not a portal handle: " +
        "the Portals.GUID column is declared NOT NULL with DEFAULT newid() " +
        "(01.00.00.SqlDataProvider:L93), so the database never produces it. Model an absent " +
        "handle as a nullable PortalGuid instead.";

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
    /// This constructor is where the invariant is established. <see cref="From"/>,
    /// <see cref="Parse"/>, <see cref="TryParse"/> and the explicit conversion from
    /// <see cref="Guid"/> all reach the value through it, so there is exactly one definition
    /// of what a valid handle is. It is not, however, the only place the invariant is
    /// <em>enforced</em>: because the implicit parameterless constructor that every struct
    /// carries cannot be suppressed, <c>default(PortalGuid)</c> bypasses this path entirely, so
    /// <see cref="Value"/> and the explicit conversion to <see cref="Guid"/> re-check the
    /// invariant on the way out. Construction is the first gate; unwrapping is the second.
    /// </remarks>
    public PortalGuid(Guid value)
    {
        if (DenotesLegacySentinel(value))
        {
            throw new DomainException(SentinelRejectionDetail);
        }

        _value = value;
    }

    /// <summary>
    /// Gets the underlying handle, refusing to surrender the legacy absence sentinel.
    /// </summary>
    /// <value>
    /// The <see cref="Guid"/> this instance wraps. It is never <c>Guid.Empty</c>: an instance
    /// holding that value did not come from the validating constructor, and reading it here is
    /// reported rather than answered.
    /// </value>
    /// <exception cref="DomainException">
    /// This instance is a zero-initialised <c>default(PortalGuid)</c> - or an unassigned field
    /// or array element of this type - and therefore holds <c>Guid.Empty</c> without ever having
    /// passed the validating constructor.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This is the unwrap point for persistence, which is exactly why the check belongs here.
    /// A struct's implicit parameterless constructor cannot be suppressed, so the type cannot
    /// prevent a defaulted instance from existing; what it can do is guarantee that no such
    /// instance ever yields a usable-looking handle. Every route by which this type surrenders
    /// a <see cref="Guid"/> a caller might store - this property and the explicit conversion to
    /// <see cref="Guid"/> - passes through the same check, so an all-zero GUID cannot reach the
    /// <c>NOT NULL</c> column described in item 2 of the migration note above by way of a
    /// defaulted wrapper.
    /// </para>
    /// <para>
    /// Two reads deliberately do not throw, because neither can leak a handle. Equality and
    /// hashing are synthesized over the private field, so a defaulted instance remains
    /// comparable and remains usable as a dictionary key. <see cref="ToString"/> likewise reads
    /// the field directly: diagnostics, logging and debugger display must never fail, and the
    /// text it produces is not a handle a persistence layer can consume.
    /// </para>
    /// <para>
    /// Any conversion between <see cref="PortalGuid"/> and the column's
    /// <c>uniqueidentifier</c> type is declared in the Infrastructure layer rather than here,
    /// because the Domain project takes no dependency on any persistence technology.
    /// </para>
    /// </remarks>
    public Guid Value
    {
        get
        {
            if (DenotesLegacySentinel(_value))
            {
                throw new DomainException(
                    "This PortalGuid holds the all-zero GUID, so it was never produced by the " +
                    "validating constructor. Every struct carries an implicit parameterless " +
                    "constructor that C# does not allow a type to suppress, so a zero-" +
                    "initialised default, an unassigned field and an unread array element all " +
                    "reach this state without passing validation. The all-zero GUID is the " +
                    "legacy Null.NullGuid absence sentinel " +
                    "(Library/Components/Shared/Null.vb:L81-L85), and the Portals.GUID column " +
                    "is declared NOT NULL with DEFAULT newid() " +
                    "(01.00.00.SqlDataProvider:L93), so surrendering it here would let a value " +
                    "the database cannot produce flow into a column that cannot hold it. " +
                    "Obtain handles through the constructor, From, Parse or TryParse, and model " +
                    "an absent handle as a nullable PortalGuid.");
            }

            return _value;
        }
    }

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
    /// <paramref name="value"/> is absent, is not well-formed GUID text, or denotes the
    /// all-zero GUID. A <see langword="null"/> reference is accepted as an argument and
    /// reported as absent text, so this method never raises a null-reference or argument
    /// exception.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Deliberately implemented on top of <see cref="TryParse"/> rather than beside it, which
    /// is the relationship the framework itself uses. There is consequently one parsing
    /// implementation and one definition of validity, and the throwing overload contributes
    /// only the diagnostic message.
    /// </para>
    /// <para>
    /// <strong>The rejected text is never repeated.</strong> The message names the clause that
    /// failed and nothing else - not the value, not its length, not its shape - so it is safe
    /// to record wherever an exception message is recorded. It is a diagnostic rather than a
    /// response: the API layer publishes no domain message to a caller, so a reader of this
    /// sentence will find it in the structured log. The reasoning is on
    /// <see cref="DescribeViolation(string?)"/>.
    /// </para>
    /// </remarks>
    public static PortalGuid Parse(string value)
    {
        if (TryParse(value, out PortalGuid parsed))
        {
            return parsed;
        }

        throw new DomainException(DescribeViolation(value));
    }

    /// <summary>
    /// Attempts to convert the canonical text form of a handle into a
    /// <see cref="PortalGuid"/> without throwing.
    /// </summary>
    /// <param name="value">
    /// The text to convert. <see langword="null"/>, empty and white-space input are treated
    /// as invalid and reported through the return value. This member draws no distinction
    /// between absent and malformed text, because it produces no message in which the
    /// distinction could be expressed; <see cref="Parse"/> is where the two are separated.
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
    /// On the failure path <paramref name="result"/> is assigned <c>default</c>, which is the
    /// framework's convention for a failed parse. That instance holds the all-zero GUID
    /// internally, but it cannot hand it out: a caller who ignores the <see langword="false"/>
    /// result and reads <see cref="Value"/> anyway gets a <see cref="DomainException"/> rather
    /// than a plausible-looking handle, so the convention costs nothing in safety here.
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
    /// <exception cref="DomainException">
    /// <paramref name="portalGuid"/> is a zero-initialised <c>default(PortalGuid)</c> and so
    /// holds <c>Guid.Empty</c> without having passed the validating constructor.
    /// </exception>
    /// <remarks>
    /// Explicit for symmetry with the inbound conversion, and because discarding the type's
    /// guarantee should be a visible act. <see cref="Value"/> is the equivalent named form and
    /// is preferred in ordinary code; this operator exists for the generic conversion sites
    /// that can only express a cast. It delegates to <see cref="Value"/> and therefore inherits
    /// its refusal to surrender the all-zero GUID: unwrapping a defaulted instance fails here
    /// too, which is what stops a cast being the loophole the property closed.
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
    /// <para>
    /// This replaces the record-synthesized rendering, which would have emitted the type name
    /// and property name around the value, so that the text is the handle itself and round
    /// trips through <see cref="Parse"/>. The output is deliberately the raw value in every
    /// case: no substitute token such as "(none)" is ever produced, because this type admits
    /// no absent state to describe - absence is expressed by a nullable
    /// <see cref="PortalGuid"/>, whose text form is the caller's concern. The format is
    /// composed of invariant characters, so the result does not vary with culture.
    /// </para>
    /// <para>
    /// It reads the private field rather than <see cref="Value"/>, and that difference is
    /// deliberate. <see cref="Value"/> refuses to surrender the all-zero GUID; rendering must
    /// not, because a throwing <c>ToString</c> breaks logging, diagnostics and debugger display
    /// at precisely the moment someone is trying to establish what went wrong. A zero-
    /// initialised instance therefore renders as thirty-two zeros, which is a truthful
    /// description of what it holds and is not a handle any persistence layer will accept.
    /// </para>
    /// </remarks>
    public override string ToString()
    {
        return _value.ToString("D");
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

    /// <summary>
    /// Names the clause that <paramref name="value"/> failed, without repeating the value.
    /// </summary>
    /// <param name="value">The text <see cref="TryParse"/> has already rejected.</param>
    /// <returns>
    /// One of three fixed sentences: the text was absent, it was not well-formed GUID text, or
    /// it denoted the legacy absence sentinel. The return value is never
    /// <see langword="null"/> and never empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Nothing derived from the argument is emitted.</strong> Not the value, not its
    /// length, not which character offended, and no substitute rendering of it either. The
    /// return value is one of three compile-time constants selected by the argument, so the
    /// argument influences which sentence is chosen and nothing at all about its content. That
    /// is a stronger guarantee than redaction, which still has to be applied correctly at every
    /// site.
    /// </para>
    /// <para>
    /// This matters because a domain message is not a private diagnostic. It travels to the
    /// structured log and, for an unhandled exception, to the edge of the process - which is
    /// exactly what the guidance on <see cref="DomainException(string)"/> already warns about.
    /// Text supplied by a caller is arbitrary by definition: it may be a credential pasted into
    /// the wrong field, a personal identifier, or content chosen to forge a log entry. None of
    /// those can be recognised here, so none is repeated. The API layer independently refuses
    /// to publish any domain message, which makes this the inner half of a deliberate pair
    /// rather than the only defence.
    /// </para>
    /// <para>
    /// The three outcomes are exhaustive, and that follows from <see cref="TryParse"/> rather
    /// than from inspection: it returns <see langword="false"/> if and only if the framework's
    /// own parse fails or <see cref="DenotesLegacySentinel(Guid)"/> holds. Absent text is
    /// separated from malformed text because the two are different mistakes with different
    /// remedies - a caller who sent nothing has a missing value, a caller who sent something
    /// unparseable has a wrong one - and the legacy sentinel module made exactly that
    /// distinction impossible by representing an absent string as the empty one
    /// (Library/Components/Shared/Null.vb:L61-L65). Naming the clause is what keeps a fixed
    /// message useful; the sibling <c>EmailAddress</c> value object resolves the same tension
    /// the same way.
    /// </para>
    /// </remarks>
    private static string DescribeViolation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "A PortalGuid must be supplied: the text was absent, empty, or whitespace only.";
        }

        // Re-parsed rather than threaded out of TryParse, which reports failure as a bare
        // false and would have to grow an out parameter or a status code to say more. Parsing
        // twice on a path that is already throwing costs nothing worth measuring, and it keeps
        // the non-throwing member the framework-shaped predicate it is meant to be.
        if (!Guid.TryParse(value, out Guid candidate))
        {
            return "A PortalGuid must be well-formed GUID text, in any of the formats " +
                "Guid.TryParse accepts.";
        }

        // Reached only when the framework parsed the text and this type still refused it, and
        // the sentinel is the sole remaining reason - see the exhaustiveness argument above.
        // Asserted rather than assumed so that a future change to TryParse's rule surfaces
        // here as a wrong message rather than passing unnoticed.
        return DenotesLegacySentinel(candidate)
            ? SentinelRejectionDetail
            : "A PortalGuid must be well-formed GUID text denoting a handle this type accepts.";
    }
}
