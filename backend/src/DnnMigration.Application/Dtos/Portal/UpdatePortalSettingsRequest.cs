using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// The shared readable member set of the two requests that replace the legacy Site Settings save.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UpdatePortalRequest"/> carries these values plus a body-level portal identifier for
/// <c>PUT /api/v1/portals/{portalId}</c>. <see cref="UpdatePortalSettingsRequest"/> carries only these
/// values because <c>PUT /api/v1/portals/{portalId}/settings</c> takes its subject exclusively from the
/// route. Keeping the shared members behind this interface makes the mapper, business guards and
/// validation rules compile against one vocabulary, so the two write paths cannot silently diverge.
/// </para>
/// <para>
/// The interface is read-only because it is consumed after model binding. Each concrete request remains
/// mutable so ASP.NET Core can bind JSON into it.
/// </para>
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

    /// <summary>
    /// Gets the write-only payment-processor managed-secret reference.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy screen wrote a plaintext password box unconditionally. The reference that
    /// replaces it is never echoed by any response, so the three states are carried on the request
    /// instead: <see langword="null"/> keeps the stored reference, the empty string clears it, and a
    /// non-empty value must be a <c>secret://</c> managed-secret reference.
    /// </remarks>
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
    /// Gets the optimistic-concurrency token the caller read on the record it is replacing, or
    /// <see langword="null"/> when the caller supplies none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE ONE MEMBER OF THIS INTERFACE THAT DESCRIBES NO PORTAL ATTRIBUTE, and the one member
    /// <c>PortalMappings.ApplyUpdate</c> never reads. It states WHICH revision of the record the caller
    /// read, so <c>PortalService</c> can refuse a stale whole-record replace with
    /// <c>portal.concurrency_conflict</c> - answered as 409 - instead of applying it. It belongs on the shared
    /// interface rather than on each concrete request because both portal write paths hand this interface to
    /// that one mapper and replace the same twenty-five columns: a token on only one of them would move the
    /// lost-update surface to the sibling route rather than remove it.
    /// </para>
    /// <para>
    /// THE VALUE IS OPAQUE. It is derived from the portal's own mutable columns by
    /// <c>PortalMappings.ConcurrencyTokenFor</c> - Rule T4 forbids adding a version column to the legacy
    /// schema - published by every portal read, and compared for equality on a write. No client may parse,
    /// order or construct one.
    /// </para>
    /// <para>
    /// NULLABLE, AND AN OMITTED TOKEN IS APPLIED. The member is net-new: the legacy screen posted the whole
    /// record back with no version check of any kind, so refusing a request that carries none would break
    /// every caller written before it existed. The trade is stated rather than hidden - a caller that omits
    /// the token gets the legacy last-write-wins behaviour, and one that supplies it is protected. The role
    /// update contract already works this way, so this API has one concurrency idiom and not two.
    /// </para>
    /// </remarks>
    string? ConcurrencyToken { get; }
}

/// <summary>
/// Body of <c>PUT /api/v1/portals/{portalId}/settings</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the write counterpart of <see cref="PortalSettingsDto"/>. The response additionally carries
/// the route-owned <c>PortalId</c> and immutable <c>Guid</c>; this request additionally carries the
/// write-only payment-processor password. No portal identifier is accepted in the body, so a caller
/// cannot present two competing subjects for the same write.
/// </para>
/// <para>
/// MIGRATION: this is a whole-row replacement, matching
/// <c>Website/admin/Portal/SiteSettings.ascx.vb</c> lines 687 to 781. A nullable numeric member does not
/// mean "leave unchanged": the mapper applies the same legacy blank-value defaults documented on
/// <see cref="UpdatePortalRequest"/>. The host-only comparison and administrator-retention guard are
/// shared by both update operations.
/// </para>
/// <para>
/// Because it is a whole-row replacement it carries the same lost-update surface as
/// <see cref="UpdatePortalRequest"/>, so it carries the same remedy: <see cref="ConcurrencyToken"/>. The
/// two portal write paths reach one mapper, <c>PortalMappings.ApplyUpdate</c>, and replace the same
/// twenty-five columns, so protecting one path and leaving the other would leave the defect reachable
/// through the sibling route rather than fixing it. The token published by <see cref="PortalSettingsDto"/>
/// and the token published by <c>PortalDetailDto</c> are derived from the same twenty-five members by the
/// same function and are therefore interchangeable: a token read from either screen is valid on either
/// write.
/// </para>
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
    /// A caller reads this value from <see cref="PortalSettingsDto.ConcurrencyToken"/> - the token that read
    /// publishes and the token this write verifies are produced by one function, so the settings screen and
    /// the portal-edit screen cannot disagree about what "the record I read" means, and a token obtained from
    /// either read is honoured by either write.
    /// </remarks>
    public string? ConcurrencyToken { get; set; }
}