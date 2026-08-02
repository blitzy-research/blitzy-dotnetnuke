using DnnMigration.Domain.Enums;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// One applicable permission grant, reduced to the three facts a decision depends on.
/// </summary>
/// <param name="ScopeKind">
/// Which kind of thing the grant was made against - see <see cref="PermissionScopeKind"/>. It exists so
/// that module grants and page grants can travel in one sequence without their identifiers colliding:
/// both <c>Modules.ModuleID</c> and <c>Tabs.TabID</c> seed at 0, so an identifier alone does not say
/// what it identifies.
/// </param>
/// <param name="ScopeId">
/// Identifier of the module or page the grant was made against. Carries no sentinel meaning: 0 is a real
/// identifier for both, and -1 is a real portal identifier, so nothing here may be read as "absent".
/// </param>
/// <param name="PermissionKey">
/// The permission key exactly as stored in <c>Permission.PermissionKey</c>. Compared
/// case-insensitively, because the legacy column is <c>varchar</c> under a case-insensitive collation
/// and the shipped rows are not consistently cased.
/// </param>
/// <param name="AllowAccess">
/// The value of the grant's <c>AllowAccess bit NOT NULL</c> column: <see langword="true"/> allows,
/// <see langword="false"/> denies.
/// </param>
internal readonly record struct PermissionGrant(
    PermissionScopeKind ScopeKind,
    int ScopeId,
    string PermissionKey,
    bool AllowAccess);

/// <summary>Distinguishes the two kinds of thing a permission grant can be made against.</summary>
internal enum PermissionScopeKind
{
    /// <summary>The grant was made against a module instance, in <c>ModulePermission</c>.</summary>
    Module = 0,

    /// <summary>The grant was made against a page, in <c>TabPermission</c>.</summary>
    Tab = 1,
}

/// <summary>
/// The single place in this solution where allow-and-deny precedence over permission grants is decided.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this supersedes logic that the legacy codebase duplicated across three controllers -
/// <c>PermissionController.vb</c>, <c>ModulePermissionController.vb</c> and
/// <c>TabPermissionController.vb</c>, carrying 9, 18 and 15 public members respectively - each of which
/// re-derived the same rules over its own grant table. Centralising them here is the whole point:
/// <em>two evaluators that can disagree is the worst available result in this area</em>, because the
/// disagreement shows up as an intermittent authorisation defect rather than as a failure.
/// </para>
/// <para>
/// <strong>Where the boundary lies.</strong> This type decides precedence; it does not fetch rows.
/// <c>PermissionRepository</c> establishes <em>which grants a caller reaches</em> - a retrieval question,
/// answered by the database because it needs the role table - and then hands those grants here to learn
/// <em>what the caller consequently holds</em>. Both effective-key reads and both single-key verdicts run
/// through <see cref="Reduce"/>, so a verdict and a listing are incapable of contradicting one another:
/// <see cref="Holds"/> is defined as a membership test against the very set <see cref="Reduce"/> returns
/// rather than as a second rule that happens to agree today.
/// </para>
/// <para>
/// <strong>Deny beats allow, within a scope.</strong> A denying grant suppresses its key on the module or
/// page it was made against, even when another grant allows the same key there. It does not suppress that
/// key elsewhere. The legacy string builders at <c>ModulePermissionController.vb:L243</c> and
/// <c>TabPermissionController.vb:L218</c> filtered on the allow flag being set and so discarded deny rows
/// altogether, but they were assembling a value for a permission grid rather than deciding access, which
/// is why their behaviour is not the behaviour reproduced here.
/// </para>
/// <para>
/// <strong>Why suppression is scoped rather than global.</strong> The portal-wide read exists to answer
/// "which sections may this caller be offered", and it spans every module and page of a tenant. Applying
/// a denial across that whole union would let one obscure denial on one forgotten page remove a key the
/// caller genuinely holds everywhere else - strictly harsher than any legacy behaviour and wrong for the
/// question being asked. Suppression is therefore correlated to the scope that carries the denial, which
/// is also exactly what the two scoped reads do, so the three reads share one rule rather than two.
/// </para>
/// <para>
/// <strong>Pseudo-roles are sentinel identifiers, not rows.</strong> The constants published here are the
/// measured legacy values from <c>Library/Components/Shared/Globals.vb</c> lines 95 to 98, and the write
/// path that produces them is <c>TabController.vb:L901-L904</c> and
/// <c>ModuleController.vb:L354-L357</c>, both of which map a display name onto a negative identifier
/// before storing it. This is why neither grant table has ever carried a foreign key to <c>Roles</c>, and
/// why the 04.05.00 script could make <c>RoleID</c> nullable: a sentinel identifies no role row. They are
/// published here, rather than restated in the repository, so that the query which selects reachable
/// grants and the rule which interprets them cannot drift apart. <c>Roles.RoleID</c> is
/// <c>IDENTITY(0, 1)</c>, so no genuine role can ever collide with a negative sentinel.
/// </para>
/// <para>
/// This type is stateless and therefore safe to register as a singleton and to share across requests.
/// </para>
/// </remarks>
internal sealed class PermissionEvaluator
{
    /// <summary>
    /// The sentinel role identifier that admits every caller, authenticated or not.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleAllUsers = "-1"</c> at <c>Library/Components/Shared/Globals.vb:L95</c>,
    /// displayed as "All Users". <c>PortalSecurity.IsInRoles</c> admitted it unconditionally at line 125.
    /// </remarks>
    public const int AllUsersRoleId = -1;

