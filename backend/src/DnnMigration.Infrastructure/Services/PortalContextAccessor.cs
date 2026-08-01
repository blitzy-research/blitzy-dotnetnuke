// =============================================================================================
// MIGRATION PROVENANCE
//
// Legacy inputs. Every one is read-only reference material; none is modified by this work.
//
//   Library/Components/Portal/PortalSettings.vb      1,397 lines. 49 property declarations, 42 of
//                                                   them read-write. Two-argument constructor at
//                                                   L548, private builder at L597, relational
//                                                   read at L608, parameterless constructor at
//                                                   L554.
//   Library/Components/Portal/PortalController.vb    L1209-L1211, the deleted globally reachable
//                                                   accessor.
//   Library/Components/Portal/PortalInfo.vb          L141-L218, the persisted portal columns and
//                                                   the two role-name projections.
//   Library/Components/Portal                       L37-L53 of the legacy alias-entity source,
//                                                   the three-property alias entity that a plain
//                                                   string replaces here.
//   Library/Components/Providers/Data/DataProvider.vb
//                                                   L31-L50, the reflection-resolved singleton
//                                                   that constructor injection replaces.
//   Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider
//                                                   L77 and L7123-L7125 the portal key seed and
//                                                   the shipped portal row, L115/L140/L221 the
//                                                   role, page and module key seeds,
//                                                   L4569-L4600 the legacy alias lookup.
//   Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb
//                                                   L164 and L170, the ambient portal-name read
//                                                   and the registration-mode gate.
//
// The six notes below record every deliberate divergence from legacy behaviour that this type
// embodies. MIGRATION_NOTES.md at the repository root is owned elsewhere and must carry the same
// six decisions; neither copy substitutes for the other, and neither may be dropped.
// =============================================================================================

using DnnMigration.Domain.Abstractions.Services;

namespace DnnMigration.Infrastructure.Services;

// MIGRATION: (1 of 6) Ambient global access is deleted rather than reproduced. The legacy entry
// point was a type-level function reachable from anywhere (PortalController.vb L1209-L1211) that
// read the ambient per-call items dictionary of the old web pipeline under an untyped string key
// and downcast the result to the composite type. All four traits - unrestricted reach, ambient
// lookup, untyped dictionary and cast - disappear here. One holder is built at the Api boundary
// immediately after alias resolution, is registered with a scoped lifetime, and reaches consumers
// only through constructor injection.

// MIGRATION: (2 of 6) Mutability and construction-time I/O are deleted. 42 of the legacy
// composite's 49 property declarations were read-write, among them a settable page member
// (PortalSettings.vb L398) and two settable per-call counters (L451, L459). Its two-argument
// constructor (L548) allocated an untyped collection and an empty page object and then called a
// private builder (L597) that issued a relational read while the object was still being built
// (L608), and a second, parameterless constructor (L554) produced a composite with every value
// unpopulated. This type has get-only members, one constructor, and performs no I/O whatsoever, so
// a half-populated tenant snapshot cannot be observed by anyone.

// MIGRATION: (3 of 6) Eight of forty-seven tenant settings survive, and the omissions are
// deliberate rather than oversights. (Of the 49 declarations, two are not settings at all: a
// computed path convenience at L143 and the token-subsystem member at L1388.) Persisted portal
// configuration is not a fact of one call: it lives as columns on the legacy Portals table, is
// materialised onto the Portal aggregate, and is projected for the wire by the Application layer.
// No key-value persistence model backs this type and no table exists to build one on - three
// negative findings establish that: no such member among the 269 abstract members of the legacy
// data abstraction, no such procedure among the 245 the legacy provider invokes, and no such table
// in any of the 88 schema scripts. The legacy procedure whose name suggests otherwise
// (01.00.00.SqlDataProvider L4569-L4600) is a join projection over portal, page and account rows,
// not a settings reader.

