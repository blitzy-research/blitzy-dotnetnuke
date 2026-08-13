using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// 5 of 10 -- NO BOUND TEST ON ANY IDENTIFIER, AND ONE IDENTITY TEST. UpdatePortalRequest carries PortalId
// as the first of its twenty-seven members, matching argument 1 of the replaced signature, and the ONE rule
// attached to it is that it must equal the {id} route segment.

// 6 of 10 -- NO PAST-DATE REJECTION ON THE EXPIRY DATE, and no rule on it at all. valExpiryDate is a
// well-formedness check (Operator="DataTypeCheck" Type="Date"), which a strongly typed DateTime? already
// enforces during model binding: a value that would have failed that check cannot bind into the request in
// the first place.

// 7 of 10 -- MARKUP MaxLength VALUES ARE NOT CARRIED ACROSS WHERE THEY ARE TEXT-ENTRY WIDTHS.
// sitesettings.ascx sets maxlength on only a handful of inputs -- txtExpiryDate=15, txtHostFee=10,
// txtHostSpace=6, txtPageQuota=6, txtUserQuota=6, txtUserId=50 -- and the first two bound the characters a
// user could type into a text box, not the semantics of the value.

// 9 of 10 -- NO UNIQUENESS, EXISTENCE OR FILESYSTEM CHECK, because every one of them needs I/O this layer
// must not perform. Rule T3 keeps all data access behind repository interfaces and Rule T1 keeps this
// project free of infrastructure, so the constructor takes no dependency at all.

// A blank hosting-fee box left the local at its initialiser of zero, so "no fee entered" was persisted as a
// real zero rather than as a database null.

/// <summary>Shared field rules for a request that replaces the editable columns of a portal.</summary>
/// <remarks>
/// <para>
/// <b>Portal fee and quota members are deliberately unbounded below.</b> <c>valHostFee</c> is a currency
/// type check with no <c>GreaterThanEqual</c> companion anywhere in the file, whereas the role screen
/// <c>Website/admin/Security/editroles.ascx</c> bounds its own fees explicitly at L94-L96 and L126-L128.
/// </para>
/// <para>
/// <b>Dependencies: none.</b> The constructor takes no argument. Every rule is a width measured from the
/// terminal schema, an enumeration-membership assertion, a fixed-point shape rule or a lexical path rule,
/// so no configuration, clock, repository, options object or file-system access is needed or acquired.
/// </para>
/// </remarks>
public abstract class PortalSettingsUpdateRequestValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : IPortalSettingsUpdateRequest
{
    /// <summary>
    /// Terminal width of <c>Portals.PortalName</c>: <c>[nvarchar] (128) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L79</c>. No <c>ALTER COLUMN</c> anywhere in the eighty-eight-script
    /// chain touches this column, so the baseline declaration is terminal.
    /// </summary>
    private const int PortalNameMaximumLength = 128;

    /// <summary>
    /// Terminal width of the two managed-file references <c>Portals.LogoFile</c> and
    /// <c>Portals.BackgroundFile</c>: <c>[nvarchar] (50) NULL</c> at <c>01.00.00.SqlDataProvider:L81</c>
    /// and <c>nvarchar(50) NULL</c> at <c>01.00.02.SqlDataProvider:L886</c> respectively. No later <c>ALTER
    /// COLUMN</c> widens either.
    /// </summary>
    private const int FileReferenceMaximumLength = 50;

    /// <summary>
    /// Terminal width of <c>Portals.FooterText</c>: <c>[nvarchar] (100) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L82</c>, with no later <c>ALTER COLUMN</c>.
    /// </summary>
    private const int FooterTextMaximumLength = 100;

    /// <summary>
    /// Terminal width of <c>Portals.Currency</c>: <c>[char] (3) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L88</c>, with no later <c>ALTER COLUMN</c>.
    /// </summary>
    private const int CurrencyMaximumLength = 3;

    /// <summary>
    /// Terminal width of the three payment-gateway columns <c>Portals.PaymentProcessor</c>,
    /// <c>Portals.ProcessorUserId</c> and legacy <c>Portals.ProcessorPassword</c>: all <c>nvarchar(50)
    /// NULL</c>, added together at <c>01.00.06.SqlDataProvider:L599-L601</c>.
    /// </summary>
    private const int ProcessorFieldMaximumLength = 50;

    /// <summary>Managed-secret reference syntax accepted by the processor credential field.</summary>
    /// <remarks>
    /// The fixed <c>secret://</c> prefix makes a plaintext password structurally invalid. The suffix is
    /// deliberately limited to portable identifier/path characters and, together with the column-width
    /// rule, fits the immutable <c>nvarchar(50)</c> column.
    /// </remarks>
    private const string ProcessorCredentialReferencePattern =
        @"^secret://[A-Za-z0-9][A-Za-z0-9._/-]{0,40}$";

    /// <summary>Caller-safe wording for an invalid managed-secret reference.</summary>
    private const string ProcessorCredentialReferenceInvalidMessage =
        "The processor credential reference must use secret:// followed by a managed-secret identifier.";

