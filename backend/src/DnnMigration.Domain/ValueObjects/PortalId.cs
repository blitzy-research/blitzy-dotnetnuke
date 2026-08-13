using System.Globalization;

namespace DnnMigration.Domain.ValueObjects;

// -1 and 0 are BOTH legitimate portal identifiers in the existing schema, and keeping them that way is this
// type's only purpose. dbo.Portals.PortalID is declared IDENTITY(-1, 1), so the first portal row is keyed
// -1 and the second 0, and the installation ships a portal keyed 0.

/// <summary>
/// The identity of a DotNetNuke portal, carried as a distinct type so that the two identity values the
/// existing schema treats as ordinary data can never be mistaken for the absence of a portal.
/// </summary>
/// <remarks>
/// <para>
/// A deliberately unopinionated wrapper: it stores one <see cref="int"/>, exposes it, compares it and
/// prints it, and validates nothing. <c>dbo.Portals.PortalID</c> is <c>int NOT NULL</c> with an identity
/// seed of -1, so every value an <see cref="int"/> can hold is a value the column can hold and there is
/// nothing to reject.
/// </para>
/// <para>
/// Absence is expressed by <c>PortalId?</c> and by nothing else, because the compiler then forces a caller
/// to unwrap before comparing. No reserved instance, predicate or comparison against a reserved number may
/// be added: each would recreate the ambiguity this type removes.
/// </para>
/// </remarks>
public readonly record struct PortalId : IEquatable<PortalId>
{
    /// <summary>Wraps a portal identifier.</summary>
    /// <param name="value">Any <see cref="int"/>; every value is legitimate, including -1 and 0.</param>
    public PortalId(int value)
    {
        Value = value;
    }

    /// <summary>The identifier as stored in <c>dbo.Portals.PortalID</c>.</summary>
    public int Value { get; }

    /// <summary>Wraps an identifier. Explicit, so the conversion is always visible at the call site.</summary>
    /// <param name="value">Identifier to wrap.</param>
    public static explicit operator PortalId(int value) => new(value);

    /// <summary>Unwraps the identifier for persistence and comparison against raw column values.</summary>
    /// <param name="portalId">Identifier to unwrap.</param>
    public static explicit operator int(PortalId portalId) => portalId.Value;

    /// <summary>Renders the identifier for diagnostics.</summary>
    /// <returns>The identifier in the invariant culture, so a log line never varies by locale.</returns>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
