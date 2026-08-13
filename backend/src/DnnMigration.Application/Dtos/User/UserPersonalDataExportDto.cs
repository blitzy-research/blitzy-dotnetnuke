using DnnMigration.Application.Dtos.Role;

namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Everything this application holds about one account within one tenant, in a single document, for the
/// account holder or an administrator of that tenant to take away.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: PRIV-01. IT EXISTS BECAUSE NOTHING DID. A module's content could be exported and a subject's own personal
/// data could not: an account holder could read their own record and their own profile through two separate
/// screens, and there was no single answer to "what do you hold about me". A security review recorded that as
/// a portability gap, and this contract is the answer to it.
/// </para>
/// <para>
/// ⚠ WHAT IT DELIBERATELY DOES NOT CARRY, AND WHY EACH OMISSION IS DELIBERATE RATHER THAN INCOMPLETE.
/// </para>
/// <para>
/// NO SECRET OF ANY KIND. No password hash, no password format, no password salt, no password question or
/// answer, no refresh-token digest, no access token. A portability document is handed to a caller and may be
/// saved to a disk, mailed to somebody or pasted into a support ticket, so a credential inside it is a
/// credential in every one of those places. The one-way hash is no exception: it is still the material an
/// offline guessing attack works against, and the subject cannot act on it in any case. The FACTS about the
/// credential that a subject can act on - when it was last changed, whether a change is required, whether the
/// account is locked out - are carried, because those are its own account state rather than the secret.
/// </para>
/// <para>
/// NO OTHER TENANT. Every member is scoped to the one tenant the request addressed. An account may belong to
/// several tenants and each of those is administered by different people, so a document naming them all would
/// disclose one tenant's membership list to another tenant's administrator - and this endpoint is reachable by
/// an administrator, not only by the subject. An account holder who belongs to several tenants exports from
/// each of them.
/// </para>
/// <para>
/// NO OTHER SUBJECT. The role assignments carried below are this account's own. They name the roles it holds,
/// never the other accounts holding them.
/// </para>
/// <para>
/// NO BILLING OR TRIAL DETAIL. Paid-service subscriptions are reachable by the ACCOUNT HOLDER ALONE through
/// the member-services endpoints, and this document is also readable by a tenant administrator. Widening what
/// an administrator can see, as a side effect of adding portability, would be a privacy regression dressed as
/// a privacy feature. The role assignment itself is carried because tenant role administration already shows
/// it.
/// </para>
/// <para>
/// It is a projection of contracts that already exist rather than a new shape for the same data, so a subject
/// reading their export and the same subject reading the screens sees the same values, and a field added to
/// the account or the profile contract appears here without this file changing.
/// </para>
/// </remarks>
public sealed class UserPersonalDataExportDto
{
    /// <summary>Gets or sets the instant this document was produced, in Coordinated Universal Time.</summary>
    /// <remarks>
    /// Stamped by the server rather than by the caller. A portability document that does not say when it was
    /// taken cannot be compared with a later one, which is the first thing a subject checking whether a
    /// correction was applied will want to do.
    /// </remarks>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>Gets or sets the tenant this document is scoped to.</summary>
    /// <remarks>
    /// Carried explicitly, because the scoping is the document's most consequential limitation: an account
    /// that belongs to several tenants has several exports, and a document that did not name its tenant would
    /// read as though it were the whole of what is held.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>Gets or sets the account identifier the document describes.</summary>
    public int UserId { get; set; }

    /// <summary>Gets or sets the account record, exactly as the account screen shows it.</summary>
    /// <remarks>
    /// The same contract the detail endpoint returns, so there is one definition of an account record rather
    /// than a second one that can drift from it. It carries no credential material.
    /// </remarks>
    public UserDetailDto Account { get; set; } = new();

    /// <summary>
    /// Gets or sets the account's profile values within this tenant, or <see langword="null"/> when the tenant
    /// defines no profile properties.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty document when there is nothing to describe, which distinguishes "this tenant
    /// defines no profile properties" from "the subject has filled none of them in" - the second is an empty
    /// property list on a present document.
    /// </remarks>
    public UserProfileDto? Profile { get; set; }

    /// <summary>Gets or sets the account's role assignments within this tenant, with their validity windows.</summary>
    /// <remarks>
    /// The assignment shape rather than a list of role names, because the dates are the part a subject cannot
    /// see anywhere else and the part that explains why an entitlement they expect is absent.
    /// </remarks>
    public IReadOnlyList<RoleMembershipDto> RoleAssignments { get; set; } = Array.Empty<RoleMembershipDto>();
}
