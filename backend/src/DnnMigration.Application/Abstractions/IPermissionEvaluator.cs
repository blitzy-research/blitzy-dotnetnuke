using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

// MIGRATION: this contract exists because reading permission ROWS and deciding what a caller
// consequently HOLDS are two different responsibilities, and the legacy codebase conflated them.
// PermissionController.vb, ModulePermissionController.vb and TabPermissionController.vb - 9, 18 and
// 15 public members - each mixed retrieval with allow-and-deny arithmetic over its own grant table,
// which is exactly how three copies of the same rule came to exist and drift.
//
// MIGRATION: the split is drawn where AAP section 0.4.3 draws it. The Domain repository contract,
// IPermissionRepository, mirrors the legacy provider blocks at core DataProvider.vb L279-L308 and is
// pure persistence: it reads and writes rows and returns no verdict. Evaluation lives in
// Infrastructure/Security/PermissionEvaluator.cs, which AAP section 0.4.3 describes as centralising
// "the permission-string evaluation that the three controllers duplicated", and this interface is
// how the application layer reaches it without seeing the store. It is declared here, alongside
// ITokenService, because the pattern is already established: the application layer holds the
// contract and Infrastructure/Security/ holds the mechanism.
//
// MIGRATION: evaluation is deliberately NOT expressed as a loop in the application layer over the
// repository's row reads. Deciding a caller's reachable grants requires correlating three tables -
// the grant table, the roles table and the owning module or page - and the portal-wide answer spans
// every module and page of a tenant. Composed here, that would be hundreds of round trips per
// question and a second implementation of the precedence rule sitting beside the first. One
// implementation that cannot disagree with itself is the whole point: two evaluators that can
// disagree present as an intermittent authorisation defect rather than as a failure.
//
// MIGRATION: the caller is always named by argument - an account identifier and the role names it
// holds. Nothing here takes a principal, a claims set or ambient request state, which is what
// PortalSecurity.vb did when it read the caller from HttpContext and from a cookie it had written
// itself at L98. Superuser short-circuiting is not performed here either: the legacy role test
// returned true for a host account at PortalSecurity.vb:L123 before examining a single role, so
// that decision belongs to the caller that knows the account, and no member below accepts a
// superuser flag.
namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Decides what a caller effectively holds, given the permission grants recorded in the store.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by <c>Infrastructure/Security/PermissionEvaluator.cs</c>, which is the single
/// authority on allow-and-deny precedence in this solution.
/// </para>
/// <para>
/// <strong>Precedence.</strong> A denying grant suppresses the key it names even when another grant
/// allows the same key, which is the legacy precedence. Suppression is scoped: a denial recorded
/// against one module suppresses that key on that module and nowhere else, so one obscure denial on
/// one forgotten page cannot strip a key the caller genuinely holds everywhere else.
/// </para>
/// <para>
/// <strong>Pseudo-principals.</strong> A grant's role identifier may hold a sentinel that names no
/// role row at all: -1 is "All Users", -2 is "Superuser" and -3 is "Unauthenticated Users". These
/// are honoured from the caller's authentication state rather than resolved against the roles table,
/// because resolving them as roles would silently discard every public and every anonymous grant.
/// <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so no genuine role can collide with a sentinel.
/// </para>
/// <para>
/// <strong>Tenant isolation.</strong> Role names are unique per portal rather than per installation,
/// so an implementation must resolve them only within the portal that owns the module or page being
/// evaluated. Resolving them installation-wide would let a grant to one tenant's "Administrators"
/// role be honoured for another tenant's.
/// </para>
/// <para>
/// <strong>Absence denies.</strong> A caller with no reachable grant holds nothing, which is the
/// closed default and the same answer an explicit denial produces.
/// </para>
/// <para>
/// <strong>Every member reports through <see cref="Result{T}"/>.</strong> M-10: an earlier revision
/// returned the verdict bare - a list, or a <see cref="bool"/> - which left this contract with no way
/// to say anything except the verdict itself. That mattered in one specific way: a denial and a
/// question about a module or page that does not exist produced the identical answer, so a caller that
/// needed to tell them apart had to read the subject a second time to find out which it had received.
/// The verdict semantics are unchanged by the conversion - <strong>a denial is still a SUCCESSFUL
/// outcome</strong>, because the caller asked a question and received an answer, and absence still
/// denies. What the outcome now also carries, when the named module or page does not exist, is an
/// advisory <see cref="Result.Reason"/> alongside the closed-default verdict, so that case is
/// distinguishable without a second read and without turning "you hold nothing" into an error. A
/// <see cref="Result.IsFailure"/> outcome is reserved for a question this contract genuinely cannot
/// answer.
/// </para>
/// </remarks>
public interface IPermissionEvaluator
{
    /// <summary>Returns every permission key a caller holds anywhere within one portal.</summary>
    /// <param name="portalId">
    /// Portal identifier. Grants in other portals are not considered. Both 0 and -1 are genuine
    /// values, because the portal identity column seeds at -1 and the shipped default portal is 0.
    /// </param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds; grants made to any of them apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful outcome carrying the distinct, upper-cased keys of the grants that allow access,
    /// after denying grants have suppressed the keys they deny within their own scope. A portal that
    /// holds no grants at all yields an empty set rather than a reason: unlike a module or a page, the
    /// portal is named by the caller's own resolved tenant rather than looked up here, so there is no
    /// "no such subject" case for this member to report.
    /// </returns>
    /// <remarks>
    /// This is the portal-level union an administration shell needs in order to decide which sections
    /// to offer, and it is what populates the permission claims of an issued access token. It exists
    /// as one member rather than as a loop over the two scoped reads below because a portal carrying
    /// hundreds of pages and modules would otherwise cost hundreds of round trips to answer one
    /// question the store can answer as a single set operation.
    /// </remarks>
    Task<Result<IReadOnlyList<string>>> ListEffectivePortalPermissionKeysAsync(
        int portalId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the permission keys a caller holds on one module.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds; grants made to any of them apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful outcome carrying the distinct, upper-cased keys of the grants that allow access. A
    /// grant that denies access suppresses its key even when another grant on the same module allows it.
    /// When no module carries the supplied identifier the outcome is still successful and still carries
    /// the empty closed default, with an advisory reason naming the absence.
    /// </returns>
    Task<Result<IReadOnlyList<string>>> ListEffectiveModulePermissionKeysAsync(
        int moduleId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the permission keys a caller holds on one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds; grants made to any of them apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful outcome carrying the distinct, upper-cased keys of the grants that allow access.
    /// When no page carries the supplied identifier the outcome is still successful and still carries the
    /// empty closed default, with an advisory reason naming the absence.
    /// </returns>
    Task<Result<IReadOnlyList<string>>> ListEffectiveTabPermissionKeysAsync(
        int tabId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Determines whether a caller holds one specific key on one module.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful outcome carrying <see langword="true"/> when the caller holds the key on that module.
    /// A denial is <see langword="false"/> on a successful outcome, never a failure. When no module
    /// carries the supplied identifier the verdict is the closed default <see langword="false"/>, with an
    /// advisory reason naming the absence.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Defined as a membership test against the very set the module-scoped read above returns, so a
    /// verdict and a listing are incapable of contradicting one another.
    /// </para>
    /// <para>
    /// THIS TAKES NO PLACEMENT, AND THAT IS CORRECT RATHER THAN AN OMISSION. The schema stores module grants
    /// against the module - <c>ModulePermission</c> keys <c>ModuleID</c> and nothing else - and stores no
    /// per-placement grant table at all, so there is no placement-scoped row set for this member to read. A
    /// module addressed on a particular page is decided from that page's own <c>TabPermission</c> rows, which
    /// is <see cref="HasTabPermissionAsync"/>.
    /// </para>
    /// <para>
    /// CONSEQUENTLY THIS MEMBER IS NOT A PLACEMENT-AWARE DECISION AND MUST NOT BE USED AS ONE. A module whose
    /// <c>InheritViewPermissions</c> flag is set takes its view key from the page it sits on, and composing the
    /// module's own grants with the addressed page's grants is <c>IPermissionService</c>'s responsibility, not
    /// this one's. Authorising a request from this member alone would consult the module's stored grants while
    /// ignoring the inheritance flag entirely - which for an inheriting module is the wrong question, not a
    /// stricter form of the right one.
    /// </para>
    /// </remarks>
    Task<Result<bool>> HasModulePermissionAsync(
        int moduleId,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Determines whether a caller holds one specific key on one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful outcome carrying <see langword="true"/> when the caller holds the key on that page. A
    /// denial is <see langword="false"/> on a successful outcome, never a failure. When no page carries
    /// the supplied identifier the verdict is the closed default <see langword="false"/>, with an
    /// advisory reason naming the absence.
    /// </returns>
    Task<Result<bool>> HasTabPermissionAsync(
        int tabId,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a caller holds one specific key on AT LEAST ONE of several pages, at a cost that
    /// does not grow with how many pages are named.
    /// </summary>
    /// <param name="tabIds">
    /// The pages to consider. Every value denotes exactly the page bearing it - the page identity seeds at 0
    /// - and a page naming no row simply cannot grant anything. An empty set holds no grant and is answered
    /// <see langword="false"/> without any read.
    /// </param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful outcome carrying <see langword="true"/> when at least one named page grants the key to
    /// the caller. A denial is <see langword="false"/> on a successful outcome, never a failure.
    /// </returns>
    /// <remarks>
    /// <para>
    /// EXISTENTIAL, AND EXISTENTIAL PER PAGE. A page is judged exactly as
    /// <see cref="HasTabPermissionAsync"/> judges it - so a page that denies the key contributes nothing
    /// rather than vetoing the answer - and the verdict is true if any page's own verdict is true. Asking
    /// this member is therefore equivalent to asking the single-page member once per page and disjoining the
    /// answers; what differs is the cost, which is a fixed number of reads instead of a fixed number PER
    /// PAGE.
    /// </para>
    /// <para>
    /// It exists for the module authorisation path. A module placed on every page of a tenant is an ordinary
    /// configuration, and deciding "may this caller administer it from a page it can edit" by testing each
    /// placement in turn made one authorisation check proportional to the tenant's page tree. It is
    /// deliberately NOT a general-purpose bulk API: nothing here reports WHICH page granted the key, because
    /// no caller needs to know and reporting it would invite a caller to re-derive a decision this member
    /// has already made.
    /// </para>
    /// </remarks>
    Task<Result<bool>> HasAnyTabPermissionAsync(
        IReadOnlyCollection<int> tabIds,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);
}
