using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Wire contract for one entry in an account's member-services catalogue: a role the tenant publishes for
/// self-service subscription, the terms it is offered on, and the state of the signed-in account's own
/// subscription to it.
/// </summary>
/// <remarks>
/// <para>
/// Why this shape exists. The legacy screen is <c>Website/admin/Users/MemberServices.ascx</c>, a
/// seventy-seven line panel hosted by the account container at <c>manageusers.ascx:L77</c>. It renders a
/// seven-column grid: a subscription command, a trial command, then <c>RoleName</c>, <c>Description</c>,
/// a formatted fee, a formatted trial and a formatted expiry date (<c>:L31-L71</c>). Every member below is
/// one of those columns or one of the three predicates the markup binds to decide what a row offers, so
/// the shape is derived from what the screen actually rendered rather than from the role entity - which is
/// what AAP 0.5.1.2 requires.
/// </para>
/// <para>
/// The catalogue is the portal's PUBLIC roles, not the account's memberships. The legacy data source is
/// <c>RoleController.GetUserRoles(portalId, userId, includePrivate:False)</c>
/// (<c>MemberServices.ascx.vb:L97</c>), which reaches <c>dataProvider.GetServices</c>
/// (<c>DNNRoleProvider.vb:L481-L488</c>), whose terminal statement selects
/// <c>from Roles R where R.PortalId = @PortalId and R.IsPublic = 1</c>
/// (<c>04.06.00.SqlDataProvider:L993-L1013</c>) and annotates each row with two correlated sub-selects
/// against <c>UserRoles</c> for the account. So an unsubscribed offer appears in the list exactly as a
/// subscribed one does, and <see cref="IsSubscribed"/> is what distinguishes them.
/// </para>
/// <para>
/// MIGRATION: the fee and trial values are the ROLE'S OWN STORED VALUES, and the legacy projection's
/// truncating suppression is not reproduced. That statement wrapped each of them in
/// <c>case when convert(int, R.ServiceFee) &lt;&gt; 0 then … else null end</c>, and
/// <c>convert(int, …)</c> TRUNCATES - so a role priced at 0.50 came back with a null fee and an empty
/// billing frequency, which the screen's own <c>FormatPrice</c> (<c>MemberServices.ascx.vb:L200-L215</c>)
/// rendered as "Free" while <c>Subscribe</c> (<c>:L105</c>) still refused to complete it and redirected to
/// the payment page. Publishing the stored value instead is the Rule T7 translation of the same intent -
/// the suppression existed because <c>RoleInfo.ServiceFee</c> was a non-nullable <c>Single</c> and had no
/// way to say "absent" other than a sentinel, whereas these members are nullable and say it directly - and
/// it keeps this contract in agreement with <c>RoleListItemDto</c>, which has always carried the stored
/// values. The divergence is a presentation one and is recorded in <c>MIGRATION_NOTES.md</c>; no rule
/// changes, because every predicate below is evaluated from the stored role exactly as the legacy
/// predicates were.
/// </para>
/// <para>
/// MIGRATION: the three predicates are SERVER-COMPUTED rather than left to a client, because they are
/// business rules measured out of legacy code and Rule T2 places them beneath the transport.
/// <see cref="SubscriptionAction"/> reproduces <c>ServiceText</c> (<c>:L288-L305</c>),
/// <see cref="SubscriptionOffered"/> reproduces <c>ShowSubscribe</c> (<c>:L307-L323</c>) and
/// <see cref="TrialOffered"/> reproduces <c>ShowTrial</c> (<c>:L325-L342</c>). Each of them read the FULL
/// role through <c>RoleController.GetRole</c> rather than the suppressed projection, and so does the
/// service that fills this contract.
/// </para>
/// <para>
/// MIGRATION: the payment redirect is out of scope, so a paid offer is REPORTED rather than hidden.
/// The legacy subscribe and unsubscribe paths both ended in
/// <c>Response.Redirect("~/admin/Sales/PayPalSubscription.aspx?…")</c> whenever the role carried a fee
/// (<c>:L113</c> and <c>:L115</c>), and AAP 0.2.2.4 excludes sales administration. Rather than drop such a
/// role from the catalogue - which would silently erase a tenant's paid offering - the row still says
/// whether the legacy screen would have offered the command, and
/// <see cref="SubscriptionRequiresPayment"/> says why this application cannot complete it. The subscribe
/// and cancel operations refuse the same case with a stable failure code, so a client that ignores the
/// flag is answered rather than half served.
/// </para>
/// <para>
/// The type is an inert data carrier: it holds no behaviour, performs no validation and reaches no
/// database. Translation from the stored role and assignment lives in
/// <c>Application/Mapping/UserMappings.cs</c>.
/// </para>
/// </remarks>
public sealed class MemberServiceDto
{
    /// <summary>
    /// Gets or sets the identifier of the role this service is expressed as.
    /// </summary>
    /// <remarks>
    /// The <c>Roles.RoleID</c> key, which the legacy grid carried as the <c>CommandArgument</c> of both
    /// row commands (<c>MemberServices.ascx:L36</c> and <c>:L47</c>) and which every write operation on
    /// this catalogue is addressed by. <b>Zero is a real key</b> - the column is declared
    /// <c>IDENTITY(0,1)</c> at <c>01.00.00.SqlDataProvider:L114</c>, so the first role an installation
    /// creates bears it - and no consumer may read it as absence.
    /// </remarks>
    public int RoleId { get; set; }

