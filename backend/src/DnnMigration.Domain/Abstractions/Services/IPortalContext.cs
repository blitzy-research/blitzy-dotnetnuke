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
// The six notes below record every deliberate divergence from legacy behaviour that this contract
// embodies. They are mirrored by MIGRATION_NOTES.md at the repository root; neither copy is a
// substitute for the other, and neither may be dropped.
// =============================================================================================

// MIGRATION: (1 of 6) The type-level ambient accessor is deleted outright. The legacy entry point
// was PortalController.GetCurrentPortalSettings() at
// Library/Components/Portal/PortalController.vb L1209-L1211: a shared function callable from
// anywhere, which read the ambient ASP.NET per-call items dictionary under an untyped string key
// and downcast the result to the composite type. A globally reachable function, an ambient lookup,
// an untyped dictionary and a cast - all four traits disappear here. Consumers now receive this
// contract by constructor injection from a scoped registration that
// Api/Middleware/PortalAliasResolutionMiddleware populates exactly once per inbound call, and that
// middleware is the single place in the solution permitted to reach into the ASP.NET pipeline.

// MIGRATION: (2 of 6) Mutability is removed. 42 of the legacy composite's property declarations
// were read-write, among them a settable active-page member (PortalSettings.vb L398) and two
// settable per-call counters (L451 and L459); the remaining 7 were recomputed on every read from
// ambient state, so even they were not stable within a call. Worse, the two-argument constructor at
// L548 allocated an untyped collection, allocated an empty page object, and then invoked a private
// builder at L597 that performed a database read during construction (L608). This contract is
// get-only, declares no constructor, holds no collection and performs no I/O.

// MIGRATION: (3 of 6) Eight of forty-seven properties are carried forward, and the omissions are
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

// MIGRATION: (6 of 6) Where the excluded legacy members went. The host-level settings dictionary
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

/// <summary>
/// Immutable portal (tenant) facts for the single inbound call being served.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one object implementing this contract exists per inbound call. It is populated once by
/// <c>Api/Middleware/PortalAliasResolutionMiddleware</c>, registered with a scoped lifetime, and
/// consumed everywhere else through constructor injection. Nothing below is settable, and no member
/// may ever be made settable: the entire purpose of this contract is that the tenant facts of a
/// call cannot be rewritten halfway through handling it.
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
/// than an oversight. The middleware resolves these values before any consumer runs, so reading one
/// is a memory read and never I/O, which leaves the solution-wide rule for deferring I/O-bound
/// members with nothing to bite on here. No member should be widened into a deferred-completion
/// shape, and no loader, refresh or persistence member belongs on this contract: it is populated,
/// never self-populating.
/// </para>
/// </remarks>
public interface IPortalContext
{
    // MIGRATION: (4 of 6) Minus one and zero are LEGITIMATE portal keys here, never an absence
    // marker. The legacy Portals table declares its key as an auto-increment column seeded at minus
    // one and stepping by one
    // (Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider L77), so the first
    // real portal is keyed zero, while the legacy sentinel module simultaneously used minus one as
    // its integer null (Library/Components/Shared/Null.vb L41-L45). The Roles, Tabs and Modules keys
    // are seeded at zero in the same script (L115, L140, L221), and that same sentinel module used
    // the empty string rather than null for absent text (L71-L75). A translation that maps these to
    // nullable types, or that treats a non-positive value as unset, silently flips legacy branch
    // outcomes and breaks tenant isolation. Consumers MUST NOT test this value for non-positivity as
    // an absence check, and no sentinel constant is declared or ported here. Absence is not
    // representable on this contract at all: resolution fails the inbound call before any consumer
    // is handed an object.

    /// <summary>
    /// Numeric key of the portal (tenant) that this call resolved to.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L119. Non-nullable, because a call that has reached a
    /// consumer has resolved to exactly one portal. Read migration note 4 immediately above before
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

    // MIGRATION: (5 of 6) The resolved alias is exposed as a plain string. The legacy member at
    // PortalSettings.vb L411 exposed an alias entity object instead, and the legacy resolution
    // procedure selected the lowest portal key whose alias column satisfied a wildcard containment
    // predicate (01.00.00.SqlDataProvider L4569-L4600, the predicate itself at L4582) - so one
    // tenant's alias could be matched by a fragment of another tenant's, a latent mis-resolution
    // hazard. The replacement middleware performs an exact-match lookup. That behavioural difference
    // is deliberate and is recorded in MIGRATION_NOTES.md rather than absorbed silently.

    /// <summary>
    /// The alias that this call was resolved by, as a plain string.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L411, which exposed an alias entity object rather
    /// than a value. Always populated, and always the exact stored alias rather than whatever
    /// fragment the caller supplied; see migration note 5 immediately above.
    /// </remarks>
    string PortalAlias { get; }

    /// <summary>
    /// Numeric key of the account designated administrator of the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L198. A portal-level authorisation fact about the
    /// tenant, never a statement about who is calling. Account keys are seeded at one rather than
    /// zero, but migration note 4 still applies: do not treat a non-positive value as absence.
    /// </remarks>
    int AdministratorId { get; }

    /// <summary>
    /// Numeric key of the role that confers portal administration rights in the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L246. Role keys are seeded at zero, so zero denotes a
    /// real role and migration note 4 applies directly.
    /// </remarks>
    int AdministratorRoleId { get; }

    /// <summary>
    /// Name of the role that confers portal administration rights in the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L254. Carried alongside its key because the legacy
    /// authorisation checks compared role names rather than keys - for example
    /// <c>PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)</c> at
    /// <c>Library/Components/Security/PortalSecurity.vb</c> L519 and
    /// <c>Library/Components/Modules/PortalModuleBase.vb</c> L121 - and behaviour is preserved rather
    /// than improved. Always populated.
    /// </remarks>
    string AdministratorRoleName { get; }

    /// <summary>
    /// Numeric key of the role granted to every signed-in member of the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L262. Role keys are seeded at zero here too, so
    /// migration note 4 applies directly.
    /// </remarks>
    int RegisteredRoleId { get; }

    /// <summary>
    /// Name of the role granted to every signed-in member of the resolved portal.
    /// </summary>
    /// <remarks>
    /// Legacy origin: <c>PortalSettings.vb</c> L270. Carried alongside its key for the same reason as
    /// the administrator role name: the legacy checks compared names. Always populated.
    /// </remarks>
    string RegisteredRoleName { get; }
}
