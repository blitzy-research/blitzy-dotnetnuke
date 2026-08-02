using DnnMigration.Application.Dtos.Portal;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// MIGRATION: this validator was promised by three separate files and did not exist. UpdatePortalRequest
// referred callers to it, PortalSettingsDto.cs:L38 assigned the legacy screen's two comparisons to it,
// and IPortalService documented request validation as a precondition of the update member. Its absence
// meant PUT /api/v1/portals/{id} applied NO length rule to any of its eleven string members, so a value
// longer than its column would reach the database and fail there as a provider exception - a 500 for
// what is a field-level problem, and the same defect class the credential rules were changed to avoid.
//
// MIGRATION: the widths below come from the SCHEMA, not from the markup, because the update screen
// declares almost no markup limits. A census of Website/admin/Portal/sitesettings.ascx finds exactly
// two validators in the entire file - both CompareValidator instances performing a DataTypeCheck, at
// L433-L435 (expiry date) and L444-L446 (hosting fee) - and no required-field or regular-expression
// validator anywhere. Only five inputs carry a maxlength attribute at all (L326, L443, L454, L462,
// L470), and none of them is one of the long text fields. So the schema is the only source of a bound
// for most of this contract, and the terminal width of each column was read from the 88-script chain
// rather than from the baseline CREATE TABLE, which is superseded for several of these columns.
//
// MIGRATION: the two legacy CompareValidator instances need no counterpart here. Both are
// Operator="DataTypeCheck" - one Type="Date", one Type="Currency" - which is a check that the submitted
// text is convertible at all. The target contract types those members as DateTime? and decimal?, so a
// value that would have failed either check cannot be bound into the request in the first place and is
// rejected by model binding before any rule runs. Reproducing them as FluentValidation rules would
// declare a check that could never fail, which is indistinguishable from enforcement under review.

/// <summary>
/// Validates the shape of a portal update request submitted to
/// <c>PUT /api/v1/portals/{id}</c>, enforcing the terminal schema widths of every string member and
/// the safety of the caller-supplied home directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>No identity rule.</b> This request carries no copy of the portal identifier: the <c>{id}</c>
/// route segment is the sole source of the subject, and
/// <c>IPortalService.UpdatePortalAsync(int, UpdatePortalRequest, CancellationToken)</c> receives it as
/// a separate argument. There is therefore no route-versus-body disagreement to detect. The reasoning
/// behind removing the duplicate rather than comparing the two copies is recorded on the request type
/// at the point where the member used to be declared.
/// </para>
/// <para>
/// <b>Nothing is required.</b> Not one member carries a requiredness rule, and that is measured
/// rather than overlooked: the legacy screen declared no required-field validator at all, so every
/// field could be submitted empty. Several backing columns are <c>NOT NULL</c>, but an empty string
/// satisfies <c>NOT NULL</c> and the legacy screen could submit one, so inferring requiredness from
/// nullability would reject saves the legacy application accepted. <c>PortalSettingsDto</c> states the
/// same conclusion and warns the validator author against exactly this inference.
/// </para>
/// <para>
/// <b>Sign and membership rules, and why they are here.</b> The monetary and integral members - the
/// hosting fee, the disk space, the page and user quotas and the site-log retention - are refused when
/// negative, and the two mode members are refused when they carry an integer that names no enumeration
/// member. Neither rule is a legacy rule, and both are recorded as intentional strengthenings. The legacy
/// screen's only check on the fee was a type check and its code-behind parsed each value with a plain
/// <c>Parse</c> call that accepted a negative number (<c>SiteSettings.ascx.vb:L704-L719</c>), while the
/// terminal columns are <c>money</c> and <c>int</c>, which permit negatives too; the two mode columns are
/// plain <c>int</c> columns that the legacy drop-down lists constrained by construction rather than by a
/// validator. A JSON caller is bound by neither constraint, and a negative quota or an unnamed mode has no
/// meaning that any part of the target honours, so both are refused at the boundary instead of being
/// stored. The time-zone offset is deliberately left unbounded: a negative offset is the western
/// hemisphere and is entirely valid.
/// </para>
/// <para>
/// <b>The authorisation rule over the same members stays out.</b> The genuine rule over the fee, the
/// quotas, the retention period and the expiry date is an <b>authorisation</b> rule rather than a range
/// rule: <c>SiteSettings.ascx.vb:L760-L770</c> rejects the whole save when a caller who is not a super
/// user has altered any of them, and that belongs to the service and the API edge, where the caller's
/// privileges are known. It is deliberately absent here, because a validator cannot see who is asking.
/// </para>
/// <para>
/// <b>Dependencies.</b> None. Every rule is a width measured from the terminal schema or a shape rule
/// over a path, so no configuration, clock, repository or file-system access is needed or acquired.
/// Instances are discovered by the Application layer's assembly scan, which registers publicly visible
/// validator types only - hence the accessibility of this class.
/// </para>
/// </remarks>
public class UpdatePortalRequestValidator : AbstractValidator<UpdatePortalRequest>
{
    /// <summary>
    /// Terminal width of <c>Portals.PortalName</c>: <c>[nvarchar] (128) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L79</c>, carried forward unchanged by the <c>Tmp_Portals</c>
    /// rebuild at <c>01.00.05.SqlDataProvider:L1368</c>. No <c>ALTER COLUMN</c> in the 88-script
    /// chain touches it.
    /// </summary>
    private const int PortalNameMaximumLength = 128;

