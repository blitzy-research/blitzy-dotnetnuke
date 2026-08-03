namespace DnnMigration.Domain.Abstractions.Services;

// =============================================================================================
// MIGRATION PROVENANCE
//
// Legacy inputs, all read-only and none of them modified by this work:
//
//   Library/Components/Portal/PortalSettings.vb                       1,397 lines; forty-seven
//                                                                    settings properties across 49
//                                                                    property declarations, 42 of
//                                                                    them read-write and 7
//                                                                    recomputed on every read
//   Library/Components/Portal/PortalController.vb                     1,632 lines
//   Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb     204 lines
//
// The seven notes below record every deliberate divergence from legacy behaviour that this contract
// embodies. They are mirrored by MIGRATION_NOTES.md at the repository root; neither copy is a
// substitute for the other, and neither may be dropped.
// =============================================================================================

// MIGRATION: (1 of 7) The type-level ambient accessor is deleted outright. The legacy entry point
// was PortalController.GetCurrentPortalSettings() at
// Library/Components/Portal/PortalController.vb L1209-L1211: a shared function callable from
// anywhere, which read the ambient ASP.NET per-call items dictionary under an untyped string key
// and downcast the result to the composite type. A globally reachable function, an ambient lookup,
// an untyped dictionary and a cast - all four traits disappear here. Consumers now receive this
// contract by constructor injection from a scoped registration that is populated exactly once per
// inbound call, by IPortalContextHolder - which is the only thing able to build a snapshot, since the
// implementing type is internal to the Infrastructure assembly.
//
// Two components ask the holder to resolve, and both do so through its one idempotent entry point:
// Api/Middleware/PortalAliasResolutionMiddleware for every request, and
// Api/Authorization/PortalAdministratorAuthorizationHandler before it decides, because the mandated
// pipeline order places authorisation ahead of that middleware and a portal-scoped policy must not
// assume a tenant it may be running before. Whichever asks first performs the resolution; the other
// observes the identical outcome. Those two are the ONLY places in the solution that read the ASP.NET
// pipeline for tenant purposes - the middleware takes the host name from the request, the handler takes
// it from the request that the authorisation framework hands it as the resource - and both convert it
// immediately into a plain string, so no pipeline type crosses into this layer or into Infrastructure.

// MIGRATION: (2 of 7) Mutability is removed. 42 of the legacy composite's property declarations
// were read-write, among them a settable active-page member (PortalSettings.vb L398) and two
// settable per-call counters (L451 and L459); the remaining 7 were recomputed on every read from
// ambient state, so even they were not stable within a call. Worse, the two-argument constructor at
// L548 allocated an untyped collection, allocated an empty page object, and then invoked a private
// builder at L597 that performed a database read during construction (L608). This contract is
// get-only, declares no constructor, holds no collection and performs no I/O.

// MIGRATION: (3 of 7) Eight of forty-seven properties are carried forward, and the omissions are
// deliberate rather than oversights. Persisted portal configuration is not part of this contract: it
// lives as columns on Domain/Entities/Portal.cs bound to the legacy Portals table, is loaded through
// IPortalRepository, and is projected for the wire by Application/Dtos/Portal/PortalSettingsDto.
// There is also no portal-setting key-value entity and no such table to model. Three negative
// findings and one positive finding establish that: no such member among the 269 abstract members of
// DataProvider.vb; no such stored procedure among the 245 invoked by SqlDataProvider.vb; no such
// table in any of the 88 schema scripts, which declare only module-level, host-level, tab-module and
// schedule-item settings tables plus one transient staging table; and, positively, the accessor
// deleted in note 1 demonstrably yielded a per-call ambient composite rather than a persisted
// aggregate.