    /// <summary>
    /// Gets or sets the role's name, which is the service's name.
    /// </summary>
    /// <remarks>
    /// The grid's third column, bound to <c>RoleName</c> under the header the resource file spells
    /// <c>Name</c> (<c>MemberServices.ascx:L55</c>).
    /// </remarks>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the role's description, or <see langword="null"/> when it carries none.
    /// </summary>
    /// <remarks>
    /// The grid's fourth column (<c>MemberServices.ascx:L56</c>). Nullable because
    /// <c>Roles.Description</c> is, and because the legacy read funnelled an absent value through
    /// <c>Null.SetNull</c> to the empty string, which a client cannot tell apart from a stored empty
    /// value; Rule T7 settles that here by publishing absence as absence.
    /// </remarks>
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
    /// Gets or sets how many <see cref="BillingFrequency"/> units one billing cycle spans, or
    /// <see langword="null"/> when the role names no period.
    /// </summary>
    /// <remarks>
    /// The stored <c>Roles.BillingPeriod</c> value, which the legacy screen interpolated into its fee
    /// wording as <c>"{0} Every {1} {2}"</c> (<c>MemberServices.ascx.vb:L209</c>).
    /// </remarks>
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// Gets or sets the code naming the unit of one billing cycle, or <see langword="null"/> when the
    /// role names none.
    /// </summary>
    /// <remarks>
    /// The stored <c>Roles.BillingFrequency</c> character. It crosses the wire as that character rather
    /// than as a member name, because the application's serialisation policy pins this enumeration with
    /// its own converter - the codes are load-bearing stored data in a <c>char(1)</c> column and the
    /// legacy screen built resource keys of the form <c>Frequency_&lt;code&gt;</c> out of them
    /// (<c>MemberServices.ascx.vb:L209</c>).
    /// </remarks>
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Gets or sets the one-off fee charged for the role's trial period, or <see langword="null"/> when
    /// it charges none.
    /// </summary>
    /// <remarks>
    /// The stored <c>Roles.TrialFee</c> value, rendered by the grid's fifth column
    /// (<c>MemberServices.ascx:L62-L66</c>). It is also the value <c>ShowTrial</c> tests: a trial is
    /// offered only when this is zero or absent.
    /// </remarks>
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Gets or sets how many <see cref="TrialFrequency"/> units the trial spans, or
    /// <see langword="null"/> when the role names no trial period.
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
    /// Gets or sets when the account's own subscription to this service took effect, or
    /// <see langword="null"/> when it holds none or the assignment names no start.
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
    /// <remarks>
    /// The <c>UserRoles.ExpiryDate</c> value, which the legacy projection carried as a correlated
    /// sub-select and the grid's seventh column rendered through <c>FormatExpiryDate</c>
    /// (<c>MemberServices.ascx.vb:L172-L186</c>) - a real date when it is in the future and the word
    /// "Expired" when it is not. The perpetual value a one-time term produces is <c>9999-12-31</c>
    /// (<c>RoleController.vb:L541</c>) and travels as that date rather than as absence.
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account currently holds this service.
    /// </summary>
    /// <remarks>
    /// The legacy projection expressed this as <c>'Subscribed' = (select UserRoleId from UserRoles …)</c>,
    /// so a non-null assignment key meant subscribed. Being subscribed is independent of being current:
    /// a lapsed subscription is still an assignment, which is exactly why
    /// <see cref="SubscriptionAction"/> has three values rather than two.
    /// </remarks>
    public bool IsSubscribed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account has already consumed this service's trial.
    /// </summary>
    /// <remarks>
    /// The <c>UserRoles.IsTrialUsed</c> flag, which is a nullable bit and is read here as
    /// <see langword="false"/> when absent - the same collapse <c>ShowTrial</c> performed with
    /// <c>(objUserRole Is Nothing) OrElse (Not objUserRole.IsTrialUsed)</c>
    /// (<c>MemberServices.ascx.vb:L336</c>).
    /// </remarks>
    public bool IsTrialUsed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account holds this service and it has already lapsed.
    /// </summary>
    /// <remarks>
    /// Reproduces the second arm of <c>ServiceText</c>: the expiry is present and strictly earlier than
    /// today (<c>MemberServices.ascx.vb:L295-L299</c>), which is also the condition
    /// <c>FormatExpiryDate</c> rendered as "Expired". <b>Today is a UTC date</b>, because the injected
    /// clock is UTC-only whereas <c>Date.Today</c> was server-local; west of Greenwich a subscription can
    /// therefore read as lapsed up to a day earlier than a legacy installation would have said, which is
    /// the same accepted divergence the cancellation path records.
    /// </remarks>
    public bool IsExpired { get; set; }

