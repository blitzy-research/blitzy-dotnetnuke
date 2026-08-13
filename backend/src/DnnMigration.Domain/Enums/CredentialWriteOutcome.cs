namespace DnnMigration.Domain.Enums;

/// <summary>
/// What happened when a credential replacement was attempted against the externally installed membership
/// store.
/// </summary>
/// <remarks>
/// <para>
/// The replacement is therefore a COMPARE-AND-SWAP: the caller states the representation it read, and the
/// store replaces the credential only while that is still what is stored. The database is the serialisation
/// point, which is what makes the guarantee hold across replicas - two API instances hold no shared lock,
/// but they do share one row.
/// </para>
/// <para>
/// The store these outcomes describe is the <c>aspnet_*</c> membership schema, which the eighty-eight
/// upgrade scripts only ever ALTER and never CREATE, so it is mapped alongside and never owned (Rule T4).
/// </para>
/// </remarks>
public enum CredentialWriteOutcome
{
    /// <summary>The store could not be reached, so nothing was written.</summary>
    /// <remarks>
    /// A SERVER FAULT rather than an outcome of the operation the caller asked for. It must never be
    /// reported to an unauthenticated caller as a credential outcome, for the same reason <see
    /// cref="MembershipWriteOutcome.StoreUnavailable"/> must not be.
    /// </remarks>
    StoreUnavailable = 0,

    /// <summary>The account holds no credential record, so there was nothing to replace.</summary>
    NoRecord = 1,

    /// <summary>The credential had already changed since the caller read it, so nothing was written.</summary>
    /// <remarks>
    /// THIS IS THE OUTCOME THE TYPE EXISTS FOR. It says the write was REFUSED rather than failed: the store
    /// is healthy, the record exists, and the caller's decision was made against a representation that is
    /// no longer in force.
    /// </remarks>
    Superseded = 2,

    /// <summary>
    /// The credential was replaced, and the representation the caller read was still the stored one.
    /// </summary>
    Replaced = 3,
}
