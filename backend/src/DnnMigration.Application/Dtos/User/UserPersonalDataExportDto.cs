using DnnMigration.Application.Dtos.Role;

namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Everything this application holds about one account within one tenant, in a single document, for the
/// account holder or an administrator of that tenant to take away.
/// </summary>
/// <remarks>
/// <para>
/// PRIV-01. IT EXISTS BECAUSE NOTHING DID. A module's content could be exported and a subject's own
/// personal data could not: an account holder could read their own record and their own profile through two
/// separate screens, and there was no single answer to "what do you hold about me".
/// </para>
/// <para>
/// NO SECRET OF ANY KIND. No password hash, no password format, no password salt, no password question or
/// answer, no refresh-token digest, no access token. A portability document is handed to a caller and may
/// be saved to a disk, mailed to somebody or pasted into a support ticket, so a credential inside it is a
/// credential in every one of those places.
/// </para>
/// </remarks>
public sealed class UserPersonalDataExportDto
{
    /// <summary>Gets or sets the instant this document was produced, in Coordinated Universal Time.</summary>
    /// <remarks>
    /// Stamped by the server rather than by the caller. A portability document that does not say when it
    /// was taken cannot be compared with a later one, which is the first thing a subject checking whether a
    /// correction was applied will want to do.
    /// </remarks>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>Gets or sets the tenant this document is scoped to.</summary>
    /// <remarks>
    /// Carried explicitly, because the scoping is the document's most consequential limitation: an account
    /// that belongs to several tenants has several exports, and a document that did not name its tenant
    /// would read as though it were the whole of what is held.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>Gets or sets the account identifier the document describes.</summary>
    public int UserId { get; set; }

    /// <summary>Gets or sets the account record, exactly as the account screen shows it.</summary>
    /// <remarks>
    /// The same contract the detail endpoint returns, so there is one definition of an account record
    /// rather than a second one that can drift from it. It carries no credential material.
    /// </remarks>
    public UserDetailDto Account { get; set; } = new();

    /// <summary>
    /// Gets or sets the account's profile values within this tenant, or <see langword="null"/> when the
    /// tenant defines no profile properties.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty document when there is nothing to describe, which distinguishes "this
    /// tenant defines no profile properties" from "the subject has filled none of them in" - the second is
    /// an empty property list on a present document.
    /// </remarks>
    public UserProfileDto? Profile { get; set; }

    /// <summary>
    /// Gets or sets the account's role assignments within this tenant, with their validity windows.
    /// </summary>
    public IReadOnlyList<RoleMembershipDto> RoleAssignments { get; set; } = Array.Empty<RoleMembershipDto>();
}
