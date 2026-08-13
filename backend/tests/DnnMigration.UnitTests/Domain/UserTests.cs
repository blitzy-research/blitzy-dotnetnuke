using System.Reflection;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

// The module aggregate and System.Reflection.Module share a simple name, and this file needs both: the
// reflection type comes in with the namespace that the structural assertions rely on. The alias resolves
// the collision explicitly rather than leaving it to import order.
using ModuleAggregate = DnnMigration.Domain.Entities.Module;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Invariant and sentinel-boundary tests for the account aggregate, its per-tenant membership, its profile
/// key/value pair and the three membership enumerations whose stored integers the migration had to carry
/// across unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The credential facts are the sharpest consequence of the merge and they are nullable for a reason.
/// <c>dbo.Users</c> holds the account row; the external ASP.NET membership tables, which DotNetNuke only
/// ever <c>ALTER</c>s and never creates, hold the credentials.
/// </para>
/// <para>
/// Scope. Nothing here validates a value object's own rules, hashes a password, exercises a
/// FluentValidation validator, evaluates the sign-in predicate or touches Entity Framework Core: those live
/// in the portal, security, validation, application-service and integration suites respectively.
/// </para>
/// </remarks>
public sealed class UserTests
{
    /// <summary>
    /// The seed of <c>dbo.Users.UserID</c>, declared <c>int IDENTITY(1, 1) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider</c> line 98 and preserved through both table rebuilds.
    /// </summary>
    private const int UserKeySeed = 1;

    /// <summary>
    /// The seed of <c>dbo.Portals.PortalID</c>, declared <c>int IDENTITY(-1, 1) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider</c> line 77. The same number the account key never reaches.
    /// </summary>
    private const int PortalKeySeed = -1;

    /// <summary>
    /// The legacy integral absence marker from <c>Library/Components/Shared/Null.vb</c> line 41
    /// (<c>NullInteger</c>) and line 37 (<c>NullShort</c>).
    /// </summary>
    private const int LegacyAbsentInteger = -1;

    /// <summary>
    /// The eighteen member names of the legacy <c>UserCreateStatus</c> enumeration, in the order
    /// <c>Library/Components/Users/Membership/UserCreateStatus.vb</c> lines 24-41 declares them.
    /// </summary>
    private static readonly string[] LegacyCreateStatusNames =
    [
        "AddUser",
        "UsernameAlreadyExists",
        "UserAlreadyRegistered",
        "DuplicateEmail",
        "DuplicateProviderUserKey",
        "DuplicateUserName",
        "InvalidAnswer",
        "InvalidEmail",
        "InvalidPassword",
        "InvalidProviderUserKey",
        "InvalidQuestion",
        "InvalidUserName",
        "ProviderError",
        "Success",
        "UnexpectedError",
        "UserRejected",
        "PasswordMismatch",
        "AddUserToPortal",
    ];

    /// <summary>
    /// Members the account aggregate must not declare: the legacy hydration flags, the two composed objects
    /// whose lazy getters performed database I/O, the token-accessor member, and the tenant identifier that
    /// belongs to the membership join rather than to the account row.
    /// </summary>
    private static readonly string[] MembersTheAccountMustNotDeclare =
    [
        "ObjectHydrated",
        "IsDirty",
        "RolesHydrated",
        "Membership",
        "Profile",
        "Cacheability",
        "PortalID",
        "PortalId",
        "Roles",
        "Password",
    ];

    // =================================================================================
    // UserCreateStatus - the account-creation outcome discriminator
    // =================================================================================

    /// <summary>Every creation outcome keeps the integer the legacy provider reported.</summary>
    /// <param name="member">The member under test.</param>
    /// <param name="expected">The integer the legacy enumeration assigned it.</param>
    [Theory]
    [InlineData(UserCreateStatus.AddUser, 0)]
    [InlineData(UserCreateStatus.UsernameAlreadyExists, 1)]
    [InlineData(UserCreateStatus.UserAlreadyRegistered, 2)]
    [InlineData(UserCreateStatus.DuplicateEmail, 3)]
    [InlineData(UserCreateStatus.DuplicateProviderUserKey, 4)]
    [InlineData(UserCreateStatus.DuplicateUserName, 5)]
    [InlineData(UserCreateStatus.InvalidAnswer, 6)]
    [InlineData(UserCreateStatus.InvalidEmail, 7)]
    [InlineData(UserCreateStatus.InvalidPassword, 8)]
    [InlineData(UserCreateStatus.InvalidProviderUserKey, 9)]
    [InlineData(UserCreateStatus.InvalidQuestion, 10)]
    [InlineData(UserCreateStatus.InvalidUserName, 11)]
    [InlineData(UserCreateStatus.ProviderError, 12)]
    [InlineData(UserCreateStatus.Success, 13)]
    [InlineData(UserCreateStatus.UnexpectedError, 14)]
    [InlineData(UserCreateStatus.UserRejected, 15)]
    [InlineData(UserCreateStatus.PasswordMismatch, 16)]
    [InlineData(UserCreateStatus.AddUserToPortal, 17)]
    public void CreationOutcome_KeepsItsLegacyInteger(UserCreateStatus member, int expected)
    {
        ((int)member).Should().Be(expected);
        Enum.IsDefined(member).Should().BeTrue();
    }

    /// <summary>
    /// The enumeration declares exactly the eighteen legacy members, under exactly the legacy names.
    /// </summary>
    [Fact]
    public void CreationOutcomes_DeclareExactlyTheEighteenLegacyMembers()
    {
        Enum.GetValues<UserCreateStatus>().Should().HaveCount(18);
        Enum.GetNames<UserCreateStatus>().Should().BeEquivalentTo(LegacyCreateStatusNames);
        Enum.GetValues<UserCreateStatus>().Select(status => (int)status).Should()
            .BeEquivalentTo(Enumerable.Range(0, 18));
    }

