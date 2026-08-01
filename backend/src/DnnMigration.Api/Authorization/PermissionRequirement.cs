using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// The kind of DotNetNuke item a <see cref="PermissionRequirement"/> asks about, and
/// therefore the permission triad an authorisation decision must consult. The
/// requirement handler treats this value purely as a dispatch discriminator: it selects
/// which evaluator method to call and which identifier to resolve from the current
/// request.
/// </summary>
/// <remarks>
/// <para>
/// EXACTLY TWO MEMBERS, AND THE SET IS CLOSED. The permission evaluator in the
/// Infrastructure layer exposes precisely two decision methods,
/// <c>HasModulePermissionAsync</c> and <c>HasTabPermissionAsync</c>. A third member
/// would therefore describe a question nothing in the solution can answer, and both
/// ways of absorbing it are defects: silently denying every request that used the
/// policy, which no test would catch because the registration still reads as
/// legitimate; or inventing a parallel evaluation path here in the API layer, which
/// would place business logic above the Application layer and leave the codebase with
/// two evaluators free to disagree.
/// </para>
/// <para>
/// WHY THERE IS NO Portal MEMBER. Site-wide administration is a role question rather
/// than a permission-triad question, so it is gated by the separate role-based policy
/// named by <c>PolicyNames.PortalAdministrator</c>. That policy is registered with the
/// framework's own role requirement, never produces a
/// <see cref="PermissionRequirement"/>, and so never reaches the requirement handler at
/// all. The legacy surface agrees: the three permission controllers expose only
/// tab-scoped and module-scoped evaluation, and no site-wide equivalent exists anywhere
/// in the legacy tree.
/// </para>
/// <para>
/// WHY THERE IS NO Folder MEMBER. The migrated domain model carries module and tab
/// permission entities only; there is no file-store permission entity, and file
/// management sits beyond the boundary of this migration. The evaluator consequently
/// never admits the file-store permission code, so such a member would be unreachable
/// for the same reason.
/// </para>
/// <para>
/// WHY IT LIVES HERE AND NOT IN THE DOMAIN ENUMERATION FOLDER. This value has no
/// persisted representation and never crosses the wire; it is an API-layer dispatch
/// concern that exists only so a policy registration can name the evaluator method to
/// use. The Domain enumeration folder holds nine enumerations that each mirror a real
/// database column, and this is not one of them. Declaring it beside its only consumer
/// keeps the authorisation folder at the three files its specification names, and keeps
/// a non-persisted API concern from reaching into the Domain layer.
/// </para>
/// <para>
/// Member names are PascalCase, deliberately unlike <see cref="PermissionKey"/>, whose
/// members are upper case because their names are values a production database already
/// holds. Nothing here is persisted or serialised, so the ordinals are incidental and
/// no member carries an explicit value.
/// </para>
/// <para>
/// MIGRATION: the legacy code needed no scope discriminator because it had no single
/// entry point. It offered two unrelated families of shared functions, one family per
/// item kind, and every call site chose a family by name. Naming that choice as a value
/// is what allows one injectable handler to serve both.
/// </para>
/// </remarks>
public enum PermissionScope
{
    /// <summary>
    /// The permission triad of a single module instance. The handler resolves the
    /// module identifier from the current request and calls
    /// <c>HasModulePermissionAsync</c>. Replaces the legacy shared functions at
    /// <c>ModulePermissionController.vb:L33-L50</c> and <c>:L52-L56</c>.
    /// </summary>
    Module,

    /// <summary>
    /// The permission triad of a single tab, the DotNetNuke page abstraction. The
    /// handler resolves the tab identifier from the current request and calls
    /// <c>HasTabPermissionAsync</c>. Replaces the legacy shared functions at
    /// <c>TabPermissionController.vb:L33-L36</c> and <c>:L38-L54</c>.
    /// </summary>
    Tab
}

