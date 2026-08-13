namespace DnnMigration.Domain.Enums;

/// <summary>
/// What happened when a credential replacement was attempted against the externally installed membership
/// store.
/// </summary>
/// <remarks>
/// <para>
/// WHY A BOOLEAN WAS NOT ENOUGH, AND WHY THAT WAS A SECURITY DEFECT RATHER THAN AN INCONVENIENCE. The
/// replacement member used to answer <see langword="true"/> or <see langword="false"/>, and it wrote
/// UNCONDITIONALLY: whatever hash it was given replaced whatever was stored. Every caller of it first READ
/// the stored credential, made a decision from that read - a self-service change verifies the current value,
/// an administrative reset authorises against the account, and a sign-in verifies a legacy representation -
/// and only then wrote. Between the read and the write, anything could change the credential, and the write
/// would silently discard it. The consequences were concrete and not hypothetical:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A self-service change that verified the OLD current password could overwrite a newer administrative
/// reset, so an administrator who reset a compromised account's credential could have that reset silently
/// undone by an in-flight request holding the credential the reset existed to retire.
/// </description></item>
/// <item><description>
/// A sign-in that proved a LEGACY credential replaced the stored value with a hash of that legacy
/// credential - correct while nothing else had changed, and a rollback of a fresh reset when something had.
/// </description></item>
/// <item><description>
/// Two concurrent changes both reported success, so the caller whose value lost had no way to learn that
/// the credential it had just been told was in force was not.
/// </description></item>
/// </list>
/// <para>
/// The replacement is therefore a COMPARE-AND-SWAP: the caller states the representation it read, and the
/// store replaces the credential only while that is still what is stored. The database is the serialisation
/// point, which is what makes the guarantee hold across replicas - two API instances hold no shared lock,
/// but they do share one row.
/// </para>
/// <para>
/// THE MEMBERS ARE ORDERED FROM WORST TO BEST AND THE ZERO MEMBER IS THE WORST ON PURPOSE, for the same
/// reason <see cref="MembershipWriteOutcome"/> is: a caller that forgets to assign one, or a double left
/// unconfigured, reports the outcome that forces the loudest handling rather than the one that quietly
/// proceeds.
/// </para>
/// <para>
/// MIGRATION: the store these outcomes describe is the <c>aspnet_*</c> membership schema, which the
/// eighty-eight upgrade scripts only ever ALTER and never CREATE, so it is mapped alongside and never owned
/// (Rule T4). The legacy provider had no equivalent concept: <c>AspNetMembershipProvider.vb</c> called
/// <c>MembershipUser.ChangePassword</c>, which read and wrote through two separate provider calls with no
/// expectation carried between them, so the legacy product had this same race and lost the same way. Closing
/// it is a documented divergence, recorded in <c>MIGRATION_NOTES.md</c>.
/// </para>
/// </remarks>
public enum CredentialWriteOutcome
{
    /// <summary>
    /// The store could not be reached, so nothing was written.
    /// </summary>
    /// <remarks>
    /// A SERVER FAULT rather than an outcome of the operation the caller asked for. It must never be
    /// reported to an unauthenticated caller as a credential outcome, for the same reason
    /// <see cref="MembershipWriteOutcome.StoreUnavailable"/> must not be.
    /// </remarks>
    StoreUnavailable = 0,

    /// <summary>
    /// The account holds no credential record, so there was nothing to replace.
    /// </summary>
    NoRecord = 1,

    /// <summary>
    /// The credential had already changed since the caller read it, so nothing was written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE OUTCOME THE TYPE EXISTS FOR. It says the write was REFUSED rather than failed: the store
    /// is healthy, the record exists, and the caller's decision was made against a representation that is no
    /// longer in force. A caller must never retry blindly with the same hash - that would reintroduce the
    /// overwrite this outcome exists to prevent - and must instead re-read, re-decide, and report a conflict
    /// to whoever asked.
    /// </para>
    /// <para>
    /// On a sign-in path it means more than a lost write: the credential the request verified is not the
    /// account's credential any more, so the sign-in itself must be refused rather than completed.
    /// </para>
    /// </remarks>
    Superseded = 2,

    /// <summary>
    /// The credential was replaced, and the representation the caller read was still the stored one.
    /// </summary>
    Replaced = 3,
}
