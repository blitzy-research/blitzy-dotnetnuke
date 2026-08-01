using System.Globalization;

namespace DnnMigration.Domain.ValueObjects;

// MIGRATION: -1 and 0 are BOTH legitimate portal identifiers in the existing SQL Server schema,
// and the single purpose of this type is to keep them that way. Nothing below may be "tidied up"
// into a range check, a reserved constant or an absence marker. Every fact in this annotation was
// measured against the checkout, and the file and line references are exact.
//
// MIGRATION: the schema seeds the portal identity column BELOW the value other codebases reserve
// for "not yet saved". In the baseline script
//     Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider
// CREATE TABLE [dbo].[Portals] opens at line 76 and line 77 declares
//     [PortalID] [int] IDENTITY (-1, 1) NOT NULL ,
// so the first portal row ever inserted is identified by -1 and the second by 0. The sibling
// in-scope tables in that same script do not agree with one another, which is precisely why no
// single "reserved" identity value exists that could safely be excluded here: Roles.RoleID
// (line 115), Tabs.TabID (line 140) and Modules.ModuleID (line 221) are each IDENTITY (0, 1),
// while ModuleDefinitions.ModuleDefID (line 66), Users.UserID (line 98) and UserRoles.UserRoleID
// (line 239) are IDENTITY (1, 1).
//
// MIGRATION: the shipped portal really does carry the identity 0, so this is measured fact and not
// inference. The same script identity-inserts it across lines 7123 to 7128; line 7125 is
//     INSERT INTO [dbo].[Portals] ([PortalID], [PortalAlias], [PortalName], ...)
//     VALUES (0, '_default', 'DotNetNuke', '/DotNetNuke/0/', 'logo.gif', ...)
// and line 7136 seeds that portal's Home tab with PortalID 0 and AuthorizedRoles '-1;'. A guard
// that rejected zero would reject the only portal a fresh installation has. (The migration plan
// cites the INSERT as line 7126. In this checkout line 7126 is the GO that terminates the batch and
// line 7125 is the statement itself; the measured line is used here.)
//
// MIGRATION: -1 is SIMULTANEOUSLY the legacy marker for "no integral value". In
// Library/Components/Shared/Null.vb, lines 41 to 45 declare a shared read-only property whose
// entire body is `Return -1`, and it is that property - never a database NULL - that the legacy
// data layer substitutes for DBNull on every integral read (Null.vb lines 88-116 for the object
// overload, lines 119-152 for the reflection overload). Lines 155-205 reverse the translation, and
// line 168 writes any integer equal to that marker back to the database as DBNull. The sibling
// markers are 255 for a byte, MinValue for the floating-point and decimal types, the earliest
// representable date, False for a boolean, the all-zero Guid, and - notably - the empty string
// rather than null for text.
//
// MIGRATION: the legacy null-test function at Null.vb lines 208-237 cannot tell the two meanings
// apart, and that ambiguity is the defect this type exists to stop propagating into C#. Lines 210
// and 211 read
//     If TypeOf objField Is Integer Then
//         <result> = objField.Equals(<the -1 marker>)
// so the helper reports EVERY boxed integer -1 as absent, including a genuine Portals.PortalID of
// -1. Line 226 does the same for the empty string and line 230 for the all-zero Guid. The legacy
// helper is therefore structurally incapable of distinguishing the host-level scope from a missing
// value, and this type refuses to inherit that opinion.
//
// MIGRATION: -1 is not merely a value the column could hold - shipped legacy code passes it AS a
// portal identifier in order to reach host-level rows.
// Library/Components/Portal/PortalSettings.vb carries the comment "Add each host Tab to
// DesktopTabs" at line 704 and then calls GetTabsByPortal with the -1 marker at line 706; lines
// 646, 650, 654 and 658 fall back to that same marker as the portalId argument of
// SkinController.GetSkin whenever the portal-specific skin is missing (the portal-specific calls
// sit immediately above at lines 644, 648, 652 and 656); line 836 tests a tab identity against the
// marker; and Library/Components/Portal/PortalController.vb line 1166 assigns -1 to its portal-id
// local outright. Refusing -1 here would make that entire host-level path unrepresentable, which is
// the silent multi-tenant defect this type prevents.
//
// MIGRATION: consequently this type carries NO absence marker of any kind, by design. There is no
// reserved static instance, no "is a value present" predicate, no "has this been persisted"
// predicate, and no member that compares Value against any reserved number. Absence is expressed
// exclusively by the nullable value type PortalId?, which is a distinct CLR type the compiler
// forces a caller to unwrap first - so no in-band integer can be mistaken for it. Sentinel
// semantics survive only at the DTO and API boundary, where a legacy wire contract is externally
// observable; the repository-root MIGRATION_NOTES.md is where those boundary decisions are
// recorded at repository level, and this annotation is its counterpart in code. The sibling base
// type Common/Entity.cs already forbids transience predicates on exactly this evidence, and this
// file is deliberately consistent with it.
//
// MIGRATION: two latent defects in Null.vb are recorded here and deliberately NOT corrected,
// because a discovered legacy defect is annotated in place rather than repaired. Null.vb line 123
// matches "System.Int32" and "System.Int64" in one Select Case arm and yields the Int32-sized -1
// for both, so a 64-bit property receives a 32-bit marker. Null.vb line 125 spells its type name
// "system.Byte" with a lower-case s and is therefore unreachable dead code: the legacy project file
// Library/DotNetNuke.Library.vbproj sets OptionCompare to Binary at line 22, with OptionExplicit On
// at line 23 and OptionStrict On at line 24, and binary comparison makes Select Case string
// matching case-sensitive. Neither defect can reach this type, which stores an int and interprets
// nothing.
//
// MIGRATION: the legacy declaration also carried serialisation metadata that is deliberately
// dropped. Library/Components/Portal/PortalInfo.vb line 33 backs the identity with
// `Private _PortalID As Integer`, line 85 decorates the public property with an XmlElement
// attribute naming it "portalid", and line 29 decorates the class itself with an XmlRoot attribute.
// In the target the wire contract belongs to the DTOs at the API boundary, so this type carries no
// attribute of any kind. The CLR width is preserved exactly: VB Integer is System.Int32, and the
// column is [int], so the wrapped field is int - never long, never unsigned.