/// <summary>
/// An authorisation requirement naming one DotNetNuke permission key and the kind of
/// item it is claimed against, so that the ASP.NET Core policy engine can express
/// declaratively what the legacy application expressed imperatively inside a page.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS TYPE IS. An immutable data carrier and nothing else: no evaluation, no
/// iteration, no I/O, no dependencies. It states the question. The answer is computed by
/// <c>PermissionAuthorizationHandler.cs</c>, which resolves the caller's identity and
/// the item identifier from the current request and delegates the decision to the
/// evaluator in the Infrastructure layer.
/// </para>
/// <para>
/// WHAT IT REPLACES. Five shared functions across two legacy classes:
/// <c>TabPermissionController.vb:L33-L36</c> and <c>:L38-L54</c>, together with
/// <c>ModulePermissionController.vb:L33-L50</c>, <c>:L52-L56</c> and the deprecated
/// <c>:L376-L381</c>. That last overload is marked deprecated in the legacy source
/// itself, which directs callers to the three-argument form, and it is deliberately not
/// carried across. Each of the five accepted its permission key as a bare string, so a
/// misspelling yielded a silent negative answer rather than a diagnostic; here the key
/// is the closed <see cref="PermissionKey"/> enumeration, so a misspelling cannot
/// compile. Measured across the whole legacy tree, only the edit and view keys ever
/// reach an evaluation call site.
/// </para>
/// <para>
/// AMBIENT STATE IS GONE. The single-argument legacy overload at
/// <c>TabPermissionController.vb:L33</c> obtained its subject from a mutable per-request
/// composite fetched from an ambient item bag, then walked into that composite's
/// currently-active tab, which was itself settable at <c>PortalSettings.vb:L398</c> and
/// <c>:L548</c>. The collection overload at <c>:L38-L54</c> went as far as fetching the
/// same composite at <c>:L39</c> and then never using it. This type receives its subject
/// explicitly and immutably, and reaches for nothing ambient.
/// </para>
/// <para>
/// STATIC BECOMES INJECTABLE. All five legacy functions were shared members, so no test
/// could substitute them. Here the decision belongs to a handler resolved from the
/// container, and this type is that handler's immutable input.
/// </para>
/// <para>
/// WHAT THIS TYPE DELIBERATELY DOES NOT CARRY. It describes what is being asked for,
/// never who holds a grant, and never which item happens to be in play on one
/// particular request.
/// </para>
/// <list type="bullet">
///   <item>
///     No grant flag. Whether an assignment row grants or denies is a column on the
///     persisted assignment, read at <c>TabPermissionController.vb:L152</c> and
///     <c>ModulePermissionController.vb:L155</c>. Deny precedence belongs to the
///     evaluator, not to the question being asked.
///   </item>
///   <item>
///     No role name and no user identifier. Those describe who holds a grant. The
///     legacy loops tested them at <c>TabPermissionController.vb:L43</c> and
///     <c>:L47</c>; the handler now supplies the caller's identity once per request.
///   </item>
///   <item>
///     No delimited permission text. The legacy builders at
///     <c>TabPermissionController.vb:L214-L227</c> and
///     <c>ModulePermissionController.vb:L239-L252</c> concatenated role names and
///     bracketed user identifiers into one semicolon-delimited string. No such string
///     appears on any migrated contract.
///   </item>
///   <item>
///     No item identifiers of any kind, and no numeric sentinel. Item identifiers are
///     per-request values, whereas a requirement instance is built once while policies
///     are registered and is then shared by every request that uses the policy. Storing
///     a request-specific identifier on a shared instance would leak one caller's
///     subject into another caller's decision.
///   </item>
///   <item>
///     No permission code. The subsystem scope of a catalogue row stays a plain string
///     property on the migrated permission entity. <see cref="PermissionScope"/> is not
///     that concept renamed, and its members are deliberately not named after those
///     values.
///   </item>
/// </list>
/// <para>
/// NO VALUE SEMANTICS. The policy engine resolves handlers by requirement type, and
/// nothing in the solution compares two requirements or places one in a hashed set, so
/// this type declares no equality members and stays a reference type. The legacy module
/// assignment class did declare value equality, at <c>ModulePermission.vb:L158</c>, but
/// that behaviour belongs to a persisted assignment rather than to a question asked of
/// the policy engine.
/// </para>
/// <para>
/// MIGRATION: the legacy checks were imperative, ambient and shared; the migrated check
/// is declarative, explicit and injected. Behaviour is preserved - the same two keys,
/// the same two item kinds, and the same deny-by-default answer when no assignment
/// matches - while the mechanism changes from an in-page function call to a named policy
/// applied by attribute.
/// </para>
/// </remarks>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Creates a requirement for one permission key at one scope. Invoked once per
    /// policy while policies are being registered at start-up, never per request, which
    /// is why the resulting instance must be free of request state.
    /// </summary>
    /// <param name="permission">
    /// The permission key the caller must hold. Only the view and edit keys are used by
    /// the registered policies; the two file-store keys exist in the permission
    /// catalogue but are never the subject of a policy.
    /// </param>
    /// <param name="scope">
    /// The kind of item the key is claimed against, which selects the evaluator method
    /// the handler will call and the identifier it must resolve.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when either argument is not a declared member of its enumeration. Both
    /// arguments are enumerations, and the language permits any integer to be cast to an
    /// enumeration type, so an undeclared value can reach this constructor through a cast
    /// or through a mis-parsed configuration value. Failing loudly here, while policies
    /// are still being registered, is far safer than the alternative: an undeclared key
    /// matches no assignment and an undeclared scope matches no evaluator method, so such
    /// a requirement would deny every request while its registration still read as
    /// legitimate. The offending value is never coerced, clamped or defaulted.
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
    /// Replaces the bare string argument that every legacy overload accepted, so the set
    /// of askable keys is now closed and checked by the compiler.
    /// </summary>
    public PermissionKey Permission { get; }

    /// <summary>
    /// The kind of item the key is claimed against. The handler reads it to choose
    /// between the module and tab evaluator methods, and to know which identifier to
    /// resolve from the current request.
    /// </summary>
    public PermissionScope Scope { get; }
}