    /// <summary>
    /// A default-initialised creation outcome is the legacy "not yet succeeded" seed, and can never be read
    /// as success.
    /// </summary>
    [Fact]
    public void CreationOutcome_DefaultsToTheNotYetSucceededSeedAndNeverToSuccess()
    {
        UserCreateStatus defaulted = default;

        defaulted.Should().Be(UserCreateStatus.AddUser);
        ((int)defaulted).Should().Be(0);

        defaulted.Should().NotBe(UserCreateStatus.Success);
        (defaulted == UserCreateStatus.Success).Should().BeFalse(
            "zero is the legacy seed for an outcome that has not been decided, so a status nobody "
            + "assigned must never be interpreted as a created account");

        // The numerically lowest member is what the legacy sentinel reader returns for a null column, so
        // the lowest member and the default member must be the same non-affirmative one.
        Enum.GetValues<UserCreateStatus>().Min().Should().Be(UserCreateStatus.AddUser);
    }

    /// <summary>
    /// Success is thirteen, asserted on its own so that a renumbering fails loudly and specifically.
    /// </summary>
    /// <remarks>
    /// Duplicated deliberately from the theory above. The theory proves the whole table; this proves the
    /// one number whose value a reader is most likely to assume, and it names the assumption in its own
    /// failure message.
    /// </remarks>
    [Fact]
    public void CreationOutcome_PinsSuccessAtThirteen()
    {
        ((int)UserCreateStatus.Success).Should().Be(
            13,
            "the legacy enumeration assigned Success the fourteenth slot rather than the first, and "
            + "the number is persisted and transmitted rather than internal");

        ((int)UserCreateStatus.Success).Should().NotBe(0);
        ((int)UserCreateStatus.Success).Should().NotBe(1);
    }

    // =================================================================================
    // UserLoginStatus - the sign-in outcome discriminator
    // =================================================================================

    /// <summary>Every sign-in outcome keeps the integer the legacy provider reported, under its new name.</summary>
    /// <param name="member">The member under test.</param>
    /// <param name="expected">The integer the legacy enumeration assigned it.</param>
    [Theory]
    [InlineData(UserLoginStatus.Failure, 0)]
    [InlineData(UserLoginStatus.Success, 1)]
    [InlineData(UserLoginStatus.SuperUser, 2)]
    [InlineData(UserLoginStatus.UserLockedOut, 3)]
    [InlineData(UserLoginStatus.UserNotApproved, 4)]
    [InlineData(UserLoginStatus.InsecureAdminPassword, 5)]
    [InlineData(UserLoginStatus.InsecureHostPassword, 6)]
    public void SignInOutcome_KeepsItsLegacyInteger(UserLoginStatus member, int expected)
    {
        ((int)member).Should().Be(expected);
        Enum.IsDefined(member).Should().BeTrue();
    }

    /// <summary>
    /// The enumeration declares exactly seven members, contiguously numbered, and the elevated sign-in
    /// outcome sits at two.
    /// </summary>
    /// <remarks>
    /// The predicate that decides which of these outcomes counts as authenticated is deliberately not
    /// asserted here - it is application behaviour and belongs to the sign-in service suite. What this file
    /// guarantees is only that the vocabulary that predicate reads has not shifted underneath it.
    /// </remarks>
    [Fact]
    public void SignInOutcomes_DeclareExactlySevenContiguousMembersWithTheElevatedOutcomeAtTwo()
    {
        Enum.GetValues<UserLoginStatus>().Should().HaveCount(7);
        Enum.GetValues<UserLoginStatus>().Select(status => (int)status).Should()
            .BeEquivalentTo(Enumerable.Range(0, 7));

        ((int)UserLoginStatus.SuperUser).Should().Be(
            2,
            "the elevated sign-in outcome is a distinct affirmative value, not a variant of Success");
        UserLoginStatus.SuperUser.Should().NotBe(UserLoginStatus.Success);
    }

    /// <summary>
    /// A default-initialised sign-in outcome is failure, which is the safe direction and the deliberate
    /// opposite of the creation enumeration's default.
    /// </summary>
    [Fact]
    public void SignInOutcome_DefaultsToFailureUnlikeTheCreationOutcome()
    {
        UserLoginStatus defaulted = default;

        defaulted.Should().Be(UserLoginStatus.Failure);
        ((int)defaulted).Should().Be(0);
        defaulted.Should().NotBe(UserLoginStatus.Success);
        defaulted.Should().NotBe(UserLoginStatus.SuperUser);

        Enum.GetValues<UserLoginStatus>().Min().Should().Be(UserLoginStatus.Failure);
    }

    // =================================================================================
    // PasswordFormat - the stored credential format discriminator
    // =================================================================================

    /// <summary>
    /// The stored credential format keeps the legacy provider's three integers and defaults to the
    /// plaintext member.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this enumeration is carried across for schema fidelity, not because the target uses every
    /// member. <c>Website/release.config</c> lines 236-247 registered the membership provider with
    /// <c>passwordFormat="Encrypted"</c> and <c>enablePasswordRetrieval="true"</c>, and the triple-DES key
    /// that decrypts every stored credential is committed to the legacy repository at lines 89-93.
    /// </remarks>
    [Fact]
    public void CredentialFormats_KeepTheirLegacyIntegersAndDefaultToPlaintext()
    {
        ((int)PasswordFormat.Clear).Should().Be(0);
        ((int)PasswordFormat.Hashed).Should().Be(1);
        ((int)PasswordFormat.Encrypted).Should().Be(
            2,
            "an existing row may declare the reversibly encrypted format and the reader has to "
            + "recognise the number even though the target never writes it");

        Enum.GetValues<PasswordFormat>().Should().HaveCount(3);
        Enum.GetNames<PasswordFormat>().Should()
            .BeEquivalentTo("Clear", "Hashed", "Encrypted");

        PasswordFormat defaulted = default;

        defaulted.Should().Be(PasswordFormat.Clear);
        Enum.GetValues<PasswordFormat>().Min().Should().Be(PasswordFormat.Clear);
    }

    // =================================================================================
    // User - identity
    // =================================================================================

