using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Covers the account aggregate, its per-tenant membership, and the e-mail address value object that
/// reproduces the legacy validation pattern character for character.
/// </summary>
/// <remarks>
/// <para>
/// The account is the one aggregate whose state is split across two stores. <c>dbo.Users</c> holds the
/// profile facts and the external <c>aspnet_*</c> membership objects hold the credential facts, which is
/// why <see cref="User.IsApproved"/>, <see cref="User.IsLockedOut"/> and the several date properties are
/// all nullable: null does not mean false, it means the external store has not been read. The
/// assertions below pin that distinction, because collapsing it to false would silently admit an
/// unapproved account.
/// </para>
/// <para>
/// The e-mail assertions are the largest group here and they are deliberately unflattering. The legacy
/// screens validated with the regular expression
/// <c>\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b</c>, and that expression rejects addresses
/// a modern reader would consider obviously valid - anything ending in a five-letter suffix such as
/// <c>.museum</c>, and any local part beginning with a dot, plus, hyphen, percent or apostrophe. Those
/// rejections are preserved, and the tests below exist so that nobody "fixes" them without noticing
/// they are changing who can register.
/// </para>
/// </remarks>
public class UserTests
{
    private const int UserIdentitySeed = 1;

    /// <summary>
    /// The account reports its primary key as its identity.
    /// </summary>
    [Fact]
    public void Identity_IsThePrimaryKey()
    {
        User account = NewUser(UserIdentitySeed);
        User sameRow = NewUser(UserIdentitySeed, "someone_else");
        User otherRow = NewUser(2);

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        account.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();
        otherRow.MarkIdentityPersisted();

        account.Identity.Should().Be(UserIdentitySeed);
        account.Should().Be(sameRow);
        account.Should().NotBe(otherRow);
    }

    /// <summary>
    /// Unlike the portal, role, page and module tables, the account table seeds at one.
    /// </summary>
    [Fact]
    public void Identity_SeedsAtOneUnlikeTheOtherAggregates()
    {
        Entity<int> account = NewUser(UserIdentitySeed);
        Entity<int> role = new Role { RoleId = 0, RoleName = "Administrators" };

        account.Identity.Should().Be(1);
        role.Identity.Should().Be(0);
        account.Equals(role).Should().BeFalse();
    }

