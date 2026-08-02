using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Portal"/> aggregate and the portal transfer
/// contracts, and between an inbound portal request and the aggregate it describes.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the reflection-driven hydrator <c>Library/Components/Shared/CBO.vb</c> for the
/// portal slice. Nothing here reflects over a type, reads a column by string key or fills a
/// collection by convention: every assignment is a named statement the compiler checks, so a renamed
/// member is a build error rather than a silently absent value at run time. AAP 0.4.3 records the
/// deliberate decision not to take a convention-based mapping dependency for exactly this reason.
/// </para>
/// <para>
/// The projections are one-directional by intent. Reads flow entity to transfer contract; writes flow
/// request to entity through <see cref="ToNewPortal"/> and <see cref="ApplyUpdate"/>. No member turns
/// a response contract back into an entity, because no caller is entitled to post one.
/// </para>
/// <para>
/// Values the entity does not carry - the member and page tallies, the role names behind the two role
/// identifiers, the administrator's electronic-mail address and the host-level root page - arrive as
/// explicit arguments. They are computed by the read path rather than stored against the portal, as
/// the legacy portal read view resolved each of them with a correlated sub-select, so passing them in
/// keeps this type free of data access.
/// </para>
/// </remarks>
public static class PortalMappings
{
    /// <summary>
    /// Projects a portal onto the row shape the portal list screen renders.
    /// </summary>
    /// <param name="portal">The portal to project.</param>
    /// <param name="aliases">The host names bound to the portal, already ordered.</param>
    /// <param name="users">The portal's member tally.</param>
    /// <param name="pages">The portal's page tally.</param>
    /// <returns>The list row.</returns>
    public static PortalListItemDto ToListItem(Portal portal, IReadOnlyList<string> aliases, int users, int pages)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(aliases);