    /// <summary>
    /// Gets or sets the one subscription command this row offers, from the closed vocabulary in
    /// <see cref="MemberServiceActions"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reproduces <c>ServiceText(Subscribed, ExpiryDate)</c> (<c>MemberServices.ascx.vb:L288-L305</c>),
    /// which the legacy markup bound to BOTH the link's text and its <c>CommandName</c>
    /// (<c>MemberServices.ascx:L34-L35</c>) - so the label the account read was also the instruction the
    /// server dispatched on (<c>:L439-L452</c>).
    /// </para>
    /// <para>
    /// It travels as a stable code rather than as the localised label the legacy value was, because the
    /// legacy arrangement meant a translated installation dispatched on translated text. A client renders
    /// its own wording from this code; the wording the legacy resource file held is
    /// <c>Subscribe</c>, <c>Unsubscribe</c> and <c>Renew</c>, and the codes are spelled to match so the
    /// correspondence is checkable.
    /// </para>
    /// <para>
    /// It is always populated, and it is independent of <see cref="SubscriptionOffered"/>: the legacy
    /// screen computed the label for every row and then decided separately whether to render the link.
    /// </para>
    /// </remarks>
    public string SubscriptionAction { get; set; } = MemberServiceActions.Subscribe;

    /// <summary>
    /// Gets or sets a value indicating whether the subscription command is offered for this row at all.
    /// </summary>
    /// <remarks>
    /// Reproduces <c>ShowSubscribe(roleID)</c> (<c>MemberServices.ascx.vb:L307-L323</c>): the role is
    /// public - which every row in this catalogue is - and either charges no fee or the tenant has a
    /// payment processor account configured. A tenant that publishes a paid role without configuring a
    /// processor offered no command for it, and neither does this.
    /// </remarks>
    public bool SubscriptionOffered { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether completing the subscription command would require taking
    /// payment, which this application cannot do.
    /// </summary>
    /// <remarks>
    /// True exactly when the role carries a fee greater than zero. The legacy screen handed such a role
    /// to <c>~/admin/Sales/PayPalSubscription.aspx</c> on both the subscribe and the cancel path
    /// (<c>MemberServices.ascx.vb:L113</c> and <c>:L115</c>); AAP 0.2.2.4 excludes sales administration,
    /// so the operations refuse it instead. Reported rather than hidden, so a client can explain the
    /// refusal before making it and a tenant's paid offering is still visible in its own catalogue.
    /// </remarks>
    public bool SubscriptionRequiresPayment { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the trial command is offered for this row.
    /// </summary>
    /// <remarks>
    /// Reproduces <c>ShowTrial(roleID)</c> (<c>MemberServices.ascx.vb:L325-L342</c>): the role is public,
    /// its service fee is NOT zero, its trial fee IS zero, and the account either holds no assignment or
    /// holds one whose trial has not been consumed. A trial that is offered is always performable - the
    /// operation's own gate is the zero trial fee - so there is no payment counterpart to this flag.
    /// </remarks>
    public bool TrialOffered { get; set; }
}

/// <summary>
/// The closed vocabulary of <see cref="MemberServiceDto.SubscriptionAction"/>.
/// </summary>
/// <remarks>
/// <para>
/// Declared beside the contract it describes rather than as an enumeration, because the application's
/// serialisation policy registers a converter per enumeration that needs one and deliberately does NOT
/// register a blanket string policy - an unpinned enumeration therefore crosses the wire as a NUMBER, and
/// a numeric subscription command would be unreadable in a response body and indistinguishable from a
/// role or period value beside it. Three constants keep the value legible on the wire, keep the wire form
/// out of the serialisation policy's ordering concerns, and still give this assembly one place the
/// spellings live.
/// </para>
/// <para>
/// The three spellings are the resource keys the legacy screen resolved its labels from -
/// <c>Subscribe.Text</c>, <c>Unsubscribe.Text</c> and <c>Renew.Text</c> in
/// <c>Website/admin/Users/App_LocalResources/MemberServices.ascx.resx</c> - so a reader can check the
/// correspondence against the legacy file directly.
/// </para>
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
