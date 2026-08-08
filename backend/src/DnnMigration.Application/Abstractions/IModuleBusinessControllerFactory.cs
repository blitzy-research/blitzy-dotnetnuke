// MIGRATION: This contract replaces reflection-based activation of a module's business controller with
// resolution from a closed set registered at start-up. Five legacy call sites late-bound a type whose name
// was read from the DesktopModules.BusinessControllerClass column and then tested the resulting untyped
// reference against a lifecycle interface: content export, content import, the queued import of content
// awaiting later processing, the version-by-version upgrade, and the capability re-probe.
//
// MIGRATION: The silent exception handlers are deliberately not reproduced. Three legacy paths swallowed
// every exception raised by a module's own code and continued as though nothing had happened - two of them
// ending in a bare handler commented "ignore errors".
//
// MIGRATION: The deferred-work path is omitted and no member here defers anything. The legacy import queued
// a message whenever the stored capability bitmask still held the "not yet determined" sentinel of -1,
// because a module's capabilities could not be established during the request that installed it.

using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Performs a module's lifecycle operations - content export, content import, version upgrade and
/// capability reporting - through a business controller resolved from a closed set registered at
/// application start-up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> It answers exactly one question:
/// <em>what can the business controller registered under this key do, and will it do it?</em> A
/// business controller is a module author's optional companion class, identified in the legacy
/// schema by the <c>DesktopModules.BusinessControllerClass</c> column, that knows how to serialise
/// the module's own content, restore it, and migrate it between versions. Most modules declare no
/// such class at all, so "there is nothing to do" is the single most common outcome of every member
/// here and is reported as a success, never as an error.
/// </para>
/// <para>
/// <b>A facade, not a locator.</b> No member hands a controller instance back to its caller - which
/// is precisely the legacy defect being retired, where a late-bound untyped reference was produced
/// and then interrogated with a run-time type test before being cast, so neither the compiler nor
/// the caller could tell which operations were available.
/// </para>
/// <para>
/// <b>A singleton factory must still produce scope-correct controllers.</b> Registration belongs to
/// the infrastructure layer and the factory is naturally a singleton, its map fixed once at
/// start-up. Cache the <em>registration</em>; never cache the <em>instance</em>.
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
    /// The controller key, taken verbatim from the legacy
    /// <c>DesktopModules.BusinessControllerClass</c> column; a <see langword="null"/>, empty or
    /// white-space value means the module declares no business controller.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome whose value is the capability bitmask, formed by combining <c>1</c>
    /// when the controller can export and import its own content, <c>2</c> when it can contribute
    /// searchable items, and <c>4</c> when it can migrate its content between versions.
    /// </para>
    /// </returns>
    /// <remarks>
    /// The bitmask is returned rather than persisted, because the write-back is the caller's. The
    /// legacy "not yet determined" state cannot arise here: its stored form was the absent-integer
    /// sentinel -1, and it existed only because capabilities were discovered by inspecting a
    /// freshly deployed module during the request that installed it. A closed registration map is
    /// complete before the first request is served, so an implementation must never return -1 to
    /// mean "unknown" - absence is carried by <see langword="null"/> alone.
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
    /// The controller key, taken verbatim from the legacy
    /// <c>DesktopModules.BusinessControllerClass</c> column; a <see langword="null"/>, empty or
    /// white-space value means the module declares no business controller.
    /// </param>
    /// <param name="moduleId">
    /// Identifier of the module instance whose content is to be serialised.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome whose value is the serialised payload exactly as the module produced
    /// it, neither parsed, validated, escaped nor re-encoded on the way through.
    /// </para>
    /// </returns>
    /// <remarks>
    /// Composing the portal template remains the caller's work, because this contract exchanges
    /// strings and never touches a template document. The legacy site consulted the module's
    /// <em>stored</em> portability flag before asking the controller AND tested the activated
    /// reference as well - two answers to one question that could disagree whenever the stored
    /// bitmask was stale. An implementation consults the live registration instead, so
    /// <c>module.controller.contract_not_supported</c> is authoritative and a caller must not
    /// pre-check the stored flag.
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
    /// The controller key, taken verbatim from the legacy <c>DesktopModules.BusinessControllerClass</c>
    /// column; a <see langword="null"/>, empty or white-space value means the module declares no
    /// business controller.
    /// </param>
    /// <param name="moduleId">Identifier of the module instance to restore content into.</param>
    /// <param name="content">The serialised payload, opaque here and passed through byte for byte.</param>
    /// <param name="version">The version stamp recorded when the payload was exported.</param>
    /// <param name="userId">The principal the module records as the author of whatever it creates.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome, carrying no reason, when and ONLY when the controller was asked and
    /// restored the content.
    /// </returns>
    /// <remarks>
    /// The restore is not a transaction boundary and this member opens none: whether the surrounding
    /// work commits or rolls back is the caller's decision, taken through the domain unit-of-work
    /// abstraction. A caller distinguishing "not restored because nobody could" from "not restored
    /// because the attempt broke" reads the failure code, which is why the five codes above are
    /// distinct rather than collapsed into one.
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
    /// The controller key, taken verbatim from the legacy
    /// <c>DesktopModules.BusinessControllerClass</c> column; a <see langword="null"/>, empty or
    /// white-space value means the module declares no business controller.
    /// </param>
    /// <param name="version">The single version to migrate to.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// <para>
    /// A successful outcome whose value is the module's own human-readable account of what it did.
    /// </para>
    /// </returns>
    /// <remarks>
    /// Taking one version per call is deliberate: the underlying module operation is itself
    /// single-version, so this is a faithful one-to-one mapping that keeps the per-version audit
    /// entry with the caller and leaves the sequencing decision visible at the call site, where a
    /// batch member would have to invent partial-outcome semantics a single outcome value cannot
    /// express honestly. Migrating content does not update the stored capability bitmask - a new
    /// release can change which lifecycle contracts a module implements - so a caller reproduces
    /// the legacy re-probe by calling <see cref="GetSupportedFeaturesAsync"/> after the last
    /// version and persisting the value.
    /// </remarks>
    Task<Result<string?>> UpgradeModuleAsync(
        string? businessControllerClass,
        string version,
        CancellationToken cancellationToken = default);
}
