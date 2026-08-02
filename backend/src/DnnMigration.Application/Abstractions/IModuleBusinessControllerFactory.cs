// MIGRATION: This contract replaces reflection-based activation of a module's business controller
// with resolution from a closed set registered at start-up. Five legacy call sites late-bound a type
// whose name was read from the Modules.BusinessControllerClass column and then tested the resulting
// untyped reference against a lifecycle interface: content export, content import, the queued import
// of content awaiting later processing, the version-by-version upgrade, and the capability re-probe.
// An implementation must resolve every controller by looking the key up in a registration map
// populated once at start-up; it must not load, enumerate or scan assemblies, must not resolve a type
// from a string at run time, and must not construct a type dynamically. The legacy deployment leaned
// further on private assembly probing paths that have no target equivalent and are not reintroduced.
//
// MIGRATION: The silent exception handlers are deliberately not reproduced. Three legacy paths
// swallowed every exception raised by a module's own code and continued as though nothing had
// happened - two of them ending in a bare handler commented "ignore errors". A swallowed export
// writes an incomplete portal template and a swallowed import loses content outright, so that is a
// defect rather than a business rule. Every member below surfaces such a failure as an explicit
// failed outcome carrying a code and a message, and no member reports success when the underlying
// operation failed.
//
// MIGRATION: The deferred-work path is omitted and no member here defers anything. The legacy import
// queued a message whenever the stored capability bitmask still held the "not yet determined"
// sentinel of -1, because a module's capabilities could not be established during the request that
// installed it. With a closed registration map every capability is known at start-up, so that state
// cannot arise.
//
// MIGRATION: The searchable lifecycle contract is not ported - its sole member took a module entity
// and returned a bespoke collection type, both of which this migration eliminates, and search is
// deferred in its entirety. The searchable *capability flag* nevertheless survives as data, because
// the legacy probe recorded all three flags together in one column and dropping one would silently
// change the stored value. This contract therefore reports the searchable bit and never invokes a
// searchable operation.
//
// MIGRATION: The payload crosses these members as an opaque string. The legacy sites HTML-encoded the
// content on the way out and decoded it on the way back, using request-pipeline helpers this project
// cannot reference. Deciding whether a payload needs escaping, and in which direction, is the
// caller's concern.

