namespace DnnMigration.Domain.Enums;

// MIGRATION: This enumeration is net-new and has no legacy ancestor.

/// <summary>The complete set of results a refresh-token operation can report.</summary>
/// <remarks>
/// <para>
/// Visible so that the store abstraction can be expressed, and no wider in meaning for being so. These
/// members are a store-level vocabulary, not a client-facing one: they must not appear in a wire contract,
/// in a serialised response or as a token claim.
/// </para>
/// <para>
/// <see cref="Unknown"/> is deliberately the zero member, so that a value left at its default reads as a
/// refusal rather than as an approval. An enumeration whose default meant success would turn any missed
/// assignment into an authorisation.
/// </para>
/// </remarks>
public enum RefreshTokenOutcome
{
    /// <summary>
    /// The presented material does not correspond to any entry, or could not be one of this store's tokens
    /// at all. Nothing was changed.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The operation completed: material was found redeemable, was redeemed, or was revoked by this call.
    /// </summary>
    Succeeded = 1,

    /// <summary>The entry's expiry has passed. It can never be redeemed again and no replacement is issued.</summary>
    Expired = 2,

    /// <summary>
    /// The entry was revoked, whether by a sign-off or by the family-wide revocation that follows a
    /// detected replay. No replacement is issued.
    /// </summary>
    Revoked = 3,

    /// <summary>
    /// The entry had already been redeemed, so presenting it again is a replay. Every generation in its
    /// family has been revoked, and no replacement survives.
    /// </summary>
    AlreadyUsed = 4,

    /// <summary>
    /// A revocation found every generation of the family already revoked, so it had nothing left to change.
    /// Reported distinctly from <see cref="Succeeded"/> so a repeated sign-off is not mistaken for a fresh
    /// one.
    /// </summary>
    AlreadyRevoked = 5,

    /// <summary>
    /// The store already holds as many entries as it is willing to hold, so no token was issued. Nothing
    /// was changed, and the caller's credentials were not at fault.
    /// </summary>
    /// <remarks>
    /// Reported only by issuing, and reported rather than absorbed: growing without bound instead would
    /// trade a refused sign-in for an exhausted process, which fails every session at once rather than one.
    /// </remarks>
    CapacityExhausted = 6,

    /// <summary>
    /// The durable store could not complete the operation. The caller's credential was not at fault, and a
    /// later retry may succeed.
    /// </summary>
    StoreUnavailable = 7,

    /// <summary>
    /// The same server-observed client presented a token again inside the tightly bounded concurrent
    /// redemption grace window. No replacement is issued, but the family is not treated as stolen and is
    /// not revoked.
    /// </summary>
    ConcurrentUse = 8,
}