// MIGRATION: (4 of 7) Where the excluded legacy members went. The host-level settings dictionary
// (PortalSettings.vb L406, which merely delegated to a shared globals module) is served by the
// sibling host-level settings contract in this folder. The four skin and container members (L419,
// L427, L435, L443) have no counterpart at all, because theming and presentation shells are excluded
// from this migration. Content visibility (L468), control-panel visibility (L483), user mode (L498)
// and control-panel security (L520) each derived from the ASP.NET pipeline, from personalisation, or
// from Web Forms control-panel state, none of which survives. The token-access member (L1388)
// belonged to the token-replacement subsystem, also excluded. The mutable active-page member (L398)
// is gone because the page key now arrives as a route parameter, and the untyped desktop-pages
// collection (L390) is gone because pre-generics collections are replaced by generic ones rather
// than ported. Finally, the portal registration-mode column (L174) is deliberately absent even
// though the legacy login flow read it ambiently (Login.ascx.vb L167-L185, the gate at L170): it is
// persisted configuration, so Application/Services/AuthService.cs loads the Portal aggregate through
// IPortalRepository instead of letting a configuration column leak into a call-scoped facts
// contract. Which caller is on the wire stays the concern of
// Application/Abstractions/ICurrentUser.cs, never of this one.

// MIGRATION: (5 of 7) Absence is representable for the administrator and the two portal roles,
// and it is represented by the ABSENCE OF A VALUE rather than by a sentinel. Three of the columns
// behind this contract are declared nullable - AdministratorId at
// Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider L86, AdministratorRoleId
// at L91 and RegisteredRoleId at L92, the latter two reaffirmed by the table rebuild at
// 01.00.05.SqlDataProvider L1379-L1380 - and Domain/Entities/Portal.cs models all three as int?.
// This does not contradict note 6; it is note 6 carried to its conclusion. Note 6 establishes that
// minus one and zero are legitimate keys, which is exactly why NO integer is free to mean "unset",
// and therefore why these three members are nullable rather than carrying a magic value. Coercing
// the columns' nulls onto integers would assert that some real account administers the portal and
// that some real role confers administration rights; because these are authorisation facts, that
// assertion is a privilege defect rather than a rounding error. The two role NAMES are nullable for
// the same reason and are never the empty string: the legacy sentinel helper used empty text as its
// absent value (Library/Components/Shared/Null.vb L71-L75) while the legacy checks compared role
// names for EQUALITY (Library/Components/Security/PortalSecurity.vb L519,
// Library/Components/Modules/PortalModuleBase.vb L121), so a blank name would be a value capable of
// matching. Note 4 continues to govern PortalId itself, which stays non-nullable: a call that has
// reached a consumer has resolved to exactly one tenant, so absence never arises there.

