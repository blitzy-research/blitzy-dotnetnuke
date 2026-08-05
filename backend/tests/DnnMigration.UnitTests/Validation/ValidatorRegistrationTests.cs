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

        // The inventory is stated BY NAME rather than by count, because which requests carry a validator is
        // the substance of the registration and a number would not say it. Every request shape an endpoint
        // binds appears below, so this list is what a reviewer checks a new endpoint against.
        //
        // The list is a SET, and each name appears exactly ONCE. That is not cosmetic: the comparison is
        // order-insensitive but NOT multiplicity-insensitive, so a name written twice demands two
        // registrations while the assembly scan correctly produces one. Three names - the role update, the
        // role assignment and the page update - were reached independently by two separate audits of the
        // write surface and were duplicated here for a while for exactly that reason.
        //
        // SIX of the entries are paging contracts. Each listed collection binds its own derived request with
        // its own sortable allowlist, rather than sharing one shape, because sharing made one listing accept
        // an ordering it silently discarded and made another refuse an ordering its service could perform.
        // The base contract keeps a registration of its own for any endpoint that binds it.
        //
        // MIGRATION: two names were WITHDRAWN from this inventory, and their absence is load-bearing.
        // RoleGroupDto and ProfilePropertyDefinitionDto were each bound by both write verbs of their
        // resource, so one validator over one response projection governed create and update alike - which is
        // precisely why those boundaries advertised members neither procedure writes. Each resource now binds
        // a create contract and an update contract carrying only what the procedure behind that verb honours,
        // and a validator resolving for either projection again would mean a verb had started binding it.
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

                    // The membership-settings update. A dedicated REQUEST contract, replacing the settings
                    // projection that both the read and the write once bound: the projection carried members
                    // the update procedure does not honour, so the boundary judged fields it then discarded.
                    typeof(UpdateMembershipSettingsRequest),

                    // The profile write. Bound by the profile update verb, whose values are stored one row
                    // per definition, so the bound shape needs its own bounds rather than the definition's.
                    typeof(UserProfileDto),
                    typeof(CreateRoleRequest),
                    typeof(CreateModuleRequest),
                    typeof(UpdateModuleRequest),

                    // The two module content-transfer contracts. Both are bound by write verbs that name a
                    // file, and both were found reaching the provider unbounded: an export names the document
                    // it writes and an import names the one it reads, and each is length- and shape-checked
                    // here rather than by the file system.
                    typeof(ModuleExportRequest),
                    typeof(ModuleImportRequest),
                    typeof(CreatePortalRequest),
                    typeof(UpdatePortalRequest),

                    // The site-settings update, which is a distinct contract from the portal update: the two
                    // verbs honour different member sets, and one shape covering both advertised members the
                    // settings procedure discards.
                    typeof(UpdatePortalSettingsRequest),
                    typeof(CreatePortalAliasRequest),
                    typeof(UpdatePortalAliasRequest),
                    typeof(UpdateRoleRequest),

                    // The two role-group write contracts. dbo.AddRoleGroup and dbo.UpdateRoleGroup write
                    // the name and the description and nothing else, so neither contract carries the group
                    // key or the owning portal - the first is issued by the store or taken from the route,
                    // the second is the resolved tenant. Both validators read RoleGroupTermsRules, so the
                    // two verbs cannot drift apart on a shared member.
                    typeof(CreateRoleGroupRequest),
                    typeof(UpdateRoleGroupRequest),

                    typeof(RoleAssignmentRequest),

                    // The two profile-definition write contracts. The procedures behind the verbs honour
                    // DIFFERENT member sets - AddPropertyDefinition (04.06.00:L1101) declares a
                    // module-definition key that UpdatePropertyDefinition (04.05.00:L1685) does not - so a
                    // single shape could not describe both without advertising a member one verb discards.
                    // Both validators read ProfileDefinitionTermsRules for the same anti-drift reason.
                    typeof(CreateProfilePropertyDefinitionRequest),
                    typeof(UpdateProfilePropertyDefinitionRequest),

                    // The page-update validator. Added when the page-edit endpoint was found to be judging
                    // nothing at all - an overlong value travelled to SQL Server and surfaced as a 500 naming
                    // no field, and a blank page name was stored as given. This inventory is the reason the
                    // gap was visible at all, and naming the request here is what keeps it closed: the
                    // endpoint binds it, so it must appear.
                    typeof(UpdateTabRequest),
                    typeof(PagedRequest),
                    typeof(PortalPagedRequest),
                    typeof(RolePagedRequest),
                    typeof(UserPagedRequest),
                    typeof(ModulePagedRequest),

                    // The role-membership paging request. Added because that listing BORROWED
                    // UserPagedRequest, so UserPagedRequestValidator resolved for it and applied the
                    // account collection's seven sortable names while RoleService.ListRoleUsersAsync
                    // enforces the role-membership set of ten - CreatedDate, LastLoginDate and IsApproved
                    // being the difference. Its ordering has an arm for each of the three, so the boundary
                    // was refusing an ordering the service could perform. This entry is the inverse of the
                    // defect that motivated the per-collection split: sharing one type made a listing
                    // accept a name it discarded, and borrowing another's made this one refuse a name it
                    // honoured. Both are cured by one type per collection.
                    typeof(RoleUserPagedRequest),

                    // Added when the write surface was audited for missing bounds. This request was bound
                    // by an endpoint while carrying NO validator at all, so every field on it reached the
                    // provider unbounded. Its presence here is what proves the assembly scan picks a new
                    // validator up with no registration edit, which is the property the scan exists to
                    // provide. The same audit reached the role update, the role assignment and the page
                    // update, each of which is already named above.
                    typeof(ModuleSettingsDto),
                },
                "a bound request without a validator reaches services and persistence without the "
                + "declarative boundary the API promises, and a validator over a shape no verb binds - a "
                + "RESPONSE projection, for instance - advertises coverage that can never execute");
    }
}