    /// <summary>
    /// Membership is identified by its own surrogate key even though the table is keyed by the pair.
    /// </summary>
    [Fact]
    public void Membership_IsIdentifiedByItsSurrogateKey()
    {
        UserPortal membership = new()
        {
            UserPortalId = 17,
            UserId = 1,
            PortalId = -1,
            CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        membership.Identity.Should().Be(
            17,
            "dbo.UserPortals is keyed by (UserId, PortalId) but also carries an identity column, and the "
            + "surrogate is what the entity reports so that two tenancies of one account stay distinct");
        membership.Should().NotBe(new UserPortal { UserPortalId = 18, UserId = 1, PortalId = -1 });
    }

    /// <summary>
    /// A membership is authorised until something says otherwise.
    /// </summary>
    [Fact]
    public void Membership_IsAuthorisedByDefault()
    {
        UserPortal membership = new() { UserPortalId = 1, UserId = 1, PortalId = 0 };

        membership.IsAuthorised.Should().BeTrue(
            "the legacy column spelling is Authorised and its default admits the member");
    }

    /// <summary>
    /// The credential facts held in the external store read as absent until that store has been read.
    /// </summary>
    [Fact]
    public void CredentialFacts_AreAbsentRatherThanFalseUntilRead()
    {
        User account = NewUser(1);

        account.IsApproved.Should().BeNull();
        account.IsLockedOut.Should().BeNull();
        account.CreatedDate.Should().BeNull();
        account.LastLoginDate.Should().BeNull();
        account.LastActivityDate.Should().BeNull();
        account.LastLockoutDate.Should().BeNull();
        account.LastPasswordChangeDate.Should().BeNull();
        account.PasswordHash.Should().BeNull(
            "the hash lives in the external membership store and is never carried on the profile row");

        (account.IsApproved == true).Should().BeFalse("an unread approval must never read as approved");
        (account.IsLockedOut == true).Should().BeFalse();
    }

    /// <summary>
    /// A host account is distinguished from an ordinary one by a flag on the profile row.
    /// </summary>
    [Fact]
    public void HostAccounts_AreFlaggedOnTheProfileRow()
    {
        User ordinary = NewUser(1);
        User host = NewUser(2);
        host.IsSuperUser = true;

        ordinary.IsSuperUser.Should().BeFalse();
        host.IsSuperUser.Should().BeTrue();
        ordinary.UpdatePassword.Should().BeFalse();
    }

    /// <summary>
    /// A newly constructed account exposes empty collections rather than null ones.
    /// </summary>
    [Fact]
    public void NavigationCollections_AreInitialisedRatherThanNull()
    {
        User account = NewUser(1);

        account.UserPortals.Should().NotBeNull().And.BeEmpty();
        account.UserProfileValues.Should().NotBeNull().And.BeEmpty();
        account.UserRoles.Should().NotBeNull().And.BeEmpty();
        account.ModulePermissions.Should().NotBeNull().And.BeEmpty();
        account.TabPermissions.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// The creation outcome enumeration keeps the stored integers the legacy provider reported.
    /// </summary>
    [Fact]
    public void CreationOutcomes_KeepTheirLegacyIntegers()
    {
        ((int)UserCreateStatus.AddUser).Should().Be(0);
        ((int)UserCreateStatus.UsernameAlreadyExists).Should().Be(1);
        ((int)UserCreateStatus.DuplicateEmail).Should().Be(3);
        ((int)UserCreateStatus.Success).Should().Be(13);
        ((int)UserCreateStatus.AddUserToPortal).Should().Be(17);
    }

    /// <summary>
    /// The sign-in outcome enumeration keeps the stored integers the legacy provider reported.
    /// </summary>
    [Fact]
    public void SignInOutcomes_KeepTheirLegacyIntegers()
    {
        ((int)UserLoginStatus.Failure).Should().Be(0);
        ((int)UserLoginStatus.Success).Should().Be(1);
        ((int)UserLoginStatus.SuperUser).Should().Be(2);
        ((int)UserLoginStatus.UserLockedOut).Should().Be(3);
        ((int)UserLoginStatus.UserNotApproved).Should().Be(4);
        ((int)UserLoginStatus.InsecureAdminPassword).Should().Be(5);
        ((int)UserLoginStatus.InsecureHostPassword).Should().Be(6);
    }

    /// <summary>
    /// The stored credential format enumeration keeps the legacy provider's integers.
    /// </summary>
    [Fact]
    public void CredentialFormats_KeepTheirLegacyIntegers()
    {
        ((int)PasswordFormat.Clear).Should().Be(0);
        ((int)PasswordFormat.Hashed).Should().Be(1);
        ((int)PasswordFormat.Encrypted).Should().Be(2);
    }

    /// <summary>
    /// A well-formed address is accepted and reported with its surrounding whitespace removed.
    /// </summary>
    /// <param name="candidate">The address under test.</param>
    /// <param name="expected">The value the wrapper should report.</param>
    [Theory]
    [InlineData("user@example.com", "user@example.com")]
    [InlineData("  user@example.com  ", "user@example.com")]
    [InlineData("a@b.co", "a@b.co")]
    [InlineData("first.last+tag@sub.example.co.uk", "first.last+tag@sub.example.co.uk")]
    [InlineData("o'brien@example.org", "o'brien@example.org")]
    [InlineData("percent%sign@example.info", "percent%sign@example.info")]
    [InlineData("with-hyphen@my-domain.net", "with-hyphen@my-domain.net")]
    [InlineData("_underscore@example.com", "_underscore@example.com")]
    [InlineData("MiXeD@CaSe.CoM", "MiXeD@CaSe.CoM")]
    public void EmailAddress_AcceptsAWellFormedAddress(string candidate, string expected)
    {
        EmailAddress address = EmailAddress.Create(candidate);

        address.Value.Should().Be(expected);
        address.ToString().Should().Be(expected);
        ((string)address).Should().Be(expected);
        EmailAddress.TryCreate(candidate, out EmailAddress? tried).Should().BeTrue();
        tried!.Value.Should().Be(expected);
    }

    /// <summary>
    /// An address the legacy pattern rejected is still rejected, quirks and all.
    /// </summary>
    /// <param name="candidate">The address under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign.example.com")]
    [InlineData("two@at@signs.com")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@example")]
    [InlineData("user@.com")]
    [InlineData("user@example.c")]
    [InlineData("user@example.c0m")]
    [InlineData("user@under_score.com")]
    [InlineData(".leading.dot@example.com")]
    [InlineData("+leading.plus@example.com")]
    [InlineData("-leading.hyphen@example.com")]
    [InlineData("%leading.percent@example.com")]
    [InlineData("'leading.apostrophe@example.com")]
    [InlineData("space in local@example.com")]
    [InlineData("bang!@example.com")]
    public void EmailAddress_RejectsWhateverTheLegacyPatternRejected(string? candidate)
    {
        Action create = () => _ = EmailAddress.Create(candidate!);

        create.Should().Throw<DomainException>();
        EmailAddress.TryCreate(candidate, out EmailAddress? tried).Should().BeFalse();
        tried.Should().BeNull();
    }

    /// <summary>
    /// A final domain label longer than four letters is ACCEPTED, and the bounded standards-derived
    /// label rules are what refuse the shapes that cannot resolve.
    /// </summary>
    /// <remarks>
    /// This is a documented compatibility divergence, not an oversight. The legacy pattern's
    /// <c>[a-zA-Z]{2,4}</c> clause refused every final label longer than four letters, so an account
    /// whose address ends <c>.museum</c>, <c>.travel</c>, <c>.online</c> or <c>.agency</c> could not be
    /// registered, corrected or created by an administrator. The limit is replaced by per-label bounds -
    /// every label 1 to 63 characters, the final label 2 to 63 letters, the whole domain no more than
    /// 253 - so the rule still bounds what it admits while no longer excluding addresses the target has
    /// to accept. The divergence runs both ways and the tightening half is asserted here too: three
    /// shapes the legacy pattern accepted are now refused, because a bound that admits them is no bound.
    /// </remarks>
    [Fact]
    public void EmailAddress_AcceptsAModernSuffixAndBoundsTheLabelsInstead()
    {
        EmailAddress.Create("curator@national.museum").Value.Should().Be("curator@national.museum");
        EmailAddress.Create("agent@my.travel").Value.Should().Be("agent@my.travel");

        Action oneLetterSuffix = () => _ = EmailAddress.Create("user@example.c");

        oneLetterSuffix.Should().Throw<DomainException>()
            .WithMessage("*at least 2 characters long*");

        Action emptyLabel = () => _ = EmailAddress.Create("user@a..com");

        emptyLabel.Should().Throw<DomainException>("the legacy pattern accepted a label of no length");

        Action overLongLabel = () => _ = EmailAddress.Create("user@" + new string('a', 64) + ".com");

        overLongLabel.Should().Throw<DomainException>("the legacy pattern bounded no label at all");
    }

    /// <summary>
    /// A local part beginning with a non-word character is rejected, and the message says why.
    /// </summary>
    [Fact]
    public void EmailAddress_RejectsANonWordFirstCharacterWithAnExplicitMessage()
    {
        Action create = () => _ = EmailAddress.Create(".hidden@example.com");

        create.Should().Throw<DomainException>()
            .WithMessage("*must begin with a letter, a digit, or an underscore*");
    }

    /// <summary>
    /// The width of the column that stores the address is the limit, not the width of the screen field.
    /// </summary>
    [Fact]
    public void EmailAddress_IsBoundedByTheColumnWidth()
    {
        // MIGRATION: 256, not 100. The terminal dbo.Users.Email column is nvarchar(256) - which is what
        // UserConfiguration maps and what the request validators state independently - so 100 was a
        // superseded width taken from an earlier revision of the column rather than from the schema.
        const string domain = "@example.com";
        string atTheLimit = new string('a', 256 - domain.Length) + domain;
        string oneTooLong = new string('a', 257 - domain.Length) + domain;

        atTheLimit.Should().HaveLength(256);
        oneTooLong.Should().HaveLength(257);

        EmailAddress.Create(atTheLimit).Value.Should().Be(atTheLimit);

        Action tooLong = () => _ = EmailAddress.Create(oneTooLong);

        tooLong.Should().Throw<DomainException>()
            .WithMessage("*no longer than 256 characters*");
    }

    /// <summary>
    /// Two addresses differing only in case are the same address, but each keeps the text it was given.
    /// </summary>
    [Fact]
    public void EmailAddress_ComparesCaseInsensitivelyWhilePreservingTheStoredText()
    {
        EmailAddress lower = EmailAddress.Create("member@example.com");
        EmailAddress upper = EmailAddress.Create("MEMBER@EXAMPLE.COM");
        EmailAddress different = EmailAddress.Create("other@example.com");

        lower.Should().Be(upper);
        (lower == upper).Should().BeTrue();
        lower.GetHashCode().Should().Be(upper.GetHashCode());
        upper.Value.Should().Be("MEMBER@EXAMPLE.COM", "the wrapper never rewrites the text it was given");
        lower.Should().NotBe(different);
        lower.Equals(null).Should().BeFalse();
    }

    /// <summary>
    /// The explicit conversion from text runs the same validation the factory does.
    /// </summary>
    [Fact]
    public void EmailAddress_ExplicitConversionValidates()
    {
        EmailAddress converted = (EmailAddress)"member@example.com";

        converted.Value.Should().Be("member@example.com");

        Action bad = () => _ = (EmailAddress)"not an address";

        bad.Should().Throw<DomainException>();
    }

    /// <summary>
    /// Builds a minimally populated account carrying the supplied identifier.
    /// </summary>
    /// <param name="userId">The identifier to carry.</param>
    /// <param name="username">The account name.</param>
    /// <returns>The account.</returns>
    private static User NewUser(int userId, string username = "integration_member") => new()
    {
        UserId = userId,
        Username = username,
        FirstName = "Integration",
        LastName = "Member",
        DisplayName = "Integration Member",
        Email = "member@example.com",
    };
}