    /// <summary>
    /// The sentinel role identifier reserved for host accounts, deliberately never matched here.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleSuperUser = "-2"</c> at <c>Library/Components/Shared/Globals.vb:L96</c>. It is
    /// published for completeness and for the reader who finds a -2 in a grant row, but no rule below
    /// matches it. A superuser is admitted before any grant is examined - which is what
    /// <c>PortalSecurity.IsInRoles</c> did at line 123, testing <c>IsSuperUser</c> first and returning
    /// true without looking at a single role - so the only caller this sentinel could serve has already
    /// been answered. Matching it here would be dead code that looked like a security rule.
    /// </remarks>
    public const int SuperUserRoleId = -2;

    /// <summary>
    /// The sentinel role identifier that admits only callers who have not signed in.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleUnauthUser = "-3"</c> at <c>Library/Components/Shared/Globals.vb:L97</c>,
    /// displayed as "Unauthenticated Users". <c>PortalSecurity.IsInRoles</c> admitted it only when
    /// <c>context.Request.IsAuthenticated</c> was false, at line 124 - so a grant to this sentinel is
    /// genuinely narrower than a grant to every user, and the two are not interchangeable.
    /// </remarks>
    public const int UnauthenticatedRoleId = -3;

    /// <summary>
    /// The sentinel role identifier that admits nobody.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleNothing = "-4"</c> at <c>Library/Components/Shared/Globals.vb:L98</c>. No rule
    /// matches it, which is the correct treatment rather than an omission: a grant carrying it reaches no
    /// caller, so leaving it unmatched is precisely what it asks for.
    /// </remarks>
    public const int NoRoleId = -4;

