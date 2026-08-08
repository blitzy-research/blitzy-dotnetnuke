using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// The kind of DotNetNuke item a <see cref="PermissionRequirement"/> asks about, and therefore
/// which permission triad an authorisation decision has to consult.
/// </summary>
/// <remarks>
/// <para>
/// <b>The set is closed at three members, and the closure test is what the Application layer can
/// answer.</b> A permission evaluator can answer a module-scoped question, a tab-scoped one, and
/// whether the caller holds a key on ANY page of the tenant; a fourth member would name a question
/// nothing can answer, and both ways of absorbing that are defects. Denying every request that used
/// the policy is silent, because the registration still reads as legitimate. Answering it in this
/// layer instead puts business logic above the Application layer and leaves two evaluators free to
/// disagree.
/// </para>
/// <para>
/// The third member was added WITH the capability that answers it, not ahead of it - the Application
/// layer's own contract gained the portal-scoped question at the same time - which is the only way a
/// member may be added. A scope whose question this layer would have to answer for itself belongs
/// nowhere in this enumeration.
/// </para>
/// <para>
/// <b>Site-wide administration is not a member.</b> It is a role question, gated by
/// <see cref="PolicyNames.PortalAdministrator"/> through the framework's own role requirement, so
/// it never produces a <see cref="PermissionRequirement"/> at all. File-store permissions are not
/// a member either: the migrated model carries module and tab permission entities only.
/// </para>
/// <para>
/// <b>Why it is declared here rather than in the Domain enumerations.</b> The value has no
/// persisted representation and never crosses the wire. It exists only so a policy registration
/// can name which evaluator to use, which makes it an API-layer dispatch concern; the Domain
/// enumerations each mirror a real database column.
/// </para>
/// <para>
/// Nothing here is persisted or serialised, so ordinals are incidental and no member carries an
/// explicit value. The PascalCase names are deliberately unlike <see cref="PermissionKey"/>, whose
/// upper-case member names are values a production database already holds.
/// </para>
/// </remarks>
public enum PermissionScope
{
    /// <summary>
    /// The permission triad of a single module instance: resolve the module identifier from the
    /// current request and ask the module-scoped evaluator.
    /// </summary>
    Module,

    /// <summary>
    /// The permission triad of a single tab, the DotNetNuke page abstraction: resolve the tab
    /// identifier from the current request and ask the tab-scoped evaluator.
    /// </summary>
    Tab,

    /// <summary>
    /// The permission triads of EVERY page of the resolved tenant, asked as a disjunction: the
    /// caller satisfies the requirement by administering that tenant or by holding the permission
    /// on at least one of its pages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one scope that reads no item identifier from the route, and that is the point of it.</b>
    /// The two scopes above answer "may this caller act on the thing this route names". This one
    /// answers "is this caller capable of the operation somewhere in this tenant", which is the
    /// question a SUPPORTING read has to answer when the item does not exist yet. The module
    /// placement form is the case that forces it: its create action names no page, because the
    /// target page arrives in the request body, so the form has to be filled before any page
    /// identifier exists to scope a permission to.
    /// </para>
    /// <para>
    /// MIGRATION: this reproduces the gate at <c>ModuleSettings.ascx.vb:L191</c>, which admitted
    /// <c>IsInRoles(PortalSettings.AdministratorRoleName)</c> OR
    /// <c>IsInRoles(PortalSettings.ActiveTab.AdministratorRoles)</c> - the tenant's administrators or
    /// the roles holding EDIT on the page being administered from. The legacy notion of an active page
    /// was ambient request state this solution deliberately does not have, so the page-administrator
    /// arm generalises from "the one page they arrived on" to "any page they hold the key on". That is
    /// the narrowest faithful reading: it admits exactly the callers who could have arrived from SOME
    /// page, and no others.
    /// </para>
    /// <para>
    /// <b>It is not a licence to enumerate.</b> Admission says only that the caller holds the key
    /// somewhere in the tenant; it does not say which pages, and an endpoint carrying this scope must
    /// still narrow what it RETURNS to what the caller may act on. A caller holding the key nowhere is
    /// refused outright, so the disclosure posture of an endpoint moved onto this scope is never wider
    /// than it was under tenant administration for callers with no grant at all.
    /// </para>
    /// </remarks>
    Portal
}

