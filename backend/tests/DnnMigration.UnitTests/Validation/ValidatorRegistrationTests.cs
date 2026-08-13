using DnnMigration.Application;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Dtos.User;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>Guards the assembly-scanned request-validation boundary.</summary>
public sealed class ValidatorRegistrationTests
{
    /// <summary>Every request shape bound by a write or paging endpoint has a registered validator.</summary>
    [Fact]
    public void ApplicationRegistration_ContainsTheCompleteValidatorInventory()
    {
        ServiceCollection services = new();
        services.AddApplication();

        // The list is a SET, and each name appears exactly ONCE. That is not cosmetic: the comparison is
        // order-insensitive but NOT multiplicity-insensitive, so a name written twice demands two
        // registrations while the assembly scan correctly produces one.
        services
            .Select(descriptor => descriptor.ServiceType)
            .Where(serviceType => serviceType.IsGenericType
                && serviceType.GetGenericTypeDefinition() == typeof(IValidator<>))
            .Select(serviceType => serviceType.GetGenericArguments()[0])
            .Distinct()
            .Should().BeEquivalentTo(
                new[]
                {
                    typeof(LoginRequest),
                    typeof(CreateUserRequest),
                    typeof(UpdateUserRequest),
                    typeof(ChangePasswordRequest),

                    // The body-bound account search.
                    typeof(UserSearchRequest),

                    typeof(UpdateMembershipSettingsRequest),

                    // The profile write. Bound by the profile update verb, whose values are stored one row
                    // per definition, so the bound shape needs its own bounds rather than the definition's.
                    typeof(UserProfileDto),
                    typeof(CreateRoleRequest),
                    typeof(CreateModuleRequest),
                    typeof(UpdateModuleRequest),

                    typeof(ModuleExportRequest),
                    typeof(ModuleImportRequest),
                    typeof(CreatePortalRequest),
                    typeof(UpdatePortalRequest),

                    typeof(UpdatePortalSettingsRequest),
                    typeof(CreatePortalAliasRequest),
                    typeof(UpdatePortalAliasRequest),
                    typeof(UpdateRoleRequest),

                    // The two role-group write contracts. dbo.AddRoleGroup and dbo.UpdateRoleGroup write
                    // the name and the description and nothing else, so neither contract carries the group
                    // key or the owning portal - the first is issued by the store or taken from the route,
                    // the second is the resolved tenant.
                    typeof(CreateRoleGroupRequest),
                    typeof(UpdateRoleGroupRequest),

                    typeof(RoleAssignmentRequest),

                    typeof(CreateProfilePropertyDefinitionRequest),
                    typeof(UpdateProfilePropertyDefinitionRequest),

                    typeof(UpdateTabRequest),
                    typeof(PagedRequest),
                    typeof(PortalPagedRequest),
                    typeof(RolePagedRequest),
                    typeof(UserPagedRequest),
                    typeof(ModulePagedRequest),

                    // The account PICKER's paging request.
                    typeof(UserChoicePagedRequest),

                    // The role-membership paging request.
                    typeof(RoleUserPagedRequest),

                    typeof(ModuleSettingsDto),

                    // The service invitation-code redemption.
                    typeof(RedeemServiceCodeRequest),
                },
                "a bound request without a validator reaches services and persistence without the "
                + "declarative boundary the API promises, and a validator over a shape no verb binds - a "
                + "RESPONSE projection, for instance - advertises coverage that can never execute");
    }
}
