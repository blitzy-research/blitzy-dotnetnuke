using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="User"/> aggregate, its profile records and the user
/// transfer contracts.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the terminal <c>dbo.Users</c> table carries nine columns. Everything else an account
/// screen displays comes from somewhere else, and these projections make each source explicit rather
/// than hiding it behind one flattened class as the legacy entity did.
/// </para>
/// <para>
/// Approval, lock-out, creation, last-login, last-activity, last-lock-out and last-password-change are
/// held in the externally installed membership store, not in <c>dbo.Users</c>. They are carried on the
/// entity as unmapped projection members which the repository fills during the same read, so they are
/// read from the entity here. Where a value is genuinely unknown the entity member is
/// <see langword="null"/>, and the contract's non-nullable counterpart therefore reports the safe
/// answer - not approved, not locked out, not online - rather than inventing a permissive one.
/// </para>
/// <para>
/// The postal address and telephone number that the legacy account grid displayed are profile values
/// and were dropped from <c>dbo.Users</c> in the 02.02.01 upgrade script. They arrive as explicit
/// arguments so the caller can resolve them in one read for a whole page.
/// </para>
/// <para>
/// No projection here reads, writes, returns or logs a credential. The password hash is deliberately
/// absent from every contract below, and the only credential-adjacent member on any of them is the
/// flag that says a change is required.
/// </para>
/// </remarks>
public static class UserMappings
{
    /// <summary>
    /// Projects an account onto the row shape the account list renders.
    /// </summary>
    /// <param name="user">The account to project.</param>
    /// <param name="portalId">The tenant the account is being listed within.</param>
    /// <param name="address">The account's postal address profile value, or <see langword="null"/> when unset.</param>
    /// <param name="telephone">The account's telephone profile value, or <see langword="null"/> when unset.</param>
    /// <returns>The list row.</returns>
    public static UserListItemDto ToListItem(User user, int portalId, string? address, string? telephone)
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
        };
    }

    /// <summary>
    /// Projects an account onto the full detail contract.
    /// </summary>
    /// <param name="user">The account to project.</param>
    /// <param name="portalId">The tenant the account is being read within.</param>
    /// <param name="roles">The names of the roles the account currently holds in that tenant.</param>
    /// <returns>The detail contract.</returns>
    public static UserDetailDto ToDetail(User user, int portalId, IReadOnlyList<string> roles)
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
        };
    }

    /// <summary>
    /// Projects a profile property definition onto its transfer contract.
    /// </summary>
    /// <param name="definition">The definition to project.</param>
    /// <param name="defaultVisibility">The tenant's default visibility hint for a value that has not been set.</param>
    /// <returns>The definition contract.</returns>
    /// <remarks>
    /// MIGRATION: there is no visibility column on the definition table - verified across all
    /// eighty-eight upgrade scripts - so the contract's visibility member is a default hint rather than
    /// stored state, and it is supplied by the caller from the tenant's membership settings. The stored
    /// per-account column defaults to zero while this hint defaults to two, an asymmetry the legacy
    /// collection loader introduced and which is preserved rather than tidied away.
    /// </remarks>
    public static ProfilePropertyDefinitionDto ToDto(ProfilePropertyDefinition definition, int defaultVisibility)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new ProfilePropertyDefinitionDto
        {
            PropertyDefinitionId = definition.PropertyDefinitionId,
            PortalId = definition.PortalId,
            ModuleDefId = definition.ModuleDefinitionId,
            DataType = definition.DataType,
            DefaultValue = definition.DefaultValue,
            PropertyCategory = definition.PropertyCategory,
            PropertyName = definition.PropertyName,
            Length = definition.Length,
            Required = definition.Required,
            ValidationExpression = definition.ValidationExpression,
            ViewOrder = definition.ViewOrder,
            Visible = definition.Visible,
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
        int defaultVisibility)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(values);

        var byDefinition = new Dictionary<int, UserProfileValue>(values.Count);
        foreach (var value in values)
        {
            byDefinition[value.PropertyDefinitionId] = value;
        }

        var properties = new List<UserProfileValueDto>(definitions.Count);
        foreach (var definition in definitions)
        {
            var hasValue = byDefinition.TryGetValue(definition.PropertyDefinitionId, out var stored);

            properties.Add(new UserProfileValueDto
            {
                PropertyDefinitionId = definition.PropertyDefinitionId,
                PropertyValue = hasValue ? stored!.EffectiveValue ?? string.Empty : string.Empty,
                Visibility = hasValue ? stored!.Visibility : defaultVisibility,
                LastUpdatedDate = hasValue ? stored!.LastUpdatedDate : null,
                Definition = ToDto(definition, defaultVisibility),
            });
        }

        return new UserProfileDto
        {
            UserId = userId,
            Properties = properties,
        };
    }

    /// <summary>
    /// Builds a new account aggregate from a creation request.
    /// </summary>
    /// <param name="request">The submitted creation request.</param>
    /// <returns>An unsaved account aggregate, without its credential record.</returns>
    /// <remarks>
    /// The display name falls back to the given and family names joined by a single space, which is
    /// what the legacy portal-creation path composed for a new administrator when the screen supplied
    /// no display name of its own. The credential is not created here: it lives in the external
    /// membership store and is written through the repository's credential members.
    /// </remarks>
    public static User ToNewUser(CreateUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var firstName = request.FirstName;
        var lastName = request.LastName ?? string.Empty;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? $"{firstName} {lastName}".Trim()
            : request.DisplayName;

        return new User
        {
            Username = request.Username,
            FirstName = firstName,
            LastName = lastName,
            DisplayName = displayName,
            Email = request.Email,
            IsSuperUser = false,
            UpdatePassword = false,
        };
    }

    /// <summary>
    /// Applies a submitted update to a tracked account aggregate.
    /// </summary>
    /// <param name="user">The tracked account to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// The account name is not updatable: the legacy edit screen rendered it as read-only text once an
    /// account existed, and the request contract carries no member for it. Neither the host-account flag
    /// nor any credential member is written from a request.
    /// </remarks>
    public static void ApplyUpdate(User user, UpdateUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        user.FirstName = request.FirstName;
        user.LastName = request.LastName ?? string.Empty;
        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? $"{request.FirstName} {request.LastName ?? string.Empty}".Trim()
            : request.DisplayName;
        user.Email = request.Email;
    }

    /// <summary>
    /// Builds a new profile property definition from a submitted contract.
    /// </summary>
    /// <param name="portalId">Identifier of the portal the definition belongs to.</param>
    /// <param name="request">The submitted definition.</param>
    /// <returns>An unsaved definition.</returns>
    public static ProfilePropertyDefinition ToNewDefinition(int portalId, ProfilePropertyDefinitionDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var definition = new ProfilePropertyDefinition
        {
            PortalId = portalId,
            ModuleDefinitionId = request.ModuleDefId,
            Deleted = false,
        };

        ApplyDefinitionCore(definition, request);
        return definition;
    }

    /// <summary>
    /// Applies a submitted update to a tracked profile property definition.
    /// </summary>
    /// <param name="definition">The tracked definition to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// The owning portal and the deletion flag are never written from a request: the portal arrives from
    /// the route, and the flag is moved by a deletion rather than by an edit.
    /// </remarks>
    public static void ApplyDefinitionUpdate(ProfilePropertyDefinition definition, ProfilePropertyDefinitionDto request)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);

        ApplyDefinitionCore(definition, request);
    }

    /// <summary>
    /// Writes the definition member set that the creation and update paths share.
    /// </summary>
    private static void ApplyDefinitionCore(ProfilePropertyDefinition definition, ProfilePropertyDefinitionDto request)
    {
        definition.DataType = request.DataType;
        definition.DefaultValue = request.DefaultValue;
        definition.PropertyCategory = request.PropertyCategory;
        definition.PropertyName = request.PropertyName;
        definition.Length = Math.Max(request.Length, 0);
        definition.Required = request.Required;
        definition.ValidationExpression = request.ValidationExpression;
        definition.ViewOrder = request.ViewOrder;
        definition.Visible = request.Visible;
    }
}
