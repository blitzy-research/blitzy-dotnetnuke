using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation.Results;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Application;

/// <summary>
/// Pins the module grant grid - the capability the first port of the module settings screen omitted
/// altogether, leaving a portal administrator unable to grant or withdraw module access to any role.
/// </summary>
/// <remarks>
/// <para>
/// Every fact here is anchored to the legacy control it replaces. The two computed cell rules come from
/// <c>Library/Controls/DataGrids/Permissions Grids/ModulePermissionsGrid.vb</c>: <c>GetEnabled</c> at
/// L237-L250 and <c>GetPermission</c> at L288-L310 for role rows, and their account overloads at L264-L277
/// and L325-L340. The row set comes from <c>PermissionsGrid.GetRoles</c> at L450-L475, whose default group
/// filter of <c>-2</c> took every portal role and appended the two built-in pseudo-roles. The replace-rather-
/// than-merge semantics come from <c>Website/admin/Modules/ModuleSettings.ascx.vb:L378-L379</c>, which
/// assigned the grid's whole collection onto the module and saved the inheritance switch in the same
/// operation, and from <c>ModulePermissionsGrid.UpdatePermission</c> at L507-L539, which removed an entry
/// the moment its box was cleared - "we only keep AllowAccess permissions".
/// </para>
/// <para>
/// THE SENTINEL IDENTITIES ARE DELIBERATE THROUGHOUT. The portal under test bears <c>-1</c> because
/// <c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c> and that value collides with the legacy
/// absent-integer sentinel; the administrators role bears <c>0</c> because <c>Roles.RoleID</c> is
/// <c>IDENTITY (0, 1)</c> and zero is the CLR default for its type. A grid that treated either as "unset"
/// would drop the row that matters most.
/// </para>
/// </remarks>
public class ModulePermissionGridTests
{
    /// <summary>The tenant under test, bearing the identity seed that collides with the legacy sentinel.</summary>
    private const int PortalId = -1;

    /// <summary>The module under test. Zero is a real module: the column is <c>IDENTITY (0, 1)</c>.</summary>
    private const int ModuleId = 0;

    /// <summary>The definition the module was created from.</summary>
    private const int ModuleDefinitionId = 2;

    /// <summary>The portal's administrator role, bearing the identity seed of its column.</summary>
    private const int AdministratorRoleId = 0;

    /// <summary>An ordinary role, whose cells are editable.</summary>
    private const int SubscribersRoleId = 2;

    /// <summary>The catalogue entry for the view key.</summary>
    private const int ViewPermissionId = 1;

    /// <summary>The catalogue entry for the edit key.</summary>
    private const int EditPermissionId = 2;

    /// <summary>An account that already holds a grant of its own.</summary>
    private const int NamedAccountId = 9;

    private const string ModuleNotFoundCode = "permission.module_not_found";

    private const string RoleNotFoundCode = "permission.role_not_found";

    private const string UserNotFoundCode = "permission.user_not_found";

    private const string KeyInvalidCode = "permission.key_invalid";

    private const string RequestInvalidCode = "permission.request_invalid";

    /// <summary>The grid carries one row per portal role plus the two built-in pseudo-roles.</summary>
    /// <remarks>
    /// Reproduces <c>PermissionsGrid.GetRoles</c>: the default group filter was <c>-2</c>, which is
    /// negative, so both pseudo-roles were appended. Their absence would leave an administrator unable to
    /// grant anonymous view access at all - the single most common module grant there is.
    /// </remarks>
    [Fact]
    public async Task GetModulePermissions_CarriesEveryPortalRoleAndBothPseudoRoles()
    {
        Harness harness = Harness.Ready();

        Result<ModulePermissionsDto> outcome = await harness.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        outcome.Value.Roles.Select(row => row.RoleId).Should().BeEquivalentTo(new[]
        {
            AdministratorRoleId,
            SubscribersRoleId,
            SpecialRoleIds.AllUsers,
            SpecialRoleIds.Unauthenticated,
        });

        outcome.Value.Roles
            .Where(row => row.IsPseudoRole)
            .Select(row => row.RoleName)
            .Should()
            .BeEquivalentTo(new[] { SpecialRoleNames.AllUsers, SpecialRoleNames.Unauthenticated });
    }