/// <summary>
/// Immutable portal (tenant) facts for the single inbound call being served.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one object implementing this contract exists per inbound call, and it is complete from
/// the moment it exists. Because nothing below is settable, the container cannot be handed a plain
/// contract-to-implementation mapping and left to fill the object in afterwards; what supplies it is
/// a call-scoped factory delegate contributed at the composition root and evaluated inside the scope
/// of the call it serves, with <c>Api/Middleware/PortalAliasResolutionMiddleware</c> settling the
/// tenant facts before the first consumer resolves the contract. Consumers see it only through
/// constructor injection. No member may ever be made settable: the entire purpose of this contract
/// is that the tenant facts of a call cannot be rewritten halfway through handling it.
/// </para>
/// <para>
/// That bridge IS in place, and the ordering invariant it rests on is what must be preserved. Three
/// pieces cooperate. <c>Infrastructure/DependencyInjection.cs</c> registers
/// <c>IPortalContextHolder</c> and <c>IPortalContext</c> as two scoped registrations, the second
/// projecting from the first (L277 and L280). <c>Infrastructure/Services/PortalContextAccessor</c> is
/// the <see langword="internal"/> type that implements this contract over the holder's settled
/// snapshot, which is why no other assembly can construct one. And the holder is asked to resolve by
/// whichever of two components reaches it first - <c>Api/Middleware/PortalAliasResolutionMiddleware</c>
/// on every request, or <c>Api/Authorization/PortalAdministratorAuthorizationHandler</c> before it
/// decides, since the mandated pipeline order can put authorisation ahead of that middleware. The
/// holder's entry point is idempotent, so whichever asks first performs the resolution and the other
/// observes the identical outcome.
/// </para>
/// <para>
/// THE INVARIANT: the snapshot is settled before the first consumer resolves this contract, and it is
/// never mutated afterwards. No member may be made settable, no value-free construction path may be
/// added, and no fill-in-afterwards step may be introduced - each would make container activation
/// appear to succeed while handing a consumer a blank or rewritable tenant snapshot, which surfaces as
/// a wrong authorisation answer rather than as an obvious failure. A call whose tenant facts cannot be
/// settled is failed at the boundary instead: this contract has no encoding for an unresolved tenant,
/// and it must never be given one.
/// </para>
/// <para>
/// It replaces the shared ambient accessor <c>PortalController.GetCurrentPortalSettings()</c> at
/// <c>Library/Components/Portal/PortalController.vb</c> L1209-L1211, and it is deliberately a small
/// subset of the 1,397-line, forty-seven-property <c>PortalSettings</c> composite it descends from.
/// Persisted portal configuration is not here: that lives as columns on the <c>Portal</c> entity, is
/// loaded through <c>IPortalRepository</c>, and is projected for the wire by
/// <c>PortalSettingsDto</c>. Nor is there any portal-setting key-value entity or table to model; see
/// migration note 3 above for the evidence.
/// </para>
/// <para>
/// Nothing from the ASP.NET pipeline is exposed, and neither is anything about the caller's own
/// principal: who is calling is answered by <c>Application/Abstractions/ICurrentUser.cs</c>, not by
/// this contract. No page-selection member, no theming member, no presentation-shell member and no
/// key-value indexer appears either.
/// </para>
/// <para>
/// Every member is a synchronously readable get-only property, and that shape is intentional rather
/// than an oversight. It rests on the ordering obligation stated above - the values are settled
/// before the first consumer resolves the contract - so reading one is a memory read and never I/O,
/// which leaves the solution-wide rule for deferring I/O-bound members with nothing to bite on here.
/// Ordering is consequently the whole of the risk borne by the two components that must supply this
/// contract. No member should be widened into a deferred-completion shape, and no loader, refresh or
/// persistence member belongs on this contract: it is supplied complete, never self-populating.
/// </para>
/// </remarks>
public interface IPortalContext
{
    // MIGRATION: (6 of 7) Minus one and zero are LEGITIMATE portal keys here, never an absence
    // marker. The legacy Portals table declares its key as an auto-increment column seeded at minus
    // one and stepping by one
    // (Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider L77), so minus one is
    // the seed and first generated value, while the shipped default portal row is inserted explicitly
    // with key zero (L7125) - both are real keys - and the legacy sentinel module simultaneously used
    // minus one as its integer null (Library/Components/Shared/Null.vb L41-L45). The Roles, Tabs and Modules keys
    // are seeded at zero in the same script (L115, L140, L221), and that same sentinel module used
    // the empty string rather than null for absent text (L71-L75). A translation that reads a
    // reserved value as unset - minus one, zero, or blank text - silently flips legacy branch
    // outcomes and breaks tenant isolation. Consumers MUST NOT test any value below for
    // non-positivity as an absence check, and no sentinel constant is declared or ported here.
    //
    // Where absence genuinely exists it is expressed by the member being nullable and by nothing
    // else. That is why three of the eight members below are non-nullable and five are nullable,
    // and the split is measured rather than chosen. The portal key, the portal name and the alias
    // are populated on every call that reaches a consumer, because a call resolves to exactly one
    // portal or fails before any consumer is handed an object. The administrator key and the two
    // role keys are a different matter: all three terminal columns are declared nullable -
    // AdministratorId at 01.00.00.SqlDataProvider L86, AdministratorRoleId at L91 and
    // RegisteredRoleId at L92, each re-declared identically through the table rebuild at
    // 01.00.05.SqlDataProvider L1374, L1379 and L1380, with no column alteration touching any of the
    // three anywhere in the 88 schema scripts. A portal row that exists but has not been fully
    // provisioned therefore holds no value in those columns, and it is a legitimate row that alias
    // resolution has to be able to describe rather than reject. The two role names follow their
    // keys: the terminal portal view projects the two role keys and no role name at all
    // (04.05.00.SqlDataProvider L1530), and the terminal portal read selects from that view
    // (04.04.00.SqlDataProvider L199), so a name is obtained by looking up the role its key names -
    // and where there is no key there is no role to look up and therefore no name. Blank text is
    // deliberately NOT used to stand in for that, because blank text is precisely the legacy
    // sentinel this migration refuses to carry forward. Nullability here consequently reinforces the
    // prohibition above instead of weakening it: the absent case has its own representation, so no
    // reserved value has to carry that meaning, and every value that IS present is a real one
    // whatever its sign.

