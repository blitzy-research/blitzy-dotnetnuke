namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// What writing a tenant's account policy did, beyond storing the policy itself.
/// </summary>
/// <remarks>
/// <para>
/// The policy write has ONE side effect, and it is unbounded in size, which is why it is reported rather
/// than performed silently: changing <c>Security_DisplayNameFormat</c> rewrites the display name of every
/// account in the tenant.
/// </para>
/// <para>
/// MIGRATION: the legacy screen performed that sweep on a BACKGROUND THREAD.
/// <c>Website/admin/Users/UserSettings.ascx.vb</c> L175-L182 compares the submitted format against the
/// stored one and, when they differ, starts a thread running <c>UserController.UpdateDisplayNames</c>
/// (<c>Library/Components/Users/UserController.vb</c> L1259-L1268), which walks
/// <c>GetUsers(PortalId)</c> calling <c>UpdateDisplayName</c> and <c>UpdateUser</c> per account. Nothing
/// was reported to the operator: the save returned immediately, the sweep ran unobserved, and a failure
/// half way through left some accounts formatted and the rest not.
/// </para>
/// <para>
/// The target performs the sweep INSIDE the same transaction as the policy write, so the two commit
/// together or neither does, and reports what it did. That is a deliberate divergence in both directions -
/// the caller waits where the legacy caller did not, and the outcome is atomic where the legacy outcome
/// was not - and it is recorded in <c>MIGRATION_NOTES.md</c>.
/// </para>
/// </remarks>
public sealed class MembershipSettingsUpdateResultDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the submitted display-name format differed from the stored
    /// one, and the sweep was therefore attempted.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="DisplayNamesRewritten"/> being zero, and the distinction is the reason this
    /// member exists: a tenant that resubmitted the same format was never swept, whereas a tenant that
    /// changed the format and holds no accounts was swept and had nothing to change. A client that could
    /// not tell them apart would have to describe both as "nothing happened", which is untrue of the second.
    /// </remarks>
    public bool DisplayNameFormatChanged { get; set; }

    /// <summary>
    /// Gets or sets the number of accounts whose stored display name the sweep actually changed.
    /// </summary>
    /// <remarks>
    /// Counts CHANGES, not accounts examined. An account whose composed name already equals its stored one
    /// is left alone and is not counted, which is what keeps a resubmitted-but-reordered format from
    /// reporting a rewrite of every account in the tenant. Zero when
    /// <see cref="DisplayNameFormatChanged"/> is <see langword="false"/>.
    /// </remarks>
    public int DisplayNamesRewritten { get; set; }
}
