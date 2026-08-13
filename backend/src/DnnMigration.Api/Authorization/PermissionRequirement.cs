using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// The kind of DotNetNuke item a <see cref="PermissionRequirement"/> asks about, and therefore which
/// permission triad an authorisation decision has to consult.
/// </summary>
/// <remarks>
/// <para>
/// <b>The set is closed at three members, and the closure test is what the Application layer can
/// answer.</b> A permission evaluator can answer a module-scoped question, a tab-scoped one, and whether
/// the caller holds a key on ANY page of the tenant; a fourth member would name a question nothing can
/// answer, and both ways of absorbing that are defects.
/// </para>
/// <para>
/// <b>Why it is declared here rather than in the Domain enumerations.</b> The value has no persisted
/// representation and never crosses the wire. It exists only so a policy registration can name which
/// evaluator to use, which makes it an API-layer dispatch concern; the Domain enumerations each mirror a
/// real database column.
/// </para>
/// </remarks>
public enum PermissionScope
{
    /// <summary>
    /// The permission triad of a single module instance: resolve the module identifier from the current
    /// request and ask the module-scoped evaluator.
    /// </summary>
    Module,

    /// <summary>
    /// The permission triad of a single tab, the DotNetNuke page abstraction: resolve the tab identifier
    /// from the current request and ask the tab-scoped evaluator.
    /// </summary>
    Tab,

    /// <summary>
    /// The permission triads of EVERY page of the resolved tenant, asked as a disjunction: the caller
    /// satisfies the requirement by administering that tenant or by holding the permission on at least one
    /// of its pages.
    /// </summary>
    /// <remarks>
    /// <b>The one scope that reads no item identifier from the route, and that is the point of it.</b> The
    /// two scopes above answer "may this caller act on the thing this route names". This one answers "is
    /// this caller capable of the operation somewhere in this tenant", which is the question a SUPPORTING
    /// read has to answer when the item does not exist yet.
    /// </remarks>
    Portal
}

/// <summary>
/// An authorisation requirement naming one DotNetNuke permission key and the kind of item it is claimed
/// against, so the policy engine can express declaratively what the legacy application expressed
/// imperatively inside a page.
/// </summary>
/// <remarks>
/// <para>
/// <b>A requirement instance is shared, so it must hold no request state.</b> One instance is built per
/// policy while policies are registered and is then used by every request that names the policy. That is
/// why no item identifier, caller identity or role name appears on it: storing a per-request subject on a
/// shared instance would leak one caller's subject into another caller's decision.
/// </para>
/// <para>
/// <b>The key is closed where the legacy key was a bare string.</b> Every legacy overload took its
/// permission key as text, so a misspelling produced a silent negative rather than a diagnostic. Here it is
/// <see cref="PermissionKey"/>, and a misspelling cannot compile.
/// </para>
/// </remarks>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Creates a requirement for one permission key at one scope. Invoked once per policy while policies
    /// are being registered, never per request, which is why the instance must be free of request state.
    /// </summary>
    /// <param name="permission">The permission key the caller must hold.</param>
    /// <param name="scope">
    /// The kind of item the key is claimed against, which selects the evaluator to consult and the
    /// identifier to resolve.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when either argument is not a declared member of its enumeration.
    /// </exception>
    public PermissionRequirement(PermissionKey permission, PermissionScope scope)
    {
        if (!Enum.IsDefined(permission))
        {
            throw new ArgumentOutOfRangeException(
                nameof(permission),
                permission,
                "The permission key is not a declared member of the permission key enumeration.");
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scope),
                scope,
                "The permission scope is not a declared member of the permission scope enumeration.");
        }

        Permission = permission;
        Scope = scope;
    }

    /// <summary>The permission key the caller must hold for this requirement to be satisfied.</summary>
    public PermissionKey Permission { get; }

    /// <summary>
    /// The kind of item the key is claimed against, selecting which evaluator to consult and which
    /// identifier to resolve from the current request.
    /// </summary>
    public PermissionScope Scope { get; }
}