    /// <summary>
    /// Terminal width of <c>Portals.Description</c> and <c>Portals.KeyWords</c>: both <c>nvarchar(500)
    /// NULL</c>, added together at <c>01.00.02.SqlDataProvider:L884-L885</c>.
    /// </summary>
    private const int MetadataMaximumLength = 500;

    /// <summary>Terminal width of <c>Portals.DefaultLanguage</c>: <c>nvarchar(10) NOT NULL</c>.</summary>
    /// <remarks>
    /// This column is the clearest case in the contract for reading the whole chain rather than the first
    /// declaration. It arrives as <c>nvarchar(6) NOT NULL</c> at <c>02.02.00.SqlDataProvider:L147</c>, is
    /// re-asserted at that width by <c>03.01.01.SqlDataProvider:L1121</c>, and is finally widened by the
    /// <c>ALTER COLUMN</c> at <c>04.03.05.SqlDataProvider:L291</c>.
    /// </remarks>
    private const int DefaultLanguageMaximumLength = 10;

    /// <summary>
    /// Terminal width of <c>Portals.HomeDirectory</c>: <c>varchar(100) NOT NULL</c>, established by the
    /// <c>ALTER TABLE</c> at <c>03.01.01.SqlDataProvider:L1123</c> and echoed by every stored-procedure
    /// parameter for the column.
    /// </summary>
    /// <remarks>
    /// Declared locally rather than borrowed from a shared helper so that this contract's only rule on the
    /// member is a measured schema width owned by this file, in exactly the form the seven widths above
    /// take. See the migration note beside the rule for why no path-shape rule accompanies it.
    /// </remarks>
    private const int HomeDirectoryMaximumLength = 100;

    /// <summary>
    /// Total number of digits permitted in <see cref="UpdatePortalRequest.HostFee"/>, matching the terminal
    /// <c>money</c> column exactly.
    /// </summary>
    private const int HostFeePrecision = 19;

    /// <summary>
    /// Number of digits permitted after the decimal point in <see cref="UpdatePortalRequest.HostFee"/>,
    /// matching the scale of the terminal <c>money</c> column.
    /// </summary>
    private const int HostFeeScale = 4;

    /// <summary>
    /// Message reported when the hosting fee is not expressible as a currency amount. Reproduced verbatim
    /// from the <c>ErrorMessage</c> of <c>valHostFee</c> at
    /// <c>Website/admin/Portal/sitesettings.ascx:L445</c>, so the wording a portal administrator saw on the
    /// legacy screen is the wording the API returns.
    /// </summary>
    private const string HostFeeInvalidMessage = "Invalid fee, needs to be a currency value!";

    /// <summary>
    /// Message reported when the hosting fee is a well-formed currency amount that the terminal
    /// <c>money</c> column still cannot hold.
    /// </summary>
    private static readonly string HostFeeUnrepresentableMessage = FormattableString.Invariant($"Invalid fee, it must fall between {SqlServerRange.MinimumMoney} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumMoney}, which is the range the stored column can hold.");

    /// <summary>
    /// Message reported when the expiry date falls outside the range the terminal <c>datetime</c> column
    /// can hold. The wording is net-new for the same reason as the fee message above.
    /// </summary>
    private static readonly string ExpiryDateUnrepresentableMessage = FormattableString.Invariant($"The expiry date must fall between {SqlServerRange.MinimumDateTime:yyyy-MM-dd} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumDateTime:yyyy-MM-dd}, which is the range the stored column can hold.");

    /// <summary>
    /// Message reported when the registration mode carries a value that names no member of
    /// <c>UserRegistrationMode</c>. It names the four modes the legacy option list offered at
    /// <c>Website/admin/Portal/sitesettings.ascx:L230-L233</c>.
    /// </summary>
    private const string RegistrationModeInvalidMessage =
        "User Registration must be None, Private, Public or Verified.";

    /// <summary>
    /// Message reported when the advertising mode carries a value that names no member of
    /// <c>BannerAdvertisingMode</c>. It names the three modes the legacy option list offered at
    /// <c>Website/admin/Portal/sitesettings.ascx:L123-L125</c>.
    /// </summary>
    private const string AdvertisingModeInvalidMessage =
        "Banner Advertising must be None, Site or Host.";

