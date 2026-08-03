// MIGRATION: this type replaces the legacy DotNetNuke.Services.Log.EventLog.LogInfo, whose shape it
// deliberately narrows. LogInfo carried twenty-odd members - a server name, a configuration identifier,
// an exception payload, a byte count, a pending-notification flag and a mutable property collection -
// because it was simultaneously a business audit record, an exception log entry and a scheduler journal
// entry. Only the business-audit facts are reproduced here, measured from the three sites that actually
// populated it in scope: UserController.vb:L70-L81 (portal identifier, portal name, filtered user name,
// user identifier, event key), UserController.vb:L240 (a named key, its value, the tenant and the
// account identifier) and PortalController.vb:L1140-L1157 (the same header plus a property list).
//
// MIGRATION: the exception half is NOT reproduced, and its absence is a decision rather than an
// omission. Exception reporting at the edge is owned by Api/ErrorHandling/GlobalExceptionHandler.cs,
// which publishes RFC 7807 problem details and logs the fault; routing exceptions through an audit
// record as well would produce two accounts of one event that could disagree. The review of this
// checkpoint also measured ZERO in-scope exception-logging call sites, so there is nothing to port.

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// One business or security fact worth keeping a durable account of, expressed entirely as plain CLR
/// data.
/// </summary>
/// <param name="EventName">
/// The stable event name, taken from <see cref="AuditEventNames"/>. Never composed at run time.
/// </param>
/// <remarks>
/// <para>
/// <b>Facts, never prose and never decisions.</b> Every member records something that already happened.
/// There is no severity, no message and no formatting: a sink decides how to render an event, and a
/// human-readable sentence assembled here would be a translation waiting to drift from the structured
/// members beside it.
/// </para>
/// <para>
/// <b>Nothing sensitive may be placed on an audit event.</b> Not a password, not a password hash, not a
/// token, not a token digest, not an authorisation header, not a cookie, not a request body and not a
/// raw exception message. An account name is carried because the legacy audit carried one - it was the
/// only way to attribute a refused sign-in, where no account identifier exists to record - and because
/// it is an identifier rather than a secret. <see cref="Properties"/> is for short descriptive facts of
/// the same kind, and a caller that cannot satisfy itself a value is safe must not add it.
/// </para>
/// <para>
/// <b>Absence is null, never a sentinel.</b> The legacy record initialised its portal and user
/// identifiers from the shared integer sentinel, whose value is -1; but <c>Portals.PortalID</c> is
/// declared <c>IDENTITY(-1, 1)</c> and the role, page and module keys all seed at 0, so -1 and 0 are
/// both real identifiers in this schema. Every optional identifier below is therefore nullable, and no
/// consumer may read -1 or 0 as meaning "absent".
/// </para>
/// <para>
/// <b>Immutable.</b> The legacy record was mutated after construction - its property collection was
/// filled item by item - which meant an event could be half-built when something threw. This type is
/// built in one expression and cannot be revised afterwards.
/// </para>
/// </remarks>
public sealed record AuditEvent(string EventName)
{
    /// <summary>An empty, shared property map, so the common case allocates nothing.</summary>
    private static readonly IReadOnlyDictionary<string, string?> NoProperties =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Gets how the recorded operation concluded.
    /// </summary>
    /// <remarks>
    /// Redundant for the legacy names that state their own outcome - <c>LOGIN_SUCCESS</c> against
    /// <c>LOGIN_FAILURE</c> - and load-bearing for the ones that do not, which is why it is a member
    /// rather than being folded into the name. Defaults to <see cref="AuditOutcome.Succeeded"/> because
    /// the legacy site recorded a mutation only after it had committed.
    /// </remarks>
    public AuditOutcome Outcome { get; init; } = AuditOutcome.Succeeded;

    /// <summary>
    /// Gets the tenant the operation was performed within, or <see langword="null"/> when it was
    /// installation-wide.
    /// </summary>
    public int? PortalId { get; init; }