/// <summary>
/// Immutable holder for the portal (tenant) facts of the single inbound call being served.
/// </summary>
/// <remarks>
/// <para>
/// This type is a snapshot and nothing more. Every value it exposes was resolved before the object
/// existed, so it issues no query, resolves no alias, inspects no route, reads no principal,
/// evaluates no permission and infers nothing about the caller. Reads are synchronous precisely
/// because they touch memory and never I/O, which leaves the solution-wide rule for deferring
/// I/O-bound members with nothing to bite on here.
/// </para>
/// <para>
/// One instance belongs to one inbound call. It is populated at the Api boundary by the
/// alias-resolution middleware - the single component in the solution permitted to read the web
/// pipeline - and registered with a scoped lifetime by this layer's registration extension, so
/// consumers receive it by constructor injection and can neither replace nor mutate it. The type is
/// deliberately <see langword="internal"/> and <see langword="sealed"/>: it is an implementation
/// detail of this assembly, and <see cref="IPortalContext"/> is what the rest of the solution
/// depends upon.
/// </para>
/// <para>
/// Every value arrives through the one constructor. There is no parameterless constructor, no
/// settable member, no loader, no refresh path and no post-construction population step, because a
/// tenant snapshot that could be rewritten midway through handling a call would defeat the reason
/// for having one. A holder therefore either exists complete or does not exist: where the eight
/// values cannot all be resolved, the Api boundary fails the call rather than fabricating one.
/// </para>
/// </remarks>
internal sealed class PortalContextAccessor : IPortalContext
{
    // MIGRATION: (4 of 6) Negative one and zero are BOTH legitimate portal keys here, never a
    // marker for an absent tenant. The legacy Portals table declares its key as an auto-increment
    // column seeded at negative one and stepping by one (01.00.00.SqlDataProvider L77), and the
    // shipped portal row is inserted with an explicit key of zero and an administrator-role key of
    // zero (L7123-L7125); the role, page and module keys are seeded at zero as well (L115, L140,
    // L221). Meanwhile the legacy sentinel helper used negative one as its integer null and the
    // empty string as its absent text (Library/Components/Shared, lines 41-45 and 71-75). Every
    // integer below is therefore stored exactly as supplied: no clamping, no reinterpretation, no
    // non-positive value read as unpopulated, and no sentinel constant declared or ported. Absence
    // is not representable on this type at all, because resolution fails the inbound call before a
    // holder is ever built.

    /// <summary>
    /// Creates the tenant snapshot for one inbound call from the eight values that the Api boundary
    /// has already resolved.
    /// </summary>
    /// <param name="portalId">
    /// Numeric key of the resolved portal. Stored verbatim; read migration note 4 above before
    /// treating any value as absence.
    /// </param>
    /// <param name="portalName">Display name of the resolved portal.</param>
    /// <param name="portalAlias">
    /// The exact stored alias that this call resolved by, as a plain string.
    /// </param>
    /// <param name="administratorId">
    /// Numeric key of the account designated administrator of the resolved portal. Stored verbatim.
    /// </param>
    /// <param name="administratorRoleId">
    /// Numeric key of the role conferring portal administration rights. Stored verbatim, and zero is
    /// a real role.
    /// </param>
    /// <param name="administratorRoleName">
    /// Name of the role conferring portal administration rights, already joined by the caller
    /// because no such column exists.
    /// </param>
    /// <param name="registeredRoleId">
    /// Numeric key of the role granted to every signed-in member. Stored verbatim, and zero is a
    /// real role.
    /// </param>
    /// <param name="registeredRoleName">
    /// Name of the role granted to every signed-in member, already joined by the caller because no
    /// such column exists.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any of the four name arguments is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when any of the four name arguments is an empty string. All four are contract-
    /// guaranteed to be populated, so a blank one is a defect in the resolution flow: it is refused
    /// here instead of being trimmed, replaced, defaulted or carried onward as a blank tenant fact.
    /// </exception>
    public PortalContextAccessor(
        int portalId,
        string portalName,
        string portalAlias,
        int administratorId,
        int administratorRoleId,
        string administratorRoleName,
        int registeredRoleId,
        string registeredRoleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(portalName);
        ArgumentException.ThrowIfNullOrEmpty(portalAlias);
        ArgumentException.ThrowIfNullOrEmpty(administratorRoleName);
        ArgumentException.ThrowIfNullOrEmpty(registeredRoleName);

        PortalId = portalId;
        PortalName = portalName;
        PortalAlias = portalAlias;
        AdministratorId = administratorId;
        AdministratorRoleId = administratorRoleId;
        AdministratorRoleName = administratorRoleName;
        RegisteredRoleId = registeredRoleId;
        RegisteredRoleName = registeredRoleName;
    }