    /// <summary>
    /// Reduces a caller's reachable grants to the distinct permission keys the caller consequently holds.
    /// </summary>
    /// <param name="grants">
    /// Every grant the caller reaches, in any order, possibly spanning several modules and pages. An
    /// empty sequence is legitimate and yields an empty result.
    /// </param>
    /// <returns>
    /// The surviving keys, upper-cased, without duplicates, in a stable ordinal order. Upper-casing
    /// matches the closed <see cref="PermissionKey"/> enumeration and the strings the Angular
    /// <c>hasPermission</c> directive matches on; the ordering exists so that a token minted twice from
    /// the same grants carries an identical claim set both times.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="grants"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A key survives when some grant allows it on a scope where no grant denies it. Suppression is
    /// evaluated per scope, for the reason given in the type remarks, so a denial on one page cannot
    /// remove a key held on another. An empty result is a positive answer - the caller holds nothing here
    /// - and never signals an error.
    /// </remarks>
    public IReadOnlyList<string> Reduce(IEnumerable<PermissionGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        // Collect the denials first, because a denial has to be known before any allowance on the same
        // scope can be judged, and the sequence may present them in any order.
        HashSet<(PermissionScopeKind Kind, int Id, string Key)> denied = new();
        List<PermissionGrant> allowances = new();

        foreach (PermissionGrant grant in grants)
        {
            string key = NormaliseKey(grant.PermissionKey);

            if (key.Length == 0)
            {
                continue;
            }

            if (grant.AllowAccess)
            {
                allowances.Add(grant);
            }
            else
            {
                denied.Add((grant.ScopeKind, grant.ScopeId, key));
            }
        }

        HashSet<string> held = new(StringComparer.Ordinal);

        foreach (PermissionGrant allowance in allowances)
        {
            string key = NormaliseKey(allowance.PermissionKey);

            if (!denied.Contains((allowance.ScopeKind, allowance.ScopeId, key)))
            {
                held.Add(key);
            }
        }

        List<string> ordered = held.ToList();
        ordered.Sort(StringComparer.Ordinal);

        return ordered;
    }

    /// <summary>
    /// Decides whether a caller's reachable grants confer one particular permission.
    /// </summary>
    /// <param name="grants">Every grant the caller reaches for the scope being decided.</param>
    /// <param name="permissionKey">The permission being asked about.</param>
    /// <returns>
    /// <see langword="true"/> when the caller holds it; otherwise <see langword="false"/>. A caller with
    /// no reachable grant holds nothing, so an absent grant denies - the closed default, and the same
    /// answer an explicit denial produces.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="grants"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Deliberately defined as a membership test over <see cref="Reduce"/> rather than as a rule of its
    /// own. Expressing the verdict twice - once for listings and once for decisions - is how the two
    /// come to disagree, and a listing that offers an action the decision then refuses is a defect a user
    /// experiences and a test rarely catches.
    /// </remarks>
    public bool Holds(IEnumerable<PermissionGrant> grants, PermissionKey permissionKey)
    {
        ArgumentNullException.ThrowIfNull(grants);

        string wanted = NormaliseKey(permissionKey.ToString());

        return Reduce(grants).Contains(wanted, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reduces a caller's role names to the distinct, lower-cased set a grant query may be matched on.
    /// </summary>
    /// <param name="roleNames">The caller's role names, in any order and possibly containing blanks.</param>
    /// <returns>The distinct lower-cased names, with blanks discarded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roleNames"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Blank names are discarded, which reproduces the <c>role &lt;&gt; ""</c> guard
    /// <c>PortalSecurity.IsInRoles</c> applied at line 123 - it had to, because it split a
    /// semicolon-delimited string whose legacy form carried a leading delimiter and therefore always
    /// produced an empty first element. Lower-casing happens here rather than being left to the database
    /// collation so that the comparison behaves identically on a case-sensitive installation.
    /// </remarks>
    public IReadOnlyList<string> NormaliseRoleNames(IReadOnlyCollection<string> roleNames)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        HashSet<string> distinct = new(StringComparer.Ordinal);

        foreach (string name in roleNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                distinct.Add(name.Trim().ToLowerInvariant());
            }
        }

        return distinct.ToList();
    }

    /// <summary>Brings a permission key into the one casing every comparison here uses.</summary>
    /// <param name="permissionKey">The key as stored, or as named by the caller.</param>
    /// <returns>The trimmed, upper-cased key, or an empty string when nothing was supplied.</returns>
    private static string NormaliseKey(string? permissionKey)
    {
        return string.IsNullOrWhiteSpace(permissionKey)
            ? string.Empty
            : permissionKey.Trim().ToUpperInvariant();
    }
}
