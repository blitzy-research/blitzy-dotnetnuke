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
// The seven notes below record every deliberate divergence from legacy behaviour that this type
// embodies. MIGRATION_NOTES.md at the repository root is owned elsewhere and must carry the same
// six decisions; neither copy substitutes for the other, and neither may be dropped.

using DnnMigration.Domain.Abstractions.Services;

namespace DnnMigration.Infrastructure.Services;

// MIGRATION: (1 of 7) Ambient global access is deleted rather than reproduced. The legacy entry
// point was a type-level function reachable from anywhere (PortalController.vb L1209-L1211) that
// read the ambient per-call items dictionary of the old web pipeline under an untyped string key
// and downcast the result to the composite type. All four traits - unrestricted reach, ambient
// lookup, untyped dictionary and cast - disappear here.
//
// What replaces them, named exactly, because a comment that describes a population path in the
// abstract is how a type ends up with no population path at all. Exactly one snapshot is built per
// inbound call, and it is built HERE IN THIS ASSEMBLY by PortalContextHolder, which is the only
// thing that can build one: this type is internal, so no caller outside this assembly is able to
// construct a snapshot even incorrectly. The holder is registered with a scoped lifetime, and
// IPortalContext is registered to project from it, so consumers receive the snapshot by constructor
// injection and can neither replace nor mutate it. Resolution itself is triggered by
// Api/Middleware/PortalAliasResolutionMiddleware for every request and, because the mandated
// pipeline order places authorisation ahead of that middleware, also by
// Api/Authorization/PortalAdministratorAuthorizationHandler before it decides. Both reach the same
// idempotent entry point on the holder, so the trigger is doubled but the resolution is not.

// MIGRATION: (2 of 7) Mutability and construction-time I/O are deleted. 42 of the legacy
// composite's 49 property declarations were read-write, among them a settable page member
// (PortalSettings.vb L398) and two settable per-call counters (L451, L459). Its two-argument
// constructor (L548) allocated an untyped collection and an empty page object and then called a
// private builder (L597) that issued a relational read while the object was still being built
// (L608), and a second, parameterless constructor (L554) produced a composite with every value
// unpopulated. This type has get-only members, one constructor, and performs no I/O whatsoever, so
// a half-populated tenant snapshot cannot be observed by anyone.