        return new PortalListItemDto
        {
            PortalId = portal.PortalId,
            PortalName = portal.PortalName,
            Aliases = aliases,
            Users = users,
            Pages = pages,
            HostSpace = portal.HostSpace,
            HostFee = portal.HostFee,
            ExpiryDate = portal.ExpiryDate,
        };
    }

    /// <summary>
    /// Projects a portal onto the full detail contract.
    /// </summary>
    /// <param name="portal">The portal to project, with its aliases loaded.</param>
    /// <param name="users">The portal's member tally.</param>
    /// <param name="pages">The portal's page tally.</param>
    /// <param name="administratorRoleName">Name of the role named by the administrator role identifier, or <see langword="null"/> when it names none.</param>
    /// <param name="registeredRoleName">Name of the role named by the registered-members role identifier, or <see langword="null"/> when it names none.</param>
    /// <param name="administratorEmail">The designated administrator's electronic-mail address, or <see langword="null"/> when no administrator is designated.</param>
    /// <param name="superTabId">Identifier of the host-level root page, or <see langword="null"/> when the installation has none.</param>
    /// <returns>The detail contract.</returns>
    public static PortalDetailDto ToDetail(
        Portal portal,
        int users,
        int pages,
        string? administratorRoleName,
        string? registeredRoleName,
        string? administratorEmail,
        int? superTabId)
    {
        ArgumentNullException.ThrowIfNull(portal);

        return new PortalDetailDto
        {
            PortalId = portal.PortalId,
            PortalName = portal.PortalName,
            Description = portal.Description,
            KeyWords = portal.KeyWords,
            FooterText = portal.FooterText,
            LogoFile = portal.LogoFile,
            BackgroundFile = portal.BackgroundFile,
            ExpiryDate = portal.ExpiryDate,
            UserRegistration = portal.UserRegistration,
            BannerAdvertising = portal.BannerAdvertising,
            Currency = portal.Currency,
            AdministratorId = portal.AdministratorId,
            Email = administratorEmail,
            HostFee = portal.HostFee,
            HostSpace = portal.HostSpace,
            PageQuota = portal.PageQuota,
            UserQuota = portal.UserQuota,
            Users = users,
            Pages = pages,
            AdministratorRoleId = portal.AdministratorRoleId,
            AdministratorRoleName = administratorRoleName,
            RegisteredRoleId = portal.RegisteredRoleId,
            RegisteredRoleName = registeredRoleName,
            Guid = portal.PortalGuid,
            PaymentProcessor = portal.PaymentProcessor,
            ProcessorUserId = portal.ProcessorUserId,
            SiteLogHistory = portal.SiteLogHistory,
            AdminTabId = portal.AdminTabId,
            SuperTabId = superTabId,
            SplashTabId = portal.SplashTabId,
            HomeTabId = portal.HomeTabId,
            LoginTabId = portal.LoginTabId,
            UserTabId = portal.UserTabId,
            DefaultLanguage = portal.DefaultLanguage,
            TimeZoneOffset = portal.TimeZoneOffset,
            HomeDirectory = portal.HomeDirectory,
            Aliases = portal.PortalAliases.OrderBy(alias => alias.HttpAlias, StringComparer.OrdinalIgnoreCase)
                                          .Select(ToDto)
                                          .ToList(),
        };
    }

    /// <summary>
    /// Projects a portal onto the configuration contract the settings screen renders.
    /// </summary>
    /// <param name="portal">The portal to project.</param>
    /// <returns>The configuration contract.</returns>
    /// <remarks>
    /// This is a projection of stored columns, not a bag of keyed values: the shipped schema defines
    /// no portal settings table, so there is nothing keyed to read.
    /// </remarks>
    public static PortalSettingsDto ToSettings(Portal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);

        return new PortalSettingsDto
        {
            PortalId = portal.PortalId,
            PortalName = portal.PortalName,
            Description = portal.Description,
            KeyWords = portal.KeyWords,
            FooterText = portal.FooterText,
            LogoFile = portal.LogoFile,
            BackgroundFile = portal.BackgroundFile,
            ExpiryDate = portal.ExpiryDate,
            UserRegistration = portal.UserRegistration,
            BannerAdvertising = portal.BannerAdvertising,
            Currency = portal.Currency,
            AdministratorId = portal.AdministratorId,
            HostFee = portal.HostFee,
            HostSpace = portal.HostSpace,
            PageQuota = portal.PageQuota,
            UserQuota = portal.UserQuota,
            PaymentProcessor = portal.PaymentProcessor,
            ProcessorUserId = portal.ProcessorUserId,
            SiteLogHistory = portal.SiteLogHistory,
            SplashTabId = portal.SplashTabId,
            HomeTabId = portal.HomeTabId,
            LoginTabId = portal.LoginTabId,
            UserTabId = portal.UserTabId,
            DefaultLanguage = portal.DefaultLanguage,
            TimeZoneOffset = portal.TimeZoneOffset,
            HomeDirectory = portal.HomeDirectory,
            Guid = portal.PortalGuid,
        };
    }

    /// <summary>
    /// Projects one bound host name onto its transfer contract.
    /// </summary>
    /// <param name="alias">The alias to project.</param>
    /// <returns>The alias contract.</returns>
    public static PortalAliasDto ToDto(PortalAlias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);

        return new PortalAliasDto
        {
            PortalAliasId = alias.PortalAliasId,
            PortalId = alias.PortalId,
            HttpAlias = alias.HttpAlias,
        };
    }

    /// <summary>
    /// Builds a new portal aggregate from a creation request and the installation-wide defaults that
    /// the legacy creation path read from host configuration.
    /// </summary>
    /// <param name="request">The submitted creation request.</param>
    /// <param name="currency">Default currency code.</param>
    /// <param name="expiryDate">Default expiry instant, or <see langword="null"/> for no expiry.</param>
    /// <param name="hostFee">Default monthly hosting charge.</param>
    /// <param name="hostSpace">Default disc-space quota in whole megabytes.</param>
    /// <param name="pageQuota">Default page quota.</param>
    /// <param name="userQuota">Default member quota.</param>
    /// <param name="siteLogHistory">Default site-log retention in days, or <see langword="null"/> for none.</param>
    /// <param name="homeDirectory">The home directory to record, which may be empty when it is derived after the identifier is assigned.</param>
    /// <returns>An unsaved portal aggregate.</returns>
    /// <remarks>
    /// MIGRATION: reproduces the private two-argument <c>CreatePortal</c> at
    /// <c>Library/Components/Portal/PortalController.vb</c> lines 326 to 377, which seeded a new
    /// portal from the host settings <c>DemoPeriod</c>, <c>HostFee</c>, <c>HostSpace</c>,
    /// <c>PageQuota</c>, <c>UserQuota</c>, <c>SiteLogHistory</c> and <c>HostCurrency</c>. Reading
    /// those settings is the caller's work; this member only records the values it is given, so it
    /// stays free of data access and is directly testable.
    /// </remarks>
    public static Portal ToNewPortal(
        CreatePortalRequest request,
        string currency,
        DateTime? expiryDate,
        decimal hostFee,
        int hostSpace,
        int pageQuota,
        int userQuota,
        int? siteLogHistory,
        string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new Portal
        {
            PortalName = request.PortalName ?? string.Empty,
            Description = request.Description,
            KeyWords = request.KeyWords,
            Currency = currency,
            ExpiryDate = expiryDate,
            HostFee = ClampFee(hostFee),
            HostSpace = Math.Max(hostSpace, 0),
            PageQuota = Math.Max(pageQuota, 0),
            UserQuota = Math.Max(userQuota, 0),
            SiteLogHistory = siteLogHistory,
            HomeDirectory = homeDirectory,
            PortalGuid = Guid.NewGuid(),
            UserRegistration = UserRegistrationMode.NoRegistration,
            BannerAdvertising = BannerAdvertisingMode.None,
            DefaultLanguage = DefaultLanguageCode,
            TimeZoneOffset = DefaultTimeZoneOffsetMinutes,
        };
    }

    /// <summary>
    /// Applies a submitted update to a tracked portal aggregate.
    /// </summary>
    /// <param name="portal">The tracked portal to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// <para>
    /// The portal's own identifier is never taken from the request: the route segment is
    /// authoritative, so a caller cannot retarget the write at another tenant by editing the body.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy save path compiled with strictness disabled and relied on coercions that
    /// C# rejects. Each is made explicit here rather than left implicit. A blank monetary or quota box
    /// left its local at zero rather than at the absent-number sentinel, so an absent value is stored
    /// as zero and not as a database null; the four page references and the retention period did use
    /// the sentinel, so an absent value there remains absent. The disc-space and member-quota locals
    /// were declared as floating-point values despite addressing integer columns and were silently
    /// truncated at the procedure boundary, which is why the modern members are integers and no
    /// truncation can occur.
    /// </para>
    /// </remarks>
    public static void ApplyUpdate(Portal portal, UpdatePortalRequest request)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(request);

        portal.PortalName = request.PortalName ?? string.Empty;
        portal.LogoFile = request.LogoFile;
        portal.FooterText = request.FooterText;
        portal.ExpiryDate = request.ExpiryDate;
        portal.UserRegistration = request.UserRegistration;
        portal.BannerAdvertising = request.BannerAdvertising;
        portal.Currency = request.Currency;
        portal.AdministratorId = request.AdministratorId;
        portal.HostFee = ClampFee(request.HostFee ?? 0m);
        portal.HostSpace = Math.Max(request.HostSpace ?? 0, 0);
        portal.PageQuota = Math.Max(request.PageQuota ?? 0, 0);
        portal.UserQuota = Math.Max(request.UserQuota ?? 0, 0);
        portal.PaymentProcessor = request.PaymentProcessor;
        portal.ProcessorUserId = request.ProcessorUserId;
        portal.ProcessorPassword = request.ProcessorPassword;
        portal.Description = request.Description;
        portal.KeyWords = request.KeyWords;
        portal.BackgroundFile = request.BackgroundFile;
        portal.SiteLogHistory = request.SiteLogHistory;
        portal.SplashTabId = request.SplashTabId;
        portal.HomeTabId = request.HomeTabId;
        portal.LoginTabId = request.LoginTabId;
        portal.UserTabId = request.UserTabId;
        portal.DefaultLanguage = string.IsNullOrWhiteSpace(request.DefaultLanguage)
            ? DefaultLanguageCode
            : request.DefaultLanguage;
        portal.TimeZoneOffset = request.TimeZoneOffset ?? DefaultTimeZoneOffsetMinutes;
        portal.HomeDirectory = request.HomeDirectory ?? string.Empty;
    }

    /// <summary>
    /// Clamps a monetary charge so that a negative submission is stored as zero.
    /// </summary>
    /// <param name="fee">The submitted charge.</param>
    /// <returns>The charge, never below zero.</returns>
    /// <remarks>
    /// MIGRATION: reproduces the two clamps inside the role-creation step of portal creation at
    /// <c>Library/Components/Portal/PortalController.vb</c> lines 395 and 398, each of which read
    /// <c>CType(IIf(fee &lt; 0, 0, fee), Single)</c>. The legacy conditional was a function and so
    /// evaluated both arms, whereas the C# conditional operator short-circuits; both arms there are
    /// side-effect-free literals, so this substitution changes no observable behaviour. The clamp is
    /// reused for the hosting charge because that column is the same kind of value and the legacy
    /// screen offered no validator that would have refused a negative entry.
    /// </remarks>
    public static decimal ClampFee(decimal fee) => Math.Max(fee, 0m);

    /// <summary>
    /// The default culture code a new portal carries, matching the column default the 02.02.00
    /// upgrade script installed.
    /// </summary>
    private const string DefaultLanguageCode = "en-US";

    /// <summary>
    /// The default offset from co-ordinated universal time, in minutes, matching the column default
    /// the 02.02.00 upgrade script installed.
    /// </summary>
    private const int DefaultTimeZoneOffsetMinutes = -8;
}
