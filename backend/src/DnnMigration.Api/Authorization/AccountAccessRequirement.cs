using Microsoft.AspNetCore.Authorization;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Requires that the caller either IS the account the route addresses, within the tenant the route addresses,
/// or administers that tenant.
/// </summary>
/// <remarks>
/// <para>
/// WHY A THIRD REQUIREMENT RATHER THAN REUSING PORTAL ADMINISTRATION. The user resource carries two kinds of
/// route. Some are unambiguously administrative - listing a tenant's accounts, creating one, deleting one,
/// clearing a lockout, setting approval, forcing a credential change, reading and writing the tenant's
/// membership settings - and those are gated on <see cref="PortalAdministratorRequirement"/> alone. The rest
/// are operations an ordinary account performs on ITSELF: reading its own record, updating its own details,
/// reading and writing its own profile, changing its own credential. Gating those on portal administration
/// would make self-service impossible for every account that is not an administrator, and an earlier revision
/// solved that by gating them on nothing but authentication - which made every one of them reachable by any
/// authenticated caller against any account in any tenant.
/// </para>
/// <para>
/// Both readings are wrong for the same reason: "may act on this account" is a distinct question from "may
/// administer this tenant", and it needs its own requirement. This is that requirement, and its handler
/// admits exactly two kinds of caller - the account itself, and an administrator of the tenant the account
/// belongs to.
/// </para>
/// <para>
/// WHAT IT DELIBERATELY DOES NOT DO. It does not decide whether the OPERATION is one the account may perform
/// on itself. Changing one's own credential requires presenting the current one, and an administrative reset
/// does not - that distinction is a business rule about credentials, so it lives in the Application service
/// that owns credentials rather than in an authorisation policy. The policy answers who may address the
/// account; the service answers what they may do once they have.
/// </para>
/// <para>
/// WHY IT CARRIES NO DATA, as with its two siblings: one instance is built when the policy is registered and
/// is then shared by every request naming the policy, so a per-request identifier stored on it would leak one
/// caller's subject into another caller's decision. The account and tenant keys come from the route at
/// decision time.
/// </para>
/// <para>
/// MIGRATION: the legacy account screens made the same distinction imperatively and inconsistently. The
/// membership panel hid its four administrative commands when the acting administrator was looking at their
/// own account (<c>Website/admin/Users/Membership.ascx.vb</c> L135), and the profile and password screens were
/// reachable by an ordinary member for their own account, but each screen re-derived the rule for itself. The
/// rule is declared once here instead.
/// </para>
/// </remarks>
internal sealed class AccountAccessRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// The single shared instance.
    /// </summary>
    /// <remarks>
    /// Safe to share precisely because the type carries no state; offered as a member so that a registration
    /// site cannot be tempted to construct a per-account variant.
    /// </remarks>
    public static AccountAccessRequirement Instance { get; } = new();
}