    /// <summary>
    /// Numeric key of the portal (tenant) that this call resolved to.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L119. Held exactly as the Api boundary supplied it;
    /// migration note 4 above governs every comparison a consumer might be tempted to write.
    /// </remarks>
    public int PortalId { get; }

    /// <summary>
    /// Display name of the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L127, which the legacy sign-in flow read ambiently
    /// while validating credentials (<c>Login.ascx.vb</c> L164). Never blank: the constructor
    /// refuses a blank value rather than storing one.
    /// </remarks>
    public string PortalName { get; }

    // MIGRATION: (5 of 6) The resolved alias is held as a plain string, and it is the stored alias
    // rather than whatever fragment a caller supplied. The legacy member (PortalSettings.vb L411)
    // exposed an alias entity object instead, and the legacy lookup selected the lowest portal key
    // whose alias column satisfied a substring containment predicate (01.00.00.SqlDataProvider
    // L4582), so a fragment of one tenant's alias could resolve to a different tenant - a latent
    // mis-resolution hazard. The Api boundary now performs an exact-match lookup and this type
    // simply keeps the string it was handed. That behavioural difference is deliberate and belongs
    // in MIGRATION_NOTES.md rather than being absorbed silently.

    /// <summary>
    /// The alias that this call was resolved by, as a plain string.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L411, which exposed an alias entity rather than a
    /// value. Always the exact stored alias; see migration note 5 immediately above.
    /// </remarks>
    public string PortalAlias { get; }

    /// <summary>
    /// Numeric key of the account designated administrator of the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L198. A portal-level authorisation fact about the
    /// tenant, never a statement about who is calling. Account keys are seeded at one, but migration
    /// note 4 still applies: no value here means absence.
    /// </remarks>
    public int AdministratorId { get; }

    /// <summary>
    /// Numeric key of the role that confers portal administration rights in the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L246. Role keys are seeded at zero and the shipped
    /// portal row carries zero here, so migration note 4 applies directly.
    /// </remarks>
    public int AdministratorRoleId { get; }

    /// <summary>
    /// Name of the role that confers portal administration rights in the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L254. Carried beside its key because the legacy
    /// authorisation checks compared role names, and behaviour is preserved rather than improved. It
    /// is a joined projection in every legacy script that produces it, never a stored column, so it
    /// is resolved before this type is built and never looked up here.
    /// </remarks>
    public string AdministratorRoleName { get; }

    /// <summary>
    /// Numeric key of the role granted to every signed-in member of the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L262. Role keys are seeded at zero here too, so
    /// migration note 4 applies directly.
    /// </remarks>
    public int RegisteredRoleId { get; }

    /// <summary>
    /// Name of the role granted to every signed-in member of the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L270. Carried beside its key for the same reason as
    /// the administrator role name, and likewise a joined projection rather than a stored column.
    /// </remarks>
    public string RegisteredRoleName { get; }

    // MIGRATION: (6 of 6) Excluded legacy state stays excluded. The mutable page member and the
    // untyped pre-generics page collection (PortalSettings.vb L398, L390), the four theming and
    // presentation-shell members (L419, L427, L435, L443), the visibility, mode and control-panel
    // members (L468, L483, L498, L520), the token-subsystem member (L1388), the two mutable
    // counters (L451, L459) and the host-level settings dictionary (L406) have no counterpart on
    // this type: theming, the presentation shell, personalisation and the token subsystem are all
    // excluded from this migration, the page key now arrives as a route value, pre-generics
    // collections are replaced rather than ported, and the host-level member is served by the
    // sibling host-level settings implementation in this folder. The persisted registration-mode
    // column (L174) is absent even though the legacy sign-in flow gated on it ambiently
    // (Login.ascx.vb L170): it is persisted configuration, so the authentication workflow loads the
    // Portal aggregate through its repository instead of letting a configuration column leak into a
    // call-scoped facts holder. Which caller is on the wire stays the concern of the separate caller
    // abstraction in the Application layer, never of this type.
}
