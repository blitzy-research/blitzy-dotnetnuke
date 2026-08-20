using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Wire contract for one entry in an account's member-services catalogue: a role the tenant publishes for
/// self-service subscription, the terms it is offered on, and the state of the signed-in account's own
/// subscription to it.
/// </summary>
/// <remarks>
/// MIGRATION: the fee and trial values are the ROLE'S OWN STORED VALUES, and the legacy projection's
/// truncating suppression is not reproduced.
/// </remarks>
public sealed class MemberServiceDto
{
    /// <summary>Gets or sets the identifier of the role this service is expressed as.</summary>
    /// <remarks>
    /// The <c>Roles.RoleID</c> key, which the legacy grid carried as the <c>CommandArgument</c> of both row
    /// commands and which every write operation on this catalogue is addressed by. <b>Zero is a real
    /// key</b> - the column is declared <c>IDENTITY(0,1)</c> at <c>01.00.00.SqlDataProvider:L114</c>, so
    /// the first role an installation creates bears it - and no consumer may read it as absence.
    /// </remarks>
    public int RoleId { get; set; }

    /// <summary>Gets or sets the role's name, which is the service's name.</summary>
    /// <remarks>
    /// The grid's third column, bound to <c>RoleName</c> under the header the resource file spells
    /// <c>Name</c>.
    /// </remarks>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>Gets or sets the role's description, or <see langword="null"/> when it carries none.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the recurring fee the role charges, or <see langword="null"/> when it charges none.
    /// </summary>
    /// <remarks>
    /// The stored <c>Roles.ServiceFee</c> value. See the type remarks for why the legacy projection's
    /// truncating suppression is not reproduced.
    /// </remarks>
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Gets or sets how many <see cref="BillingFrequency"/> units one billing cycle spans, or <see
    /// langword="null"/> when the role names no period.
    /// </summary>
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// Gets or sets the code naming the unit of one billing cycle, or <see langword="null"/> when the role
    /// names none.
    /// </summary>
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Gets or sets the one-off fee charged for the role's trial period, or <see langword="null"/> when it
    /// charges none.
    /// </summary>
    /// <remarks>
    /// The stored <c>Roles.TrialFee</c> value, rendered by the grid's fifth column. It is also the value
    /// <c>ShowTrial</c> tests: a trial is offered only when this is zero or absent.
    /// </remarks>
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Gets or sets how many <see cref="TrialFrequency"/> units the trial spans, or <see langword="null"/>
    /// when the role names no trial period.
    /// </summary>
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Gets or sets the code naming the unit of the trial period, or <see langword="null"/> when the role
    /// names none.
    /// </summary>
    /// <remarks>
    /// The stored <c>Roles.TrialFrequency</c> character. <see cref="Domain.Enums.BillingFrequency.None"/>
    /// is the <c>N</c> code, and it is what the legacy projection tested before suppressing the trial
    /// columns entirely.
    /// </remarks>
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// Gets or sets when the account's own subscription to this service took effect, or <see
    /// langword="null"/> when it holds none or the assignment names no start.
    /// </summary>
    /// <remarks>
    /// The <c>UserRoles.EffectiveDate</c> value of the account's own assignment. The legacy grid rendered
    /// no column for it, but the subscription it describes is time-bounded at both ends and a client that
    /// can see when a subscription ends and not when it began cannot explain a pending one.
    /// </remarks>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// Gets or sets when the account's own subscription to this service lapses, or <see langword="null"/>
    /// when it holds none or the subscription never lapses.
    /// </summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Gets or sets a value indicating whether the account currently holds this service.</summary>
    public bool IsSubscribed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account has already consumed this service's trial.
    /// </summary>
    public bool IsTrialUsed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account holds this service and it has already lapsed.
    /// </summary>
    /// <remarks>
    /// Reproduces the second arm of <c>ServiceText</c>: the expiry is present and strictly earlier than
    /// today, which is also the condition <c>FormatExpiryDate</c> rendered as "Expired". <b>Today is a UTC
    /// date</b>, because the injected clock is UTC-only whereas <c>Date.Today</c> was server-local; west of
    /// Greenwich a subscription can therefore read as lapsed up to a day earlier than a legacy installation
    /// would have said, which is the same accepted divergence the cancellation path records.
    /// </remarks>
    public bool IsExpired { get; set; }

    /// <summary>
    /// Gets or sets the one subscription command this row offers, from the closed vocabulary in <see
    /// cref="MemberServiceActions"/>.
    /// </summary>
    public string SubscriptionAction { get; set; } = MemberServiceActions.Subscribe;

    /// <summary>
    /// Gets or sets a value indicating whether the subscription command is offered for this row at all.
    /// </summary>
    public bool SubscriptionOffered { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether completing the subscription command would require taking
    /// payment, which this application cannot do.
    /// </summary>
    public bool SubscriptionRequiresPayment { get; set; }

    /// <summary>Gets or sets a value indicating whether the trial command is offered for this row.</summary>
    public bool TrialOffered { get; set; }
}

/// <summary>The closed vocabulary of <see cref="MemberServiceDto.SubscriptionAction"/>.</summary>
/// <remarks>
/// Declared beside the contract it describes rather than as an enumeration, because the application's
/// serialisation policy registers a converter per enumeration that needs one and deliberately does NOT
/// register a blanket string policy - an unpinned enumeration therefore crosses the wire as a NUMBER, and a
/// numeric subscription command would be unreadable in a response body and indistinguishable from a role or
/// period value beside it.
/// </remarks>
public static class MemberServiceActions
{
    /// <summary>The account does not hold the service and may take it up.</summary>
    public const string Subscribe = "Subscribe";

    /// <summary>The account holds the service and it has not lapsed.</summary>
    public const string Unsubscribe = "Unsubscribe";

    /// <summary>The account holds the service and it has lapsed.</summary>
    public const string Renew = "Renew";
}
