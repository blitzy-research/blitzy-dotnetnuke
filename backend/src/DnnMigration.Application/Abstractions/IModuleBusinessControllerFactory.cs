// MIGRATION: this contract replaces reflection-based activation of a module's business
// controller with resolution from a closed set registered at start-up. Five legacy call
// sites late-bound a type whose name was read from the Modules.BusinessControllerClass
// column and then tested the resulting untyped reference against a lifecycle interface.
// Two of the five are in ModuleController.vb: L231 exported a module's content into a
// portal template, and L431 imported that content back.
//
// MIGRATION: the remaining three activation sites are in EventMessageProcessor.vb, where
// L32 imported content that had been queued for later processing, L52 upgraded a module
// version by version, and L77 re-probed which lifecycle contracts a controller
// implements. All five sites are replaced by the four members declared below.
//
// An implementation resolves every controller by looking the key up in a registration
// map populated once at start-up. It must not load, enumerate or scan assemblies, must
// not resolve a type from a string at run time, and must not construct a type
// dynamically. The legacy deployment leaned further on the private probing paths
// declared in Website/release.config, which listed bin plus five bin subdirectories;
// that mechanism has no target equivalent and none is reintroduced here. A key that no
// registration covers is an ordinary, expected outcome rather than an error, because the
// bundled module tree is excluded from this migration wholesale.
//
// MIGRATION: the COM interop, VB6 and ActiveX exclusion is vacuous for this file, so
// nothing here should be read as retiring an inter-process component technology. A
// census of the checkout found no such call site anywhere in it; the apparent hits, five
// of which are the sites named above, are ordinary .NET reflection. Late-bound
// reflection is the accurate and only description of what these members supersede.
//
// MIGRATION: the silent exception handlers are deliberately NOT reproduced, which is a
// documented behavioural difference rather than an oversight. Three legacy paths
// swallowed every exception raised by a module's own code and continued as though
// nothing had happened: the two ModuleController.vb sites, L231 and L431, both end in a
// bare handler commented "ignore errors".
//
// MIGRATION: the third swallow site is the queued import at EventMessageProcessor.vb:L29,
// which ends in a bare handler commented "an error occurred". A swallowed export writes
// an incomplete portal template and a swallowed import loses content outright, so this is
// a defect rather than a business rule, and carrying it into new code would be worse than
// diverging from it. Every member below surfaces such a failure as an explicit failed
// outcome carrying a code and a message. No member reports success when the underlying
// operation failed, and no member discards an exception it did not expect.
//
// MIGRATION: the EventQueue deferral is omitted, and no member here defers work. The
// legacy import site built a queued message at ModuleController.vb:L269 and took that
// branch at L426 whenever the stored capability bitmask still held the legacy
// "not yet determined" sentinel of -1, because the module's capabilities could not be
// established during the request that installed it. The legacy event-queue sub-tree is
// one of the excluded sub-trees, and the deferral is also unnecessary: with a closed
// registration map every capability is known at start-up, so the sentinel state cannot
// arise and there is nothing to postpone.
//
// MIGRATION: the ISearchable lifecycle contract is not ported, and this asymmetry is
// deliberate. Its sole member took a module entity and returned a bespoke collection
// type, and both of those types are eliminated by this migration; search itself is
// deferred in its entirety. The searchable *capability flag* nevertheless survives as
// data, because the legacy capability probe recorded all three flags together in one
// column and dropping one would silently change the stored value. This contract
// therefore reports the searchable bit and never invokes a searchable operation.
//
// MIGRATION: the legacy Server.HtmlEncode and Server.HtmlDecode calls that wrapped the
// content payload have no equivalent on this contract. The export site encoded the
// payload before writing it into the template and the two import sites decoded it again
// on the way back. Those calls belong to the request pipeline that this migration
// retires, and this project references neither a web framework nor the legacy hosting
// stack, so the payload crosses these members as an opaque string. Deciding whether a
// payload needs escaping, and in which direction, is the caller's concern.