    /// <summary>
    /// Gets the identifier of the account that performed the operation, or <see langword="null"/> when
    /// the caller was anonymous or could not be identified.
    /// </summary>
    /// <remarks>
    /// The acting account, which is not always the account acted upon: an administrator removing a
    /// member populates this with the administrator and <see cref="SubjectUserId"/> with the member.
    /// The legacy record had one user field and therefore could not distinguish the two.
    /// </remarks>
    public int? ActorUserId { get; init; }

    /// <summary>
    /// Gets the account name the operation was performed under, or <see langword="null"/> when none is
    /// known.
    /// </summary>
    /// <remarks>
    /// On a refused sign-in this is the name that was SUBMITTED, which is the only attribution
    /// available when no account matched it - exactly what the legacy site recorded at
    /// <c>UserController.vb:L76</c>. It is stored as given: the legacy call passed it through a
    /// script-and-markup input filter because the value was about to be rendered into an HTML admin
    /// grid, and nothing here renders HTML, so filtering it would corrupt the recorded fact rather than
    /// protect anything. A sink that renders an event into a markup context owns its own encoding.
    /// </remarks>
    public string? ActorUserName { get; init; }

    /// <summary>
    /// Gets the identifier of the account the operation was performed upon, when that differs from
    /// <see cref="ActorUserId"/>.
    /// </summary>
    public int? SubjectUserId { get; init; }

    /// <summary>
    /// Gets the kind of thing the operation acted upon - <c>Portal</c>, <c>Tab</c>, <c>Role</c>,
    /// <c>User</c> - or <see langword="null"/> when the event names no resource.
    /// </summary>
    /// <remarks>
    /// A short, stable, singular noun matching the domain entity's own type name. Deliberately a string
    /// rather than an enumeration: an enumeration would have to be extended in lockstep with the entity
    /// set, and a missing member would silently become a default one.
    /// </remarks>
    public string? ResourceType { get; init; }

    /// <summary>
    /// Gets the identifier of the thing acted upon, rendered invariantly, or <see langword="null"/> when
    /// the event names no resource.
    /// </summary>
    /// <remarks>
    /// A string because the identifiers being recorded are not all integers and because an audit record
    /// never computes with this value. Callers render integers with
    /// <see cref="System.Globalization.CultureInfo.InvariantCulture"/> so a record written on one host
    /// reads identically on another.
    /// </remarks>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Gets the stable failure code when <see cref="Outcome"/> is not
    /// <see cref="AuditOutcome.Succeeded"/>; otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The same code the operation reported to its caller, so an audit record and the response the
    /// caller received can be reconciled. Never a message, and never an exception's own text.
    /// </remarks>
    public string? FailureCode { get; init; }

    /// <summary>
    /// Gets additional short, non-sensitive descriptive facts about the operation.
    /// </summary>
    /// <remarks>
    /// The counterpart to the legacy property list, which recorded values such as the portal alias, the
    /// template file and the child-portal flag alongside a tenant creation
    /// (<c>PortalController.vb:L1140-L1156</c>). Keys are compared ordinally and are short, stable
    /// identifiers. The map is never <see langword="null"/>, so a consumer never guards against one.
    /// </remarks>
    public IReadOnlyDictionary<string, string?> Properties { get; init; } = NoProperties;
}

/// <summary>How an audited operation concluded.</summary>
/// <remarks>
/// MIGRATION: the legacy record had no outcome member. Its event NAME carried the outcome for the
/// sign-in family and every other site logged only after success, so success was implicit. Three members
/// are declared because the target genuinely distinguishes three cases, and the middle one is the reason
/// the type exists: a request that was understood and deliberately refused is not the same event as one
/// that could not be carried out.
/// </remarks>
public enum AuditOutcome
{
    /// <summary>The operation completed and its effects are durable.</summary>
    Succeeded = 0,

    /// <summary>
    /// The operation was understood and deliberately refused - a credential rejected, a session cut
    /// short, an invariant protected.
    /// </summary>
    Denied = 1,

    /// <summary>
    /// The operation was permitted but could not be completed. Reserved for the cases where proceeding
    /// is correct and the failure must still leave a trace, such as a credential whose work factor could
    /// not be upgraded.
    /// </summary>
    Failed = 2,
}