    /// <summary>
    /// Numeric key of the portal (tenant) that this call resolved to.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L119. Non-nullable, because a call that has reached a
    /// consumer has resolved to exactly one portal. Read migration note 6 immediately above before
    /// writing any comparison against this value.
    /// </remarks>
    int PortalId { get; }

    /// <summary>
    /// Display name of the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L127, read ambiently by the legacy login flow at
    /// <c>Login.ascx.vb</c> L164 while validating credentials. Always populated; an empty value would
    /// be a name of zero length, not an absence marker.
    /// </remarks>
    string PortalName { get; }

    // MIGRATION: (7 of 7) The resolved alias is exposed as a plain string. The legacy member at
    // PortalSettings.vb L411 exposed an alias entity object instead.
    //
    // The matching story has two stages and only the second one contains the divergence, so both are
    // stated rather than the first being reported as though it were current. STAGE ONE, superseded:
    // the earliest resolution procedure selected the lowest portal key whose alias column satisfied a
    // wildcard containment predicate (01.00.00.SqlDataProvider L4569-L4600, the predicate itself at
    // L4582), so one tenant's alias could be matched by a fragment of another tenant's. That
    // procedure was dropped outright at 02.02.00.SqlDataProvider L267 and the Portals.PortalAlias
    // column it read was dropped at 02.02.02.SqlDataProvider L3925-L3926, so neither survives.
    // STAGE TWO, terminal: once aliases lived in the PortalAlias table the lookups compared whole
    // values - GetPortalAlias at 02.02.02.SqlDataProvider L3846-L3856 and GetPortalByAlias at
    // L3930-L3938 both write HTTPAlias = @HTTPAlias, and no later script recreates either.
    //
    // EXACT MATCHING IS THEREFORE PRESERVED LEGACY BEHAVIOUR, NOT A CHANGE TO IT. The genuine
    // divergence is narrower: the terminal lookup still collapsed multiple candidates with
    // min(PortalId), and the replacement refuses ambiguity instead of silently serving whichever
    // portal was created first. That difference is deliberate and is recorded in MIGRATION_NOTES.md
    // rather than absorbed silently.

    /// <summary>
    /// The alias that this call was resolved by, as a plain string.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L411, which exposed an alias entity object rather
    /// than a value. Always populated, and always the exact stored alias rather than whatever
    /// fragment the caller supplied; see migration note 7 immediately above.
    /// </remarks>
    string PortalAlias { get; }