    /// <summary>The rows are ordered case-insensitively by name, as the legacy comparer ordered them.</summary>
    /// <remarks>
    /// <c>RoleComparer</c> used a <c>CaseInsensitiveComparer</c> over <c>RoleName</c>
    /// (<c>Library/Components/Security/Roles/RoleComparer.vb:L56</c>). Asserted with names whose ordinal and
    /// case-insensitive orders differ, so an ordinal sort cannot pass this.
    /// </remarks>
    [Fact]
    public async Task GetModulePermissions_OrdersRowsCaseInsensitivelyByName()
    {
        Harness harness = Harness.Ready();
        harness.PortalRoles =
        [
            new Role { RoleId = 7, PortalId = PortalId, RoleName = "apprentices" },
            new Role { RoleId = 8, PortalId = PortalId, RoleName = "Analysts" },
        ];

        Result<ModulePermissionsDto> outcome = await harness.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        outcome.Value.Roles.Select(row => row.RoleName).Should().ContainInOrder(
            "All Users",
            "Analysts",
            "apprentices",
            "Unauthenticated Users");
    }

    /// <summary>The administrator row is granted on every column and editable on none.</summary>
    /// <remarks>
    /// <c>ModulePermissionsGrid.GetPermission</c> returned <c>True</c> and <c>GetEnabled</c> returned
    /// <c>False</c> whenever <c>role.RoleID = AdministratorRoleId</c>, with no grant row consulted at all.
    /// The row is identified from <c>Portals.AdministratorRoleId</c> rather than from the role's NAME,
    /// because the name is data an installation may change.
    /// </remarks>
    [Fact]
    public async Task GetModulePermissions_AdministratorRowIsAlwaysGrantedAndNeverEditable()
    {
        Harness harness = Harness.Ready();

        Result<ModulePermissionsDto> outcome = await harness.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        ModulePermissionRoleDto administrators = outcome.Value.Roles
            .Single(row => row.RoleId == AdministratorRoleId);

        administrators.IsAdministrator.Should().BeTrue();
        administrators.Cells.Should().OnlyContain(cell => cell.AllowAccess && !cell.Editable);
    }

    /// <summary>A portal that designates no administrator role locks no row.</summary>
    [Fact]
    public async Task GetModulePermissions_PortalWithNoAdministratorRoleLocksNoRow()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow.AdministratorRoleId = null;

