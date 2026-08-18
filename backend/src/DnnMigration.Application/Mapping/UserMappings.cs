using DnnMigration.Application.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using RoleEntity = DnnMigration.Domain.Entities.Role;
using UserEntity = DnnMigration.Domain.Entities.User;

namespace DnnMigration.Application.Mapping;

// INVARIANT every edit to this file must preserve: no projection here reads User.PasswordHash,
// User.PasswordAnswer or User.PasswordQuestion, and none of the three is logged, formatted or
// interpolated. ChangePasswordRequest is the only inbound contract that carries a password and is
// deliberately not mapped here at all - the plaintext travels from the controller to
// IUserService.ChangePasswordAsync, and hashing lives in Infrastructure/Security/BcryptPasswordHasher.cs
// behind the Domain-declared abstraction. THIS FILE NEVER HASHES.

/// <summary>
/// Hand-written projections between the <see cref="UserEntity"/> aggregate, its profile records and the
/// user transfer contracts.
/// </summary>
/// <remarks>
/// <para>
/// Every method here is a pure, synchronous function of its arguments. Nothing awaits, nothing reads a
/// clock, nothing touches a repository or a cache, nothing validates and nothing hashes.
/// </para>
/// <para>
/// No projection here reads, writes, returns or logs a credential. See the credential boundary recorded
/// immediately above this type.
/// </para>
/// </remarks>
public static class UserMappings
{
    /// <summary>
    /// The legacy integral encoding of a host-level profile-property declaration, published OUTBOUND only.
    /// </summary>
    private const int LegacyHostPortalId = -1;

    /// <summary>Projects one account picker row onto its wire contract.</summary>
    /// <param name="choice">The projected choice, as the repository read it.</param>
    /// <returns>The wire contract for one selectable account.</returns>
    /// <remarks>
    /// Nothing is defaulted, coerced or substituted. The domain projection and the contract carry the same
    /// three values with the same nullability, so an empty display name arrives as an empty display name -
    /// the legacy absent-string sentinel is <c>""</c> literally, and a client captions such an option with
    /// the login name rather than treating the row as unusable.
    /// </remarks>
    public static UserChoiceDto ToChoice(AccountChoice choice)
    {
        return new UserChoiceDto
        {
            UserId = choice.UserId,
            Username = choice.Username,
            DisplayName = choice.DisplayName,
        };
    }

