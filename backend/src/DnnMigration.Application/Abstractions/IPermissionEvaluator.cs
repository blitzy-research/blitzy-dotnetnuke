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
    /// The distinct, upper-cased keys of the grants that allow access, after denying grants have
    /// suppressed the keys they deny within their own scope.
    /// </returns>
    /// <remarks>
    /// This is the portal-level union an administration shell needs in order to decide which sections
    /// to offer, and it is what populates the permission claims of an issued access token. It exists
    /// as one member rather than as a loop over the two scoped reads below because a portal carrying
    /// hundreds of pages and modules would otherwise cost hundreds of round trips to answer one
    /// question the store can answer as a single set operation.
    /// </remarks>
    Task<IReadOnlyList<string>> ListEffectivePortalPermissionKeysAsync(
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
    /// The distinct, upper-cased keys of the grants that allow access. A grant that denies access
    /// suppresses its key even when another grant on the same module allows it.
    /// </returns>
    Task<IReadOnlyList<string>> ListEffectiveModulePermissionKeysAsync(
        int moduleId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the permission keys a caller holds on one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds; grants made to any of them apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The distinct, upper-cased keys of the grants that allow access.</returns>
    Task<IReadOnlyList<string>> ListEffectiveTabPermissionKeysAsync(
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
    /// <returns><see langword="true"/> when the caller holds the key on that module.</returns>
    /// <remarks>
    /// Defined as a membership test against the very set the module-scoped read above returns, so a
    /// verdict and a listing are incapable of contradicting one another.
    /// </remarks>
    Task<bool> HasModulePermissionAsync(
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
    /// <returns><see langword="true"/> when the caller holds the key on that page.</returns>
    Task<bool> HasTabPermissionAsync(
        int tabId,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);
}