        Result<ModulePermissionsDto> outcome = await harness.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        outcome.Value.Roles.Should().OnlyContain(row => !row.IsAdministrator);
        outcome.Value.Roles.SelectMany(row => row.Cells).Should().OnlyContain(cell => cell.Editable);
    }

    /// <summary>An ordinary role's cell reports the stored grant, and false when no grant exists.</summary>
    [Fact]
    public async Task GetModulePermissions_OrdinaryRowReportsTheStoredGrant()
    {
        Harness harness = Harness.Ready();
        harness.Grants =
        [
            new ModulePermission
            {
                ModulePermissionId = 1,
                ModuleId = ModuleId,
                PermissionId = ViewPermissionId,
                RoleId = SubscribersRoleId,
                AllowAccess = true,
            },
        ];

        Result<ModulePermissionsDto> outcome = await harness.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        ModulePermissionRoleDto subscribers = outcome.Value.Roles
            .Single(row => row.RoleId == SubscribersRoleId);

        subscribers.Cells.Single(cell => cell.PermissionId == ViewPermissionId)
            .AllowAccess.Should().BeTrue();
        subscribers.Cells.Single(cell => cell.PermissionId == EditPermissionId)
            .AllowAccess.Should().BeFalse();
        subscribers.Cells.Should().OnlyContain(cell => cell.Editable);
    }

    /// <summary>A recorded DENY reads as not granted, exactly as the legacy two-state checkbox showed it.</summary>
    /// <remarks>
    /// The legacy grid read <c>objModulePermission.AllowAccess</c> straight into a checkbox, so a deny row
    /// and an absent row were indistinguishable on screen. That is reproduced rather than improved on,
    /// because the alternative is a third visual state the save path cannot express.
    /// </remarks>
    [Fact]
    public async Task GetModulePermissions_RecordedDenyReadsAsNotGranted()
    {
        Harness harness = Harness.Ready();
        harness.Grants =
        [
            new ModulePermission
            {
                ModulePermissionId = 1,
                ModuleId = ModuleId,
                PermissionId = ViewPermissionId,
                RoleId = SubscribersRoleId,
                AllowAccess = false,
            },
        ];

        Result<ModulePermissionsDto> outcome = await harness.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        outcome.Value.Roles
            .Single(row => row.RoleId == SubscribersRoleId)
            .Cells.Single(cell => cell.PermissionId == ViewPermissionId)
            .AllowAccess.Should().BeFalse();
    }

    /// <summary>A role grant is never read into an account row, nor an account grant into a role row.</summary>
    /// <remarks>
    /// The legacy lookups were distinct members - <c>ModuleHasRolePermission</c> and
    /// <c>ModuleHasUserPermission</c> - and the stored table carries both columns. A row that matched on the
    /// permission alone would show every principal holding every other principal's grants.
    /// </remarks>
    [Fact]
    public async Task GetModulePermissions_DoesNotCrossPrincipalKinds()
    {
        Harness harness = Harness.Ready();
        harness.Grants =
        [
            new ModulePermission
            {
                ModulePermissionId = 1,
                ModuleId = ModuleId,
                PermissionId = EditPermissionId,
                UserId = NamedAccountId,
                AllowAccess = true,
            },
        ];

        Result<ModulePermissionsDto> outcome = await harness.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        outcome.Value.Roles
            .Single(row => row.RoleId == SubscribersRoleId)
            .Cells.Should().OnlyContain(cell => !cell.AllowAccess);

        outcome.Value.Users.Should().HaveCount(1);
        outcome.Value.Users[0].UserId.Should().Be(NamedAccountId);
        outcome.Value.Users[0].Cells.Single(cell => cell.PermissionId == EditPermissionId)
            .AllowAccess.Should().BeTrue();
    }

    /// <summary>Inheritance collapses the view column for every row, and leaves the others alone.</summary>
    /// <remarks>
    /// <c>ModulePermissionsGrid</c> returned <c>False</c> from both overrides for the view column whenever
    /// <c>InheritViewPermissionsFromTab</c> was set, and did so BEFORE the administrator special case - which
    /// is why the administrator's view cell is cleared here while its edit cell stays granted.
    /// </remarks>
    [Fact]
    public async Task GetModulePermissions_InheritanceCollapsesOnlyTheViewColumn()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Grants =
        [
            new ModulePermission
            {
                ModulePermissionId = 1,
                ModuleId = ModuleId,
                PermissionId = ViewPermissionId,
                RoleId = SubscribersRoleId,
                AllowAccess = true,
            },
        ];

        Result<ModulePermissionsDto> outcome = await harness.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        outcome.Value.InheritViewPermissions.Should().BeTrue();
        outcome.Value.InheritedPermissionKey.Should().Be("VIEW");

        outcome.Value.Roles
            .SelectMany(row => row.Cells)
            .Where(cell => cell.PermissionId == ViewPermissionId)
            .Should()
            .OnlyContain(cell => !cell.AllowAccess && !cell.Editable);

        outcome.Value.Roles
            .Single(row => row.RoleId == AdministratorRoleId)
            .Cells.Single(cell => cell.PermissionId == EditPermissionId)
            .AllowAccess.Should().BeTrue();
    }

    /// <summary>An unset inheritance column is not inheritance.</summary>
    /// <remarks>
    /// <c>Modules.InheritViewPermissions</c> is <c>bit NULL</c>, and the legacy reader mapped <c>DBNull</c>
    /// through <c>Null.SetNull</c> to <c>Null.NullBoolean</c>, which is <c>False</c>. Treating the unset
    /// column as inheritance would lock the view column of every module an upgrade left null.
    /// </remarks>
    [Fact]
    public async Task GetModulePermissions_NullInheritanceColumnIsNotInheritance()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = null;

        Result<ModulePermissionsDto> outcome = await harness.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        outcome.Value.InheritViewPermissions.Should().BeFalse();
        outcome.Value.Roles
            .Single(row => row.RoleId == SubscribersRoleId)
            .Cells.Should().OnlyContain(cell => cell.Editable);
    }

    /// <summary>A module in another tenant is refused, and refused identically to one that does not exist.</summary>
    [Fact]
    public async Task GetModulePermissions_RefusesAModuleOfAnotherTenantIdentically()
    {
        Harness foreign = Harness.Ready();
        foreign.Module.PortalId = PortalId + 5;

        Result<ModulePermissionsDto> foreignOutcome = await foreign.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        Harness absent = Harness.Ready();
        absent.ModuleRow = null;

        Result<ModulePermissionsDto> absentOutcome = await absent.Service
            .GetModulePermissionsAsync(PortalId, ModuleId, CancellationToken.None);

        foreignOutcome.Reason!.Code.Should().Be(ModuleNotFoundCode);
        absentOutcome.Reason!.Code.Should().Be(ModuleNotFoundCode);
        foreignOutcome.Reason!.Message.Should().Be(absentOutcome.Reason!.Message);
    }

    /// <summary>A replacement stores exactly what was submitted and nothing that was not.</summary>
    [Fact]
    public async Task ReplaceModulePermissions_StoresExactlyTheSubmittedSet()
    {
        Harness harness = Harness.Ready();
        harness.Grants =
        [
            new ModulePermission
            {
                ModulePermissionId = 1,
                ModuleId = ModuleId,
                PermissionId = EditPermissionId,
                RoleId = SubscribersRoleId,
                AllowAccess = true,
            },
        ];

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest
            {
                InheritViewPermissions = false,
                Grants =
                [
                    new ModulePermissionGrantRequest
                    {
                        PermissionId = ViewPermissionId,
                        RoleId = SpecialRoleIds.AllUsers,
                        AllowAccess = true,
                    },
                ],
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        // The withdrawal is the DELETE, which is why it is asserted rather than inferred from the insert:
        // a merge would have left the edit grant in place and no assertion on the inserts would notice.
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByModuleIdAsync(
                ModuleId,
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.Added.Should().HaveCount(1);
        harness.Added[0].PermissionId.Should().Be(ViewPermissionId);
        harness.Added[0].RoleId.Should().Be(SpecialRoleIds.AllUsers);
        harness.Added[0].UserId.Should().BeNull();
        harness.Added[0].AllowAccess.Should().BeTrue();
    }

    /// <summary>An empty submission withdraws every grant rather than being treated as an omission.</summary>
    [Fact]
    public async Task ReplaceModulePermissions_EmptySetWithdrawsEveryGrant()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest { Grants = [] },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByModuleIdAsync(
                ModuleId,
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Added.Should().BeEmpty();
    }

    /// <summary>The inheritance switch is written in the same commit as the grants.</summary>
    /// <remarks>
    /// <c>ModuleSettings.ascx.vb:L378-L379</c> assigned both onto the module before one save. A module whose
    /// stored rights contradict its stored inheritance is a state no screen can correct, so the commit
    /// boundary is asserted rather than the two writes independently.
    /// </remarks>
    [Fact]
    public async Task ReplaceModulePermissions_WritesTheInheritanceSwitchInTheSameCommit()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest { InheritViewPermissions = true, Grants = [] },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Module.InheritViewPermissions.Should().BeTrue();
        harness.Modules.Verify(
            modules => modules.UpdateAsync(harness.Module, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Turning inheritance on discards submitted view grants, as the legacy save did.</summary>
    /// <remarks>
    /// The legacy grid rendered every view cell cleared and disabled while inheritance was on, and its save
    /// path then removed the row. Dropping the grant here reproduces that outcome exactly; refusing the
    /// request instead would make the screen unusable, because the tick and the switch are saved together.
    /// </remarks>
    [Fact]
    public async Task ReplaceModulePermissions_DiscardsViewGrantsWhenInheritanceIsOn()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest
            {
                InheritViewPermissions = true,
                Grants =
                [
                    new ModulePermissionGrantRequest
                    {
                        PermissionId = ViewPermissionId,
                        RoleId = SubscribersRoleId,
                    },
                    new ModulePermissionGrantRequest
                    {
                        PermissionId = EditPermissionId,
                        RoleId = SubscribersRoleId,
                    },
                ],
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Added.Should().HaveCount(1);
        harness.Added[0].PermissionId.Should().Be(EditPermissionId);
    }

    /// <summary>A cell naming a permission the module does not declare is refused before any statement.</summary>
    [Fact]
    public async Task ReplaceModulePermissions_RefusesAnUndeclaredPermissionWithoutWriting()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest
            {
                Grants =
                [
                    new ModulePermissionGrantRequest
                    {
                        PermissionId = 4242,
                        RoleId = SubscribersRoleId,
                    },
                ],
            },
            CancellationToken.None);

        outcome.Reason!.Code.Should().Be(KeyInvalidCode);
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Added.Should().BeEmpty();
    }

    /// <summary>A cell naming a role this portal does not hold is refused before any statement.</summary>
    [Fact]
    public async Task ReplaceModulePermissions_RefusesAForeignRoleWithoutWriting()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest
            {
                Grants =
                [
                    new ModulePermissionGrantRequest
                    {
                        PermissionId = ViewPermissionId,
                        RoleId = 999,
                    },
                ],
            },
            CancellationToken.None);

        outcome.Reason!.Code.Should().Be(RoleNotFoundCode);
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>The host pseudo-role is refused rather than stored as a row that can never take effect.</summary>
    /// <remarks>
    /// The evaluator's principal matcher returns false for the host pseudo-role, so a grant against it would
    /// be a stored row with no reachable consequence, and an operator who chose it would be told nothing.
    /// </remarks>
    [Fact]
    public async Task ReplaceModulePermissions_RefusesTheHostPseudoRole()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest
            {
                Grants =
                [
                    new ModulePermissionGrantRequest
                    {
                        PermissionId = ViewPermissionId,
                        RoleId = SpecialRoleIds.SuperUser,
                    },
                ],
            },
            CancellationToken.None);

        outcome.Reason!.Code.Should().Be(RoleNotFoundCode);
        harness.Added.Should().BeEmpty();
    }

    /// <summary>A cell naming an account outside the portal is refused before any statement.</summary>
    [Fact]
    public async Task ReplaceModulePermissions_RefusesAForeignAccountWithoutWriting()
    {
        Harness harness = Harness.Ready();
        harness.Account = null;

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest
            {
                Grants =
                [
                    new ModulePermissionGrantRequest
                    {
                        PermissionId = ViewPermissionId,
                        UserId = NamedAccountId,
                    },
                ],
            },
            CancellationToken.None);

        outcome.Reason!.Code.Should().Be(UserNotFoundCode);
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A cell naming both a role and an account, or neither, is refused.</summary>
    [Theory]
    [InlineData(SubscribersRoleId, NamedAccountId)]
    [InlineData(null, null)]
    public async Task ReplaceModulePermissions_RefusesAnAmbiguousPrincipal(int? roleId, int? userId)
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest
            {
                Grants =
                [
                    new ModulePermissionGrantRequest
                    {
                        PermissionId = ViewPermissionId,
                        RoleId = roleId,
                        UserId = userId,
                    },
                ],
            },
            CancellationToken.None);

        outcome.Reason!.Code.Should().Be(RequestInvalidCode);
        harness.Added.Should().BeEmpty();
    }

    /// <summary>The same cell submitted twice is a contradiction rather than a duplicate.</summary>
    [Fact]
    public async Task ReplaceModulePermissions_RefusesARepeatedCell()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest
            {
                Grants =
                [
                    new ModulePermissionGrantRequest
                    {
                        PermissionId = ViewPermissionId,
                        RoleId = SubscribersRoleId,
                        AllowAccess = true,
                    },
                    new ModulePermissionGrantRequest
                    {
                        PermissionId = ViewPermissionId,
                        RoleId = SubscribersRoleId,
                        AllowAccess = false,
                    },
                ],
            },
            CancellationToken.None);

        outcome.Reason!.Code.Should().Be(RequestInvalidCode);
        harness.Added.Should().BeEmpty();
    }

    /// <summary>A replacement on a module of another tenant is refused.</summary>
    [Fact]
    public async Task ReplaceModulePermissions_RefusesAModuleOfAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.Module.PortalId = PortalId + 5;

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest { Grants = [] },
            CancellationToken.None);

        outcome.Reason!.Code.Should().Be(ModuleNotFoundCode);
    }

    /// <summary>A successful replacement evicts the evaluator's cached answers, after the commit.</summary>
    [Fact]
    public async Task ReplaceModulePermissions_EvictsCachedAnswers()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ReplaceModulePermissionsAsync(
            PortalId,
            ModuleId,
            new ReplaceModulePermissionsRequest { Grants = [] },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Cache.Verify(cache => cache.RemoveByPrefix(It.IsAny<string>()), Times.AtLeastOnce);
    }

    /// <summary>A missing body is refused rather than faulting.</summary>
    [Fact]
    public async Task ReplaceModulePermissions_RefusesAMissingBody()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service
            .ReplaceModulePermissionsAsync(PortalId, ModuleId, null!, CancellationToken.None);

        outcome.Reason!.Code.Should().Be(RequestInvalidCode);
    }

    /// <summary>The validator refuses a null grant list and accepts an empty one.</summary>
    /// <remarks>
    /// The distinction matters: an empty array is an instruction to withdraw everything, whereas a null is a
    /// caller that forgot the member, and answering both the same way would make a serialisation bug look
    /// like a deliberate withdrawal.
    /// </remarks>
    [Fact]
    public void Validator_SeparatesAnAbsentGrantListFromAnEmptyOne()
    {
        ReplaceModulePermissionsRequestValidator validator = new();

        ValidationResult absent = validator.Validate(
            new ReplaceModulePermissionsRequest { Grants = null! });
        ValidationResult empty = validator.Validate(
            new ReplaceModulePermissionsRequest { Grants = [] });

        absent.IsValid.Should().BeFalse();
        empty.IsValid.Should().BeTrue();
    }

    /// <summary>The validator refuses a non-positive permission identifier without reading the store.</summary>
    /// <remarks>
    /// <c>Permission.PermissionID</c> is <c>IDENTITY (1, 1)</c>, so zero and every negative value name no
    /// definition and can be refused on shape alone.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validator_RefusesANonPositivePermissionIdentifier(int permissionId)
    {
        ReplaceModulePermissionsRequestValidator validator = new();

        ValidationResult result = validator.Validate(new ReplaceModulePermissionsRequest
        {
            Grants =
            [
                new ModulePermissionGrantRequest
                {
                    PermissionId = permissionId,
                    RoleId = SubscribersRoleId,
                },
            ],
        });

        result.IsValid.Should().BeFalse();
    }

    /// <summary>The validator refuses a cell that names both principals or neither.</summary>
    [Theory]
    [InlineData(SubscribersRoleId, NamedAccountId)]
    [InlineData(null, null)]
    public void Validator_RefusesAnAmbiguousPrincipal(int? roleId, int? userId)
    {
        ReplaceModulePermissionsRequestValidator validator = new();

        ValidationResult result = validator.Validate(new ReplaceModulePermissionsRequest
        {
            Grants =
            [
                new ModulePermissionGrantRequest
                {
                    PermissionId = ViewPermissionId,
                    RoleId = roleId,
                    UserId = userId,
                },
            ],
        });

        result.IsValid.Should().BeFalse();
    }

    /// <summary>
    /// The collaborators of <see cref="PermissionService"/>, wired so that every question the grant grid asks
    /// has an answer and every fact a test needs to change is a settable member.
    /// </summary>
    private sealed class Harness
    {
        private Harness()
        {
            Module = new Module
            {
                ModuleId = ModuleId,
                PortalId = PortalId,
                ModuleDefinitionId = ModuleDefinitionId,
                InheritViewPermissions = false,
            };

            ModuleRow = Module;

            PortalRow = new Portal
            {
                PortalId = PortalId,
                PortalName = "Measured Portal",
                AdministratorRoleId = AdministratorRoleId,
            };

            Account = new User
            {
                UserId = NamedAccountId,
                Username = "measured_member",
                DisplayName = "Measured Member",
            };

            Catalogue =
            [
                new Permission
                {
                    PermissionId = ViewPermissionId,
                    PermissionCode = "SYSTEM_MODULE_DEFINITION",
                    ModuleDefinitionId = ModuleDefinitionId,
                    PermissionKey = nameof(PermissionKey.VIEW),
                    PermissionName = "View Module",
                },
                new Permission
                {
                    PermissionId = EditPermissionId,
                    PermissionCode = "SYSTEM_MODULE_DEFINITION",
                    ModuleDefinitionId = ModuleDefinitionId,
                    PermissionKey = nameof(PermissionKey.EDIT),
                    PermissionName = "Edit Module",
                },
            ];

            PortalRoles =
            [
                new Role { RoleId = AdministratorRoleId, PortalId = PortalId, RoleName = "Administrators" },
                new Role { RoleId = SubscribersRoleId, PortalId = PortalId, RoleName = "Subscribers" },
            ];

            Grants = [];
            Added = [];

            Permissions = new Mock<IPermissionRepository>(MockBehavior.Loose);
            Evaluator = new Mock<IPermissionEvaluator>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Roles = new Mock<IRoleRepository>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            Transaction = new Mock<ITransactionScope>(MockBehavior.Loose);

            Service = new PermissionService(
                Permissions.Object,
                Evaluator.Object,
                Portals.Object,
                Modules.Object,
                Tabs.Object,
                Users.Object,
                Roles.Object,
                UnitOfWork.Object,
                Cache.Object,
                Clock.Object,
                new CachingOptions());
        }

        /// <summary>The module row, mutable so a foreign tenant and a null inheritance column are testable.</summary>
        public Module Module { get; }

        /// <summary>
        /// What the module store answers with, settable to <see langword="null"/> so that "does not exist"
        /// and "belongs to another tenant" can be compared against each other.
        /// </summary>
        public Module? ModuleRow { get; set; }

        public Portal PortalRow { get; }

        /// <summary>The named account, settable to <see langword="null"/> to model one outside the portal.</summary>
        public User? Account { get; set; }

        public IReadOnlyList<Permission> Catalogue { get; set; }

        public IReadOnlyList<Role> PortalRoles { get; set; }

        public IReadOnlyList<ModulePermission> Grants { get; set; }

        /// <summary>Every grant the service asked the store to insert, in the order it asked.</summary>
        public List<ModulePermission> Added { get; }

        public Mock<IPermissionRepository> Permissions { get; }

        public Mock<IPermissionEvaluator> Evaluator { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IModuleRepository> Modules { get; }

        public Mock<ITabRepository> Tabs { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IRoleRepository> Roles { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<IClock> Clock { get; }

        public Mock<ITransactionScope> Transaction { get; }

        public PermissionService Service { get; }

        /// <summary>Builds a harness whose collaborators can answer every question the grid asks.</summary>
        /// <returns>The harness.</returns>
        public static Harness Ready()
        {
            Harness harness = new();

            harness.Clock.SetupGet(clock => clock.UtcNow).Returns(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));

            harness.Modules
                .Setup(modules => modules.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ModuleRow);

            harness.Portals
                .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            harness.Portals
                .Setup(portals => portals.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalRow);

            harness.Permissions
                .Setup(permissions => permissions.GetByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Catalogue);

            harness.Permissions
                .Setup(permissions => permissions.GetModulePermissionsByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Grants);

            harness.Permissions
                .Setup(permissions => permissions.AddModulePermissionAsync(
                    It.IsAny<ModulePermission>(),
                    It.IsAny<CancellationToken>()))
                .Callback<ModulePermission, CancellationToken>((grant, _) => harness.Added.Add(grant))
                .Returns(Task.CompletedTask);

            harness.Roles
                .Setup(roles => roles.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalRoles);

            harness.Users
                .Setup(users => users.GetAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Account);

            // The read-through cache must actually invoke the factory, or every catalogue read would answer
            // with a default and the grid would have no columns at all.
            harness.Cache
                .Setup(cache => cache.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<IReadOnlyList<PermissionDto>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, Func<CancellationToken, Task<IReadOnlyList<PermissionDto>>>, TimeSpan, CancellationToken>(
                    (_, factory, _, token) => factory(token));

            harness.UnitOfWork
                .Setup(unitOfWork => unitOfWork.BeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(harness.Transaction.Object);

            harness.UnitOfWork
                .Setup(unitOfWork => unitOfWork.JoinOrBeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(harness.Transaction.Object);

            return harness;
        }
    }
}