    /// <summary>
    /// The account reports its primary key as its identity, and two rows of the same key are the same
    /// entity.
    /// </summary>
    /// <remarks>
    /// The identity is declared persisted first, because the base class refuses to compare by identity
    /// until the persistence layer says the key is the database's. Reaching for the value sooner is the
    /// mistake this schema punishes hardest, and the following test asserts that refusal directly.
    /// </remarks>
    [Fact]
    public void Identity_IsThePrimaryKeyOfTheAccountRow()
    {
        User account = NewUser(UserKeySeed);
        User sameRow = NewUser(UserKeySeed, "a_different_login");
        User otherRow = NewUser(UserKeySeed + 1);

        account.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();
        otherRow.MarkIdentityPersisted();

        account.Identity.Should().Be(UserKeySeed);
        account.Should().Be(sameRow, "identity decides, not the values carried alongside it");
        account.Should().NotBe(otherRow);
        account.GetHashCode().Should().Be(sameRow.GetHashCode());
    }

    /// <summary>Equality is decided by object reference until a caller declares the key real.</summary>
    /// <remarks>
    /// This is what makes the identity-seed collisions in this schema harmless. The base class never
    /// deduces from an identity value whether a row exists, so it does not matter that <c>dbo.Portals</c>
    /// seeds at -1 or that <c>dbo.Roles</c>, <c>dbo.Tabs</c> and <c>dbo.Modules</c> seed at 0: two freshly
    /// constructed objects are two entities regardless of what their key properties happen to hold.
    /// </remarks>
    [Fact]
    public void Identity_IsReferenceBasedUntilThePersistenceLayerDeclaresTheKeyReal()
    {
        User unsaved = NewUser(UserKeySeed);
        User alsoUnsaved = NewUser(UserKeySeed);

        unsaved.IdentityIsPersisted.Should().BeFalse();
        unsaved.Should().NotBe(alsoUnsaved, "an undeclared key cannot make two objects one entity");
        unsaved.Should().Be(unsaved);

        unsaved.MarkIdentityPersisted();
        unsaved.IdentityIsPersisted.Should().BeTrue();
        unsaved.Should().NotBe(
            alsoUnsaved,
            "one side alone declaring its key is not enough; the comparison needs both");

        alsoUnsaved.MarkIdentityPersisted();
        unsaved.Should().Be(alsoUnsaved);
    }

    /// <summary>Two entities of different types are never equal, even carrying the same identity value.</summary>
    [Fact]
    public void Identity_DoesNotCrossAggregateBoundaries()
    {
        Entity<int> account = NewUser(UserKeySeed);
        Entity<int> membership = NewMembership(UserKeySeed, UserKeySeed, PortalKeySeed);

        account.MarkIdentityPersisted();
        membership.MarkIdentityPersisted();

        object foreignObject = "not an entity";

        account.Identity.Should().Be(
            membership.Identity,
            "the two carry the same number, which is what makes the next assertion meaningful");
        account.Equals(membership).Should().BeFalse();
        membership.Equals(account).Should().BeFalse("the refusal is symmetric");
        account.Equals(foreignObject).Should().BeFalse();
        (account == membership).Should().BeFalse();
    }

    /// <summary>The account key seeds at one, so neither -1 nor 0 is ever a persisted account identifier.</summary>
    [Fact]
    public void AccountKey_SeedsAtOneSoTheLegacyAbsenceMarkerIsNeverARealAccount()
    {
        User seeded = NewUser(UserKeySeed);
        User carryingTheLegacyMarker = NewUser(LegacyAbsentInteger);
        User carryingZero = NewUser(0);

        seeded.UserId.Should().Be(1, "IDENTITY(1, 1) makes one the lowest key the store can issue");
        carryingTheLegacyMarker.UserId.Should().Be(LegacyAbsentInteger);
        carryingTheLegacyMarker.Identity.Should().Be(LegacyAbsentInteger);
        carryingZero.UserId.Should().Be(0);

        carryingTheLegacyMarker.IdentityIsPersisted.Should().BeFalse(
            "persistence is declared by the layer that wrote the row, never inferred from the value");
        carryingZero.IdentityIsPersisted.Should().BeFalse();
    }

    /// <summary>
    /// The tenant key seeds at minus one, so the very number that marks absence for an account is a real
    /// identifier here - and zero is a live foreign key.
    /// </summary>
    /// <remarks>
    /// Zero is not hypothetical either: the shipped baseline inserts <c>INSERT INTO [dbo].[UserPortals]
    /// ([UserId], [PortalId], [Authorized]) VALUES (9, 0, 1)</c> at <c>01.00.00.SqlDataProvider</c> line
    /// 7229, so the administrator account's membership row refers to portal 0 as a live foreign key.
    /// </remarks>
    [Theory]
    [InlineData(PortalKeySeed)]
    [InlineData(0)]
    [InlineData(1)]
    public void TenantKey_SeedsAtMinusOneSoEveryValueIncludingTheLegacyMarkerIsAnIdentifier(int portalId)
    {
        UserPortal membership = NewMembership(userPortalId: 17, userId: UserKeySeed, portalId: portalId);

        membership.PortalId.Should().Be(
            portalId,
            "the store issues -1 and 0 as ordinary keys, so neither may be reinterpreted as absence");
        typeof(UserPortal).GetProperty(nameof(UserPortal.PortalId))!.PropertyType.Should()
            .Be<int>("a nullable tenant key would invite exactly the reinterpretation this forbids");
    }

    /// <summary>
    /// The account is not audited, and its creation date is membership data rather than audit metadata.
    /// </summary>
    /// <remarks>
    /// So all five dates are domain data describing the credential's own history, and treating the creation
    /// date as audit metadata would imply the other four were too. The account therefore derives straight
    /// from the plain entity base and declares no last-updated timestamp.
    /// </remarks>
    [Fact]
    public void Account_IsNotAuditedAndItsDatesAreDomainDataRatherThanAuditMetadata()
    {
        typeof(User).BaseType.Should().Be(typeof(Entity<int>));
        typeof(AuditableEntity<int>).IsAssignableFrom(typeof(User)).Should().BeFalse(
            "the five membership dates are the credential's own history, not audit metadata");

        typeof(User).GetProperty("LastUpdatedDate").Should().BeNull();

        typeof(AuditableEntity<int>)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .Should().BeEquivalentTo("CreatedDate", "LastUpdatedDate");

        typeof(User).GetProperty(nameof(User.CreatedDate))!.PropertyType.Should().Be<DateTime?>();
        typeof(User).GetProperty(nameof(User.LastActivityDate))!.PropertyType.Should().Be<DateTime?>();
        typeof(User).GetProperty(nameof(User.LastLockoutDate))!.PropertyType.Should().Be<DateTime?>();
        typeof(User).GetProperty(nameof(User.LastLoginDate))!.PropertyType.Should().Be<DateTime?>();
        typeof(User).GetProperty(nameof(User.LastPasswordChangeDate))!.PropertyType.Should()
            .Be<DateTime?>();
    }