    /// <summary>
    /// Declares the rule set. The constructor takes no argument: every rule is a width measured from the
    /// terminal schema, an enumeration-membership assertion, a fixed-point shape rule matching the
    /// <c>money</c> column, or a lexical rule over a caller-supplied relative path.
    /// </summary>
    protected PortalSettingsUpdateRequestValidator()
    {
        // Each rule stops at its own first failure, so one over-long value yields one message rather
        // than a width failure and a shape failure for the same field.
        RuleLevelCascadeMode = CascadeMode.Stop;

        ClassLevelCascadeMode = CascadeMode.Continue;

        // A WIDTH BOUND AND NOTHING ELSE, which is the whole of what the legacy markup declared on this
        // control.
        RuleFor(request => request.PortalName)
            .MaximumLength(PortalNameMaximumLength);

        RuleFor(request => request.LogoFile)
            .MaximumLength(FileReferenceMaximumLength);

        RuleFor(request => request.BackgroundFile)
            .MaximumLength(FileReferenceMaximumLength);

        RuleFor(request => request.FooterText)
            .MaximumLength(FooterTextMaximumLength);

        RuleFor(request => request.Currency)
            .MaximumLength(CurrencyMaximumLength);

        RuleFor(request => request.Description)
            .MaximumLength(MetadataMaximumLength);

        RuleFor(request => request.KeyWords)
            .MaximumLength(MetadataMaximumLength);

        RuleFor(request => request.DefaultLanguage)
            .MaximumLength(DefaultLanguageMaximumLength);

        RuleFor(request => request.PaymentProcessor)
            .MaximumLength(ProcessorFieldMaximumLength);

        RuleFor(request => request.ProcessorUserId)
            .MaximumLength(ProcessorFieldMaximumLength);

        // The legacy free-text password is replaced by a managed-secret reference. Null and empty remain
        // valid because they mean keep and clear respectively; every replacement must carry the secret://
        // prefix so a plaintext credential cannot be stored accidentally.
        RuleFor(request => request.ProcessorCredentialReference)
            .MaximumLength(ProcessorFieldMaximumLength);

        RuleFor(request => request.ProcessorCredentialReference)
            .Matches(ProcessorCredentialReferencePattern)
            .When(request => !string.IsNullOrEmpty(request.ProcessorCredentialReference))
            .WithMessage(ProcessorCredentialReferenceInvalidMessage);

        RuleFor(request => request.UserRegistration)
            .IsInEnum().WithMessage(RegistrationModeInvalidMessage);

        RuleFor(request => request.BannerAdvertising)
            .IsInEnum().WithMessage(AdvertisingModeInvalidMessage);

        // This is the ONLY rule on the hosting fee, and it is the typed expression of valHostFee's
        // Operator="DataTypeCheck" Type="Currency" -- a well-formedness check and nothing more. decimal?
        // already rejects a non-numeric value during model binding, but it does not enforce the one thing
        // "is a currency value" additionally means against this column: the terminal type is money
        // (03.01.01.SqlDataProvider:L1118), which holds nineteen digits with four after the point, and a
        // decimal carrying more scale than that is well formed as a decimal yet is not storable as this
        // currency.
        RuleFor(request => request.HostFee)
            .PrecisionScale(HostFeePrecision, HostFeeScale, ignoreTrailingZeros: true)
                .WithMessage(HostFeeInvalidMessage)
            .Must(SqlServerRange.CanStore)
                .WithMessage(HostFeeUnrepresentableMessage)
            .When(request => request.HostFee.HasValue);

        RuleFor(request => request.HomeDirectory)
            .MaximumLength(HomeDirectoryMaximumLength);

        // The expiry date carries a REPRESENTABILITY bound and no business rule.
        RuleFor(request => request.ExpiryDate)
            .Must(SqlServerRange.CanStore)
            .WithMessage(ExpiryDateUnrepresentableMessage);
    }
}

/// <summary>Validates the body of <c>PUT /api/v1/portals/{portalId}</c>.</summary>
public sealed class UpdatePortalRequestValidator : PortalSettingsUpdateRequestValidator<UpdatePortalRequest>
{
    /// <summary>
    /// The key under which <c>Api/Filters/FluentValidationActionFilter.cs</c> publishes the <c>portalId</c>
    /// route value into the validation context's root data.
    /// </summary>
    private const string RoutePortalIdKey = "RoutePortalId";

    /// <summary>
    /// Message reported when the identifier in the body does not name the portal the request path
    /// addresses.
    /// </summary>
    private const string PortalIdMismatchMessage =
        "The portal identifier in the body must match the portal identifier in the request path.";

    /// <summary>Initialises the validator and adds the route-versus-body identity rule.</summary>
    public UpdatePortalRequestValidator()
    {
        // The route segment is the subject of the write. The rule stands down when no route context
        // exists so direct application-layer callers can validate the DTO without inventing an HTTP path.
        RuleFor(request => request.PortalId)
            .Must((request, portalId, context) => MatchesRoutePortalId(portalId, context))
            .WithMessage(PortalIdMismatchMessage);
    }

    /// <summary>
    /// Reports whether the submitted portal identifier names the portal the request path addresses.
    /// </summary>
    /// <param name="portalId">The identifier carried by the body.</param>
    /// <param name="context">
    /// The validation context, whose root data carries the route values published by the API layer.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the two agree, or when no route identifier is present at all; <see
    /// langword="false"/> only when both are present and they differ.
    /// </returns>
    private static bool MatchesRoutePortalId(int portalId, ValidationContext<UpdatePortalRequest> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.RootContextData.TryGetValue(RoutePortalIdKey, out object? routeValue)
            || routeValue is not int routePortalId)
        {
            return true;
        }

        return portalId == routePortalId;
    }
}
