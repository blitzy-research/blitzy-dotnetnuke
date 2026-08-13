using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.ValueObjects;

/// <summary>
/// The stable, globally unique handle of a portal: a typed wrapper over the <c>Portals.GUID</c> column that
/// is distinct from the portal's integer identity.
/// </summary>
/// <remarks>
/// <para>
/// A DotNetNuke portal carries two independent identifiers. The integer key is a database-allocated
/// identity that is meaningful only within one installation, whereas this GUID is generated once, never
/// reassigned, and remains valid across export, re-import and installation boundaries.
/// </para>
/// <para>
/// <strong>Absence is modelled as a nullable <c>PortalGuid</c>, never as a value.</strong> The all-zero
/// GUID was the legacy absence marker - <c>Null.NullGuid</c> is <c>Guid.Empty</c> - and it is rejected here
/// rather than re-exposed, so this type deliberately offers no <c>Empty</c>, <c>None</c> or <c>IsNull</c>
/// member and no test against its own default.
/// </para>
/// </remarks>
public readonly record struct PortalGuid : IEquatable<PortalGuid>
{
    /// <summary>
    /// The wrapped handle, held in an explicit field rather than an auto-property so that the reads which
    /// must enforce the invariant and the reads which must not can be told apart.
    /// </summary>
    /// <remarks>
    /// <see cref="Value"/> and the explicit conversion to <see cref="Guid"/> reach the handle through the
    /// invariant check, so neither can hand the all-zero GUID to a caller. <see cref="ToString"/> reads
    /// this field directly, because rendering must never throw.
    /// </remarks>
    private readonly Guid _value;

    /// <summary>Explanation given when a handle is rejected for being the legacy absence sentinel.</summary>
    private const string SentinelRejectionDetail =
        "A PortalGuid cannot be the all-zero GUID. That value is the legacy Null.NullGuid " +
        "absence sentinel (Library/Components/Shared/Null.vb:L81-L85), not a portal handle: " +
        "the Portals.GUID column is declared NOT NULL with DEFAULT newid() " +
        "(01.00.00.SqlDataProvider:L93), so the database never produces it. Model an absent " +
        "handle as a nullable PortalGuid instead.";

    /// <summary>
    /// Initialises a new <see cref="PortalGuid"/> over the supplied handle, rejecting the legacy absence
    /// sentinel.
    /// </summary>
    /// <param name="value">The portal handle as stored in the <c>Portals.GUID</c> column.</param>
    /// <exception cref="DomainException"><paramref name="value"/> is <c>Guid.Empty</c>.</exception>
    /// <remarks>
    /// This constructor is where the invariant is established. <see cref="From"/>, <see cref="Parse"/>,
    /// <see cref="TryParse"/> and the explicit conversion from <see cref="Guid"/> all reach the value
    /// through it, so there is exactly one definition of what a valid handle is.
    /// </remarks>
    public PortalGuid(Guid value)
    {
        if (DenotesLegacySentinel(value))
        {
            throw new DomainException(SentinelRejectionDetail);
        }

        _value = value;
    }

    /// <summary>Gets the underlying handle, refusing to surrender the legacy absence sentinel.</summary>
    /// <value>The <see cref="Guid"/> this instance wraps.</value>
    /// <exception cref="DomainException">
    /// This instance is a zero-initialised <c>default(PortalGuid)</c> - or an unassigned field or array
    /// element of this type - and therefore holds <c>Guid.Empty</c> without ever having passed the
    /// validating constructor.
    /// </exception>
    /// <remarks>
    /// Two reads deliberately do not throw, because neither can leak a handle.
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

    /// <summary>Creates a <see cref="PortalGuid"/> from an existing handle.</summary>
    /// <param name="value">The portal handle to wrap.</param>
    /// <returns>A <see cref="PortalGuid"/> wrapping <paramref name="value"/>.</returns>
    /// <exception cref="DomainException"><paramref name="value"/> is <c>Guid.Empty</c>.</exception>
    /// <remarks>
    /// A named counterpart to the explicit conversion from <see cref="Guid"/>, provided so that call sites
    /// and expression trees that cannot express a cast operator - notably a persistence value conversion -
    /// have a first-class factory to use. It delegates to the validating constructor and adds no rule of
    /// its own.
    /// </remarks>
    public static PortalGuid From(Guid value)
    {
        return new PortalGuid(value);
    }

    /// <summary>Converts the canonical text form of a handle into a <see cref="PortalGuid"/>.</summary>
    /// <param name="value">The text to convert.</param>
    /// <returns>The parsed <see cref="PortalGuid"/>.</returns>
    /// <exception cref="DomainException">
    /// <paramref name="value"/> is absent, is not well-formed GUID text, or denotes the all-zero GUID. A
    /// <see langword="null"/> reference is accepted as an argument and reported as absent text, so this
    /// method never raises a null-reference or argument exception.
    /// </exception>
    /// <remarks>
    /// Deliberately implemented on top of <see cref="TryParse"/> rather than beside it, which is the
    /// relationship the framework itself uses. There is consequently one parsing implementation and one
    /// definition of validity, and the throwing overload contributes only the diagnostic message.
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
    /// Attempts to convert the canonical text form of a handle into a <see cref="PortalGuid"/> without
    /// throwing.
    /// </summary>
    /// <param name="value">
    /// The text to convert. <see langword="null"/>, empty and white-space input are treated as invalid and
    /// reported through the return value.
    /// </param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, the parsed handle; when it returns <see
    /// langword="false"/>, <c>default</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="value"/> is well-formed GUID text denoting a handle other
    /// than the all-zero GUID; otherwise <see langword="false"/>.
    /// </returns>
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

    /// <summary>Converts a <see cref="Guid"/> into a <see cref="PortalGuid"/>.</summary>
    /// <param name="value">The portal handle to wrap.</param>
    /// <returns>A <see cref="PortalGuid"/> wrapping <paramref name="value"/>.</returns>
    /// <exception cref="DomainException"><paramref name="value"/> is <c>Guid.Empty</c>.</exception>
    public static explicit operator PortalGuid(Guid value)
    {
        return new PortalGuid(value);
    }

    /// <summary>Converts a <see cref="PortalGuid"/> into the <see cref="Guid"/> it wraps.</summary>
    /// <param name="portalGuid">The handle to unwrap.</param>
    /// <returns>The underlying <see cref="Guid"/>.</returns>
    /// <exception cref="DomainException">
    /// <paramref name="portalGuid"/> is a zero-initialised <c>default(PortalGuid)</c> and so holds
    /// <c>Guid.Empty</c> without having passed the validating constructor.
    /// </exception>
    public static explicit operator Guid(PortalGuid portalGuid)
    {
        return portalGuid.Value;
    }

    /// <summary>Returns the handle in the canonical hyphenated GUID form.</summary>
    /// <returns>
    /// The underlying handle formatted as 32 hexadecimal digits in five hyphen-separated groups, for
    /// example <c>57ad7180-c5e7-49f5-b282-c6475cdb7ee7</c>.
    /// </returns>
    /// <remarks>
    /// This replaces the record-synthesized rendering, which would have emitted the type name and property
    /// name around the value, so that the text is the handle itself and round trips through <see
    /// cref="Parse"/>.
    /// </remarks>
    public override string ToString()
    {
        return _value.ToString("D");
    }

    /// <summary>
    /// Determines whether a candidate handle is the legacy absence sentinel rather than a real portal
    /// handle.
    /// </summary>
    /// <param name="candidate">The handle to test.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="candidate"/> is the all-zero GUID; otherwise <see
    /// langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Deliberately private, and named for the legacy concept it detects rather than for the mechanical
    /// comparison it performs, so that both call sites read as a statement about the migration instead of
    /// an unexplained equality test. Keeping it in one place means the validating constructor and <see
    /// cref="TryParse"/> cannot drift apart.
    /// </remarks>
    private static bool DenotesLegacySentinel(Guid candidate)
    {
        return candidate == Guid.Empty;
    }

    /// <summary>Names the clause that <paramref name="value"/> failed, without repeating the value.</summary>
    /// <param name="value">The text <see cref="TryParse"/> has already rejected.</param>
    /// <returns>
    /// One of three fixed sentences: the text was absent, it was not well-formed GUID text, or it denoted
    /// the legacy absence sentinel.
    /// </returns>
    /// <remarks>
    /// This matters because a domain message is not a private diagnostic. It travels to the structured log
    /// and, for an unhandled exception, to the edge of the process - which is exactly what the guidance on
    /// <see cref="DomainException(string)"/> already warns about.
    /// </remarks>
    private static string DescribeViolation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "A PortalGuid must be supplied: the text was absent, empty, or whitespace only.";
        }

        if (!Guid.TryParse(value, out Guid candidate))
        {
            return "A PortalGuid must be well-formed GUID text, in any of the formats " +
                "Guid.TryParse accepts.";
        }

        // Reached only when the framework parsed the text and this type still refused it, and the sentinel
        // is the sole remaining reason - see the exhaustiveness argument above.
        return DenotesLegacySentinel(candidate)
            ? SentinelRejectionDetail
            : "A PortalGuid must be well-formed GUID text denoting a handle this type accepts.";
    }
}