    // =================================================================================
    // User - the sentinel boundary
    // =================================================================================

    /// <summary>
    /// Every fact held in the external credential store reads as absent, not as false, until that store has
    /// been read.
    /// </summary>
    [Fact]
    public void CredentialFacts_AreAbsentRatherThanFalseUntilTheExternalStoreIsRead()
    {
        User account = NewUser(UserKeySeed);

        account.IsApproved.Should().BeNull();
        account.IsLockedOut.Should().BeNull();
        account.IsOnline.Should().BeNull();
        account.CreatedDate.Should().BeNull();
        account.LastActivityDate.Should().BeNull();
        account.LastLockoutDate.Should().BeNull();
        account.LastLoginDate.Should().BeNull();
        account.LastPasswordChangeDate.Should().BeNull();
        account.PasswordHash.Should().BeNull(
            "the credential never lives on the account row; the legacy baseline stored it there as "
            + "plaintext nvarchar(20) and the upgrade chain removed the column");
        account.PasswordAnswer.Should().BeNull();
        account.PasswordQuestion.Should().BeNull();
    }

    /// <summary>
    /// An unread credential fact can be mistaken for neither answer, and the two flags fail safe in
    /// opposite directions.
    /// </summary>
    [Fact]
    public void CredentialFacts_FailSafeInOppositeDirectionsAndKeepAbsentDistinctFromFalse()
    {
        User unread = NewUser(UserKeySeed);

        (unread.IsApproved == true).Should().BeFalse("an unread approval must never admit an account");
        (unread.IsApproved == false).Should().BeFalse("nor may it claim the account was refused");
        (unread.IsLockedOut == true).Should().BeFalse("an unread lockout must never refuse an account");
        (unread.IsLockedOut == false).Should().BeFalse();

        User reviewed = NewUser(UserKeySeed);
        reviewed.IsApproved = false;
        reviewed.IsLockedOut = false;

        reviewed.IsApproved.Should().NotBeNull();
        reviewed.IsApproved.Should().BeFalse();
        reviewed.IsLockedOut.Should().BeFalse();
        reviewed.IsApproved.Should().NotBe(unread.IsApproved, "false and absent are different answers");

        User approved = NewUser(UserKeySeed);
        approved.IsApproved = true;

        (approved.IsApproved == true).Should().BeTrue();
    }