    /// <summary>
    /// Terminal width of <c>Portals.LogoFile</c> and <c>Portals.BackgroundFile</c>: both
    /// <c>[nvarchar] (50) NULL</c>, at <c>01.00.00.SqlDataProvider:L81</c> and
    /// <c>01.00.02.SqlDataProvider:L886</c> respectively, and both carried forward by the
    /// <c>Tmp_Portals</c> rebuild (<c>01.00.05.SqlDataProvider:L1369</c> and L1383).
    /// </summary>
    private const int FileReferenceMaximumLength = 50;

    /// <summary>
    /// Terminal width of <c>Portals.FooterText</c>: <c>[nvarchar] (100) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L82</c>, carried forward at
    /// <c>01.00.05.SqlDataProvider:L1370</c>.
    /// </summary>
    private const int FooterTextMaximumLength = 100;

    /// <summary>
    /// Terminal width of <c>Portals.Currency</c>: <c>[char] (3) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L88</c>, carried forward at
    /// <c>01.00.05.SqlDataProvider:L1376</c>.
    /// </summary>
    /// <remarks>
    /// A fixed-width column, so a longer value is not truncated silently by the provider - it is
    /// rejected. The rule is a maximum rather than an exact length because the legacy screen used a
    /// drop-down list, which could submit the empty string but never a two-character code, and
    /// because existing rows may hold an empty value that a subsequent save must be able to
    /// round-trip.
    /// </remarks>
    private const int CurrencyMaximumLength = 3;

    /// <summary>
    /// Terminal width of the three payment-gateway columns
    /// <c>Portals.PaymentProcessor</c>, <c>Portals.ProcessorUserId</c> and
    /// <c>Portals.ProcessorPassword</c>: all <c>nvarchar(50) NULL</c>, added together at
    /// <c>01.00.06.SqlDataProvider:L599-L601</c>.
    /// </summary>
    private const int ProcessorFieldMaximumLength = 50;

    /// <summary>
    /// Terminal width of <c>Portals.Description</c> and <c>Portals.KeyWords</c>: both
    /// <c>nvarchar(500) NULL</c>, added at <c>01.00.02.SqlDataProvider:L884-L885</c> and carried
    /// forward by the <c>Tmp_Portals</c> rebuild at <c>01.00.05.SqlDataProvider:L1381-L1382</c>.
    /// </summary>
    private const int MetadataMaximumLength = 500;

