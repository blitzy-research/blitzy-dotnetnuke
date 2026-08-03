using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Covers the permission catalogue, the two grant tables that reference it, and the outcome primitives
/// every permission answer is carried in.
/// </summary>
/// <remarks>
/// <para>
/// The single most important assertion in this file is that a freshly constructed grant is a
/// <em>denial</em>: <c>AllowAccess</c> defaults to false. A grant row that was created without an
/// explicit decision therefore withholds access rather than conferring it, which is the only safe
/// default for a security row and is the opposite of what a reader who skimmed the type name would
/// assume.
/// </para>
/// <para>
/// <see cref="Result"/>, <see cref="Result{T}"/> and <see cref="ResultReason"/> are covered here rather
/// than in a suite of their own because a permission check is the canonical producer of
/// <c>Result&lt;bool&gt;</c>, and because they are <c>Domain.Common</c> primitives rather than
/// Application concerns. The behaviour that most needs pinning is the advisory channel: a
/// <em>successful</em> result may still carry a reason, and on success <c>Error</c> stays null while
/// <c>Reason</c> does not. Sign-in relies on exactly that to report a shipped credential without
/// failing the request.
/// </para>
/// </remarks>
public class PermissionTests
{
    /// <summary>
    /// The catalogue entry reports its primary key as its identity.
    /// </summary>
    [Fact]
    public void CatalogueIdentity_IsThePrimaryKey()
    {
        Permission entry = NewPermission(4, PermissionKey.VIEW);
        Permission sameRow = NewPermission(4, PermissionKey.EDIT);
        Permission otherRow = NewPermission(5, PermissionKey.VIEW);

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        entry.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();
        otherRow.MarkIdentityPersisted();

        entry.Identity.Should().Be(4);
        entry.Should().Be(sameRow);
        entry.Should().NotBe(otherRow);
    }

    /// <summary>
    /// A catalogue entry is anchored to a permission code and a module definition, not to a tenant.
    /// </summary>
    [Fact]
    public void CatalogueEntry_IsAnchoredToACodeAndADefinition()
    {
        Permission entry = new()
        {
            PermissionId = 1,
            PermissionCode = "SYSTEM_MODULE_DEFINITION",
            ModuleDefinitionId = 7,
            PermissionKey = PermissionKey.VIEW,
            PermissionName = "View Module",
        };

        entry.PermissionCode.Should().Be("SYSTEM_MODULE_DEFINITION");
        entry.ModuleDefinitionId.Should().Be(7);
        entry.PermissionKey.Should().Be(
            PermissionKey.VIEW,
            "the key is the closed enumeration rather than free text, so a misspelling is now a "
            + "compile error instead of a row that silently matches nothing");
        entry.PermissionName.Should().Be("View Module");
        entry.ModulePermissions.Should().NotBeNull().And.BeEmpty();
        entry.TabPermissions.Should().NotBeNull().And.BeEmpty();

        Permission bare = new() { PermissionId = 2, ModuleDefinitionId = 7 };

        bare.PermissionCode.Should().BeEmpty();
        bare.PermissionName.Should().BeEmpty();
        bare.PermissionKey.Should().Be(
            PermissionKey.VIEW,
            "VIEW is the zero member, so an entry constructed without an explicit key reports it; the "
            + "column is NOT NULL and the enumeration declares no absent member, so a caller that "
            + "means something else must say so");
    }

    /// <summary>
    /// A grant withholds access until something explicitly confers it.
    /// </summary>
    [Fact]
    public void Grants_WithholdAccessByDefault()
    {
        ModulePermission moduleGrant = new() { ModulePermissionId = 1, ModuleId = 0, PermissionId = 1 };
        TabPermission tabGrant = new() { TabPermissionId = 1, TabId = 0, PermissionId = 1 };

        moduleGrant.AllowAccess.Should().BeFalse(
            "a grant row created without an explicit decision must not confer access");
        tabGrant.AllowAccess.Should().BeFalse();
    }

    /// <summary>
    /// A grant is scoped to a role or to an account, and both scopes may be absent.
    /// </summary>
    [Fact]
    public void Grants_AreScopedToARoleOrToAnAccount()
    {
        ModulePermission roleScoped = new()
        {
            ModulePermissionId = 1,
            ModuleId = 0,
            PermissionId = 1,
            RoleId = 0,
            AllowAccess = true,
        };

        ModulePermission accountScoped = new()
        {
            ModulePermissionId = 2,
            ModuleId = 0,
            PermissionId = 1,
            UserId = 3,
            AllowAccess = false,
        };

        ModulePermission unscoped = new() { ModulePermissionId = 3, ModuleId = 0, PermissionId = 1 };

        roleScoped.RoleId.Should().Be(0, "zero is the administrator role of a fresh installation");
        roleScoped.UserId.Should().BeNull();
        accountScoped.UserId.Should().Be(3);
        accountScoped.RoleId.Should().BeNull();
        unscoped.RoleId.Should().BeNull();
        unscoped.UserId.Should().BeNull();
    }