    /// <summary>
    /// Numeric key of the account designated administrator of the resolved portal, or
    /// <see langword="null"/> when the portal designates none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy origin: <c>PortalSettings.vb</c> L198. A portal-level authorisation fact about the
    /// tenant, never a statement about who is calling.
    /// </para>
    /// <para>
    /// Nullable because the column is: <c>[AdministratorId] [int] NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> L86, modelled
    /// as <c>int?</c> on <c>Domain/Entities/Portal.cs</c>. This is migration note 6 applied rather
    /// than waived: precisely BECAUSE minus one and zero are legitimate keys, no integer is free to
    /// mean "absent", so absence has to be carried by the absence of a value. Coercing the column's
    /// null onto any integer would not merely lose information - it would manufacture a real,
    /// valid-looking administrator key, and this member is an authorisation fact.
    /// </para>
    /// <para>
    /// A consumer must therefore branch on whether a value is present, and must never test the
    /// value itself for non-positivity as an absence check.
    /// </para>
    /// </remarks>
    int? AdministratorId { get; }

    /// <summary>
    /// Numeric key of the role that confers portal administration rights in the resolved portal, or
    /// <see langword="null"/> when the portal names no such role.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L246. Nullable for the same reason as
    /// <see cref="AdministratorId"/> and with the same force: the column is
    /// <c>[AdministratorRoleId] [int] NULL</c> at <c>01.00.00.SqlDataProvider</c> L91 - reaffirmed as
    /// <c>AdministratorRoleId int NULL</c> by the table rebuild at <c>01.00.05.SqlDataProvider</c>
    /// L1379 - and role keys are seeded at ZERO, so zero denotes a real role. Coercing a null onto
    /// zero would name an existing role as the administrator role of a portal that names none.
    /// </remarks>
    int? AdministratorRoleId { get; }

    /// <summary>
    /// Name of the role that confers portal administration rights in the resolved portal, or
    /// <see langword="null"/> when the portal has no such role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy origin: <c>PortalSettings.vb</c> L254. Carried alongside its key because the legacy
    /// authorisation checks compared role names rather than keys - for example
    /// <c>PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)</c> at
    /// <c>Library/Components/Security/PortalSecurity.vb</c> L519 and
    /// <c>Library/Components/Modules/PortalModuleBase.vb</c> L121 - and behaviour is preserved rather
    /// than improved.
    /// </para>
    /// <para>
    /// Present exactly when <see cref="AdministratorRoleId"/> is, and <see langword="null"/>
    /// otherwise: the name is read from the <c>Roles</c> table using that key, so a portal naming no
    /// administrator role has no name to carry either. It is deliberately NOT the empty string. The
    /// legacy sentinel module used empty text as its absent value
    /// (<c>Library/Components/Shared/Null.vb</c> L71-L75), and the legacy checks cited above compared
    /// role names for equality - so an empty name would be a value that could match, which is the
    /// one outcome an authorisation fact must not permit.
    /// </para>
    /// </remarks>
    string? AdministratorRoleName { get; }

    /// <summary>
    /// Numeric key of the role granted to every signed-in member of the resolved portal, or
    /// <see langword="null"/> when the portal has no such role.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L262. Nullable on the same evidence:
    /// <c>[RegisteredRoleId] [int] NULL</c> at <c>01.00.00.SqlDataProvider</c> L92, reaffirmed as
    /// <c>RegisteredRoleId int NULL</c> by the rebuild at <c>01.00.05.SqlDataProvider</c> L1380. Role
    /// keys are seeded at zero here too, so zero is a real role and cannot stand in for absence.
    /// </remarks>
    int? RegisteredRoleId { get; }

    /// <summary>
    /// Name of the role granted to every signed-in member of the resolved portal, or
    /// <see langword="null"/> when the portal has no such role.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L270. Carried alongside its key for the same reason as
    /// the administrator role name: the legacy checks compared names. Present exactly when
    /// <see cref="RegisteredRoleId"/> is, <see langword="null"/> otherwise, and never the empty
    /// string - for the reason given on <see cref="AdministratorRoleName"/>.
    /// </remarks>
    string? RegisteredRoleName { get; }
}
