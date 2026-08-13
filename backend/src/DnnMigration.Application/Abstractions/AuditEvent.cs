namespace DnnMigration.Application.Abstractions;

/// <summary>
/// One business or security fact worth keeping a durable account of, expressed entirely as plain CLR data.
/// </summary>
/// <param name="EventName">The stable event name, taken from <see cref="AuditEventNames"/>.</param>
/// <remarks>
/// <para>
/// <b>Nothing sensitive or directly identifying may be placed on an audit event.</b> Not a password, not a
/// password hash, not a token, not a token digest, not an authorisation header, not a cookie, not a request
/// body, not a raw exception message, and not a person's name, account name, electronic-mail address,
/// tenant alias or other caller-authored prose. Stable database identifiers are the attribution mechanism.
/// </para>
/// <para>
/// <b>Absence is null, never a sentinel.</b> The legacy record initialised its portal and user identifiers
/// from the shared integer sentinel, whose value is -1; but <c>Portals.PortalID</c> is declared
/// <c>IDENTITY(-1, 1)</c> and the role, page and module keys all seed at 0, so -1 and 0 are both real
/// identifiers in this schema.
/// </para>
/// </remarks>
public sealed record AuditEvent(string EventName)
{
    /// <summary>An empty, shared property map, so the common case allocates nothing.</summary>
    private static readonly IReadOnlyDictionary<string, string?> NoProperties =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>Gets how the recorded operation concluded.</summary>
    /// <remarks>
    /// Redundant for the legacy names that state their own outcome - <c>LOGIN_SUCCESS</c> against
    /// <c>LOGIN_FAILURE</c> - and load-bearing for the ones that do not, which is why it is a member rather
    /// than being folded into the name. Defaults to <see cref="AuditOutcome.Succeeded"/> because the legacy
    /// site recorded a mutation only after it had committed.
    /// </remarks>
    public AuditOutcome Outcome { get; init; } = AuditOutcome.Succeeded;

    /// <summary>
    /// Gets the tenant the operation was performed within, or <see langword="null"/> when it was
    /// installation-wide.
    /// </summary>
    public int? PortalId { get; init; }

    /// <summary>
    /// Gets the identifier of the account that performed the operation, or <see langword="null"/> when the
    /// caller was anonymous or could not be identified.
    /// </summary>
    public int? ActorUserId { get; init; }

    /// <summary>
    /// Gets the identifier of the account the operation was performed upon, when that differs from <see
    /// cref="ActorUserId"/>.
    /// </summary>
    public int? SubjectUserId { get; init; }

    /// <summary>
    /// Gets the kind of thing the operation acted upon - <c>Portal</c>, <c>Tab</c>, <c>Role</c>,
    /// <c>User</c> - or <see langword="null"/> when the event names no resource.
    /// </summary>
    public string? ResourceType { get; init; }

    /// <summary>
    /// Gets the identifier of the thing acted upon, rendered invariantly, or <see langword="null"/> when
    /// the event names no resource.
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Gets the stable failure code when <see cref="Outcome"/> is not <see cref="AuditOutcome.Succeeded"/>;
    /// otherwise <see langword="null"/>.
    /// </summary>
    public string? FailureCode { get; init; }

    /// <summary>Gets additional short, non-sensitive, machine-readable facts about the operation.</summary>
    /// <remarks>
    /// A value is DATA and is kept, but bounded: the sink applies a count ceiling, a length ceiling and
    /// delimiter/control-character rejection, replacing a value that fails any of them with a fixed marker
    /// rather than writing part of it. Callers must therefore keep these facts short and descriptive, and
    /// must not use them to carry a document, a payload or a rendered list.
    /// </remarks>
    public IReadOnlyDictionary<string, string?> Properties { get; init; } = NoProperties;
}

/// <summary>How an audited operation concluded.</summary>
/// <remarks>
/// The legacy record had no outcome member. Its event NAME carried the outcome for the sign-in family and
/// every other site logged only after success, so success was implicit.
/// </remarks>
public enum AuditOutcome
{
    /// <summary>The operation completed and its effects are durable.</summary>
    Succeeded = 0,

    /// <summary>
    /// The operation was understood and deliberately refused - a credential rejected, a session cut short,
    /// an invariant protected.
    /// </summary>
    Denied = 1,

    /// <summary>
    /// The operation was permitted but could not be completed. Reserved for the cases where proceeding is
    /// correct and the failure must still leave a trace, such as a credential whose work factor could not
    /// be upgraded.
    /// </summary>
    Failed = 2,
}