    /// <summary>Projects an account onto the row shape the account list renders.</summary>
    /// <param name="user">The account to project.</param>
    /// <param name="portalId">The tenant the account is being listed within.</param>
    /// <param name="address">
    /// The account's postal address profile value, or <see langword="null"/> when unset.
    /// </param>
    /// <param name="telephone">The account's telephone profile value, or <see langword="null"/> when unset.</param>
    /// <param name="portalAdministratorId">
    /// The account named by the tenant's <c>Portals.AdministratorId</c>, or <see langword="null"/> when the
    /// tenant designates nobody.
    /// </param>
    /// <returns>The list row.</returns>
    /// <remarks>
    /// The tenant is an argument rather than a property read because the aggregate has no portal scalar; it
    /// is assigned through unchanged, since a portal identifier of -1 is the genuine host portal created by
    /// the <c>IDENTITY(-1, 1)</c> seed on <c>Portals.PortalID</c> and 0 is the shipped <c>_default</c>
    /// portal. Neither may be mistaken for an absent value.
    /// </remarks>
    public static UserListItemDto ToListItem(
        UserEntity user,
        int portalId,
        string? address,
        string? telephone,
        int? portalAdministratorId)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new UserListItemDto
        {
            UserId = user.UserId,
            PortalId = portalId,
            Username = user.Username,
            FirstName = user.FirstName,
            LastName = user.LastName,
            DisplayName = user.DisplayName,
            Address = address,
            Telephone = telephone,

            Email = user.Email ?? string.Empty,

            CreatedDate = user.CreatedDate,
            LastLoginDate = user.LastLoginDate,

            IsApproved = user.IsApproved ?? false,
            IsOnline = user.IsOnline ?? false,
            IsSuperUser = user.IsSuperUser,
            IsLockedOut = user.IsLockedOut ?? false,

            // ⚠ THE ADMINISTRATOR COMPARISON IS AN EQUALITY AND MUST STAY ONE. Users.UserID seeds
            // IDENTITY(1, 1), but Portals.AdministratorId is an ordinary nullable integer column and the
            // legacy null contract spells a missing integer as MINUS ONE - so a portal that designates
            // nobody may hold either null or -1, and neither may be read as "matches this row".
            CanDelete = CanBeDeleted(user, portalAdministratorId),
        };
    }

    /// <summary>Whether an account may be removed from a tenant.</summary>
    /// <param name="user">The account being judged.</param>
    /// <param name="portalAdministratorId">
    /// The account named by the tenant's <c>Portals.AdministratorId</c>, or <see langword="null"/> when the
    /// tenant designates nobody.
    /// </param>
    /// <returns><see langword="true"/> when the removal operation would be permitted.</returns>
    /// <remarks>
    /// ⚠ THE ADMINISTRATOR COMPARISON IS AN EQUALITY AND MUST STAY ONE. <c>Users.UserID</c> seeds
    /// <c>IDENTITY(1, 1)</c>, but <c>Portals.AdministratorId</c> is an ordinary nullable integer column and
    /// the legacy null contract spells a missing integer as MINUS ONE - so a portal that designates nobody
    /// may hold either null or -1, and neither may be read as "matches this row".
    /// </remarks>
    private static bool CanBeDeleted(UserEntity user, int? portalAdministratorId) =>
        !user.IsSuperUser
        && (portalAdministratorId is not { } designated || designated != user.UserId);

    /// <summary>Projects an account onto the full detail contract.</summary>
    /// <param name="user">The account to project.</param>
    /// <param name="portalId">The tenant the account is being read within.</param>
    /// <param name="roles">The names of the roles the account currently holds in that tenant.</param>
    /// <param name="portalAdministratorId">
    /// The account named by the tenant's <c>Portals.AdministratorId</c>, or <see langword="null"/> when the
    /// tenant designates nobody.
    /// </param>
    /// <returns>The detail contract.</returns>
    /// <remarks>
    /// <c>MustChangePassword</c> is the only credential-adjacent member on this contract. It maps
    /// <c>User.UpdatePassword</c>, a genuine <c>dbo.Users</c> column and a boolean flag - it is not, and
    /// must never become, a channel for the credential itself.
    /// </remarks>
    public static UserDetailDto ToDetail(
        UserEntity user,
        int portalId,
        IReadOnlyList<string> roles,
        int? portalAdministratorId)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roles);

        return new UserDetailDto
        {
            UserId = user.UserId,
            PortalId = portalId,
            Username = user.Username,
            FirstName = user.FirstName,
            LastName = user.LastName,
            DisplayName = user.DisplayName,

            // MIGRATION: one email member, never two - see the F3 note above this type.
            Email = user.Email ?? string.Empty,

            IsSuperUser = user.IsSuperUser,
            AffiliateId = user.AffiliateId,
            IsApproved = user.IsApproved ?? false,
            IsLockedOut = user.IsLockedOut ?? false,
            IsOnline = user.IsOnline ?? false,
            MustChangePassword = user.UpdatePassword,

            CreatedDate = user.CreatedDate,
            LastLoginDate = user.LastLoginDate,
            LastActivityDate = user.LastActivityDate,
            LastLockoutDate = user.LastLockoutDate,
            LastPasswordChangeDate = user.LastPasswordChangeDate,

            Roles = roles,

            // The SAME rule the list projection publishes, from the one member that owns it. Before this
            // the detail contract carried no capability at all and the client screen approximated the rule,
            // omitting the administrator clause - so the two surfaces disagreed for one account.
            CanDelete = CanBeDeleted(user, portalAdministratorId),

            ConcurrencyToken = ConcurrencyTokenFor(user),
        };
    }

    /// <summary>Derives the optimistic-concurrency token for an account.</summary>
    /// <param name="user">The account whose current values are hashed.</param>
    /// <returns>The token.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="user"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// ⚠ THE MEMBER ORDER IS PART OF THE CONTRACT, and so is the member SET. The token published by a read
    /// and the token verified by a write are both produced here, so a change stays self-consistent - but a
    /// token already in a browser's hands stops matching, and every open editor is refused once.
    /// </para>
    /// <para>
    /// ⚠ ONLY THE FOUR MEMBERS AN UPDATE REPLACES CONTRIBUTE, and excluding the rest is the point rather
    /// than an economy. Approval, lock state, the last-login moment and the must-change-password flag all
    /// move through paths of their own - a sign-in, a failed sign-in, an administrator's approval switch -
    /// and several move with no operator acting at all. Including any of them would refuse an
    /// administrator's rename because the account's owner happened to sign in while the form was open,
    /// which is a false conflict: nothing the caller proposed to write had been touched.
    /// </para>
    /// </remarks>
    internal static string ConcurrencyTokenFor(UserEntity user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return ConcurrencyToken.From(
            user.FirstName,
            user.LastName,
            user.DisplayName,
            user.Email);
    }

    /// <summary>Projects a profile property definition onto its transfer contract.</summary>
    /// <param name="definition">The definition to project.</param>
    /// <param name="defaultVisibility">
    /// The tenant's default visibility hint for a value that has not been set.
    /// </param>
    /// <returns>The definition contract.</returns>
    /// <remarks>
    /// There is no visibility column on the definition table - verified across all eighty-eight upgrade
    /// scripts - because per Pattern F5 visibility is a fact about an account's answer and lives on
    /// <c>UserProfileValue</c>. The contract's visibility member is therefore a default HINT rather than
    /// stored state, and the caller supplies it from the tenant's membership settings.
    /// </remarks>
    public static ProfilePropertyDefinitionDto ToDto(ProfilePropertyDefinition definition, int defaultVisibility)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new ProfilePropertyDefinitionDto
        {
            PropertyDefinitionId = definition.PropertyDefinitionId,

            PortalId = definition.PortalId ?? LegacyHostPortalId,

            ModuleDefId = definition.ModuleDefinitionId,
            DataType = definition.DataType,
            DefaultValue = definition.DefaultValue,
            PropertyCategory = definition.PropertyCategory,
            PropertyName = definition.PropertyName,
            Length = definition.Length,

            // The three Is-prefixed entity members map back to their unprefixed column names here -
            // IsRequired to Required and IsVisible to Visible.
            Required = definition.IsRequired,
            ValidationExpression = definition.ValidationExpression,
            ViewOrder = definition.ViewOrder,
            Visible = definition.IsVisible,

            Visibility = defaultVisibility,
        };
    }

    /// <summary>
    /// Projects an account's profile as the full set of its tenant's definitions, each paired with the
    /// value the account has supplied for it.
    /// </summary>
    /// <param name="userId">The account whose profile is projected.</param>
    /// <param name="definitions">The tenant's definitions, already in view order.</param>
    /// <param name="values">The values the account has supplied.</param>
    /// <param name="defaultVisibility">The tenant's default visibility hint.</param>
    /// <param name="displayVisibilityEnabled">
    /// Whether the tenant lets an account holder choose who may see each of their own values.
    /// </param>
    /// <returns>The profile contract.</returns>
    /// <remarks>
    /// Every definition appears in the result, including one the account has never filled in, which is
    /// reported with an empty value and no last-updated instant. That is what lets a client render the
    /// whole form from a single read instead of having to reconcile two collections itself.
    /// </remarks>
    public static UserProfileDto ToProfile(
        int userId,
        IReadOnlyList<ProfilePropertyDefinition> definitions,
        IReadOnlyList<UserProfileValue> values,
        int defaultVisibility,
        bool displayVisibilityEnabled)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(values);

        // Indexed by definition so the pairing below is a lookup rather than a nested scan. A duplicate row
        // for one definition is not expected - the table is keyed by (UserID, PropertyDefinitionID) - and
        // the later row wins if one ever appears.
        var byDefinition = new Dictionary<int, UserProfileValue>(values.Count);
        foreach (UserProfileValue value in values)
        {
            byDefinition[value.PropertyDefinitionId] = value;
        }

        var properties = new List<UserProfileValueDto>(definitions.Count);
        foreach (ProfilePropertyDefinition definition in definitions)
        {
            UserProfileValue? stored = byDefinition.GetValueOrDefault(definition.PropertyDefinitionId);

            properties.Add(new UserProfileValueDto
            {
                PropertyDefinitionId = definition.PropertyDefinitionId,

                // Two physical columns, one logical value, and the coalescing is this file's job because
                // the entity exposes both columns raw and no derived member.
                PropertyValue = stored is null
                    ? string.Empty
                    : stored.PropertyValue ?? stored.PropertyText ?? string.Empty,

                // A stored visibility of zero is a real answer and is reported as zero; only the ABSENCE of
                // a row falls back to the tenant's hint. Testing the integer for zero instead would
                // collapse the two.
                Visibility = stored is null ? defaultVisibility : stored.Visibility,

                LastUpdatedDate = stored?.LastUpdatedDate,
                Definition = ToDto(definition, defaultVisibility),
            });
        }

        return new UserProfileDto
        {
            UserId = userId,
            Properties = properties,
            DisplayVisibilityEnabled = displayVisibilityEnabled,
        };
    }

    /// <summary>Builds a new account aggregate from a creation request.</summary>
    /// <param name="request">The submitted creation request.</param>
    /// <returns>An unsaved account aggregate, without its credential record.</returns>
    /// <remarks>
    /// MIGRATION: the credential is deliberately absent from the result. <c>CreateUserRequest</c> does
    /// carry a password, and this projection does not read it: the plaintext goes from the service to
    /// <c>IPasswordHasher</c> and from there to the external membership store, so <c>PasswordHash</c> is
    /// left null on the returned aggregate and this file never hashes.
    /// </remarks>
    public static UserEntity ToNewUser(CreateUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The coalescing is not redundant even though the request member is declared non-nullable.
        string firstName = request.FirstName;
        string lastName = request.LastName ?? string.Empty;

        string displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? $"{firstName} {lastName}".Trim()
            : request.DisplayName;

        return new UserEntity
        {
            // THE ACCOUNT NAME IS CANONICALISED HERE, AT THE ONE PLACE THAT BUILDS THE ROW, and storing it
            // verbatim was a defect that produced an account nobody could reach.
            Username = request.Username.Trim(),
            FirstName = firstName,
            LastName = lastName,
            DisplayName = displayName,
            Email = request.Email,

            IsSuperUser = false,
            UpdatePassword = false,
        };
    }

    /// <summary>Applies a submitted update to a tracked account aggregate.</summary>
    /// <param name="user">The tracked account to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// Exactly four members are written. Identity, sign-in name, the host-account flag, the affiliate,
    /// every membership instant and every credential member are all left untouched, so an update that
    /// carries no password cannot blank one.
    /// </remarks>
    public static void ApplyUpdate(UserEntity user, UpdateUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        user.FirstName = request.FirstName;

        // MIGRATION: floored to the empty string for the NOT NULL column, for the reason given in
        // ToNewUser above.
        user.LastName = request.LastName ?? string.Empty;

        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? $"{request.FirstName} {request.LastName ?? string.Empty}".Trim()
            : request.DisplayName;

        user.Email = request.Email;
    }

    /// <summary>Builds a new profile property definition from a submitted contract.</summary>
    /// <param name="portalId">
    /// The scope the definition belongs to: an identifier for a tenant-owned declaration, or <see
    /// langword="null"/> for a host-level declaration stored with a SQL <c>NULL</c> portal.
    /// </param>
    /// <param name="request">The submitted definition.</param>
    /// <returns>An unsaved definition.</returns>
    /// <remarks>
    /// This path binds the CREATE request rather than the response projection, and the module definition
    /// key below is the reason the two verbs cannot share one contract.
    /// </remarks>
    public static ProfilePropertyDefinition ToNewDefinition(
        int? portalId,
        CreateProfilePropertyDefinitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var definition = new ProfilePropertyDefinition
        {
            PortalId = portalId,
            ModuleDefinitionId = request.ModuleDefId,

            // MIGRATION: a definition is born live. IsDeleted is moved by a deletion, never by a
            // create or an edit, so neither path below reads it from the request.
            IsDeleted = false,
        };

        ApplyDefinitionCore(definition, request);
        return definition;
    }

    /// <summary>Applies a submitted update to a tracked profile property definition.</summary>
    /// <param name="definition">The tracked definition to modify.</param>
    /// <param name="request">The submitted values.</param>
    public static void ApplyDefinitionUpdate(
        ProfilePropertyDefinition definition,
        UpdateProfilePropertyDefinitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);

        ApplyDefinitionCore(definition, request);
    }

    /// <summary>Writes the definition member set that the creation and update paths share.</summary>
    /// <param name="definition">The definition to write to.</param>
    /// <param name="request">The submitted values.</param>
    private static void ApplyDefinitionCore(
        ProfilePropertyDefinition definition,
        IProfilePropertyDefinitionWriteMembers request)
    {
        definition.DataType = request.DataType;
        definition.DefaultValue = request.DefaultValue;
        definition.PropertyCategory = request.PropertyCategory;

        definition.PropertyName = request.PropertyName;

        // This floor is a COLUMN WIDTH, not an identifier, and the distinction is the whole point. A
        // negative display width is meaningless, so it is floored to zero - the behaviour the profile
        // editor relied on.
        definition.Length = Math.Max(request.Length, 0);

        definition.IsRequired = request.Required;
        definition.ValidationExpression = request.ValidationExpression;
        definition.ViewOrder = request.ViewOrder;

        definition.IsVisible = request.Required || request.Visible;
    }

    /// <summary>
    /// Projects one published role, and the account's own assignment to it, onto a member-services
    /// catalogue row.
    /// </summary>
    /// <param name="role">The published role the service is expressed as.</param>
    /// <param name="assignment">
    /// The account's assignment to that role, or <see langword="null"/> when it holds none.
    /// </param>
    /// <param name="today">
    /// The current date, supplied by the caller from the injected clock, against which a lapsed
    /// subscription is recognised.
    /// </param>
    /// <param name="tenantTakesPayment">
    /// Whether the tenant has a payment processor account configured, which is the second arm of the legacy
    /// subscribe predicate.
    /// </param>
    /// <returns>The catalogue row.</returns>
    /// <remarks>
    /// The current date is a PARAMETER rather than a reading taken here, for two reasons. A mapper that
    /// read a clock would be untestable, and - the substantive one - one row's classification must not be
    /// able to disagree with the next row's because the two were mapped either side of midnight.
    /// </remarks>
    public static MemberServiceDto ToMemberService(
        RoleEntity role,
        UserRole? assignment,
        DateTime today,
        bool tenantTakesPayment)
    {
        ArgumentNullException.ThrowIfNull(role);

        bool subscribed = assignment is not null;
        bool trialUsed = assignment?.IsTrialUsed ?? false;

        // ServiceText's own two tests, in its own order: not subscribed at all, otherwise subscribed with
        // an expiry strictly in the past.
        bool expired = subscribed
            && assignment!.ExpiryDate is DateTime expiry
            && expiry.Date < today.Date;

        bool chargesFee = role.ServiceFee is decimal serviceFee && serviceFee > 0m;
        bool chargesTrialFee = role.TrialFee is decimal trialFee && trialFee > 0m;

        return new MemberServiceDto
        {
            RoleId = role.RoleId,
            RoleName = role.RoleName,
            Description = role.Description,
            ServiceFee = role.ServiceFee,
            BillingPeriod = role.BillingPeriod,
            BillingFrequency = role.BillingFrequency,
            TrialFee = role.TrialFee,
            TrialPeriod = role.TrialPeriod,
            TrialFrequency = role.TrialFrequency,
            EffectiveDate = assignment?.EffectiveDate,
            ExpiryDate = assignment?.ExpiryDate,
            IsSubscribed = subscribed,
            IsTrialUsed = trialUsed,
            IsExpired = expired,
            SubscriptionAction = !subscribed
                ? MemberServiceActions.Subscribe
                : expired
                    ? MemberServiceActions.Renew
                    : MemberServiceActions.Unsubscribe,

            SubscriptionOffered = role.IsPublic && (!chargesFee || tenantTakesPayment),
            SubscriptionRequiresPayment = chargesFee,

            // ShowTrial: its first arm returns FALSE for a public role with no fee - a free service has
            // nothing to trial - and its second offers the trial only when the trial itself is free and
            // this account has not already consumed it.
            TrialOffered = role.IsPublic && chargesFee && !chargesTrialFee && !trialUsed,
        };
    }

    /// <summary>Projects a role an invitation code enrolled an account in onto the redemption result row.</summary>
    /// <param name="role">The role the account was enrolled in.</param>
    /// <returns>The result row.</returns>
    public static RedeemedServiceDto ToRedeemedService(RoleEntity role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return new RedeemedServiceDto
        {
            RoleId = role.RoleId,
            RoleName = role.RoleName,
        };
    }
}
