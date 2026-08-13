using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Application-layer contract for the user aggregate - an identity with credentials and a profile -
/// together with profile values, profile property definitions and the tenant-level membership settings that
/// govern how accounts are presented and administered.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this contract owns.</b> It is the single entry point for the eight client routes that administer
/// accounts: the account list, account creation, the account editor, the profile editor, the credential
/// screen, the account's own member-services catalogue, the tenant membership settings and the profile
/// definition catalogue.
/// </para>
/// <para>
/// <b>Signing in is not asked here either.</b> Credential verification, token issue, refresh rotation and
/// session termination belong to the sibling authentication and token contracts in this folder, as do both
/// replacements a successful sign-in may perform: a current-scheme work-factor upgrade and the bounded
/// legacy-to-BCrypt migration.
/// </para>
/// </remarks>
public interface IUserService
{
    /// <summary>
    /// Returns one page of the accounts belonging to a tenant, optionally narrowed by account name,
    /// electronic-mail address, a single profile property value, or approval state.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the tenant whose accounts are listed, backed by the legacy <c>Portals.PortalID</c>
    /// column.
    /// </param>
    /// <param name="page">The page coordinates to read.</param>
    /// <param name="userNameFilter">
    /// Optional account-name filter, matched as a prefix. <see langword="null"/> means "do not filter by
    /// account name"; the empty string is not a synonym for that and is rejected by request validation.
    /// </param>
    /// <param name="emailFilter">
    /// Optional electronic-mail filter, matched as a prefix, with the same <see langword="null"/> semantics
    /// as <paramref name="userNameFilter"/>.
    /// </param>
    /// <param name="profilePropertyName">Optional name of the profile property to filter on.</param>
    /// <param name="profilePropertyValue">
    /// Optional profile property value to match as a prefix, paired with <paramref
    /// name="profilePropertyName"/>.
    /// </param>
    /// <param name="isApproved">
    /// Optional approval-state filter. <see langword="false"/> selects the accounts awaiting approval, <see
    /// langword="true"/> selects the approved ones, and <see langword="null"/> selects both.
    /// </param>
    /// <param name="cancellationToken">Token observed while the page is read.</param>
    /// <returns>
    /// A successful result carrying the requested page, which is an empty page - never a failure and never
    /// a <see langword="null"/> value - when no account matches or when the tenant holds no accounts at
    /// all.
    /// </returns>
    /// <remarks>
    /// The page coordinates and the paging contract's own free-text filter are bounded by the shared
    /// <c>PagedRequestValidator</c>. The sort field is bounded HERE, and for this one collection the
    /// permitted set is EMPTY: a named ordering is refused with <c>user.list.sort-unsupported</c>.
    /// </remarks>
    Task<Result<PagedResult<UserListItemDto>>> ListUsersAsync(
        int portalId,
        PagedRequest page,
        string? userNameFilter = null,
        string? emailFilter = null,
        string? profilePropertyName = null,
        string? profilePropertyValue = null,
        bool? isApproved = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page of a tenant's accounts as an account PICKER needs them: the key to submit and the two
    /// captions an option shows, and nothing else.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant whose accounts are offered.</param>
    /// <param name="page">
    /// Page coordinates, the optional ordering, and the optional name filter carried by the paging
    /// contract's own free-text member.
    /// </param>
    /// <param name="cancellationToken">Token observed while the page is read.</param>
    /// <returns>
    /// A successful result carrying the requested page, which is an empty page - never a failure and never
    /// a <see langword="null"/> value - when no account matches or when the tenant holds none.
    /// </returns>
    /// <remarks>
    /// ⚠ WHY THIS IS NOT <see cref="ListUsersAsync"/> WITH A THINNER PROJECTION. A performance and privacy
    /// review measured the role-assignment screen filling its account drop-down, and its account count
    /// probe, from the account LISTING. Every candidate row carried a postal address, a telephone number,
    /// an electronic-mail address, a creation instant, a last-login instant and the approval, lockout,
    /// online and super-user flags out of the database and into browser memory so that three values could
    /// be rendered - and the drop-down is permitted to enumerate a tenant of up to a thousand accounts.
    /// </remarks>
    Task<Result<PagedResult<UserChoiceDto>>> ListAccountChoicesAsync(
        int portalId,
        PagedRequest page,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a single account within a tenant.</summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">
    /// Identifier of the account, backed by <c>Users.UserID</c>, which is declared <c>IDENTITY(1, 1)</c>,
    /// so zero is never a valid value.
    /// </param>
    /// <param name="cancellationToken">Token observed while the account is read.</param>
    /// <returns>
    /// A successful result whose value is the account, or a successful result whose value is <see
    /// langword="null"/> when no such account exists within the tenant.
    /// </returns>
    /// <remarks>
    /// The four legacy switches on those overloads all disappear.
    /// </remarks>
    Task<Result<UserDetailDto?>> GetUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates an account within a tenant and returns it as persisted.</summary>
    /// <param name="portalId">Identifier of the tenant that will own the new account.</param>
    /// <param name="request">The account to create.</param>
    /// <param name="cancellationToken">Token observed while the account is created.</param>
    /// <returns>
    /// A successful result carrying the persisted account, including its server-assigned identifier, which
    /// is what lets the API layer answer 201 Created with a location.
    /// </returns>
    /// <remarks>
    /// Read the status mapping carefully, because the legacy numbering is a trap. The creation enumeration
    /// declares 18 members valued 0 through 17 and its <c>Success</c> member is <b>13, not 0</b>.
    /// </remarks>
    Task<Result<UserDetailDto>> CreateUserAsync(
        int portalId,
        CreateUserRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Updates the editable columns of an existing account and returns it as persisted.</summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account to update.</param>
    /// <param name="request">The new values for the editable columns.</param>
    /// <param name="cancellationToken">Token observed while the account is updated.</param>
    /// <returns>
    /// A successful result carrying the account as persisted, which is what lets the API layer answer 200
    /// OK with the updated representation.
    /// </returns>
    /// <remarks>
    /// Electronic-mail uniqueness is deliberately <b>not</b> enforced, and there is consequently no
    /// duplicate-address failure code on this member.
    /// </remarks>
    Task<Result<UserDetailDto>> UpdateUserAsync(
        int portalId,
        int userId,
        UpdateUserRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an account from a tenant, together with the permission grants that reference it.</summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account to delete.</param>
    /// <param name="cancellationToken">Token observed while the account is deleted.</param>
    /// <returns>A successful result with no value, which is what lets the API layer answer 204 No Content.</returns>
    /// <remarks>
    /// Deletion cascades. Before removing the account, L200 removed the folder, module and tab permission
    /// grants keyed by its identifier - the three cleanup procedures the data-surface census records - and
    /// the implementation reproduces that by calling the sibling permission contract inside one unit of
    /// work, so a partial deletion cannot leave orphaned grants behind.
    /// </remarks>
    Task<Result> DeleteUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes an account's credential as a self-service change, presenting and verifying the current
    /// credential.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose credential is changing.</param>
    /// <param name="request">The credential change.</param>
    /// <param name="cancellationToken">Token observed while the credential is changed.</param>
    /// <returns>
    /// A successful result with no value. <b>It never carries a credential.</b> Documented failure codes
    /// correspond to the reachable members of the legacy credential-update status:
    /// <c>user.password.missing</c>, <c>user.password.invalid</c>, <c>user.password.mismatch</c>,
    /// <c>user.password.not-different</c> and <c>user.password.reset-failed</c>; together with
    /// <c>user.not-found</c>, <c>user.password.current-incorrect</c>, when a self-service change presents
    /// the wrong current credential, <c>user.password.reset-not-enabled</c>, when the deployment has
    /// administrative reset switched off, and <c>user.password.unsupported-operation</c>, for an
    /// unrecognised operation discriminator.
    /// </returns>
    /// <remarks>
    /// <b>The reset path never returns the new credential.</b> L906 assigned the provider's answer onto the
    /// account and then returned it, so the credential travelled back to the caller in clear text. That is
    /// not reproduced in any form.
    /// </remarks>
    Task<Result> ChangePasswordAsync(
        int portalId,
        int userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Resets an account's credential administratively, without presenting the current one.</summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose credential is being reset.</param>
    /// <param name="request">The new credential.</param>
    /// <param name="cancellationToken">Token observed while the credential is written.</param>
    /// <returns>
    /// A successful result with no value. <b>It never carries a credential.</b> Documented failure codes
    /// are those of <see cref="ChangePasswordAsync"/> less <c>user.password.current-incorrect</c>, which
    /// cannot arise, plus <c>user.password.reset-not-enabled</c> when the deployment has reset switched
    /// off.
    /// </returns>
    /// <remarks>
    /// This member owns the administrative fallback of credential migration. The first-login path belongs
    /// to <see cref="IAuthService"/> and is available only inside its bounded compatibility window; reset
    /// remains the route after that deadline, for an unsupported representation, or when the owner no
    /// longer knows the credential.
    /// </remarks>
    Task<Result> ResetPasswordAsync(
        int portalId,
        int userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the lockout on an account so that its holder can attempt to sign in again.</summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the locked account.</param>
    /// <param name="cancellationToken">Token observed while the lockout is cleared.</param>
    /// <returns>A successful result with no value.</returns>
    /// <remarks>
    /// A caveat worth recording next to this member: the legacy sign-in path treated a locked account as
    /// authenticated, because it derived its verdict by testing the status against outright failure alone.
    /// That is a discovered defect in the sign-in path, annotated where it lives - on the sibling
    /// authentication contract - and deliberately not repaired here.
    /// </remarks>
    Task<Result> UnlockUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>Approves an account, or revokes an approval already granted.</summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose approval state changes.</param>
    /// <param name="isApproved">
    /// <see langword="true"/> to approve the account, <see langword="false"/> to revoke an approval already
    /// granted.
    /// </param>
    /// <param name="cancellationToken">Token observed while the transition is applied.</param>
    /// <returns>A successful result with no value.</returns>
    /// <remarks>
    /// <b>Withdrawing an approval must revoke every refresh token the account holds</b>, through <see
    /// cref="ITokenService.RevokeAllRefreshTokensAsync"/>, in the same request.
    /// </remarks>
    Task<Result> SetUserApprovalAsync(
        int portalId,
        int userId,
        bool isApproved,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an account as required to change its credential at its next successful sign-in.</summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account to flag.</param>
    /// <param name="cancellationToken">Token observed while the flag is set.</param>
    /// <returns>A successful result with no value.</returns>
    Task<Result> RequirePasswordChangeAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the tenant-level membership settings that govern how accounts are listed, which profile fields
    /// are shown, and where a visitor is sent after signing in, registering or ending a session.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant whose settings are read.</param>
    /// <param name="cancellationToken">Token observed while the settings are read.</param>
    /// <returns>
    /// A successful result carrying the settings, <b>or a successful result whose value is <see
    /// langword="null"/> when the tenant has no settings source at all</b>.
    /// </returns>
    Task<Result<MembershipSettingsDto?>> GetMembershipSettingsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the tenant-level membership settings.</summary>
    /// <param name="portalId">Identifier of the tenant whose settings are written.</param>
    /// <param name="request">The complete settings to store.</param>
    /// <param name="cancellationToken">Token observed while the settings are written.</param>
    /// <returns>
    /// A successful result carrying what the write did BEYOND storing the policy - whether the display-name
    /// format changed, and how many accounts were consequently rewritten.
    /// </returns>
    /// <remarks>
    /// (24): CHANGING THE DISPLAY-NAME FORMAT REWRITES EVERY ACCOUNT IN THE TENANT, and this member owns
    /// that sweep. The legacy screen compared the submitted format against the stored one and, when they
    /// differed, started a BACKGROUND THREAD running <c>UserController.UpdateDisplayNames</c>, which walked
    /// the tenant's accounts issuing one update each.
    /// </remarks>
    Task<Result<MembershipSettingsUpdateResultDto>> UpdateMembershipSettingsAsync(
        int portalId,
        UpdateMembershipSettingsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates an electronic-mail address against the validation expression configured for one portal.
    /// </summary>
    /// <param name="portalId">The portal whose membership policy is applied.</param>
    /// <param name="email">The address to validate.</param>
    /// <param name="cancellationToken">Token observed while membership settings are read.</param>
    /// <returns>A successful result carrying the validation outcome.</returns>
    Task<Result<bool>> IsEmailValidAsync(
        int portalId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether the named account must complete or correct its profile before it may continue.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant the account is signing in to.</param>
    /// <param name="userId">Identifier of the account being admitted.</param>
    /// <param name="cancellationToken">Token observed while the two reads are made.</param>
    /// <returns>
    /// A successful result carrying <see langword="true"/> when the tenant requires a valid profile at
    /// sign-in AND the account leaves at least one required property empty; <see langword="false"/> in
    /// every other case, including a tenant with no settings source and an account that does not exist.
    /// </returns>
    Task<Result<bool>> RequiresProfileCompletionAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an account's profile: the values it holds for the profile properties its tenant defines.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose profile is read.</param>
    /// <param name="cancellationToken">Token observed while the profile is read.</param>
    /// <returns>
    /// A successful result carrying the profile, or a successful result whose value is <see
    /// langword="null"/> when no such account exists within the tenant.
    /// </returns>
    /// <remarks>
    /// The empty string is preserved and never normalised. The legacy string sentinel <em>is</em> the empty
    /// string, so a stored blank and a value that was never supplied were indistinguishable once read
    /// through the legacy path.
    /// </remarks>
    // Nullable to match the returns clause, which distinguishes two absences that must not be conflated -
    // an account that does not exist, reported as a null payload, and a defined property the account has
    // never filled in, which is present in the projection with an absent value so a client can render the
    // whole form from one read.
    Task<Result<UserProfileDto?>> GetProfileAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assembles everything this application holds about one account within one tenant into a single
    /// portability document.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant the export is scoped to.</param>
    /// <param name="userId">Identifier of the account being described.</param>
    /// <param name="cancellationToken">Token observed while the document is assembled.</param>
    /// <returns>
    /// A successful result carrying the document, or carrying <see langword="null"/> when the account is
    /// not a member of that tenant - the same absence convention as <see cref="GetUserAsync"/>, and for the
    /// same reason: an account that is not there is not an error.
    /// </returns>
    /// <remarks>
    /// PRIV-01. <b>It exists because nothing did.</b> A module's content could be exported and a subject's
    /// own personal data could not: the account record and the profile were readable through two separate
    /// endpoints, the role assignments through a third that is addressed by role rather than by account,
    /// and there was no single answer to "what do you hold about me".
    /// </remarks>
    // MIGRATION: NET-NEW, with no legacy antecedent. The legacy administration had no data-portability
    // affordance of any kind: Website/admin/Users/ViewProfile.ascx rendered a profile to a screen and no
    // legacy page, procedure or provider member assembled a subject's record for export.
    Task<Result<UserPersonalDataExportDto?>> ExportPersonalDataAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces an account's profile values with the supplied set.</summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose profile is written.</param>
    /// <param name="profile">The complete set of profile values to store.</param>
    /// <param name="cancellationToken">Token observed while the profile is written.</param>
    /// <returns>A successful result with no value.</returns>
    /// <remarks>
    /// The three validation codes are the three rules a profile property definition actually carries -
    /// required, declared length and a validation expression - so they are preserved rather than invented,
    /// and they are enforced here because they depend on tenant data that a static request validator cannot
    /// see.
    /// </remarks>
    Task<Result> UpdateProfileAsync(
        int portalId,
        int userId,
        UserProfileDto profile,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the profile property definitions a tenant declares, in their configured display order.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant whose definitions are listed.</param>
    /// <param name="cancellationToken">Token observed while the definitions are read.</param>
    /// <returns>
    /// A successful result carrying the definitions, which is an empty sequence - never <see
    /// langword="null"/> and never a failure - when the tenant declares none.
    /// </returns>
    Task<Result<IReadOnlyList<ProfilePropertyDefinitionDto>>> ListProfilePropertyDefinitionsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a single profile property definition within a tenant.</summary>
    /// <param name="portalId">Identifier of the tenant that declares the definition.</param>
    /// <param name="propertyDefinitionId">Identifier of the definition to read.</param>
    /// <param name="cancellationToken">Token observed while the definition is read.</param>
    /// <returns>
    /// A successful result carrying the definition, or a successful result whose value is <see
    /// langword="null"/> when no such definition exists within the tenant.
    /// </returns>
    Task<Result<ProfilePropertyDefinitionDto?>> GetProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>Declares a new profile property definition for a tenant.</summary>
    /// <param name="portalId">Identifier of the tenant that will declare the definition.</param>
    /// <param name="request">The definition to declare.</param>
    /// <param name="cancellationToken">Token observed while the definition is declared.</param>
    /// <returns>
    /// A successful result carrying the definition as persisted, including its assigned identifier, which
    /// is what lets the API layer answer 201 Created.
    /// </returns>
    /// <remarks>
    /// The duplicate-name code preserves a measured outcome rather than inventing one: the legacy add
    /// returned a value below the sentinel to signal a name collision, and the screen turned that into its
    /// duplicate-name message at L453 through L455. Encoding an error in the returned identifier is exactly
    /// the idiom a result with a code replaces.
    /// </remarks>
    Task<Result<ProfilePropertyDefinitionDto>> CreateProfilePropertyDefinitionAsync(
        int portalId,
        CreateProfilePropertyDefinitionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing profile property definition, including its position in the display order.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that declares the definition.</param>
    /// <param name="propertyDefinitionId">Identifier of the definition to update.</param>
    /// <param name="request">The new state of the definition.</param>
    /// <param name="cancellationToken">Token observed while the definition is updated.</param>
    /// <returns>
    /// A successful result carrying the definition as persisted, which is what lets the API layer answer
    /// 200 OK. Documented failure codes are <c>profile-definition.not-found</c>,
    /// <c>profile-definition.duplicate-name</c> and <c>persistence.conflict</c>.
    /// </returns>
    /// <remarks>
    /// MIGRATION: A WITHDRAWN DECLARATION READS AS ABSENT HERE, reported with the not-found code, exactly
    /// as it does from the single read and the tenant listing. Withdrawal is logical because stored answers
    /// reference the declaration, and this contract exposes no member that reads, restores or acknowledges
    /// a withdrawn one - so a declaration this contract will not show is a declaration it will not edit.
    /// </remarks>
    Task<Result<ProfilePropertyDefinitionDto>> UpdateProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        UpdateProfilePropertyDefinitionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a profile property definition from a tenant, together with the values accounts hold for it.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that declares the definition.</param>
    /// <param name="propertyDefinitionId">Identifier of the definition to remove.</param>
    /// <param name="cancellationToken">Token observed while the definition is removed.</param>
    /// <returns>A successful result with no value, which is what lets the API layer answer 204 No Content.</returns>
    /// <remarks>
    /// MIGRATION: A WITHDRAWN DECLARATION READS AS ABSENT HERE TOO, reported with the not-found code, so
    /// every member of this contract agrees on which declarations exist.
    /// </remarks>
    Task<Result> DeleteProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the member-services catalogue an account sees: every role the tenant publishes for
    /// self-service subscription, on the terms it is offered, annotated with that account's own
    /// subscription state and with the commands the legacy panel would have offered for each.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant whose published services are listed.</param>
    /// <param name="userId">Identifier of the account whose subscription state annotates each row.</param>
    /// <param name="cancellationToken">Token observed while the catalogue is read.</param>
    /// <returns>
    /// A successful result carrying the catalogue, which is EMPTY - not absent - when the tenant publishes
    /// no services.
    /// </returns>
    /// <remarks>
    /// The read is deliberately UNPAGED, because the read it replaces was: the legacy grid bound the whole
    /// result and hid itself when the count was zero (<c>:L154</c>). A tenant's set of public roles is
    /// small by construction - it is a published price list, not a data set.
    /// </remarks>
    Task<Result<IReadOnlyList<MemberServiceDto>>> ListMemberServicesAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes an account to one of the tenant's published services, or renews a subscription that has
    /// lapsed.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that publishes the service.</param>
    /// <param name="userId">Identifier of the account being subscribed.</param>
    /// <param name="roleId">
    /// Identifier of the role the service is expressed as. <b>Zero is a real key</b>: <c>Roles.RoleID</c>
    /// is <c>IDENTITY(0, 1)</c>.
    /// </param>
    /// <param name="cancellationToken">Token observed while the subscription is written.</param>
    /// <returns>A successful result with no value, which is what lets the API layer answer 204 No Content.</returns>
    Task<Result> SubscribeToServiceAsync(
        int portalId,
        int userId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels an account's subscription to one of the tenant's published services.</summary>
    /// <param name="portalId">Identifier of the tenant that publishes the service.</param>
    /// <param name="userId">Identifier of the account whose subscription is cancelled.</param>
    /// <param name="roleId">Identifier of the role the service is expressed as.</param>
    /// <param name="cancellationToken">Token observed while the cancellation is written.</param>
    /// <returns>A successful result with no value.</returns>
    Task<Result> CancelServiceAsync(
        int portalId,
        int userId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>Starts an account's free trial of one of the tenant's published paid services.</summary>
    /// <param name="portalId">Identifier of the tenant that publishes the service.</param>
    /// <param name="userId">Identifier of the account starting the trial.</param>
    /// <param name="roleId">Identifier of the role the service is expressed as.</param>
    /// <param name="cancellationToken">Token observed while the trial subscription is written.</param>
    /// <returns>A successful result with no value.</returns>
    Task<Result> StartServiceTrialAsync(
        int portalId,
        int userId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a service invitation code, subscribing an account to every role of the tenant that bears it.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant whose roles are searched for the code.</param>
    /// <param name="userId">Identifier of the account being subscribed.</param>
    /// <param name="request">The submitted code.</param>
    /// <param name="cancellationToken">Token observed while the subscriptions are written.</param>
    /// <returns>A successful result naming the services the code enrolled the account in, never empty.</returns>
    /// <remarks>
    /// First, it searched EVERY role of the tenant - <c>GetPortalRoles(PortalSettings.PortalId)</c> at
    /// <c>:L407</c> - and applied neither the public-role test nor the fee test that the grid's own
    /// commands applied. An invitation code is precisely the bypass for a service that is not published, so
    /// narrowing the search to public or free roles here would break the feature's whole purpose.
    /// </remarks>
    Task<Result<RedeemServiceCodeResultDto>> RedeemServiceCodeAsync(
        int portalId,
        int userId,
        RedeemServiceCodeRequest request,
        CancellationToken cancellationToken = default);
}