// MIGRATION: (3 of 7) Eight of forty-seven tenant settings survive, and the omissions are
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
/// One instance belongs to one inbound call, and <c>PortalContextHolder</c> in this same namespace is
/// what builds it - after resolving the alias through <c>IPortalAliasRepository</c> and after checking
/// that all eight facts are actually present. Two components trigger that resolution, both through the
/// holder's single idempotent entry point: <c>PortalAliasResolutionMiddleware</c> for every request, and
/// <c>PortalAdministratorAuthorizationHandler</c> before it decides, because the mandated pipeline order
/// places authorisation ahead of the middleware. The holder is registered with a scoped lifetime and
/// <see cref="IPortalContext"/> is registered to project from it, so consumers receive the snapshot by
/// constructor injection and can neither replace nor mutate it. The type is deliberately
/// <see langword="internal"/> and <see langword="sealed"/>: it is an implementation detail of this
/// assembly, <see cref="IPortalContext"/> is what the rest of the solution depends upon, and the
/// inaccessibility is what makes "only the holder builds one" a compile-time fact rather than a
/// convention.
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
    // MIGRATION: (4 of 7) Negative one and zero are BOTH legitimate portal keys here, never a
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
    /// <param name="portalAliasId">
    /// Surrogate key of the <c>dbo.PortalAlias</c> row this call resolved through. Stored verbatim;
    /// see <see cref="PortalAliasId"/> for why the KEY rather than the host name is what an alias
    /// administration screen must compare against.
    /// </param>
    /// <param name="administratorId">
    /// Numeric key of the account designated administrator, or <see langword="null"/> when the
    /// portal designates none. Stored verbatim; never coerced onto an integer.
    /// </param>
    /// <param name="administratorRoleId">
    /// Numeric key of the role conferring portal administration rights, or <see langword="null"/>
    /// when the portal names no such role. Stored verbatim, and zero is a real role.
    /// </param>
    /// <param name="administratorRoleName">
    /// Name of the role conferring portal administration rights, already joined by the caller
    /// because no such column exists. Supply it exactly when
    /// <paramref name="administratorRoleId"/> is supplied.
    /// </param>
    /// <param name="registeredRoleId">
    /// Numeric key of the role granted to every signed-in member, or <see langword="null"/> when
    /// the portal names no such role. Stored verbatim, and zero is a real role.
    /// </param>
    /// <param name="registeredRoleName">
    /// Name of the role granted to every signed-in member, already joined by the caller because no
    /// such column exists. Supply it exactly when <paramref name="registeredRoleId"/> is supplied.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="portalName"/> or <paramref name="portalAlias"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="portalName"/> or <paramref name="portalAlias"/> is an empty
    /// string - both are contract-guaranteed to be populated, so a blank one is a defect in the
    /// resolution flow and is refused here rather than trimmed, defaulted or carried onward as a
    /// blank tenant fact - and thrown when a role key and its name disagree about being present, for
    /// the reason given in migration note 7 below.
    /// </exception>
    public PortalContextAccessor(
        int portalId,
        string portalName,
        string portalAlias,
        int portalAliasId,
        int? administratorId,
        int? administratorRoleId,
        string? administratorRoleName,
        int? registeredRoleId,
        string? registeredRoleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(portalName);
        ArgumentException.ThrowIfNullOrEmpty(portalAlias);

        ThrowIfRolePairIncoherent(
            administratorRoleId,
            administratorRoleName,
            nameof(administratorRoleId),
            nameof(administratorRoleName));

        ThrowIfRolePairIncoherent(
            registeredRoleId,
            registeredRoleName,
            nameof(registeredRoleId),
            nameof(registeredRoleName));

        PortalId = portalId;
        PortalName = portalName;
        PortalAlias = portalAlias;
        PortalAliasId = portalAliasId;
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

    // MIGRATION: (5 of 6) The resolved alias is held as a plain string, and it is the STORED alias
    // rather than whatever the caller supplied. The legacy member (PortalSettings.vb L411) exposed an
    // alias entity object instead; a string is kept because nothing here needs the entity.
    //
    // The alias-matching story has two stages and only the second one is the divergence, so both are
    // stated rather than the first being reported as though it were current. STAGE ONE, superseded:
    // the earliest lookup selected the lowest portal key whose alias column satisfied a substring
    // containment predicate (01.00.00.SqlDataProvider L4582), so a fragment of one tenant's alias
    // could resolve to a different tenant, and because the value was interpolated into the pattern
    // the wildcards were caller-controlled. That procedure was dropped outright at 02.02.00 L267 and
    // the Portals.PortalAlias column it read was dropped at 02.02.02 L3925-L3926, so neither
    // survives. STAGE TWO, terminal: once aliases lived in the PortalAlias table the lookups compared
    // whole values - GetPortalAlias at 02.02.02 L3846-L3856 and GetPortalByAlias at L3930-L3938 both
    // write HTTPAlias = @HTTPAlias, and no later script recreates either.
    //
    // EXACT MATCHING IS THEREFORE PRESERVED LEGACY BEHAVIOUR, NOT A CHANGE TO IT. The genuine
    // divergence is narrower and is what belongs in MIGRATION_NOTES.md: the terminal lookup still
    // collapsed multiple candidates with min(PortalId), and the replacement refuses ambiguity instead
    // of silently serving whichever portal was created first.

    /// <summary>
    /// The alias that this call was resolved by, as a plain string.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L411, which exposed an alias entity rather than a
    /// value. Always the exact stored alias; see migration note 5 immediately above.
    /// </remarks>
    public string PortalAlias { get; }

    /// <summary>
    /// Surrogate key of the <c>dbo.PortalAlias</c> row that this call was resolved by.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.PortalAlias.PortalAliasID</c>, the single member the legacy
    /// alias administration screen read from the resolved alias entity — <c>IsNotCurrent</c> at
    /// <c>Website/admin/Portal/PortalAlias.ascx.vb</c> L51 to L60 compared each grid row's key
    /// against it and hid the edit affordance on a match. Held exactly as the Api boundary supplied
    /// it; migration note 4 above governs any comparison, which must test equality and never a
    /// magnitude.
    /// </remarks>
    public int PortalAliasId { get; }

    /// <summary>
    /// Numeric key of the account designated administrator of the resolved portal, or
    /// <see langword="null"/> when the portal designates none.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L198. A portal-level authorisation fact about the
    /// tenant, never a statement about who is calling. Nullable because the column is -
    /// <c>[AdministratorId] [int] NULL</c> at <c>01.00.00.SqlDataProvider</c> L86 - and held exactly
    /// as supplied; see migration note 7 below for why no integer may stand in for absence.
    /// </remarks>
    public int? AdministratorId { get; }

    /// <summary>
    /// Numeric key of the role that confers portal administration rights in the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L246. Nullable because the column is -
    /// <c>[AdministratorRoleId] [int] NULL</c> at <c>01.00.00.SqlDataProvider</c> L91, reaffirmed by
    /// the rebuild at <c>01.00.05.SqlDataProvider</c> L1379. Role keys are seeded at zero and the
    /// shipped portal row carries zero here, so zero is a real role rather than an absence marker.
    /// </remarks>
    public int? AdministratorRoleId { get; }

    /// <summary>
    /// Name of the role that confers portal administration rights in the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L254. Carried beside its key because the legacy
    /// authorisation checks compared role names, and behaviour is preserved rather than improved. It
    /// is a joined projection in every legacy script that produces it, never a stored column, so it
    /// is resolved before this type is built and never looked up here. Present exactly when
    /// <see cref="AdministratorRoleId"/> is, and never blank when present.
    /// </remarks>
    public string? AdministratorRoleName { get; }

    /// <summary>
    /// Numeric key of the role granted to every signed-in member of the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L262. Nullable because the column is -
    /// <c>[RegisteredRoleId] [int] NULL</c> at <c>01.00.00.SqlDataProvider</c> L92, reaffirmed by the
    /// rebuild at <c>01.00.05.SqlDataProvider</c> L1380. Role keys are seeded at zero here too.
    /// </remarks>
    public int? RegisteredRoleId { get; }

    /// <summary>
    /// Name of the role granted to every signed-in member of the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L270. Carried beside its key for the same reason as
    /// the administrator role name, and likewise a joined projection rather than a stored column.
    /// Present exactly when <see cref="RegisteredRoleId"/> is, and never blank when present.
    /// </remarks>
    public string? RegisteredRoleName { get; }

    // MIGRATION: (6 of 7) Excluded legacy state stays excluded. The mutable page member and the
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

    // MIGRATION: (7 of 7) Absence of an administrator, or of a portal role, is carried by the
    // ABSENCE OF A VALUE and by nothing else. Three of the columns behind this type are declared
    // nullable - AdministratorId at 01.00.00.SqlDataProvider L86, AdministratorRoleId at L91 and
    // RegisteredRoleId at L92, the latter two reaffirmed by the table rebuild at
    // 01.00.05.SqlDataProvider L1379-L1380 - so a resolved portal genuinely may designate no
    // administrator and name no role. This is migration note 4 carried to its conclusion rather
    // than an exception to it: BECAUSE negative one and zero are legitimate keys, no integer is
    // free to mean "unset", and coercing a null column onto one would not lose information quietly,
    // it would assert that some real account administers the portal or that some real role confers
    // administration rights. These are authorisation facts, so that assertion is a privilege
    // defect. The empty string is refused for the same reason on the two names: the legacy sentinel
    // helper used empty text as its absent value (Library/Components/Shared lines 71-75) while the
    // legacy checks compared role names for EQUALITY (PortalSecurity.vb L519,
    // PortalModuleBase.vb L121), so a blank name would be a value capable of matching.
    //
    // A key and its name must therefore agree about being present, and the guard below refuses a
    // pair that disagrees instead of repairing it. A name without a key, or a key without a name,
    // means the resolution flow that built this holder is defective - the name is a joined
    // projection of the key, so one cannot legitimately exist without the other - and a holder
    // that silently absorbed the mismatch would hand every downstream authorisation check a fact
    // that is half true.

    /// <summary>
    /// Refuses a role key and role name that disagree about being present, or a name that is
    /// present but blank.
    /// </summary>
    /// <param name="roleId">The role key supplied for the pair, or <see langword="null"/>.</param>
    /// <param name="roleName">The role name supplied for the pair, or <see langword="null"/>.</param>
    /// <param name="roleIdParameterName">Parameter name of the key, for the thrown message.</param>
    /// <param name="roleNameParameterName">Parameter name of the name, for the thrown message.</param>
    /// <exception cref="ArgumentException">
    /// The pair disagrees about being present, or the name is present and blank.
    /// </exception>
    /// <remarks>
    /// Deliberately reports which pair is inconsistent and in which direction, and nothing more: no
    /// message here reproduces a portal name, an alias, an account key or a role key, because this
    /// type's own migration notes make it a holder of authorisation facts rather than a diagnostic
    /// surface. Whitespace counts as blank, so a name of spaces is refused exactly as an empty one
    /// is; a role whose name is whitespace could still match a whitespace comparison.
    /// </remarks>
    private static void ThrowIfRolePairIncoherent(
        int? roleId,
        string? roleName,
        string roleIdParameterName,
        string roleNameParameterName)
    {
        if (roleId.HasValue && string.IsNullOrWhiteSpace(roleName))
        {
            throw new ArgumentException(
                $"{roleIdParameterName} was supplied without a populated {roleNameParameterName}. "
                + "The name is a joined projection of the key, so one cannot exist without the "
                + "other, and a blank name would be a value capable of matching a role-name "
                + "comparison.",
                roleNameParameterName);
        }

        if (!roleId.HasValue && roleName is not null)
        {
            throw new ArgumentException(
                $"{roleNameParameterName} was supplied without {roleIdParameterName}. A portal that "
                + "names no role has no name to carry either, so the pair must be absent together.",
                roleNameParameterName);
        }
    }
}