    /// <summary>
    /// Terminal width of <c>Portals.DefaultLanguage</c>: <c>nvarchar(10) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// This width is the clearest case in the contract for reading the whole chain rather than the
    /// first declaration. The column arrives as <c>nvarchar(6) NOT NULL</c> at
    /// <c>02.02.00.SqlDataProvider:L147</c>, is re-asserted at that width by
    /// <c>03.01.01.SqlDataProvider:L1121</c>, and is finally widened to 10 by the
    /// <c>ALTER COLUMN</c> at <c>04.03.05.SqlDataProvider:L290-L291</c>. Enforcing 6 would reject
    /// values the terminal schema stores.
    /// </remarks>
    private const int DefaultLanguageMaximumLength = 10;

    /// <summary>
    /// Message reported when the registration mode carries an integer that names no member of
    /// <c>UserRegistrationMode</c>. It names the four permitted modes rather than the integers behind
    /// them, because the wire contract serialises the member name.
    /// </summary>
    private const string RegistrationModeInvalidMessage =
        "User Registration must be None, Private, Public or Verified.";

    /// <summary>
    /// Message reported when the advertising mode carries an integer that names no member of
    /// <c>BannerAdvertisingMode</c>.
    /// </summary>
    private const string AdvertisingModeInvalidMessage =
        "Banner Advertising must be None, Site or Host.";

    /// <summary>Message reported when a negative hosting charge is submitted.</summary>
    private const string HostFeeNegativeMessage = "Hosting Fee must be zero or greater.";

    /// <summary>Message reported when a negative disk-space allowance is submitted.</summary>
    private const string HostSpaceNegativeMessage = "Disk Space must be zero or greater.";

    /// <summary>Message reported when a negative page quota is submitted.</summary>
    private const string PageQuotaNegativeMessage = "Page Quota must be zero or greater.";

    /// <summary>Message reported when a negative user quota is submitted.</summary>
    private const string UserQuotaNegativeMessage = "User Quota must be zero or greater.";

    /// <summary>Message reported when a negative site-log retention period is submitted.</summary>
    private const string SiteLogHistoryNegativeMessage = "Site Log History must be zero or greater.";