/// <summary>
/// An authorisation requirement naming one DotNetNuke permission key and the kind of item it is
/// claimed against, so the policy engine can express declaratively what the legacy application
/// expressed imperatively inside a page.
/// </summary>
/// <remarks>
/// <para>
/// <b>It states the question and never answers it.</b> An immutable data carrier with no
/// evaluation, no I/O and no dependencies. An authorisation handler must resolve the caller's
/// identity and the item identifier from the current request and delegate the decision to a
/// permission evaluator; deny-by-default when no assignment matches is that evaluator's
/// obligation, as is deny precedence over grant.
/// </para>
/// <para>
/// <b>A requirement instance is shared, so it must hold no request state.</b> One instance is
/// built per policy while policies are registered and is then used by every request that names
/// the policy. That is why no item identifier, caller identity or role name appears on it:
/// storing a per-request subject on a shared instance would leak one caller's subject into
/// another caller's decision.
/// </para>
/// <para>
/// <b>The key is closed where the legacy key was a bare string.</b> Every legacy overload took
/// its permission key as text, so a misspelling produced a silent negative rather than a
/// diagnostic. Here it is <see cref="PermissionKey"/>, and a misspelling cannot compile. Only the
/// view and edit keys are ever the subject of a policy; the two file-store keys exist in the
/// catalogue and are never asked for.
/// </para>
/// <para>
/// <b>No delimited permission text and no grant flag.</b> The legacy builders concatenated role
/// names and bracketed user identifiers into one semicolon-delimited string, and read the
/// grant-or-deny flag off the persisted assignment. Neither belongs on the question: no such
/// string appears on any migrated contract, and the flag stays a column on the assignment entity.
/// </para>
/// <para>
/// <b>No value semantics.</b> The policy engine resolves handlers by requirement type, and nothing
/// compares two requirements or puts one in a hashed set, so this type declares no equality
/// members and stays a reference type. Value equality belongs to a persisted assignment, not to a
/// question asked of the policy engine.
/// </para>
/// <para>
/// MIGRATION: behaviour is preserved - the same two keys, the same two item kinds, the same
/// deny-by-default answer - while the mechanism changes from an ambient, shared, in-page function
/// call to a named policy applied by attribute and served by an injected handler.
/// </para>
/// </remarks>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Creates a requirement for one permission key at one scope. Invoked once per policy while
    /// policies are being registered, never per request, which is why the instance must be free of
    /// request state.
    /// </summary>
    /// <param name="permission">The permission key the caller must hold.</param>
    /// <param name="scope">
    /// The kind of item the key is claimed against, which selects the evaluator to consult and the
    /// identifier to resolve.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when either argument is not a declared member of its enumeration. The language
    /// permits any integer to be cast to an enumeration type, so an undeclared value can arrive
    /// through a cast or a mis-parsed configuration value. Failing loudly while policies are still
    /// being registered is far safer than the alternative: an undeclared key matches no assignment
    /// and an undeclared scope matches no evaluator, so the requirement would deny every request
    /// while its registration still read as legitimate. The offending value is never coerced,
    /// clamped or defaulted.
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

    /// <summary>
    /// The permission key the caller must hold for this requirement to be satisfied.
    /// </summary>
    public PermissionKey Permission { get; }

    /// <summary>
    /// The kind of item the key is claimed against, selecting which evaluator to consult and which
    /// identifier to resolve from the current request.
    /// </summary>
    public PermissionScope Scope { get; }
}