using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Performs a module's lifecycle operations - content export, content import, version
/// upgrade and capability reporting - through a business controller resolved from a
/// closed set registered at application start-up.
/// </summary>
/// <remarks>
/// <para>
/// Scope of this contract. It answers exactly one question: <em>what can the business
/// controller registered under this key do, and will it do it?</em> A business controller
/// is a module author's optional companion class, identified in the legacy schema by the
/// <c>Modules.BusinessControllerClass</c> column, that knows how to serialise the
/// module's own content, restore it, and migrate it between versions. Most modules
/// declare no such class at all, so "there is nothing to do" is the single most common
/// outcome of every member here and is reported as a success, never as an error.
/// </para>
/// <para>
/// A facade, not a locator. No member hands a controller instance back to its caller.
/// The legacy defect being retired was precisely that: a late-bound, untyped reference
/// was produced and then interrogated with a run-time type test before being cast, so
/// neither the compiler nor the caller could tell which operations were actually
/// available. Here the contract performs the operation itself and reports the outcome,
/// which keeps every controller instance, and the whole question of how one is built,
/// inside the layer that owns it.
/// </para>
/// <para>
/// Registration and lifetime. This abstraction is intentionally NOT registered by the
/// application layer's <c>AddApplication()</c> extension, which registers exactly seven
/// services - <c>IPortalService</c>, <c>IModuleService</c>, <c>IUserService</c>,
/// <c>IRoleService</c>, <c>IPermissionService</c>, <c>ITabService</c> and
/// <c>IAuthService</c> - and excludes this one by design, because no implementation of it
/// can exist in a project that cannot see the registration map. The infrastructure
/// layer's <c>AddInfrastructure(IConfiguration)</c> extension registers it instead. The
/// factory itself is naturally a singleton: its map is fixed once at start-up and is
/// never mutated afterwards.
/// </para>
/// <para>
/// A singleton factory must still produce scope-correct controllers. This is the one
/// implementation hazard on this contract that no compiler catches. A business controller
/// may legitimately depend on scoped services - a unit of work, a per-request tenant
/// context - so an implementation must resolve each controller from the scope that is
/// current when the member is called, obtaining it from the request's own service
/// provider or by opening a scope from a captured scope factory. Storing a resolved
/// controller in a field of the singleton captures whichever scope happened to be active
/// first and then serves it to every later caller, which corrupts data across tenants and
/// requests. Cache the <em>registration</em>; never cache the <em>instance</em>.
/// </para>
/// <para>
/// The consumer is the application layer's module service, which already holds the module
/// identifier and the controller key it loaded from the repository and passes both in as
/// plain values. Nothing on this surface is a domain entity, a persistence type, a
/// template document type or a transport type, and this project declares exactly one
/// project reference - the domain layer - so reaching for any of them here does not
/// compile. That is the design, not an inconvenience.
/// </para>
/// <para>
/// Responsibilities that deliberately live elsewhere. This contract does not read or
/// write modules: the caller supplies the identifier and the key, and the repository
/// abstractions in the domain layer own persistence. It does not cache and it does not
/// invalidate - the legacy cache synchronisation that followed a queued import, at
/// EventMessageProcessor.vb:L43, is the cache abstraction's concern, orchestrated by the
/// module service. It does not write the audit trail: the legacy upgrade path logged one
/// module-updated entry per version at EventMessageProcessor.vb:L68, and that becomes a
/// structured log event emitted by the caller, which is why the upgrade member returns
/// the module's own result text rather than logging it. And it does not persist the
/// capability bitmask it computes; it reports the value and the caller stores it.
/// </para>
/// <para>
/// Outcome conventions, uniform across every member. A failed outcome means the operation
/// was attempted and did not complete, and it always carries a code and a message. A
/// successful outcome means no failure occurred, and it may still carry an advisory
/// reason explaining that nothing was done - because no controller key was supplied,
/// because no registration covers the key, or because the registered controller does not
/// implement the lifecycle contract the member needs. Read <see cref="Result.Error"/>
/// when reacting to failure and <see cref="Result.Reason"/> when reading an advisory
/// code. Reasons are carried generically, as a code paired with a message, so that a
/// caller can branch on the code without this layer naming a status enumeration.
/// </para>
/// <para>
/// Absence is never failure. Where a member returns a value, a <see langword="null"/>
/// value on a <em>successful</em> outcome means the value is <em>absent</em> - the
/// operation ran to completion and there was genuinely nothing to report. It does
/// <em>not</em> mean the operation failed; that is what a failed outcome is for.
/// Collapsing the two would reintroduce exactly the ambiguity this migration removes,
/// because the legacy null contract represented an absent integer with -1 and an absent
/// string with the empty string rather than with a null reference. Reading the value of a
/// failed outcome is a programming error and throws.
/// </para>
/// <para>
/// Identifiers carry no sentinel meaning. Neither -1 nor 0 may be interpreted as
/// "absent" in any argument accepted here. Both are real identifiers in this schema: the
/// portal table seeds its key at -1, which is simultaneously the legacy absent-integer
/// marker, and the module, tab and role tables all seed at 0. An implementation must
/// pass every identifier through exactly as supplied.
/// </para>
/// <para>
/// Implementer's checklist. Resolve from the registration map and from nowhere else.
/// Resolve per call, from the current scope. Treat an unsupplied, unregistered or
/// unsupported controller as a successful no-op carrying the documented advisory code,
/// never as a failure and never as an exception. Let no exception raised by a module's
/// own code escape silently: translate it into a failed outcome with the documented
/// failure code, preserving the original message. Honour the cancellation token on every
/// member. Perform no caching, no invalidation, no persistence and no audit logging.
/// </para>
/// </remarks>
public interface IModuleBusinessControllerFactory
{
    /// <summary>
    /// Reports which lifecycle contracts the business controller registered under
    /// <paramref name="businessControllerClass"/> implements, as the capability bitmask
    /// the legacy schema stores in the <c>DesktopModules.SupportedFeatures</c> column.
    /// </summary>
    /// <param name="businessControllerClass">
    /// The controller key, taken verbatim from the legacy
    /// <c>Modules.BusinessControllerClass</c> column. A <see langword="null"/>, empty or
    /// white-space value means the module declares no business controller.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome whose value is the capability bitmask, formed by combining
    /// <c>1</c> when the controller can export and import its own content, <c>2</c> when
    /// it can contribute searchable items, and <c>4</c> when it can migrate its content
    /// between versions. A registered controller that implements none of the three yields
    /// <c>0</c>, which is a positive statement that it supports nothing rather than an
    /// absent answer.
    /// </para>
    /// <para>
    /// A successful outcome whose value is <see langword="null"/> means no answer exists:
    /// either no key was supplied, carrying advisory code
    /// <c>module.controller.not_specified</c>, or no registration covers the supplied
    /// key, carrying advisory code <c>module.controller.not_registered</c>. Both are
    /// expected and neither is a failure.
    /// </para>
    /// <para>
    /// A failed outcome carrying <c>module.controller.capability_probe_failed</c> when the
    /// registered controller could not be brought into existence to be examined - for
    /// instance because one of its own dependencies could not be satisfied.
    /// </para>
    /// </returns>
    /// <remarks>
    /// <para>
    /// This member supersedes the legacy capability probe reached from
    /// EventMessageProcessor.vb:L77 and from the tail of its upgrade path at
    /// EventMessageProcessor.vb:L52. That probe reset the stored value to zero and then
    /// applied three run-time type tests, one per lifecycle interface, before writing the
    /// combined value back. The three tests become this single lookup; the write-back
    /// remains the caller's, which is why the bitmask is returned rather than persisted.
    /// </para>
    /// <para>
    /// The searchable bit is reported as a flag only. No searchable operation is exposed
    /// anywhere on this contract and none is invoked to determine the bit, because the
    /// legacy searchable interface depended on two types this migration eliminates and
    /// search is deferred in its entirety. Reporting the bit regardless preserves the
    /// stored column value: the legacy probe wrote all three flags in one assignment, so
    /// omitting one would silently change the number a caller persists.
    /// </para>
    /// <para>
    /// The legacy "not yet determined" state cannot arise here. Its stored form was the
    /// absent-integer sentinel -1, which the legacy accessor screened for before testing
    /// any bit, and it existed only because capabilities were discovered by inspecting a
    /// freshly deployed module during the request that installed it. A closed registration
    /// map is complete before the first request is served, so the answer is always either
    /// a genuine bitmask or a documented absence. An implementation must never return -1
    /// to mean "unknown"; absence is carried by <see langword="null"/> alone.
    /// </para>
    /// </remarks>
    Task<Result<int?>> GetSupportedFeaturesAsync(
        string? businessControllerClass,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the business controller registered under
    /// <paramref name="businessControllerClass"/> to serialise the content it holds for
    /// the module instance identified by <paramref name="moduleId"/>.
    /// </summary>
    /// <param name="businessControllerClass">
    /// The controller key, taken verbatim from the legacy
    /// <c>Modules.BusinessControllerClass</c> column. A <see langword="null"/>, empty or
    /// white-space value means the module declares no business controller.
    /// </param>
    /// <param name="moduleId">
    /// Identifier of the module instance whose content is to be serialised,
    /// corresponding to the legacy <c>Modules.ModuleID</c> column. Passed through
    /// unaltered; 0 and -1 are legitimate identifiers and must not be read as absent.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome whose value is the serialised payload exactly as the module
    /// produced it. The payload is opaque to this contract: it is neither parsed,
    /// validated, escaped nor re-encoded on the way through.
    /// </para>
    /// <para>
    /// A successful outcome whose value is an <em>empty string</em> means the controller
    /// ran and had nothing to give - the module holds no content worth exporting. This is
    /// the legacy non-empty guard restated, and it is emphatically not a failure. A caller
    /// assembling a portal template writes no content element in this case, which is
    /// exactly what the legacy export site did.
    /// </para>
    /// <para>
    /// A successful outcome whose value is <see langword="null"/> means the controller was
    /// never asked: either no key was supplied, carrying advisory code
    /// <c>module.controller.not_specified</c>, or no registration covers the supplied key,
    /// carrying advisory code <c>module.controller.not_registered</c>, or the registered
    /// controller cannot serialise its content at all, carrying advisory code
    /// <c>module.controller.contract_not_supported</c>. The empty string and
    /// <see langword="null"/> are therefore distinct answers, and an implementation must
    /// not substitute one for the other: the first says "asked, nothing to give", the
    /// second says "not asked".
    /// </para>
    /// <para>
    /// A failed outcome carrying <c>module.content.export_failed</c> when the controller
    /// was asked and its own serialisation raised an exception. The message must preserve
    /// the underlying explanation so the caller can log something actionable.
    /// </para>
    /// </returns>
    /// <remarks>
    /// <para>
    /// This member supersedes the legacy export site at ModuleController.vb:L231, which
    /// guarded on a non-empty key and on the stored portability flag, activated the
    /// controller, applied a run-time type test, called the export operation, and wrote
    /// the payload into a portal-template element only when the returned text was
    /// non-empty. Everything except that last step is absorbed here; composing the
    /// template remains the caller's work, because this contract exchanges strings and
    /// never touches a template document.
    /// </para>
    /// <para>
    /// The legacy site consulted the module's <em>stored</em> portability flag before
    /// asking the controller, and then tested the activated reference as well - two
    /// answers to one question that could disagree whenever the stored bitmask was stale.
    /// An implementation of this member consults the live registration instead, so the
    /// advisory <c>module.controller.contract_not_supported</c> is authoritative. A caller
    /// does not need to pre-check the stored flag and should not: doing so would
    /// resurrect the same divergence.
    /// </para>
    /// </remarks>
    Task<Result<string?>> ExportModuleContentAsync(
        string? businessControllerClass,
        int moduleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the business controller registered under
    /// <paramref name="businessControllerClass"/> to restore previously serialised
    /// content into the module instance identified by <paramref name="moduleId"/>.
    /// </summary>
    /// <param name="businessControllerClass">
    /// The controller key, taken verbatim from the legacy
    /// <c>Modules.BusinessControllerClass</c> column. A <see langword="null"/>, empty or
    /// white-space value means the module declares no business controller.
    /// </param>
    /// <param name="moduleId">
    /// Identifier of the module instance to restore content into, corresponding to the
    /// legacy <c>Modules.ModuleID</c> column. Passed through unaltered; 0 and -1 are
    /// legitimate identifiers and must not be read as absent.
    /// </param>
    /// <param name="content">
    /// The serialised payload to restore, opaque to this contract and passed through
    /// byte for byte. An empty or white-space payload reproduces the legacy non-empty
    /// guard and yields a successful no-op.
    /// </param>
    /// <param name="version">
    /// The version stamp recorded alongside the payload when it was exported, which the
    /// module uses to interpret a payload written by an older release of itself. Opaque
    /// to this contract and neither parsed nor compared here.
    /// </param>
    /// <param name="userId">
    /// Identifier of the principal on whose behalf the content is being restored, which
    /// the module records as the author of whatever it creates. Always supplied by the
    /// caller and never read from ambient state: the legacy import sites passed the
    /// destination portal's administrator identifier and, on the queued path, an
    /// identifier carried on the queued message. Passed through unaltered; 0 and -1 are
    /// legitimate identifiers and must not be read as absent.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome carrying no reason when the controller restored the content.
    /// </para>
    /// <para>
    /// A successful outcome carrying an advisory reason when nothing was restored because
    /// the controller was never asked: <c>module.controller.not_specified</c> when no key
    /// was supplied, <c>module.controller.not_registered</c> when no registration covers
    /// the key, <c>module.controller.contract_not_supported</c> when the registered
    /// controller cannot restore content, and <c>module.content.not_supplied</c> when the
    /// payload was empty or white-space. All four are expected outcomes.
    /// </para>
    /// <para>
    /// A failed outcome carrying <c>module.content.import_failed</c> when the controller
    /// was asked and its own restore raised an exception. The message must preserve the
    /// underlying explanation. A failure here means content was lost, so a caller is
    /// expected to log it and to treat the surrounding operation as incomplete - which is
    /// the whole point of not swallowing it.
    /// </para>
    /// </returns>
    /// <remarks>
    /// <para>
    /// This member supersedes two legacy sites that differed only in where their
    /// arguments came from. The direct site at ModuleController.vb:L431 read the payload
    /// and its version stamp from a portal template and took the administrator identifier
    /// from the destination portal; the queued site at EventMessageProcessor.vb:L32 read
    /// all four values from a queued message. Both then activated the controller, applied
    /// a run-time type test and called the same restore operation, so both collapse into
    /// this one member with its four values supplied explicitly.
    /// </para>
    /// <para>
    /// The restore is not a transaction boundary and this member opens none. Whether the
    /// surrounding work commits or rolls back is the caller's decision, taken through the
    /// unit-of-work abstraction the domain layer owns. Nor does this member refresh any
    /// cache: the legacy queued path called a cache synchronisation helper immediately
    /// afterwards, at EventMessageProcessor.vb:L43, and that responsibility now sits with
    /// the caller and the cache abstraction.
    /// </para>
    /// </remarks>
    Task<Result> ImportModuleContentAsync(
        string? businessControllerClass,
        int moduleId,
        string? content,
        string? version,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the business controller registered under
    /// <paramref name="businessControllerClass"/> to migrate its stored content to a
    /// single named <paramref name="version"/>.
    /// </summary>
    /// <param name="businessControllerClass">
    /// The controller key, taken verbatim from the legacy
    /// <c>Modules.BusinessControllerClass</c> column. A <see langword="null"/>, empty or
    /// white-space value means the module declares no business controller.
    /// </param>
    /// <param name="version">
    /// The single version to migrate to. The legacy path derived a comma-separated list
    /// of applicable versions and iterated it in order; a caller reproduces that by
    /// invoking this member once per version, in the same ascending order.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome whose value is the module's own human-readable account of
    /// what it did. The text is opaque to this contract; the caller records it, and the
    /// legacy path recorded it as a module-updated audit entry.
    /// </para>
    /// <para>
    /// A successful outcome whose value is <see langword="null"/> means the controller was
    /// never asked: <c>module.controller.not_specified</c> when no key was supplied,
    /// <c>module.controller.not_registered</c> when no registration covers the key, or
    /// <c>module.controller.contract_not_supported</c> when the registered controller
    /// cannot migrate its content. An empty string, by contrast, means the controller ran
    /// and had nothing to say, which the legacy path treated as a migration that
    /// succeeded quietly and logged without a result line.
    /// </para>
    /// <para>
    /// A failed outcome carrying <c>module.upgrade_failed</c> when the controller was
    /// asked and its own migration raised an exception. The message must preserve the
    /// underlying explanation.
    /// </para>
    /// </returns>
    /// <remarks>
    /// <para>
    /// This member supersedes the legacy upgrade site at EventMessageProcessor.vb:L52,
    /// which activated the controller once, applied a run-time type test, split the
    /// applicable-versions attribute on commas, and looped the migration call over the
    /// resulting versions, logging each returned text as it went.
    /// </para>
    /// <para>
    /// Taking one version per call is deliberate. The underlying module operation is
    /// itself single-version, so this shape is a faithful one-to-one mapping, and it keeps
    /// two things where they belong: the audit entry per version stays with the caller,
    /// which is the layer that owns structured logging, and the sequencing decision stays
    /// visible at the call site. A batch member would have to invent partial-outcome
    /// semantics - what a caller should conclude when the third of five versions fails and
    /// the remaining two are never attempted - that no legacy site ever needed and that a
    /// single outcome value cannot express honestly. A caller stops iterating on the first
    /// failed outcome.
    /// </para>
    /// <para>
    /// Migrating content does not update the stored capability bitmask. The legacy upgrade
    /// path re-probed capabilities immediately after its loop, at
    /// EventMessageProcessor.vb:L72, because deploying a new release could change which
    /// lifecycle contracts a module implemented. A caller reproduces that by calling
    /// <see cref="GetSupportedFeaturesAsync"/> after the last version and persisting the
    /// value it returns.
    /// </para>
    /// </remarks>
    Task<Result<string?>> UpgradeModuleAsync(
        string? businessControllerClass,
        string version,
        CancellationToken cancellationToken = default);
}