/// <summary>
/// The identity of a DotNetNuke portal - the multi-tenant site container - carried as a distinct
/// type so that the two identity values the existing schema treats as ordinary data can never be
/// mistaken for the absence of a portal.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberately unopinionated wrapper. It stores one <see cref="int"/>, exposes it,
/// compares it and prints it, and it validates nothing whatsoever. That is the whole point: the
/// underlying <c>dbo.Portals.PortalID</c> column is declared <c>[int] NOT NULL</c> with an identity
/// seed of -1, so every value an <see cref="int"/> can hold is a value the column can hold, and
/// there is genuinely nothing left to reject. A wrapper with no rejection path is the correct
/// outcome here, not an unfinished one.
/// </para>
/// <para>
/// Two values in particular must never be refused. Both are load-bearing production data rather
/// than theoretical edge cases, and each is measured against the baseline schema script.
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Identity</term>
///     <description>Why it is legitimate</description>
///   </listheader>
///   <item>
///     <term>-1</term>
///     <description>
///     The identity seed of <c>dbo.Portals</c>, and therefore the identity of the first portal row
///     ever inserted. It is simultaneously the legacy marker for "no integral value", and shipped
///     legacy code passes it as a portal identifier to reach host-level rows - so it names a real
///     scope rather than an absent one.
///     </description>
///   </item>
///   <item>
///     <term>0</term>
///     <description>
///     The identity of the <c>_default</c> portal that a fresh installation ships with, and the
///     second value the identity seed produces. It is also the value carried by
///     <c>default(PortalId)</c>, which this type consequently treats as an ordinary portal identity
///     and never as "not yet assigned".
///     </description>
///   </item>
/// </list>
/// <para>
/// Absence is expressed by <c>PortalId?</c> and by nothing else. Because <see cref="Nullable{T}"/>
/// is a distinct CLR type, the compiler forces a caller to unwrap it before comparing, which is
/// exactly the step the legacy sentinel scheme had no way to demand. No reserved instance, no
/// predicate and no comparison against a reserved number appears on this type, and none may be
/// added: any of them would recreate the ambiguity the type was introduced to remove. Where a
/// legacy wire contract genuinely exposes the sentinel, it is preserved at the DTO and API
/// boundary, never here.
/// </para>
/// <para>
/// The conversions to and from <see cref="int"/> are explicit on purpose. An implicit conversion
/// would let a bare integer drift into a portal-identity position unannounced, which is the class
/// of mistake this type is meant to make visible. Read <see cref="Value"/> when the intent is to
/// hand the raw column value to persistence or to a wire contract.
/// </para>
/// <para>
/// The type is a <c>readonly record struct</c>, so it allocates nothing, is immutable, is exactly
/// as thread-safe as the <see cref="int"/> it wraps, and obtains value equality, hashing and the
/// equality operators from the compiler rather than from hand-written members that could drift out
/// of step with one another. <see cref="IEquatable{T}"/> is stated explicitly as well as
/// synthesised, because the solution's <c>Entity&lt;TId&gt;</c> base type compares identities
/// through <see cref="EqualityComparer{T}.Default"/>: with the interface present that resolves to
/// the generic, non-boxing comparer, so identity comparison on an entity keyed by this type costs
/// no allocation.
/// </para>
/// </remarks>
/// <example>
/// Constructing the two boundary identities, and expressing absence:
/// <code>
/// PortalId hostLevel = new PortalId(-1);   // a real portal identity, not "no portal"
/// PortalId shipped   = new PortalId(0);    // the _default portal of a fresh installation
/// PortalId? unknown  = null;               // the only way to say "no portal"
///
/// int forPersistence = shipped.Value;      // 0, handed to the column unaltered
/// </code>
/// </example>
public readonly record struct PortalId : IEquatable<PortalId>
{
    /// <summary>
    /// Initialises a new <see cref="PortalId"/> around a raw portal identity read from, or destined
    /// for, the <c>dbo.Portals.PortalID</c> column.
    /// </summary>
    /// <param name="value">
    /// The portal identity, exactly as the column holds it. Every <see cref="int"/> is accepted,
    /// and that is a requirement rather than an oversight: -1 identifies both the host-level scope
    /// and the first row the identity seed produces, while 0 identifies the <c>_default</c> portal
    /// that a fresh installation ships with. This constructor therefore performs no range check, no
    /// clamp and no substitution, and must never be given one.
    /// </param>
    public PortalId(int value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the portal identity as the raw <see cref="int"/> that the <c>dbo.Portals.PortalID</c>
    /// column stores.
    /// </summary>
    /// <value>
    /// The wrapped identity, returned unaltered. This is the accessor the Infrastructure layer's
    /// entity configuration unwraps when mapping the value object onto its column, and the accessor
    /// a DTO uses where a legacy wire contract expects a bare integer.
    /// </value>
    public int Value { get; }

    /// <summary>
    /// Converts a raw portal identity into a <see cref="PortalId"/>.
    /// </summary>
    /// <param name="value">
    /// The portal identity to wrap. Every <see cref="int"/> is accepted, -1 and 0 included.
    /// </param>
    /// <returns>A <see cref="PortalId"/> carrying <paramref name="value"/> unchanged.</returns>
    /// <remarks>
    /// Explicit rather than implicit so that every crossing from a loosely typed integer into a
    /// portal identity is written down at the call site, where it can be reviewed.
    /// </remarks>
    public static explicit operator PortalId(int value) => new(value);

    /// <summary>
    /// Converts a <see cref="PortalId"/> back to its raw portal identity.
    /// </summary>
    /// <param name="portalId">The portal identity to unwrap.</param>
    /// <returns>The wrapped <see cref="int"/>, returned unaltered.</returns>
    /// <remarks>
    /// Equivalent to reading <see cref="Value"/>. The operator exists for the persistence and
    /// serialisation boundaries, where a conversion expression reads better than a projection.
    /// </remarks>
    public static explicit operator int(PortalId portalId) => portalId.Value;

    /// <summary>
    /// Returns the portal identity rendered as its decimal digits, so that -1 prints as <c>-1</c>
    /// and 0 prints as <c>0</c>.
    /// </summary>
    /// <returns>
    /// The wrapped identity formatted with <see cref="CultureInfo.InvariantCulture"/>, which keeps
    /// the sign character stable across cultures and so keeps log lines, diagnostics and test
    /// expectations comparable.
    /// </returns>
    /// <remarks>
    /// Overridden rather than left to the compiler for two reasons. The synthesised record form
    /// would wrap the number in the type name, which is noise in a log line; and, far more
    /// importantly, substituting a word such as "none", "unset" or "host" for -1 would reintroduce
    /// - in the one place a maintainer is most likely to be reading - precisely the conflation this
    /// type removes. A portal identity always prints as a number.
    /// </remarks>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