    /// <summary>A text property keeps the empty string it was given and never collapses it to absence.</summary>
    /// <param name="stored">The value written to the property.</param>
    /// <param name="expected">The value the property must report back.</param>
    /// <remarks>
    /// MIGRATION: <c>Library/Components/Shared/Null.vb</c> line 71 defines the textual absence marker as
    /// the EMPTY STRING rather than as null, and the reader at line 226 reports any empty string as absent,
    /// so the legacy stack could not tell a column holding <c>''</c> from one holding <c>NULL</c>. The
    /// target keeps the two apart: absence is null and an empty string is a stored value.
    /// </remarks>
    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("member@example.com", "member@example.com")]
    [InlineData(null, null)]
    public void TextProperties_KeepTheEmptyStringApartFromAbsence(string? stored, string? expected)
    {
        User account = NewUser(UserKeySeed);
        account.Email = stored;
        account.PasswordQuestion = stored;

        account.Email.Should().Be(expected);
        account.PasswordQuestion.Should().Be(expected);
    }

    /// <summary>Absence and the empty string remain two distinguishable states on the same property.</summary>
    [Fact]
    public void TextProperties_TreatAbsenceAndTheEmptyStringAsDifferentStates()
    {
        User empty = NewUser(UserKeySeed);
        User absent = NewUser(UserKeySeed);

        empty.Email = string.Empty;
        absent.Email = null;

        empty.Email.Should().NotBeNull().And.BeEmpty();
        absent.Email.Should().BeNull();
        empty.Email.Should().NotBe(absent.Email);

        typeof(User).GetProperty(nameof(User.Email))!.PropertyType.Should().Be<string>();
    }

    /// <summary>
    /// No text property is seeded with the empty string on construction, and the account now agrees with
    /// the other aggregates on that where the legacy classes did not.
    /// </summary>
    /// <remarks>
    /// The target resolves the disagreement in one direction rather than picking a winner: no string
    /// property on any of these aggregates carries an initialiser, so a freshly constructed instance holds
    /// null everywhere and an empty string is always a value somebody stored.
    /// </remarks>
    [Fact]
    public void TextProperties_AreNotSeededWithTheEmptyStringOnAnyAggregate()
    {
        User account = new();
        ModuleAggregate module = new();
        Tab page = new();

        account.Username.Should().BeNull("the legacy account constructor left it unset too");
        account.DisplayName.Should().BeNull();
        account.Email.Should().BeNull();
        account.FirstName.Should().BeNull();
        account.LastName.Should().BeNull();

        module.ModuleTitle.Should().BeNull(
            "the legacy module class seeded this to the empty string; the target does not");
        page.TabName.Should().BeNull(
            "the legacy page class seeded this to the empty string; the target does not");

        foreach (Type aggregate in new[] { typeof(User), typeof(ModuleAggregate), typeof(Tab) })
        {
            object instance = Activator.CreateInstance(aggregate)!;

            aggregate
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.PropertyType == typeof(string))
                .Where(property => property.GetValue(instance) is not null)
                .Select(property => property.Name)
                .Should().BeEmpty(
                    "{0} must seed no text property, so absence stays null and the empty string stays "
                    + "a stored value",
                    aggregate.Name);
        }
    }

    /// <summary>
    /// An identifier keeps the legacy absence marker as an ordinary number, and expresses absence with the
    /// nullable wrapper instead.
    /// </summary>
    [Fact]
    public void Identifiers_ExpressAbsenceWithTheNullableWrapperAndNotWithMinusOne()
    {
        User carryingTheMarker = NewUser(UserKeySeed);
        User withoutAnAffiliate = NewUser(UserKeySeed);

        carryingTheMarker.AffiliateId = LegacyAbsentInteger;
        withoutAnAffiliate.AffiliateId = null;

        carryingTheMarker.AffiliateId.Should().Be(LegacyAbsentInteger);
        carryingTheMarker.AffiliateId.Should().NotBeNull("minus one is a number, not an absence");
        withoutAnAffiliate.AffiliateId.Should().BeNull();

        typeof(User).GetProperty(nameof(User.AffiliateId))!.PropertyType.Should().Be<int?>();
    }

    /// <summary>
    /// A date property stores the earliest representable instant verbatim, and distinguishes the whole of
    /// that first day from absence.
    /// </summary>
    /// <remarks>
    /// The target expresses absence as null, so it distinguishes what the legacy contract conflated: the
    /// earliest representable instant is a value, an instant five hours later is a different value, and
    /// neither is absence.
    /// </remarks>
    [Fact]
    public void DateProperties_DistinguishTheEarliestInstantAndItsWholeDayFromAbsence()
    {
        DateTime legacyAbsentDate = DateTime.MinValue;
        DateTime insideTheSameDay = DateTime.MinValue.AddHours(5);

        User atTheMarker = NewUser(UserKeySeed);
        User insideTheMarkersDay = NewUser(UserKeySeed);
        User withoutADate = NewUser(UserKeySeed);

        atTheMarker.LastLoginDate = legacyAbsentDate;
        insideTheMarkersDay.LastLoginDate = insideTheSameDay;

        atTheMarker.LastLoginDate.Should().Be(legacyAbsentDate);
        atTheMarker.LastLoginDate.Should().NotBeNull(
            "the earliest representable instant is a stored value here, not the absence marker");
        insideTheMarkersDay.LastLoginDate.Should().Be(insideTheSameDay);
        withoutADate.LastLoginDate.Should().BeNull();

        // The legacy date-part-only comparison made these two the same answer. They are not.
        insideTheSameDay.Date.Should().Be(legacyAbsentDate.Date);
        insideTheMarkersDay.LastLoginDate.Should().NotBe(atTheMarker.LastLoginDate);
        atTheMarker.LastLoginDate.Should().NotBe(withoutADate.LastLoginDate);
    }

    /// <summary>
    /// No property on any of the four user-domain types is byte-typed, so the one sentinel that is neither
    /// minus one, a minimum value nor an empty string has no surface to reach.
    /// </summary>
    [Fact]
    public void NoUserDomainTypeDeclaresAByteProperty()
    {
        Type[] userDomainTypes =
        [
            typeof(User),
            typeof(UserPortal),
            typeof(UserProfileValue),
            typeof(ProfilePropertyDefinition),
        ];

        foreach (Type domainType in userDomainTypes)
        {
            domainType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property =>
                    property.PropertyType == typeof(byte) || property.PropertyType == typeof(byte?))
                .Should().BeEmpty(
                    "{0} declares no byte-typed property, so the 255 marker has nowhere to land",
                    domainType.Name);
        }
    }

    // =================================================================================
    // User - the merge of the two legacy classes
    // =================================================================================

    /// <summary>The address is one property on the aggregate, not the legacy pair that could disagree.</summary>
    [Fact]
    public void Address_IsASinglePropertyRatherThanTheLegacyPairThatCouldDisagree()
    {
        User account = NewUser(UserKeySeed);
        account.Email = "one.answer@example.com";

        account.Email.Should().Be("one.answer@example.com");

        typeof(User)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.Name.Contains("Email", StringComparison.Ordinal))
            .Select(property => property.Name)
            .Should().ContainSingle().Which.Should().Be(
                nameof(User.Email),
                "a second address member is what let the legacy pair drift apart");
    }

    /// <summary>
    /// The given and family names are columns on the account row, not delegates to a lazily fetched
    /// profile.
    /// </summary>
    /// <remarks>
    /// The delegation was therefore pure cost: a database round trip for data already in hand. The target
    /// models the table, so both live here as plain non-nullable strings and the composed profile object
    /// that made the delegation possible does not exist.
    /// </remarks>
    [Fact]
    public void Names_AreColumnsOnTheAccountRowRatherThanProfileDelegates()
    {
        User account = NewUser(UserKeySeed);

        account.FirstName.Should().Be("Integration");
        account.LastName.Should().Be("Member");
        account.DisplayName.Should().Be("Integration Member");

        typeof(User).GetProperty(nameof(User.FirstName))!.PropertyType.Should().Be<string>();
        typeof(User).GetProperty(nameof(User.LastName))!.PropertyType.Should().Be<string>();
        typeof(User).GetProperty(nameof(User.DisplayName))!.PropertyType.Should().Be<string>();

        typeof(User).GetProperty(nameof(User.UserProfileValues))!.PropertyType.Should()
            .Be<ICollection<UserProfileValue>>(
                "profile answers are rows, reached explicitly, never a lazily hydrated wrapper");
    }

    /// <summary>
    /// The aggregate reproduces no legacy hydration flag, no composed lazily fetched object and no tenant
    /// identifier of its own.
    /// </summary>
    [Theory]
    [InlineData("ObjectHydrated")]
    [InlineData("IsDirty")]
    [InlineData("RolesHydrated")]
    [InlineData("Membership")]
    [InlineData("Profile")]
    [InlineData("Cacheability")]
    [InlineData("PortalID")]
    [InlineData("PortalId")]
    [InlineData("Roles")]
    [InlineData("Password")]
    public void Account_DeclaresNoneOfTheLegacyMembersThatDidNotSurvive(string memberName)
    {
        MembersTheAccountMustNotDeclare.Should().Contain(memberName);

        typeof(User).GetProperty(memberName).Should().BeNull(
            "the member is deliberately absent from the target aggregate");
        typeof(User).GetField(memberName).Should().BeNull();
    }

    /// <summary>The aggregate carries no serialisation, validation or user-interface attribute of any kind.</summary>
    /// <remarks>
    /// MIGRATION: the legacy class implemented a token-accessor interface and decorated its properties with
    /// the Web Forms property-editor and validation attributes - a browsable flag, a sort order, a required
    /// flag, a maximum length, a read-only flag and a regular-expression validator, at lines 87, 104,
    /// 121-123, 144, 161, 178, 195, 219, 236, 261, 284 and 301.
    /// </remarks>
    [Fact]
    public void Account_CarriesNoSerialisationValidationOrUserInterfaceAttribute()
    {
        AuthoredAttributeNames(typeof(User).GetCustomAttributesData()).Should().BeEmpty();

        foreach (PropertyInfo property in typeof(User)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            AuthoredAttributeNames(property.GetCustomAttributesData()).Should().BeEmpty(
                "{0} must carry no attribute of any kind", property.Name);
        }
    }

    /// <summary>A freshly constructed account exposes empty relationship collections rather than null ones.</summary>
    [Fact]
    public void NavigationCollections_AreInitialisedRatherThanNull()
    {
        User account = NewUser(UserKeySeed);

        account.UserPortals.Should().NotBeNull().And.BeEmpty();
        account.UserProfileValues.Should().NotBeNull().And.BeEmpty();
        account.UserRoles.Should().NotBeNull().And.BeEmpty();
        account.ModulePermissions.Should().NotBeNull().And.BeEmpty();
        account.TabPermissions.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// Host authority and the forced-credential-change flag are plain flags on the account row and both
    /// start clear.
    /// </summary>
    [Fact]
    public void AccountRowFlags_AreNonNullableAndStartClear()
    {
        User ordinary = NewUser(UserKeySeed);
        User host = NewUser(UserKeySeed + 1);
        host.IsSuperUser = true;

        ordinary.IsSuperUser.Should().BeFalse();
        ordinary.UpdatePassword.Should().BeFalse();
        host.IsSuperUser.Should().BeTrue();

        typeof(User).GetProperty(nameof(User.IsSuperUser))!.PropertyType.Should().Be<bool>(
            "host authority is a column on the account row, so it is never absent");
        typeof(User).GetProperty(nameof(User.UpdatePassword))!.PropertyType.Should().Be<bool>();
        typeof(User).GetProperty(nameof(User.IsApproved))!.PropertyType.Should().Be<bool?>(
            "approval is held in the external store, so absent is one of its three states");
        typeof(User).GetProperty(nameof(User.IsLockedOut))!.PropertyType.Should().Be<bool?>();
    }

    // =================================================================================
    // UserPortal - the per-tenant membership join
    // =================================================================================

    /// <summary>A membership is identified by its own surrogate key rather than by the pair it joins.</summary>
    /// <remarks>
    /// The table is keyed by the account and tenant pair but also carries an identity column, and the
    /// surrogate is what the entity reports. Reporting the pair instead would make one account's two
    /// tenancies indistinguishable in any identity-keyed collection.
    /// </remarks>
    [Fact]
    public void Membership_IsIdentifiedByItsSurrogateKeyRatherThanByThePairItJoins()
    {
        UserPortal first = NewMembership(userPortalId: 17, userId: UserKeySeed, portalId: PortalKeySeed);
        UserPortal second = NewMembership(userPortalId: 18, userId: UserKeySeed, portalId: PortalKeySeed);
        UserPortal sameRow = NewMembership(userPortalId: 17, userId: UserKeySeed, portalId: 0);

        first.MarkIdentityPersisted();
        second.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();

        first.Identity.Should().Be(17);
        first.Should().NotBe(
            second,
            "two tenancies of one account are two rows and must not collapse into one entity");
        first.Should().Be(sameRow);
    }

    /// <summary>A membership admits the member until something says otherwise.</summary>
    /// <remarks>
    /// This is the one place a non-sentinel legacy default survives as an initialiser, and it is the
    /// counterpart to the credential facts. The flag is a column on the membership row rather than a fact
    /// held in an external store, so there is no third state to express: the row either exists with an
    /// answer or does not exist at all.
    /// </remarks>
    [Fact]
    public void Membership_AdmitsTheMemberByDefault()
    {
        UserPortal membership = new() { UserPortalId = 1, UserId = UserKeySeed, PortalId = 0 };

        membership.IsAuthorised.Should().BeTrue(
            "the legacy column default admits the member, and the flag has no absent state to model");
        typeof(UserPortal).GetProperty(nameof(UserPortal.IsAuthorised))!.PropertyType.Should()
            .Be<bool>();
    }

    /// <summary>
    /// The membership carries the tenant facts, and its creation date is data rather than audit metadata.
    /// </summary>
    /// <remarks>
    /// The tenant identifier and the authorisation flag live here and nowhere else, which is what makes the
    /// account row single-valued across tenants.
    /// </remarks>
    [Fact]
    public void Membership_CarriesTheTenantFactsAndIsNotAudited()
    {
        DateTime joined = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        UserPortal membership = NewMembership(1, UserKeySeed, 0);
        membership.CreatedDate = joined;

        membership.UserId.Should().Be(UserKeySeed);
        membership.PortalId.Should().Be(0);
        membership.CreatedDate.Should().Be(joined);

        typeof(UserPortal).BaseType.Should().Be(typeof(Entity<int>));
        typeof(AuditableEntity<int>).IsAssignableFrom(typeof(UserPortal)).Should().BeFalse();
        typeof(UserPortal).GetProperty(nameof(UserPortal.CreatedDate))!.PropertyType.Should()
            .Be<DateTime>("the column is not nullable, so neither is the property");
        typeof(UserPortal).GetProperty("LastUpdatedDate").Should().BeNull();
    }

    // =================================================================================
    // UserProfileValue - the profile key/value row
    // =================================================================================

    /// <summary>A profile answer is one row keyed by the account and the definition it answers.</summary>
    [Fact]
    public void ProfileAnswer_IsKeyedByTheAccountAndTheDefinitionItAnswers()
    {
        UserProfileValue answer = NewProfileAnswer(profileId: 5, propertyDefinitionId: 9);

        answer.UserId.Should().Be(UserKeySeed);
        answer.PropertyDefinitionId.Should().Be(9);

        answer.MarkIdentityPersisted();
        answer.Identity.Should().Be(5, "the row carries its own surrogate key alongside the pair");

        typeof(UserProfileValue).GetProperty(nameof(UserProfileValue.UserId))!.PropertyType.Should()
            .Be<int>();
        typeof(UserProfileValue)
            .GetProperty(nameof(UserProfileValue.PropertyDefinitionId))!.PropertyType.Should().Be<int>();
    }

    /// <summary>
    /// A profile answer keeps the text it was given, including an empty answer, and keeps that apart from
    /// an unanswered question.
    /// </summary>
    /// <param name="stored">The value written to the row.</param>
    /// <param name="expected">The value the row must report back.</param>
    [Theory]
    [InlineData("", "")]
    [InlineData("Wellington", "Wellington")]
    [InlineData(null, null)]
    public void ProfileAnswer_KeepsAnEmptyAnswerApartFromAnUnansweredQuestion(
        string? stored,
        string? expected)
    {
        UserProfileValue answer = NewProfileAnswer(profileId: 5, propertyDefinitionId: 9);
        answer.PropertyValue = stored;
        answer.PropertyText = stored;

        answer.PropertyValue.Should().Be(expected);
        answer.PropertyText.Should().Be(expected);
    }

    /// <summary>
    /// Per-answer visibility is the raw stored number, and it defaults to the number the database itself
    /// defaults to.
    /// </summary>
    [Fact]
    public void ProfileAnswerVisibility_IsTheRawStoredNumberAndDefaultsToTheDatabaseDefault()
    {
        UserProfileValue answer = NewProfileAnswer(profileId: 5, propertyDefinitionId: 9);

        answer.Visibility.Should().Be(
            0,
            "the column defaults to 0 - all users - and rows created by the address migration rely on "
            + "that default, so the entity must not assign a different one");

        typeof(UserProfileValue).GetProperty(nameof(UserProfileValue.Visibility))!.PropertyType
            .Should().Be<int>("an enumeration would publish an incomplete mapping as a full contract");
    }

    /// <summary>
    /// Visibility accepts every number the column can hold, including the legacy marker and values no
    /// legacy member maps.
    /// </summary>
    /// <param name="storedNumber">The number written to the column.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ProfileAnswerVisibility_AcceptsEveryStoredNumberWithoutReinterpretation(int storedNumber)
    {
        UserProfileValue answer = NewProfileAnswer(profileId: 5, propertyDefinitionId: 9);
        answer.Visibility = storedNumber;

        answer.Visibility.Should().Be(
            storedNumber,
            "the raw number survives so the layer that presents it can decide what an unmapped value "
            + "means, rather than the domain silently rewriting it");
    }

    /// <summary>
    /// The domain declares no account-visibility enumeration, so nothing can accidentally narrow the stored
    /// number to three members.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy enumeration lived at <c>Library/Components/Users/UserVisibilityMode.vb</c>
    /// lines 23-26 with three explicitly valued members - all users 0, members only 1, administrators only
    /// 2.
    /// </remarks>
    [Fact]
    public void Domain_DeclaresNoAccountVisibilityEnumeration()
    {
        Assembly domainAssembly = typeof(UserProfileValue).Assembly;

        domainAssembly.GetType("DnnMigration.Domain.Enums.UserVisibilityMode").Should().BeNull(
            "the persisted column holds numbers no legacy member maps, so it stays an integer");

        domainAssembly.GetTypes()
            .Where(type => type.IsEnum
                && type.Name.Contains("Visibility", StringComparison.Ordinal))
            .Select(type => type.Name)
            .Should().BeEquivalentTo(
                [nameof(ModuleVisibility)],
                "module display state is the only visibility the domain enumerates, and it has nothing "
                + "to do with who may read a profile answer");
    }

    /// <summary>
    /// The last-written instant is a required column on the row, not audit metadata inherited from a base
    /// class.
    /// </summary>
    /// <remarks>
    /// This is the one in-scope table that carries a last-updated column at all, which is exactly why the
    /// audit base declares one - and it is still not applied here, because the column is mandatory while
    /// the base declares it nullable. Modelling it through the base would weaken a <c>NOT NULL</c> column
    /// into an optional one.
    /// </remarks>
    [Fact]
    public void ProfileAnswer_LastWrittenInstantIsARequiredColumnRatherThanAuditMetadata()
    {
        DateTime written = new(2026, 2, 3, 12, 0, 0, DateTimeKind.Utc);
        UserProfileValue answer = NewProfileAnswer(profileId: 5, propertyDefinitionId: 9);
        answer.LastUpdatedDate = written;

        answer.LastUpdatedDate.Should().Be(written);

        typeof(AuditableEntity<int>).IsAssignableFrom(typeof(UserProfileValue)).Should().BeFalse();
        typeof(UserProfileValue).GetProperty(nameof(UserProfileValue.LastUpdatedDate))!.PropertyType
            .Should().Be<DateTime>("the column is mandatory, and the audit base would make it optional");
    }

    // =================================================================================
    // ProfilePropertyDefinition - the profile question
    // =================================================================================

    /// <summary>An absent owner on a definition is null rather than the legacy numeric marker.</summary>
    [Fact]
    public void Definition_ExpressesAnAbsentOwnerAsNullRatherThanAsTheLegacyMarker()
    {
        ProfilePropertyDefinition hostLevel = NewDefinition(propertyDefinitionId: 3);
        hostLevel.PortalId = null;
        hostLevel.ModuleDefinitionId = null;

        hostLevel.PortalId.Should().BeNull("a definition shared across tenants has no owning tenant");
        hostLevel.ModuleDefinitionId.Should().BeNull();

        ProfilePropertyDefinition tenantLevel = NewDefinition(propertyDefinitionId: 4);
        tenantLevel.PortalId = PortalKeySeed;

        tenantLevel.PortalId.Should().Be(
            PortalKeySeed,
            "minus one is the first real tenant key, so it must round-trip as a value");

        typeof(ProfilePropertyDefinition)
            .GetProperty(nameof(ProfilePropertyDefinition.PortalId))!.PropertyType.Should().Be<int?>();
        typeof(ProfilePropertyDefinition)
            .GetProperty(nameof(ProfilePropertyDefinition.ModuleDefinitionId))!.PropertyType.Should()
            .Be<int?>();
        typeof(ProfilePropertyDefinition)
            .GetProperty(nameof(ProfilePropertyDefinition.DataType))!.PropertyType.Should().Be<int>();

        NewDefinition(propertyDefinitionId: 5).DataType.Should().Be(0);
    }

    /// <summary>A definition is constructed without reading anything ambient.</summary>
    [Fact]
    public void Definition_IsConstructedWithoutReadingAmbientRequestState()
    {
        ConstructorInfo[] constructors = typeof(ProfilePropertyDefinition).GetConstructors();

        constructors.Should().ContainSingle("only the implicit parameterless constructor may exist")
            .Which.GetParameters().Should().BeEmpty();

        ProfilePropertyDefinition definition = new();

        definition.PortalId.Should().BeNull(
            "a new definition belongs to no tenant until a caller that knows one assigns it");
        definition.Identity.Should().Be(0);
        definition.IdentityIsPersisted.Should().BeFalse();
        definition.ProfileValues.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// The definition stores only its own visibility flag, carries no per-account visibility and no
    /// user-interface metadata, and imposes no name format.
    /// </summary>
    /// <remarks>
    /// The name's anchored format rule, a regular-expression validator attribute at line 228 whose
    /// character class matches the local part of the address pattern, and the list-editor metadata on the
    /// data type at lines 88-90 are all dropped. The first is a validation rule and moves to a validator in
    /// the application layer; the second named a control from the excluded control library.
    /// </remarks>
    [Fact]
    public void Definition_StoresItsOwnVisibilityFlagOnlyAndImposesNoNameFormat()
    {
        ProfilePropertyDefinition definition = NewDefinition(propertyDefinitionId: 3);

        definition.IsVisible.Should().BeFalse("the flag is a plain column with no absent state");
        typeof(ProfilePropertyDefinition)
            .GetProperty(nameof(ProfilePropertyDefinition.IsVisible))!.PropertyType.Should().Be<bool>();
        typeof(ProfilePropertyDefinition).GetProperty("Visibility").Should().BeNull(
            "per-account visibility is stored per answer; no column of this table backs it");

        definition.PropertyName = "Not.A_Legal-Legacy+Name%But'Stored";
        definition.PropertyName.Should().Be(
            "Not.A_Legal-Legacy+Name%But'Stored",
            "format rules belong to a validator in the application layer, not to the domain");

        AuthoredAttributeNames(typeof(ProfilePropertyDefinition).GetCustomAttributesData()).Should()
            .BeEmpty("the legacy class carried an XML root attribute that does not survive");

        foreach (PropertyInfo property in typeof(ProfilePropertyDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            AuthoredAttributeNames(property.GetCustomAttributesData()).Should().BeEmpty(
                "{0} must carry no editor, validation or serialisation attribute", property.Name);
        }
    }

    // =================================================================================
    // Fixtures
    // =================================================================================

    /// <summary>Builds a minimally populated account carrying the supplied key.</summary>
    /// <param name="userId">The key to carry.</param>
    /// <param name="username">The login name.</param>
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

    /// <summary>Builds a membership row joining an account to a tenant.</summary>
    /// <param name="userPortalId">The surrogate key of the row.</param>
    /// <param name="userId">The account joined.</param>
    /// <param name="portalId">The tenant joined.</param>
    /// <returns>The membership.</returns>
    private static UserPortal NewMembership(int userPortalId, int userId, int portalId) => new()
    {
        UserPortalId = userPortalId,
        UserId = userId,
        PortalId = portalId,
    };

    /// <summary>Builds a profile answer for the account key used throughout these tests.</summary>
    /// <param name="profileId">The surrogate key of the row.</param>
    /// <param name="propertyDefinitionId">The question being answered.</param>
    /// <returns>The profile answer.</returns>
    private static UserProfileValue NewProfileAnswer(int profileId, int propertyDefinitionId) => new()
    {
        ProfileId = profileId,
        UserId = UserKeySeed,
        PropertyDefinitionId = propertyDefinitionId,
    };

    /// <summary>Builds a profile question.</summary>
    /// <param name="propertyDefinitionId">The surrogate key of the row.</param>
    /// <returns>The definition.</returns>
    private static ProfilePropertyDefinition NewDefinition(int propertyDefinitionId) => new()
    {
        PropertyDefinitionId = propertyDefinitionId,
        PropertyCategory = "Address",
        PropertyName = "City",
    };

    /// <summary>
    /// Names the attributes an author wrote, discarding those the compiler emits for nullable reference
    /// types and other language features.
    /// </summary>
    /// <param name="attributes">The attribute data to filter.</param>
    /// <returns>The authored attribute names.</returns>
    /// <remarks>
    /// Enabling nullable reference types makes the compiler stamp its own attributes onto types and
    /// members, and those are not the subject of any assertion here. They are excluded by an exact
    /// namespace comparison rather than by matching a prefix of the qualified name, so a hand-written
    /// attribute that merely happened to share a name prefix could not slip through unnoticed.
    /// </remarks>
    private static IEnumerable<string> AuthoredAttributeNames(IList<CustomAttributeData> attributes)
    {
        return attributes
            .Where(attribute =>
                !string.Equals(
                    attribute.AttributeType.Namespace,
                    "System.Runtime.CompilerServices",
                    StringComparison.Ordinal))
            .Select(attribute => attribute.AttributeType.Name);
    }
}
