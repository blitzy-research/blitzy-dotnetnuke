using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>The shared readable member set of the two requests that replace the legacy Site Settings save.</summary>
/// <remarks>
/// The interface is read-only because it is consumed after model binding. Each concrete request remains
/// mutable so ASP.NET Core can bind JSON into it.
/// </remarks>
public interface IPortalSettingsUpdateRequest
{
    /// <summary>Gets the portal title.</summary>
    string? PortalName { get; }

    /// <summary>Gets the portal-relative logo reference.</summary>
    string? LogoFile { get; }

    /// <summary>Gets the footer text.</summary>
    string? FooterText { get; }

    /// <summary>Gets the hosting expiry instant, or <see langword="null"/> for no expiry.</summary>
    DateTime? ExpiryDate { get; }

    /// <summary>Gets the account-registration mode.</summary>
    UserRegistrationMode UserRegistration { get; }

    /// <summary>Gets the banner-administration mode.</summary>
    BannerAdvertisingMode BannerAdvertising { get; }

    /// <summary>Gets the currency code.</summary>
    string? Currency { get; }

    /// <summary>Gets the designated administrator account identifier.</summary>
    int? AdministratorId { get; }

    /// <summary>Gets the monthly hosting charge.</summary>
    decimal? HostFee { get; }

    /// <summary>Gets the disk-space allowance in megabytes.</summary>
    int? HostSpace { get; }

    /// <summary>Gets the page quota.</summary>
    int? PageQuota { get; }

    /// <summary>Gets the account quota.</summary>
    int? UserQuota { get; }

    /// <summary>Gets the payment-processor name.</summary>
    string? PaymentProcessor { get; }

    /// <summary>Gets the payment-processor account identifier.</summary>
    string? ProcessorUserId { get; }

    /// <summary>Gets the write-only payment-processor managed-secret reference.</summary>
    string? ProcessorCredentialReference { get; }

    /// <summary>Gets the portal description.</summary>
    string? Description { get; }

    /// <summary>Gets the search keywords.</summary>
    string? KeyWords { get; }

    /// <summary>Gets the portal-relative background reference.</summary>
    string? BackgroundFile { get; }

    /// <summary>Gets the number of activity-history days to retain.</summary>
    int? SiteLogHistory { get; }

    /// <summary>Gets the splash page identifier.</summary>
    int? SplashTabId { get; }

    /// <summary>Gets the home page identifier.</summary>
    int? HomeTabId { get; }

    /// <summary>Gets the sign-in page identifier.</summary>
    int? LoginTabId { get; }

    /// <summary>Gets the account page identifier.</summary>
    int? UserTabId { get; }

    /// <summary>Gets the default culture code.</summary>
    string? DefaultLanguage { get; }

    /// <summary>Gets the offset from UTC in minutes.</summary>
    int? TimeZoneOffset { get; }

    /// <summary>Gets the portal-relative home directory.</summary>
    string? HomeDirectory { get; }

    /// <summary>
    /// Gets the optimistic-concurrency token the caller read on the record it is replacing, or <see
    /// langword="null"/> when the caller supplies none.
    /// </summary>
    /// <remarks>
    /// THE ONE MEMBER OF THIS INTERFACE THAT DESCRIBES NO PORTAL ATTRIBUTE, and the one member
    /// <c>PortalMappings.ApplyUpdate</c> never reads. It states WHICH revision of the record the caller
    /// read, so <c>PortalService</c> can refuse a stale whole-record replace with
    /// <c>portal.concurrency_conflict</c> - answered as 409 - instead of applying it.
    /// </remarks>
    string? ConcurrencyToken { get; }
}

/// <summary>Body of <c>PUT /api/v1/portals/{portalId}/settings</c>.</summary>
/// <remarks>
/// Because it is a whole-row replacement it carries the same lost-update surface as <see
/// cref="UpdatePortalRequest"/>, so it carries the same remedy: <see cref="ConcurrencyToken"/>.
/// </remarks>
public sealed class UpdatePortalSettingsRequest : IPortalSettingsUpdateRequest
{
    /// <inheritdoc />
    public string? PortalName { get; set; }

    /// <inheritdoc />
    public string? LogoFile { get; set; }

    /// <inheritdoc />
    public string? FooterText { get; set; }

    /// <inheritdoc />
    public DateTime? ExpiryDate { get; set; }

    /// <inheritdoc />
    public UserRegistrationMode UserRegistration { get; set; }

    /// <inheritdoc />
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <inheritdoc />
    public string? Currency { get; set; }

    /// <inheritdoc />
    public int? AdministratorId { get; set; }

    /// <inheritdoc />
    public decimal? HostFee { get; set; }

    /// <inheritdoc />
    public int? HostSpace { get; set; }

    /// <inheritdoc />
    public int? PageQuota { get; set; }

    /// <inheritdoc />
    public int? UserQuota { get; set; }

    /// <inheritdoc />
    public string? PaymentProcessor { get; set; }

    /// <inheritdoc />
    public string? ProcessorUserId { get; set; }

    /// <inheritdoc />
    public string? ProcessorCredentialReference { get; set; }

    /// <inheritdoc />
    public string? Description { get; set; }

    /// <inheritdoc />
    public string? KeyWords { get; set; }

    /// <inheritdoc />
    public string? BackgroundFile { get; set; }

    /// <inheritdoc />
    public int? SiteLogHistory { get; set; }

    /// <inheritdoc />
    public int? SplashTabId { get; set; }

    /// <inheritdoc />
    public int? HomeTabId { get; set; }

    /// <inheritdoc />
    public int? LoginTabId { get; set; }

    /// <inheritdoc />
    public int? UserTabId { get; set; }

    /// <inheritdoc />
    public string? DefaultLanguage { get; set; }

    /// <inheritdoc />
    public int? TimeZoneOffset { get; set; }

    /// <inheritdoc />
    public string? HomeDirectory { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// A caller reads this value from <see cref="PortalSettingsDto.ConcurrencyToken"/> - the token that
    /// read publishes and the token this write verifies are produced by one function, so the settings
    /// screen and the portal-edit screen cannot disagree about what "the record I read" means, and a token
    /// obtained from either read is honoured by either write.
    /// </remarks>
    public string? ConcurrencyToken { get; set; }
}