    /// <summary>
    /// Declares the rule set. The constructor takes no argument: every rule is a width measured from
    /// the terminal schema or a shape rule over a caller-supplied path.
    /// </summary>
    public UpdatePortalRequestValidator()
    {
        // Each rule stops at its own first failure, so one over-long value yields one message rather
        // than a width failure and a shape failure for the same field.
        RuleLevelCascadeMode = CascadeMode.Stop;

        // The legacy screen rendered every failing validator at once, so every failing field is
        // reported together instead of one per round trip.
        ClassLevelCascadeMode = CascadeMode.Continue;

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

        // MIGRATION: the two mode members are enumerations on the wire but plain int columns underneath,
        // and the legacy screen constrained them by rendering a drop-down list rather than by declaring a
        // validator. A JSON caller has no drop-down, and the serialiser accepts a bare integer, so an
        // unnamed value would be stored and then read back as a mode no branch in the target recognises.
        // Membership is therefore asserted here. Both enumerations define a zero member - NoRegistration
        // and None - so a request that simply omits these members is still valid, which matters because
        // this contract has no required members at all.
        RuleFor(request => request.UserRegistration)
            .IsInEnum().WithMessage(RegistrationModeInvalidMessage);

        RuleFor(request => request.BannerAdvertising)
            .IsInEnum().WithMessage(AdvertisingModeInvalidMessage);

        // MIGRATION: the five non-negative rules are a deliberate, documented strengthening and not a
        // legacy rule. The legacy screen accepted whatever its currency and integer type checks allowed,
        // including a negative number, and the terminal columns are money and int with defaults of zero
        // and no check constraint. A negative hosting charge, disk allowance, quota or retention count has
        // no meaning that any part of the target honours, so it is refused at the boundary rather than
        // clamped silently deeper in the write path, where the caller would be told nothing about it.
        //
        // MIGRATION: each rule is guarded by HasValue rather than left unguarded, because this contract
        // requires nothing: an omitted member must not be reported as a negative one. The mapping layer
        // still clamps, so a value that reaches it cannot go negative either - the two are deliberately
        // belt and braces, and the clamp is what preserves the legacy outcome on any path that does not
        // run this validator.
        //
        // MIGRATION: the time-zone offset is absent from this block on purpose. Negative offsets are the
        // whole western hemisphere, so a lower bound there would reject valid data.
        RuleFor(request => request.HostFee)
            .GreaterThanOrEqualTo(0m).WithMessage(HostFeeNegativeMessage)
            .When(request => request.HostFee.HasValue);

        RuleFor(request => request.HostSpace)
            .GreaterThanOrEqualTo(0).WithMessage(HostSpaceNegativeMessage)
            .When(request => request.HostSpace.HasValue);

        RuleFor(request => request.PageQuota)
            .GreaterThanOrEqualTo(0).WithMessage(PageQuotaNegativeMessage)
            .When(request => request.PageQuota.HasValue);

        RuleFor(request => request.UserQuota)
            .GreaterThanOrEqualTo(0).WithMessage(UserQuotaNegativeMessage)
            .When(request => request.UserQuota.HasValue);

        RuleFor(request => request.SiteLogHistory)
            .GreaterThanOrEqualTo(0).WithMessage(SiteLogHistoryNegativeMessage)
            .When(request => request.SiteLogHistory.HasValue);

        RuleFor(request => request.PaymentProcessor)
            .MaximumLength(ProcessorFieldMaximumLength);

        RuleFor(request => request.ProcessorUserId)
            .MaximumLength(ProcessorFieldMaximumLength);

        // MIGRATION: the gateway password is a SECRET travelling through a request body, and the only
        // rule this layer can usefully place on it is the column width. What matters as much as the
        // rule is what the rule must not do: FluentValidation's default message for a length failure
        // interpolates the submitted value's length but never the value itself, and no rule here
        // overrides that message or adds one that could. A validation response is returned to the
        // caller and is a plausible thing to log, so a message quoting this field would put a
        // credential into both. The same prohibition applies to request logging, which must not
        // serialise this body verbatim - recorded on the request type and enforced at the API edge.
        RuleFor(request => request.ProcessorPassword)
            .MaximumLength(ProcessorFieldMaximumLength);

        RuleFor(request => request.Description)
            .MaximumLength(MetadataMaximumLength);

        RuleFor(request => request.KeyWords)
            .MaximumLength(MetadataMaximumLength);

        RuleFor(request => request.DefaultLanguage)
            .MaximumLength(DefaultLanguageMaximumLength);

        // MIGRATION: the home directory carries the SAME defence as the create contract, applied from
        // the one shared definition rather than restated. The update path is not the lesser of the two
        // risks: create at least derives the default from a brand-new identifier, whereas update lets a
        // caller replace an established portal's content root outright. The legacy update screen
        // applied no shape rule to it whatsoever - sitesettings.ascx declares no validator on the
        // control - and the value was concatenated into a physical path when it was next mapped
        // (PortalController.vb:L994), so a rooted or parent-traversing value reached the file system
        // unchallenged. The rule is purely lexical and touches no file system; the physical checks
        // remain the service's responsibility at the point it maps the value.
        RuleFor(request => request.HomeDirectory)
            .MaximumLength(PortalHomeDirectoryRules.MaximumLength)
            .Must(PortalHomeDirectoryRules.IsSafeRelativeDirectory)
                .WithMessage(PortalHomeDirectoryRules.InvalidMessage);

        // MIGRATION: no rule on the four tab references or the administrator reference, and none is
        // possible here. SplashTabId, HomeTabId, LoginTabId, UserTabId and AdministratorId are foreign
        // keys, so the only meaningful check is that the referenced row exists AND belongs to this
        // portal - a tenant-isolation question that needs a repository this layer must not acquire, and
        // that the database's own foreign keys settle authoritatively for existence. A lower-bound test
        // would additionally be wrong rather than merely insufficient: the Tabs primary key seeds at
        // zero and the Portals primary key seeds at negative one, so both values are real identifiers
        // even though the legacy contract used negative one as its absent-integer sentinel. The
        // ownership check belongs to PortalService.
    }
}
