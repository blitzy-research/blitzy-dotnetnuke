using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Abstractions;

/// <summary>Decides what a caller effectively holds, given the permission grants recorded in the store.</summary>
/// <remarks>
/// <para>
/// Implemented by <c>Infrastructure/Security/PermissionEvaluator.cs</c>, which is the single authority on
/// allow-and-deny precedence in this solution.
/// </para>
/// <para>
/// <strong>Precedence.</strong> A denying grant suppresses the key it names even when another grant allows
/// the same key, which is the legacy precedence. Suppression is scoped: a denial recorded against one
/// module suppresses that key on that module and nowhere else, so one obscure denial on one forgotten page
/// cannot strip a key the caller genuinely holds everywhere else.
/// </para>
/// </remarks>
public interface IPermissionEvaluator
{
    /// <summary>Returns every permission key a caller holds anywhere within one portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds; grants made to any of them apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful outcome carrying the distinct, upper-cased keys of the grants that allow access, after
    /// denying grants have suppressed the keys they deny within their own scope.
    /// </returns>
    /// <remarks>
    /// This is the portal-level union an administration shell needs in order to decide which sections to
    /// offer, and it is what populates the permission claims of an issued access token.
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
    /// A successful outcome carrying the distinct, upper-cased keys of the grants that allow access.
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
    /// </returns>
    /// <remarks>
    /// THIS TAKES NO PLACEMENT, AND THAT IS CORRECT RATHER THAN AN OMISSION. The schema stores module
    /// grants against the module - <c>ModulePermission</c> keys <c>ModuleID</c> and nothing else - and
    /// stores no per-placement grant table at all, so there is no placement-scoped row set for this member
    /// to read.
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
    /// A successful outcome carrying <see langword="true"/> when the caller holds the key on that page.
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
    /// <param name="tabIds">The pages to consider.</param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful outcome carrying <see langword="true"/> when at least one named page grants the key to
    /// the caller.
    /// </returns>
    Task<Result<bool>> HasAnyTabPermissionAsync(
        IReadOnlyCollection<int> tabIds,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Reports WHICH of the named pages grant one permission key to the caller.</summary>
    /// <param name="tabIds">The pages to judge.</param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful outcome carrying the identifiers of the granting pages, in the order they were named.
    /// </returns>
    /// <remarks>
    /// THE SIBLING OF <see cref="HasAnyTabPermissionAsync"/>, DIFFERING ONLY IN WHAT IT REPORTS. Both judge
    /// each page exactly as <see cref="HasTabPermissionAsync"/> judges it, so a page that denies the key
    /// contributes nothing rather than vetoing its neighbours, and both issue the same fixed number of
    /// reads regardless of how many pages are named.
    /// </remarks>
    Task<Result<IReadOnlyList<int>>> ListTabsWithPermissionAsync(
        IReadOnlyCollection<int> tabIds,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);
}
