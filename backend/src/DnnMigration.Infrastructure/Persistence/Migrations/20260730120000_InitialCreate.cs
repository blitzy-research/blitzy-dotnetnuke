using Microsoft.EntityFrameworkCore.Migrations;

namespace DnnMigration.Infrastructure.Persistence.Migrations;

// MIGRATION: Intentionally empty baseline migration — history only, never schema.
//
// This migration deliberately performs NO schema work. Both method bodies below are empty
// by design, so the only change `dotnet ef database update` makes to an existing DotNetNuke
// database is the row '20260730120000_InitialCreate' it inserts into __EFMigrationsHistory
// (creating that table first if the database does not already have it). Every application
// table, every column, every constraint and every row is left exactly as it was; the history
// row is what establishes the baseline that future migrations are diffed from.
//
// Why a generated schema is impossible rather than merely undesirable:
//   * The 88 *.SqlDataProvider upgrade scripts under
//     Website/Providers/DataProviders/SqlDataProvider/ — 83,614 lines carrying 849
//     table-altering and 89 table-dropping statements — create no aspnet_* table at all.
//   * Four Microsoft installer scripts (InstallCommon.sql, InstallMembership.sql,
//     InstallProfile.sql, InstallRoles.sql) require the ASP.NET application-services
//     objects to be installed externally by aspnet_regsql.exe; they RAISERROR and abort
//     otherwise (see InstallMembership.sql L36-L68).
//   * DotNetNuke PATCHES 19 distinct aspnet_* procedures rather than owning their tables:
//     04.00.00.SqlDataProvider L31 alters aspnet_Membership_UpdateUser and L119 alters
//     aspnet_Membership_UpdateUserInfo, adding the failed-attempt and lockout bookkeeping
//     that DotNetNuke needs. Those names occur in four spellings across the chain — bare,
//     owner-qualified, bracketed and qualifier-templated — so any audit of them must be
//     case-insensitive and match all four, or it will undercount.
//   * The chain is destructive: six Tmp_ tables are built, populated and then renamed over
//     the originals. 01.00.04.SqlDataProvider L1320-L1354 rebuilds Roles through
//     sp_rename, and 01.00.05.SqlDataProvider L2746-L2780 rebuilds it a second time. Only
//     the terminal cumulative shape is meaningful, and it is not what the 21-entity model
//     alone would emit: 01.00.00.SqlDataProvider L114-L122 declares Roles.ServiceFee as
//     decimal(5,2) while 03.01.01.SqlDataProvider L1173 later widens it to money, and
//     01.00.00.SqlDataProvider L97-L110 declares a plaintext 20-character password column
//     that the membership objects above ultimately supersede.
//
// Consequently a scaffolded create-migration would omit the externally provisioned
// aspnet_* objects and every out-of-scope legacy table, so it could not produce a valid
// DotNetNuke database even in principle. Building the schema straight from the model —
// the Entity Framework shortcut that bypasses migrations altogether — is forbidden
// project-wide for exactly that reason.
//
// The mapped shape of the 21 entities lives in Persistence/Configurations/*.cs (Fluent
// API, AAP Rule T4 "Schema is immutable"); the target model is recorded in
// 20260730120000_InitialCreate.Designer.cs and DnnDbContextModelSnapshot.cs.
// DO NOT add any operation to Up or Down.
internal partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