    /// <summary>
    /// A module grant and a page grant carrying the same numeric key are different entities.
    /// </summary>
    [Fact]
    public void Grants_OfDifferentScopesAreDistinctEntities()
    {
        Entity<int> moduleGrant = new ModulePermission { ModulePermissionId = 1, ModuleId = 0, PermissionId = 1 };
        Entity<int> tabGrant = new TabPermission { TabPermissionId = 1, TabId = 0, PermissionId = 1 };

        moduleGrant.Equals(tabGrant).Should().BeFalse(
            "both grant tables are keyed by their own identity column, so only the aggregate type "
            + "separates a module grant from a page grant that happens to share a number");
    }

    /// <summary>
    /// The permission-key enumeration holds exactly the four legacy keys, in their legacy order.
    /// </summary>
    [Fact]
    public void PermissionKeys_AreTheFourLegacyKeys()
    {
        Enum.GetValues<PermissionKey>().Should().HaveCount(4);
        ((int)PermissionKey.VIEW).Should().Be(0);
        ((int)PermissionKey.EDIT).Should().Be(1);
        ((int)PermissionKey.READ).Should().Be(2);
        ((int)PermissionKey.WRITE).Should().Be(3);

        PermissionKey.VIEW.ToString().Should().Be(
            "VIEW",
            "the member names are the stored keys verbatim, in upper case, so a permission check can be "
            + "written against the enumeration and compared against the column without a translation table");
    }

    /// <summary>
    /// A value outside the enumeration is recognised as undefined rather than silently accepted.
    /// </summary>
    [Fact]
    public void PermissionKeys_RejectAnUndefinedValue()
    {
        Enum.IsDefined(default(PermissionKey)).Should().BeTrue("VIEW is the zero member");
        Enum.IsDefined((PermissionKey)4).Should().BeFalse();
        Enum.IsDefined((PermissionKey)(-1)).Should().BeFalse(
            "the permission service tests for a defined member before it reaches the repository, so an "
            + "out-of-range cast is refused rather than resolved to nothing");
    }

    /// <summary>
    /// A success carries no failure detail.
    /// </summary>
    [Fact]
    public void Result_Success_CarriesNoFailureDetail()
    {
        Result outcome = Result.Success();

        outcome.IsSuccess.Should().BeTrue();
        outcome.IsFailure.Should().BeFalse();
        outcome.Reason.Should().BeNull();
        outcome.Error.Should().BeNull();
        outcome.ToString().Should().Be("Success");
    }

    /// <summary>
    /// A success may carry an advisory reason without becoming a failure.
    /// </summary>
    [Fact]
    public void Result_Success_MayCarryAnAdvisoryReason()
    {
        ResultReason advisory = new("auth.insecure_admin_password", "Change the shipped credential.");
        Result outcome = Result.Success(advisory);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().Be(advisory);
        outcome.Error.Should().BeNull(
            "Error is the failure channel, so it stays empty on a success even when a reason is attached - "
            + "this is what lets sign-in report a shipped credential while still signing the caller in");
        outcome.ToString().Should().Be("Success (auth.insecure_admin_password: Change the shipped credential.)");
    }

    /// <summary>
    /// A failure reports the same reason through both channels.
    /// </summary>
    [Fact]
    public void Result_Failure_ReportsTheReasonThroughBothChannels()
    {
        Result outcome = Result.Failure("permission.key_invalid", "The permission key is not recognised.");

        outcome.IsSuccess.Should().BeFalse();
        outcome.IsFailure.Should().BeTrue();
        outcome.Reason.Should().NotBeNull();
        outcome.Reason!.Code.Should().Be("permission.key_invalid");
        outcome.Reason!.Message.Should().Be("The permission key is not recognised.");
        outcome.Error.Should().Be(outcome.Reason);
        outcome.ToString().Should().StartWith("Failure (permission.key_invalid:");
    }

    /// <summary>
    /// A successful permission answer exposes the decision it reached.
    /// </summary>
    /// <param name="granted">The decision under test.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResultOfT_Success_ExposesItsValue(bool granted)
    {
        Result<bool> outcome = Result<bool>.Success(granted);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().Be(granted);
        outcome.Reason.Should().BeNull();
    }

    /// <summary>
    /// A refusal and a failure are different answers: false is a decision, a failure is not.
    /// </summary>
    [Fact]
    public void ResultOfT_DistinguishesARefusalFromAFailure()
    {
        Result<bool> refused = Result<bool>.Success(false);
        Result<bool> failed = Result<bool>.Failure("permission.module_not_found", "No such module.");

        refused.IsSuccess.Should().BeTrue("the check ran and answered no");
        refused.Value.Should().BeFalse();
        failed.IsFailure.Should().BeTrue("the check could not run at all");

        Func<bool> readValue = () => failed.Value;

        readValue.Should().Throw<InvalidOperationException>()
            .WithMessage("*failed result*");
    }

