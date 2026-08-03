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
/// MIGRATION: six members of the legacy portal entity are not columns of the portal table, and every
/// one of them arrives here as an explicit argument rather than being fetched. The member and page
/// tallies were lazy getters that each performed a count when the backing field was negative; the two
/// role names, the host-level root page and the administrator's electronic-mail address were resolved
/// by the legacy read view through correlated sub-queries and a left outer join onto the account
/// table. Passing them in is what keeps this type free of data access: no member below reaches a
/// repository, opens a connection or performs work of any kind that could fail. The alternative -
/// letting a mapper fetch what it lacks - is exactly the impurity the legacy hydrator exhibited, where
/// filling one entity issued further queries mid-projection.
/// </para>
/// <para>
/// MIGRATION: the portal key is carried through verbatim, and no member below treats any particular
/// value of it as meaning "absent". Two facts make that mandatory rather than fastidious. The key
/// column is declared as an identity seeded at minus one, so minus one names the host-level portal;
/// and the legacy sentinel module used that same minus one as its integer null, so the legacy
/// predicate that asked "is this value null" answered yes for a real portal. Zero is a real key too:
/// the shipped installation script identity-inserts the default portal with a key of zero. There is
/// therefore no comparison against zero, no comparison against minus one, no flooring of a key and no
/// negative-argument guard on a key anywhere in this file. Absence of a portal is expressible only as
/// a nullable key on some other record.
/// </para>
/// <para>
/// MIGRATION: the legacy null sentinel for a string was the empty string rather than a null
/// reference, which means an empty stored value and an absent one were indistinguishable once read.
/// The projections below preserve whichever of the two the entity holds: an empty string is copied as
/// an empty string and is never promoted to null, and a null is copied as null and is never demoted to
/// an empty string. A consumer that still reads the legacy contract therefore sees what it saw before,
/// and the one substitution that does occur - supplying an empty string where a request omitted a
/// value for a column that cannot hold null - is stated at the member that performs it.
/// </para>
/// <para>
/// MIGRATION: two members are renamed on the way across, and neither rename carries a semantic
/// change. The handle column is spelled in full upper case in the schema and is exposed by the entity
/// as <c>PortalGuid</c>, which the transfer contracts publish as <c>Guid</c>; and the legacy alias
/// property spelled its protocol prefix in full upper case, which the entity and the contract both
/// spell in title case. Both are handled by naming the C# members explicitly, which is why a rename
/// cannot silently drop a value here the way a by-convention match could.
/// </para>
/// <para>
/// MIGRATION: every serialisation decoration the legacy entity carried is dropped and replaced by
/// nothing at all. The legacy type was annotated for XML serialisation so that portal templates could
/// round-trip through it, with two members explicitly excluded from that document. No equivalent
/// annotation appears here or on the contracts: the wire format belongs to the serialiser configured
/// at the API edge, column binding belongs to the Infrastructure entity configuration, and constraint
/// checking belongs to the validators.
/// </para>
/// <para>
/// MIGRATION: there is no portal-settings entity to map, because the schema defines no such table.
/// The 88-script upgrade chain creates keyed settings tables for modules, for placements, for the host
/// and for scheduled items, and none for portals; the abstract data surface declares no member for one
/// and the concrete provider invokes no procedure for one. The legacy type of that name was a
/// per-request composite assembled into ambient request state, not a persisted aggregate. Portal
/// configuration is columns on the portal row, which is why <see cref="ToSettings"/> is an ordinary
/// column projection.
/// </para>
/// <para>
/// MIGRATION: one latent defect in the legacy sentinel helper is recorded here and deliberately left
/// unfixed, because correcting it would change behaviour this migration is required to preserve. Its
/// type-directed overload mapped both the 32-bit and the 64-bit signed integer types onto the same
/// 32-bit sentinel, so a 64-bit column could never have expressed absence correctly. Nothing in the
/// portal slice is 64-bit, so the defect is unreachable from here; it is noted rather than repaired.
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
    /// <remarks>
    /// MIGRATION: this row shape declares the hosting charge and the disc-space quota as required
    /// values rather than optional ones, which matches the entity and the terminal columns exactly -
    /// both are NOT NULL with a zero default constraint - so both copy straight across with no
    /// coalescing and no widening. The detail and configuration contracts declare the same two members
    /// as optional, and the reason for the asymmetry is recorded at those members. The tallies are
    /// arguments because they are counts the read path computes, never columns of the portal row.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: the host-level root page is the same value for every portal in an installation. The
    /// legacy read view resolved it with a sub-query that filtered the page table for rows belonging to
    /// no portal and having no parent, so it never varied by portal even though it was published on
    /// each portal's record. It is reproduced as an argument, with that oddity recorded rather than
    /// tidied away, because a consumer reading the legacy contract expects to find it here.
    /// </para>
    /// <para>
    /// MIGRATION: the electronic-mail address on this contract is the designated administrator's
    /// address, not an address of the portal. The portal table has no such column in any of the 88
    /// upgrade scripts; the legacy read view produced it by joining the account table on the
    /// administrator key, so a portal designating no administrator produced no address. That is why the
    /// argument is optional, and why an absent administrator yields a null here rather than an empty
    /// string.
    /// </para>
    /// </remarks>
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

            // MIGRATION: both file members carry the raw stored value, and a caller must not assume it
            // is a path. The legacy read path went through a view that rewrote each of them: where the
            // stored value began with the literal marker "fileid", the view substituted the folder and
            // name it found by matching that marker against the file table, and otherwise passed the
            // value through. THAT MAKES A READ-THEN-WRITE ROUND TRIP DESTRUCTIVE: reading a resolved
            // path and writing it back replaces the marker with the path and permanently severs the
            // link to the file record, which cannot be reconstructed from the path alone. Resolution
            // therefore does not happen here: the file subsystem is not part of this migration, and
            // inventing a half-resolution in a projection is how that link would be lost. The same
            // reasoning is why the update path stores whatever it is given, marker included.
            LogoFile = portal.LogoFile,
            BackgroundFile = portal.BackgroundFile,
            ExpiryDate = portal.ExpiryDate,
            UserRegistration = portal.UserRegistration,
            BannerAdvertising = portal.BannerAdvertising,
            Currency = portal.Currency,
            AdministratorId = portal.AdministratorId,
            Email = administratorEmail,

            // MIGRATION: the hosting terms are widened, never narrowed. Their type has disagreed with
            // itself across three layers of the legacy stack: the entity property was single-precision
            // floating point for the charge and a 32-bit integer for the space, the 27-argument save
            // signature declared BOTH as double-precision floating point, and the terminal columns are
            // money and int. The rule that settles it is that the schema and the entity win, so the
            // entity holds an exact decimal and a plain integer, both required. This contract declares
            // all four terms optional, so each widens implicitly on the outbound projection and
            // nothing is lost. The reverse direction cannot be implicit and is handled where it occurs.
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

            // MIGRATION: the contract declares this member optional, which admits three states -
            // absent, present but empty, and populated. This projection never yields the first of
            // them. The entity's alias collection is documented as meaning "this portal has none"
            // when it is empty and never "not loaded", because which navigation was loaded is the
            // repository's decision and is not observable from a count. Materialising it here
            // therefore reports what the aggregate actually says, and a caller reading an empty
            // sequence may rely on the portal genuinely having no bound host name. The ordering is
            // applied so the sequence is stable across calls; it is case-insensitive because a host
            // name is.
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
    /// <para>
    /// This is a projection of stored columns, not a bag of keyed values: the shipped schema defines
    /// no portal settings table, so there is nothing keyed to read. The evidence for that is
    /// recorded on the type.
    /// </para>
    /// <para>
    /// MIGRATION: the payment-gateway credential is absent from this contract and from the detail
    /// contract, and its absence is a decision rather than an omission. The legacy entity published it
    /// as an ordinary serialised member, so it travelled into every portal template and back from
    /// every settings screen in clear text. Neither response contract declares it, so no projection
    /// here is even able to echo it; it can only be written, and only through the update path.
    /// </para>
    /// <para>
    /// MIGRATION: the two file members and the four hosting terms behave here exactly as they do on the
    /// detail contract, for the reasons recorded there - the stored value is carried verbatim rather
    /// than resolved, and the required entity values widen into optional contract members.
    /// </para>
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
    /// <remarks>
    /// MIGRATION: the legacy alias entity declared three properties and no serialisation decoration of
    /// any kind, so this is a three-for-three copy with a single spelling change: the legacy property
    /// spelled its protocol prefix in full upper case, and both the entity and the contract spell it in
    /// title case. The host name is carried verbatim - neither trimmed, lower-cased nor otherwise
    /// normalised - because normalising a stored value on the way to a reader would report something
    /// the row does not contain, and tenant resolution matches on the stored form. Normalisation on the
    /// way in belongs to the write path, which owns it. The owning portal key is likewise copied as it
    /// stands, including the minus one that names the host-level portal.
    /// </remarks>
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
    /// <para>
    /// MIGRATION: reproduces the private two-argument <c>CreatePortal</c> at
    /// <c>Library/Components/Portal/PortalController.vb</c> lines 326 to 377, which seeded a new
    /// portal from the host settings <c>DemoPeriod</c>, <c>HostFee</c>, <c>HostSpace</c>,
    /// <c>PageQuota</c>, <c>UserQuota</c>, <c>SiteLogHistory</c> and <c>HostCurrency</c>. Reading
    /// those settings is the caller's work; this member only records the values it is given, so it
    /// stays free of data access and is directly testable.
    /// </para>
    /// <para>
    /// MIGRATION: every host setting the legacy seeding path read was a string, and it treated an empty
    /// one as zero by leaving its freshly declared local at zero. The shipped installation script shows
    /// why that mattered: it identity-inserts the default portal with an EMPTY STRING supplied for the
    /// hosting charge, into a column that a later upgrade converted to money. A real installation may
    /// therefore hold values that look nothing like a charge. The empty-means-zero behaviour is
    /// preserved by the caller reading the setting, and nothing here sanitises a stored value.
    /// </para>
    /// <para>
    /// MIGRATION: this is the ONE member in the file that is not a pure function of its arguments,
    /// because it generates the portal handle. That is deliberate and is the lesser of two evils. The
    /// handle column is NOT NULL with a newly generated default, and the all-zero handle is precisely
    /// the legacy sentinel for an absent one, so leaving the member at its type default would write a
    /// sentinel into a column that can never legitimately hold it. Generating it here writes a real
    /// handle instead. No other member below varies between calls: nothing reads a clock, a counter or
    /// any ambient state.
    /// </para>
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
            HostSpace = ClampQuota(hostSpace),
            PageQuota = ClampQuota(pageQuota),
            UserQuota = ClampQuota(userQuota),
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
    /// <para>
    /// MIGRATION: the four required numeric columns are the one place a null must be substituted rather
    /// than propagated, because none of them can hold one. The terminal schema declares the hosting
    /// charge as money NOT NULL with a zero default constraint and the disc-space, page and member
    /// quotas as int NOT NULL with the same, so an omitted term is recorded as zero. That is not an
    /// invention: the legacy save signature took all four by value and its callers declared each local
    /// at zero before conditionally overwriting it, so an omitted term already reached the database as
    /// zero. Zero is a meaningful value for each of them - it waives the charge and lifts the
    /// corresponding limit - which is why an authorisation rule, not this member, decides whether the
    /// caller was entitled to submit it.
    /// </para>
    /// <para>
    /// MIGRATION: the payment-gateway credential travels in ONE DIRECTION ONLY. It is written here and
    /// read nowhere: neither response contract declares it, so no projection can echo it back. It is
    /// also assigned unconditionally, which preserves a legacy behaviour worth stating plainly - the
    /// legacy save signature had no optional arguments, so submitting the settings screen with the box
    /// empty stored an empty credential and cleared the stored one. A blank therefore still clears it,
    /// and a caller intending to leave it alone must echo the value it already holds.
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
        portal.HostSpace = ClampQuota(request.HostSpace ?? 0);
        portal.PageQuota = ClampQuota(request.PageQuota ?? 0);
        portal.UserQuota = ClampQuota(request.UserQuota ?? 0);
        portal.PaymentProcessor = request.PaymentProcessor;
        portal.ProcessorUserId = request.ProcessorUserId;

        // MIGRATION: written, never read back. The contract that carries this credential is the update
        // request and nothing else, and assigning it unconditionally is what preserves the legacy
        // clear-on-blank behaviour described on this member.
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
    /// screen offered no validator that would have refused a negative entry. That reuse is a defensive
    /// floor the legacy save path did not apply, which is why it is named, published and tested rather
    /// than buried in an assignment.
    /// </remarks>
    public static decimal ClampFee(decimal fee) => fee < 0m ? 0m : fee;

    /// <summary>
    /// Floors a quota or allowance so that a negative submission is stored as zero.
    /// </summary>
    /// <param name="quota">The submitted quota or allowance.</param>
    /// <returns>The quota, never below zero.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the counterpart of <see cref="ClampFee"/> for the three integer allowances - the
    /// disc-space allowance and the page and member quotas. It is written as a conditional rather than
    /// as a maximum of two values so that it reads as the legacy conditional it descends from, and so
    /// that a reader can see at a glance that the floor is applied to an ALLOWANCE and never to a key.
    /// Zero is the documented meaning of "no limit" for all three columns, so flooring a negative
    /// submission is a coercion into the column's own domain rather than a business rule about quotas.
    /// </para>
    /// <para>
    /// MIGRATION: this member exists so that the distinction between a quota and an identifier is
    /// explicit in the source. A floor of this shape is correct for an allowance and would be a defect
    /// on a portal key, where both zero and minus one name real rows; no key in this file passes
    /// through here or through any other floor, guard or comparison.
    /// </para>
    /// </remarks>
    public static int ClampQuota(int quota) => quota < 0 ? 0 : quota;

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