using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Performs a module's lifecycle operations - content export, content import, version upgrade and
/// capability reporting - through a business controller resolved from a closed set registered at
/// application start-up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> It answers exactly one question: <em>what can the business controller registered
/// under this key do, and will it do it?</em> A business controller is a module author's optional
/// companion class, identified in the legacy schema by the <c>Modules.BusinessControllerClass</c>
/// column, that knows how to serialise the module's own content, restore it, and migrate it between
/// versions. Most modules declare no such class at all, so "there is nothing to do" is the single
/// most common outcome of every member here and is reported as a success, never as an error.
/// </para>
/// <para>
/// <b>A facade, not a locator.</b> No member hands a controller instance back to its caller. The
/// legacy defect being retired was precisely that: a late-bound, untyped reference was produced and
/// then interrogated with a run-time type test before being cast, so neither the compiler nor the
/// caller could tell which operations were actually available. Here the contract performs the
/// operation itself and reports the outcome, which keeps every controller instance - and the whole
/// question of how one is built - inside the layer that owns it. Nothing on this surface is a domain
/// entity, a persistence type, a template document type or a transport type; this project declares
/// exactly one project reference, to the domain layer, so reaching for any of them does not compile.
/// </para>
/// <para>
/// <b>A singleton factory must still produce scope-correct controllers.</b> Registration belongs to
/// the infrastructure layer, because no implementation can exist in a project that cannot see the
/// registration map, and the factory itself is naturally a singleton: its map is fixed once at
/// start-up and never mutated. But a business controller may legitimately depend on scoped services -
/// a unit of work, a per-request tenant context - so an implementation must resolve each controller
/// from the scope current when the member is called, obtaining it from the request's own service
/// provider or by opening a scope from a captured scope factory. Storing a resolved controller in a
/// field of the singleton captures whichever scope happened to be active first and then serves it to
/// every later caller, which corrupts data across tenants and requests. Cache the
/// <em>registration</em>; never cache the <em>instance</em>. No compiler catches this.
/// </para>
/// <para>
/// <b>Responsibilities that deliberately live elsewhere.</b> This contract does not read or write
/// modules - the caller supplies the identifier and the key, and the domain repository abstractions
/// own persistence. It does not cache and it does not invalidate. It does not write the audit trail:
/// the legacy upgrade path logged one module-updated entry per version, and that becomes a structured
/// log event emitted by the caller, which is why the upgrade member returns the module's own result
/// text rather than logging it. And it does not persist the capability bitmask it computes; it reports
/// the value and the caller stores it.
/// </para>
/// <para>
/// <b>Outcome conventions, uniform across every member.</b> A failed outcome means the operation was
/// attempted and did not complete, and it always carries a code and a message. A successful outcome
/// means no failure occurred, and it may still carry an advisory reason explaining that nothing was
/// done - because no controller key was supplied, because no registration covers the key, or because
/// the registered controller does not implement the lifecycle contract the member needs. Read
/// <see cref="Result.Error"/> when reacting to failure and <see cref="Result.Reason"/> when reading an
/// advisory code. Reasons are carried generically, as a code paired with a message, so a caller can
/// branch on the code without this layer naming a status enumeration.
/// </para>
/// <para>
/// <b>Absence is never failure, and no identifier carries sentinel meaning.</b> Where a member returns
/// a value, a <see langword="null"/> value on a <em>successful</em> outcome means the value is absent -
/// the operation ran to completion and there was genuinely nothing to report - not that the operation
/// failed. Collapsing the two would reintroduce exactly the ambiguity this migration removes, because
/// the legacy null contract represented an absent integer with -1 and an absent string with the empty
/// string. Reading the value of a failed outcome is a programming error and throws. Neither -1 nor 0
/// may be interpreted as "absent" in any argument accepted here: the portal table seeds its key at -1,
/// which is simultaneously the legacy absent-integer marker, and the module, page and role tables all
/// seed at 0. Every identifier is passed through exactly as supplied.
/// </para>
/// <para>
/// <b>Implementer's obligations.</b> Resolve from the registration map and from nowhere else. Resolve
/// per call, from the current scope. Treat an unsupplied, unregistered or unsupported controller as a
/// successful no-op carrying the documented advisory code, never as a failure and never as an
/// exception. Let no exception raised by a module's own code escape silently: translate it into a
/// failed outcome with the documented failure code, preserving the original message. Honour the
/// cancellation token on every member. Perform no caching, no invalidation, no persistence and no
/// audit logging.
/// </para>
/// </remarks>
public interface IModuleBusinessControllerFactory
{
    /// <summary>
    /// Reports which lifecycle contracts the business controller registered under
    /// <paramref name="businessControllerClass"/> implements, as the capability bitmask the legacy
    /// schema stores in the <c>DesktopModules.SupportedFeatures</c> column.
    /// </summary>
    /// <param name="businessControllerClass">
    /// The controller key, taken verbatim from the legacy <c>Modules.BusinessControllerClass</c>
    /// column. A <see langword="null"/>, empty or white-space value means the module declares no
    /// business controller.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome whose value is the capability bitmask, formed by combining <c>1</c> when
    /// the controller can export and import its own content, <c>2</c> when it can contribute
    /// searchable items, and <c>4</c> when it can migrate its content between versions. A registered
    /// controller that implements none of the three yields <c>0</c>, which is a positive statement
    /// that it supports nothing rather than an absent answer.
    /// </para>
    /// <para>
    /// A successful outcome whose value is <see langword="null"/> means no answer exists: either no
    /// key was supplied, carrying advisory code <c>module.controller.not_specified</c>, or no
    /// registration covers the supplied key, carrying advisory code
    /// <c>module.controller.not_registered</c>. Both are expected and neither is a failure.
    /// </para>
    /// <para>
    /// A failed outcome carrying <c>module.controller.capability_probe_failed</c> when the registered
    /// controller could not be brought into existence to be examined - for instance because one of its
    /// own dependencies could not be satisfied.
    /// </para>
    /// </returns>
    /// <remarks>
    /// The bitmask is returned rather than persisted, because the write-back is the caller's. The
    /// legacy "not yet determined" state cannot arise here: its stored form was the absent-integer
    /// sentinel -1, screened for before any bit was tested, and it existed only because capabilities
    /// were discovered by inspecting a freshly deployed module during the request that installed it. A
    /// closed registration map is complete before the first request is served, so an implementation
    /// must never return -1 to mean "unknown" - absence is carried by <see langword="null"/> alone.
    /// </remarks>
    Task<Result<int?>> GetSupportedFeaturesAsync(
        string? businessControllerClass,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the business controller registered under <paramref name="businessControllerClass"/> to
    /// serialise the content it holds for the module instance identified by
    /// <paramref name="moduleId"/>.
    /// </summary>
    /// <param name="businessControllerClass">
    /// The controller key, taken verbatim from the legacy <c>Modules.BusinessControllerClass</c>
    /// column. A <see langword="null"/>, empty or white-space value means the module declares no
    /// business controller.
    /// </param>
    /// <param name="moduleId">
    /// Identifier of the module instance whose content is to be serialised. Passed through unaltered.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome whose value is the serialised payload exactly as the module produced it,
    /// neither parsed, validated, escaped nor re-encoded on the way through.
    /// </para>
    /// <para>
    /// A successful outcome whose value is an <em>empty string</em> means the controller ran and had
    /// nothing to give - the module holds no content worth exporting. This restates the legacy
    /// non-empty guard and is emphatically not a failure; a caller assembling a portal template writes
    /// no content element in this case.
    /// </para>
    /// <para>
    /// A successful outcome whose value is <see langword="null"/> means the controller was never
    /// asked: no key was supplied, carrying <c>module.controller.not_specified</c>; no registration
    /// covers the key, carrying <c>module.controller.not_registered</c>; or the registered controller
    /// cannot serialise its content at all, carrying
    /// <c>module.controller.contract_not_supported</c>. The empty string and <see langword="null"/>
    /// are therefore distinct answers and an implementation must not substitute one for the other -
    /// the first says "asked, nothing to give", the second says "not asked".
    /// </para>
    /// <para>
    /// A failed outcome carrying <c>module.content.export_failed</c> when the controller was asked and
    /// its own serialisation raised an exception. The message must preserve the underlying explanation
    /// so the caller can log something actionable.
    /// </para>
    /// </returns>
    /// <remarks>
    /// Composing the portal template remains the caller's work, because this contract exchanges
    /// strings and never touches a template document. The legacy site consulted the module's
    /// <em>stored</em> portability flag before asking the controller and then tested the activated
    /// reference as well - two answers to one question that could disagree whenever the stored bitmask
    /// was stale. An implementation consults the live registration instead, so
    /// <c>module.controller.contract_not_supported</c> is authoritative; a caller must not pre-check
    /// the stored flag, which would resurrect the same divergence.
    /// </remarks>
    Task<Result<string?>> ExportModuleContentAsync(
        string? businessControllerClass,
        int moduleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the business controller registered under <paramref name="businessControllerClass"/> to
    /// restore previously serialised content into the module instance identified by
    /// <paramref name="moduleId"/>.
    /// </summary>
    /// <param name="businessControllerClass">
    /// The controller key, taken verbatim from the legacy <c>Modules.BusinessControllerClass</c>
    /// column. A <see langword="null"/>, empty or white-space value means the module declares no
    /// business controller.
    /// </param>
    /// <param name="moduleId">
    /// Identifier of the module instance to restore content into. Passed through unaltered.
    /// </param>
    /// <param name="content">
    /// The serialised payload to restore, opaque to this contract and passed through byte for byte. An
    /// empty or white-space payload reproduces the legacy non-empty guard and yields a successful
    /// no-op.
    /// </param>
    /// <param name="version">
    /// The version stamp recorded alongside the payload when it was exported, which the module uses to
    /// interpret a payload written by an older release of itself. Opaque to this contract and neither
    /// parsed nor compared here.
    /// </param>
    /// <param name="userId">
    /// Identifier of the principal on whose behalf the content is being restored, which the module
    /// records as the author of whatever it creates. Always supplied by the caller and never read from
    /// ambient state. Passed through unaltered.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome carrying no reason when the controller restored the content.
    /// </para>
    /// <para>
    /// A successful outcome carrying an advisory reason when nothing was restored because the
    /// controller was never asked: <c>module.controller.not_specified</c> when no key was supplied,
    /// <c>module.controller.not_registered</c> when no registration covers the key,
    /// <c>module.controller.contract_not_supported</c> when the registered controller cannot restore
    /// content, and <c>module.content.not_supplied</c> when the payload was empty or white-space. All
    /// four are expected outcomes.
    /// </para>
    /// <para>
    /// A failed outcome carrying <c>module.content.import_failed</c> when the controller was asked and
    /// its own restore raised an exception. The message must preserve the underlying explanation. A
    /// failure here means content was lost, so a caller is expected to log it and to treat the
    /// surrounding operation as incomplete - which is the whole point of not swallowing it.
    /// </para>
    /// </returns>
    /// <remarks>
    /// The restore is not a transaction boundary and this member opens none: whether the surrounding
    /// work commits or rolls back is the caller's decision, taken through the domain unit-of-work
    /// abstraction. Nor does this member refresh any cache, which the legacy queued path did
    /// immediately afterwards.
    /// </remarks>
    Task<Result> ImportModuleContentAsync(
        string? businessControllerClass,
        int moduleId,
        string? content,
        string? version,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the business controller registered under <paramref name="businessControllerClass"/> to
    /// migrate its stored content to a single named <paramref name="version"/>.
    /// </summary>
    /// <param name="businessControllerClass">
    /// The controller key, taken verbatim from the legacy <c>Modules.BusinessControllerClass</c>
    /// column. A <see langword="null"/>, empty or white-space value means the module declares no
    /// business controller.
    /// </param>
    /// <param name="version">
    /// The single version to migrate to. The legacy path derived a comma-separated list of applicable
    /// versions and iterated it in order; a caller reproduces that by invoking this member once per
    /// version, in the same ascending order, and stops on the first failed outcome.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome whose value is the module's own human-readable account of what it did. The
    /// text is opaque to this contract; the caller records it, as the legacy path recorded it in its
    /// audit trail. An empty string means the controller ran and had nothing to say.
    /// </para>
    /// <para>
    /// A successful outcome whose value is <see langword="null"/> means the controller was never
    /// asked: <c>module.controller.not_specified</c> when no key was supplied,
    /// <c>module.controller.not_registered</c> when no registration covers the key, or
    /// <c>module.controller.contract_not_supported</c> when the registered controller cannot migrate
    /// its content.
    /// </para>
    /// <para>
    /// A failed outcome carrying <c>module.upgrade_failed</c> when the controller was asked and its own
    /// migration raised an exception. The message must preserve the underlying explanation.
    /// </para>
    /// </returns>
    /// <remarks>
    /// Taking one version per call is deliberate. The underlying module operation is itself
    /// single-version, so this is a faithful one-to-one mapping, and it keeps the per-version audit
    /// entry with the caller - the layer that owns structured logging - while leaving the sequencing
    /// decision visible at the call site. A batch member would have to invent partial-outcome
    /// semantics that no legacy site ever needed and that a single outcome value cannot express
    /// honestly. Migrating content does not update the stored capability bitmask: a new release can
    /// change which lifecycle contracts a module implements, so a caller reproduces the legacy
    /// re-probe by calling <see cref="GetSupportedFeaturesAsync"/> after the last version and
    /// persisting the value it returns.
    /// </remarks>
    Task<Result<string?>> UpgradeModuleAsync(
        string? businessControllerClass,
        string version,
        CancellationToken cancellationToken = default);
}