    /// <summary>
    /// A typed success may also carry an advisory reason.
    /// </summary>
    [Fact]
    public void ResultOfT_Success_MayCarryAnAdvisoryReason()
    {
        ResultReason advisory = new("role_assignment.expired_not_removed", "The assignment had already lapsed.");
        Result<int> outcome = Result<int>.Success(7, advisory);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().Be(7);
        outcome.Reason.Should().Be(advisory);
        outcome.Error.Should().BeNull();
    }

    /// <summary>
    /// A typed failure may be built from a reason as well as from its parts.
    /// </summary>
    [Fact]
    public void ResultOfT_Failure_AcceptsAReasonOrItsParts()
    {
        ResultReason reason = new("permission.portal_not_found", "No such portal.");

        Result<IReadOnlyList<string>> fromReason = Result<IReadOnlyList<string>>.Failure(reason);
        Result<IReadOnlyList<string>> fromParts =
            Result<IReadOnlyList<string>>.Failure("permission.portal_not_found", "No such portal.");

        fromReason.IsFailure.Should().BeTrue();
        fromParts.IsFailure.Should().BeTrue();
        fromReason.Reason.Should().Be(fromParts.Reason);
    }

    /// <summary>
    /// A reason must actually say something: neither part may be blank.
    /// </summary>
    /// <param name="code">The code under test.</param>
    /// <param name="message">The message under test.</param>
    [Theory]
    [InlineData(null, "a message")]
    [InlineData("", "a message")]
    [InlineData("   ", "a message")]
    [InlineData("a.code", null)]
    [InlineData("a.code", "")]
    [InlineData("a.code", "   ")]
    public void ResultReason_RejectsABlankPart(string? code, string? message)
    {
        Action construct = () => _ = new ResultReason(code!, message!);

        construct.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Two reasons carrying the same parts are the same reason.
    /// </summary>
    [Fact]
    public void ResultReason_IsComparedByItsParts()
    {
        ResultReason left = new("permission.key_invalid", "The permission key is not recognised.");
        ResultReason right = new("permission.key_invalid", "The permission key is not recognised.");
        ResultReason other = new("permission.key_invalid", "Something else entirely.");

        left.Should().Be(right);
        left.GetHashCode().Should().Be(right.GetHashCode());
        left.Should().NotBe(other);
        left.ToString().Should().Be("permission.key_invalid: The permission key is not recognised.");
    }

    /// <summary>
    /// The domain exception carries its message and any inner cause.
    /// </summary>
    [Fact]
    public void DomainException_CarriesItsMessageAndCause()
    {
        DomainException bare = new();
        DomainException withMessage = new("A PortalGuid cannot be the all-zero GUID.");
        InvalidOperationException cause = new("the underlying problem");
        DomainException withCause = new("wrapped", cause);

        bare.Message.Should().NotBeNullOrWhiteSpace();
        withMessage.Message.Should().Be("A PortalGuid cannot be the all-zero GUID.");
        withCause.Message.Should().Be("wrapped");
        withCause.InnerException.Should().BeSameAs(cause);
        withMessage.Should().BeAssignableTo<Exception>();
    }

    /// <summary>
    /// The auditable base adds the two schema audit columns to the entity identity contract.
    /// </summary>
    [Fact]
    public void AuditableEntity_AddsTheAuditColumnsToTheIdentityContract()
    {
        AuditedThing thing = new(5);
        AuditedThing sameRow = new(5);

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        thing.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();

        thing.Should().BeAssignableTo<Entity<int>>();
        thing.Identity.Should().Be(5);
        thing.CreatedDate.Should().BeNull("an unstamped row reports no creation instant, rather than the epoch");
        thing.LastUpdatedDate.Should().BeNull();

        DateTime stamp = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        thing.CreatedDate = stamp;
        thing.LastUpdatedDate = stamp;

        thing.CreatedDate.Should().Be(stamp);
        thing.LastUpdatedDate.Should().Be(stamp);
        thing.Should().Be(sameRow, "the audit columns are not part of the identity contract");
    }

    /// <summary>
    /// Builds a catalogue entry carrying the supplied identifier and key.
    /// </summary>
    /// <param name="permissionId">The identifier to carry.</param>
    /// <param name="permissionKey">The permission key.</param>
    /// <returns>The catalogue entry.</returns>
    private static Permission NewPermission(int permissionId, PermissionKey permissionKey) => new()
    {
        PermissionId = permissionId,
        PermissionCode = "SYSTEM_MODULE_DEFINITION",
        ModuleDefinitionId = 1,
        PermissionKey = permissionKey,
        PermissionName = permissionKey + " Module",
    };

    /// <summary>
    /// A minimal concrete auditable entity, present only so the shared base can be exercised.
    /// </summary>
    private sealed class AuditedThing : AuditableEntity<int>
    {
        private readonly int _identity;

        /// <summary>
        /// Initialises a new instance of the <see cref="AuditedThing"/> class.
        /// </summary>
        /// <param name="identity">The identity the instance reports.</param>
        public AuditedThing(int identity) => _identity = identity;

        /// <inheritdoc />
        public override int Identity => _identity;
    }
}
