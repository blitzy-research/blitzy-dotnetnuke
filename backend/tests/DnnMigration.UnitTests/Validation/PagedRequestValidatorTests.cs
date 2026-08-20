// This suite exists for one property: a listing must never accept a sort field that the collection it
// addresses then ignores.
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves that each listed collection admits exactly its own sortable vocabulary and refuses every other
/// collection's, and that the shared paging bounds are inherited unchanged by all four.
/// </summary>
public class PagedRequestValidatorTests
{
    /// <summary>Sortable vocabulary of the portal listing, honoured by <c>PortalRepository</c>.</summary>
    private static readonly string[] PortalFields =
        ["PortalId", "PortalName", "ExpiryDate", "HostFee", "HostSpace"];

    /// <summary>Sortable vocabulary of the role listing, honoured by <c>RoleService</c>.</summary>
    private static readonly string[] RoleFields =
    [
        "RoleId", "RoleName", "Description", "ServiceFee", "BillingFrequency", "BillingPeriod",
        "TrialFee", "TrialFrequency", "TrialPeriod", "IsPublic", "AutoAssignment",
    ];

    /// <summary>Sortable vocabulary of the member listing, honoured by <c>UserRepository</c>.</summary>
    private static readonly string[] UserFields =
        ["UserId", "Username", "FirstName", "LastName", "DisplayName", "Email", "IsSuperUser"];

    /// <summary>Sortable vocabulary of the module listing, honoured by <c>ModuleService</c>.</summary>
    private static readonly string[] ModuleFields =
        ["ModuleId", "ModuleTitle", "IsDeleted", "StartDate", "EndDate"];

    /// <summary>
    /// Sortable vocabulary of the role-membership listing, honoured by
    /// <c>RoleService.OrderRoleMemberships</c>.
    /// </summary>
    /// <remarks>
    /// Ten names, three more than the account listing's. The extra three are legitimate rather than
    /// accidental: this listing composes each assignment row with its account and pages IN MEMORY, so the
    /// values the external membership store supplies are present on every row before any page is cut,
    /// whereas the account listing pages in the STORE and cannot reach them until afterwards.
    /// </remarks>
    private static readonly string[] RoleUserFields =
    [
        "UserId", "Username", "FirstName", "LastName", "DisplayName", "Email", "IsSuperUser",
    ];

    // ------------------------------------------------------------------------
    // EACH COLLECTION ADMITS ITS OWN VOCABULARY
    // ------------------------------------------------------------------------

    /// <summary>Every name in the portal vocabulary is admitted by the portal listing.</summary>
    /// <param name="sortBy">The field the caller named.</param>
    [Theory]
    [MemberData(nameof(PortalFieldCases))]
    public void PortalListing_AdmitsEveryFieldItHonours(string sortBy)
        => ShouldAcceptSort(new PortalPagedRequestValidator(), new PortalPagedRequest(), sortBy);

    /// <summary>Every name in the role vocabulary is admitted by the role listing.</summary>
    /// <param name="sortBy">The field the caller named.</param>
    [Theory]
    [MemberData(nameof(RoleFieldCases))]
    public void RoleListing_AdmitsEveryFieldItHonours(string sortBy)
        => ShouldAcceptSort(new RolePagedRequestValidator(), new RolePagedRequest(), sortBy);

    /// <summary>Every name in the member vocabulary is admitted by the member listing.</summary>
    /// <param name="sortBy">The field the caller named.</param>
    [Theory]
    [MemberData(nameof(UserFieldCases))]
    public void UserListing_AdmitsEveryFieldItHonours(string sortBy)
        => ShouldAcceptSort(new UserPagedRequestValidator(), new UserPagedRequest(), sortBy);

    /// <summary>Every name in the module vocabulary is admitted by the module listing.</summary>
    /// <param name="sortBy">The field the caller named.</param>
    [Theory]
    [MemberData(nameof(ModuleFieldCases))]
    public void ModuleListing_AdmitsEveryFieldItHonours(string sortBy)
        => ShouldAcceptSort(new ModulePagedRequestValidator(), new ModulePagedRequest(), sortBy);

    /// <summary>Every name in the role-membership vocabulary is admitted by the role-membership listing.</summary>
    /// <param name="sortBy">The field the caller named.</param>
    [Theory]
    [MemberData(nameof(RoleUserFieldCases))]
    public void RoleMembershipListing_AdmitsEveryFieldItHonours(string sortBy)
        => ShouldAcceptSort(new RoleUserPagedRequestValidator(), new RoleUserPagedRequest(), sortBy);

    // ------------------------------------------------------------------------
    // AND REFUSES EVERY OTHER COLLECTION'S
    // ------------------------------------------------------------------------

    /// <summary>
    /// The portal listing refuses names that belong only to the role, member or module vocabularies.
    /// </summary>
    /// <param name="sortBy">The foreign field the caller named.</param>
    /// <remarks>
    /// Each of these was accepted before the repair, because the union was the only bound. The portal
    /// repository would then have ordered by the portal name regardless, so the caller received a page
    /// ordered by a field it had not asked for and no indication that its request had been ignored.
    /// </remarks>
    [Theory]
    [InlineData("RoleName")]
    [InlineData("TrialFrequency")]
    [InlineData("AutoAssignment")]
    [InlineData("Username")]
    [InlineData("DisplayName")]
    [InlineData("IsSuperUser")]
    [InlineData("ModuleTitle")]
    [InlineData("StartDate")]
    public void PortalListing_RefusesEveryForeignField(string sortBy)
        => ShouldRefuseSort(new PortalPagedRequestValidator(), new PortalPagedRequest(), sortBy, PortalFields);

    /// <summary>
    /// The role listing refuses names that belong only to the portal, member or module vocabularies.
    /// </summary>
    /// <param name="sortBy">The foreign field the caller named.</param>
    [Theory]
    [InlineData("PortalName")]
    [InlineData("HostFee")]
    [InlineData("HostSpace")]
    [InlineData("ExpiryDate")]
    [InlineData("Username")]
    [InlineData("Email")]
    [InlineData("ModuleTitle")]
    [InlineData("EndDate")]
    public void RoleListing_RefusesEveryForeignField(string sortBy)
        => ShouldRefuseSort(new RolePagedRequestValidator(), new RolePagedRequest(), sortBy, RoleFields);

    /// <summary>
    /// The member listing refuses names that belong only to the portal, role or module vocabularies.
    /// </summary>
    /// <param name="sortBy">The foreign field the caller named.</param>
    [Theory]
    [InlineData("PortalName")]
    [InlineData("HostFee")]
    [InlineData("RoleName")]
    [InlineData("ServiceFee")]
    [InlineData("IsPublic")]
    [InlineData("ModuleId")]
    [InlineData("IsDeleted")]
    public void UserListing_RefusesEveryForeignField(string sortBy)
        => ShouldRefuseSort(new UserPagedRequestValidator(), new UserPagedRequest(), sortBy, UserFields);

    /// <summary>
    /// The module listing refuses names that belong only to the portal, role or member vocabularies.
    /// </summary>
    /// <param name="sortBy">The foreign field the caller named.</param>
    [Theory]
    [InlineData("PortalName")]
    [InlineData("HostSpace")]
    [InlineData("RoleName")]
    [InlineData("TrialPeriod")]
    [InlineData("Username")]
    [InlineData("DisplayName")]
    public void ModuleListing_RefusesEveryForeignField(string sortBy)
        => ShouldRefuseSort(new ModulePagedRequestValidator(), new ModulePagedRequest(), sortBy, ModuleFields);

    /// <summary>
    /// The role-membership listing refuses names that belong only to the portal, role or module
    /// vocabularies, so widening its set to reach the three account fields did not widen it to everything.
    /// </summary>
    /// <param name="sortBy">The foreign field the caller named.</param>
    /// <remarks>
    /// <c>RoleName</c> is the pointed case and is asserted deliberately. The role is fixed by the route for
    /// every record on the page, so ordering by its name could not change any order, and admitting it would
    /// be advertising an ordering that does nothing - which is the shape of the defect this whole suite
    /// exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData("PortalName")]
    [InlineData("HostFee")]
    [InlineData("RoleId")]
    [InlineData("RoleName")]
    [InlineData("ServiceFee")]
    [InlineData("IsPublic")]
    [InlineData("ModuleTitle")]
    [InlineData("EffectiveDate")]
    [InlineData("ExpiryDate")]
    public void RoleMembershipListing_RefusesEveryForeignField(string sortBy)
        => ShouldRefuseSort(
            new RoleUserPagedRequestValidator(),
            new RoleUserPagedRequest(),
            sortBy,
            RoleUserFields);

    // ------------------------------------------------------------------------
    // THE VOCABULARIES ARE EXACT, NOT MERELY SUFFICIENT
    // ------------------------------------------------------------------------

    /// <summary>
    /// The refusal message enumerates exactly the collection's own vocabulary, in a stable ordinal order,
    /// so the accepted set is observable by a caller and pinned by this test.
    /// </summary>
    /// <remarks>
    /// This is the assertion that makes the four vocabularies EXACT rather than merely sufficient. Adding a
    /// name to a set without adding an ordering arm for it would change this message and fail here, which
    /// is the point: the sets and the ordering switches have to move together.
    /// </remarks>
    [Fact]
    public void EachListing_NamesExactlyItsOwnVocabularyWhenRefusing()
    {
        MessageFor(new PortalPagedRequestValidator(), new PortalPagedRequest())
            .Should().Be(ExpectedMessage(PortalFields));
        MessageFor(new RolePagedRequestValidator(), new RolePagedRequest())
            .Should().Be(ExpectedMessage(RoleFields));
        MessageFor(new UserPagedRequestValidator(), new UserPagedRequest())
            .Should().Be(ExpectedMessage(UserFields));
        MessageFor(new ModulePagedRequestValidator(), new ModulePagedRequest())
            .Should().Be(ExpectedMessage(ModuleFields));
        MessageFor(new RoleUserPagedRequestValidator(), new RoleUserPagedRequest())
            .Should().Be(ExpectedMessage(RoleUserFields));
    }

    /// <summary>
    /// The three member fields that cannot be ordered by the database are refused, even though each is a
    /// projected member of the listing's representation.
    /// </summary>
    /// <param name="sortBy">The field the caller named.</param>
    [Theory]
    [InlineData("CreatedDate")]
    [InlineData("LastLoginDate")]
    [InlineData("IsApproved")]
    public void UserListing_RefusesTheFieldsItCannotOrderInTheDatabase(string sortBy)
        => ShouldRefuseSort(new UserPagedRequestValidator(), new UserPagedRequest(), sortBy, UserFields);

    /// <summary>
    /// The role-membership listing refuses the same three membership-store fields the account listing
    /// refuses, because neither can order by them.
    /// </summary>
    /// <param name="sortBy">The field the caller named.</param>
    /// <remarks>
    /// ⚠ THIS TEST ASSERTED THE OPPOSITE, AND THE JUSTIFICATION IT CARRIED WAS FACTUALLY WRONG. It claimed
    /// the role-membership listing materialises the assignment rows and pages them IN MEMORY, so that the
    /// three values the external <c>aspnet_*</c> membership objects supply are present on every row before
    /// a page is cut.
    /// </remarks>
    [Theory]
    [InlineData("CreatedDate")]
    [InlineData("LastLoginDate")]
    [InlineData("IsApproved")]
    public void RoleMembershipListing_RefusesTheMembershipStoreFieldsItCannotOrder(string sortBy)
        => ShouldRefuseSort(
            new RoleUserPagedRequestValidator(),
            new RoleUserPagedRequest(),
            sortBy,
            RoleUserFields);

    /// <summary>The two module fields that cannot be ordered are refused, even though both are projected.</summary>
    /// <param name="sortBy">The field the caller named.</param>
    /// <remarks>
    /// The placement position is a column of a different table, is meaningful only within one pane of one
    /// page, and carries an append sentinel - so ordering a cross-page listing by it would sort unrelated
    /// positions against each other. The display title is derived at projection time and so does not exist
    /// until after the ordering has run.
    /// </remarks>
    [Theory]
    [InlineData("ModuleOrder")]
    [InlineData("DisplayTitle")]
    public void ModuleListing_RefusesTheFieldsItCannotOrder(string sortBy)
        => ShouldRefuseSort(new ModulePagedRequestValidator(), new ModulePagedRequest(), sortBy, ModuleFields);

    /// <summary>
    /// A name no collection declares is refused by every collection, which is the case that already worked
    /// and must keep working.
    /// </summary>
    /// <param name="sortBy">The invented field the caller named.</param>
    [Theory]
    [InlineData("Nonsense")]
    [InlineData("1; DROP TABLE Portals")]
    [InlineData("PortalName; DROP TABLE Portals")]
    public void EveryListing_RefusesAnUndeclaredField(string sortBy)
    {
        ShouldRefuseSort(new PortalPagedRequestValidator(), new PortalPagedRequest(), sortBy, PortalFields);
        ShouldRefuseSort(new RolePagedRequestValidator(), new RolePagedRequest(), sortBy, RoleFields);
        ShouldRefuseSort(new UserPagedRequestValidator(), new UserPagedRequest(), sortBy, UserFields);
        ShouldRefuseSort(new ModulePagedRequestValidator(), new ModulePagedRequest(), sortBy, ModuleFields);
        ShouldRefuseSort(
            new RoleUserPagedRequestValidator(),
            new RoleUserPagedRequest(),
            sortBy,
            RoleUserFields);
    }

    // ------------------------------------------------------------------------
    // THE BASE CONTRACT STILL APPLIES THE UNION
    // ------------------------------------------------------------------------

    /// <summary>
    /// A bare paging request still admits the union, so the outer bound is unchanged for a caller of the
    /// application layer that has not said which collection it addresses.
    /// </summary>
    /// <param name="sortBy">A field drawn from each of the four vocabularies in turn.</param>
    /// <remarks>
    /// The union is the correct bound for a request that names no collection: it still guarantees that what
    /// reaches an ordering clause is a name this assembly declared, while leaving the narrower question to
    /// the derived validators. No registered endpoint binds this type any longer.
    /// </remarks>
    [Theory]
    [InlineData("HostSpace")]
    [InlineData("TrialFrequency")]
    [InlineData("IsSuperUser")]
    [InlineData("EndDate")]
    public void BareRequest_StillAdmitsTheUnion(string sortBy)
        => ShouldAcceptSort(new PagedRequestValidator(), new PagedRequest(), sortBy);

    // ------------------------------------------------------------------------
    // THE DERIVATIONS ADD NOTHING BUT THEIR VOCABULARY
    // ------------------------------------------------------------------------

    /// <summary>
    /// Each derived request type declares no member of its own, so the four collections share one wire
    /// contract and differ only in which vocabulary their validator applies.
    /// </summary>
    [Fact]
    public void EachDerivedRequestType_DeclaresNoMemberOfItsOwn()
    {
        foreach (Type derived in new[]
        {
            typeof(PortalPagedRequest),
            typeof(RolePagedRequest),
            typeof(UserPagedRequest),
            typeof(ModulePagedRequest),
        })
        {
            derived.BaseType.Should().Be(
                typeof(PagedRequest),
                "{0} exists only to give the container a distinct type to resolve a validator for",
                derived.Name);

            derived.GetProperties(BindingFlagsDeclaredInstance).Should().BeEmpty(
                "{0} must add no member, so every listing shares one paging contract",
                derived.Name);

            derived.GetFields(BindingFlagsDeclaredInstance).Should().BeEmpty(
                "{0} must add no state", derived.Name);
        }
    }

    // ------------------------------------------------------------------------
    // THE SHARED BOUNDS ARE INHERITED UNCHANGED
    // ------------------------------------------------------------------------

    /// <summary>
    /// Every derived validator inherits the page-size ceiling, so narrowing the sort vocabulary did not
    /// disturb the paging bounds.
    /// </summary>
    [Fact]
    public void EveryDerivedValidator_InheritsThePagingBounds()
    {
        AssertPagingBounds(new PortalPagedRequestValidator(), () => new PortalPagedRequest());
        AssertPagingBounds(new RolePagedRequestValidator(), () => new RolePagedRequest());
        AssertPagingBounds(new UserPagedRequestValidator(), () => new UserPagedRequest());
        AssertPagingBounds(new ModulePagedRequestValidator(), () => new ModulePagedRequest());
        AssertPagingBounds(new RoleUserPagedRequestValidator(), () => new RoleUserPagedRequest());
    }

    /// <summary>
    /// An absent sort field is accepted by every validator, so a caller that expressed no preference is
    /// never asked to justify a field it did not name.
    /// </summary>
    /// <param name="sortBy">The absent, empty or blank value the caller supplied.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EveryListing_AcceptsAnAbsentSortField(string? sortBy)
    {
        new PortalPagedRequestValidator().Validate(new PortalPagedRequest { SortBy = sortBy })
            .IsValid.Should().BeTrue();
        new RolePagedRequestValidator().Validate(new RolePagedRequest { SortBy = sortBy })
            .IsValid.Should().BeTrue();
        new UserPagedRequestValidator().Validate(new UserPagedRequest { SortBy = sortBy })
            .IsValid.Should().BeTrue();
        new ModulePagedRequestValidator().Validate(new ModulePagedRequest { SortBy = sortBy })
            .IsValid.Should().BeTrue();
        new RoleUserPagedRequestValidator().Validate(new RoleUserPagedRequest { SortBy = sortBy })
            .IsValid.Should().BeTrue();
    }

    // ------------------------------------------------------------------------
    // THEORY DATA
    // ------------------------------------------------------------------------

    /// <summary>Supplies every portal sort field to a theory.</summary>
    /// <returns>One row per field.</returns>
    public static TheoryData<string> PortalFieldCases() => ToData(PortalFields);

    /// <summary>Supplies every role sort field to a theory.</summary>
    /// <returns>One row per field.</returns>
    public static TheoryData<string> RoleFieldCases() => ToData(RoleFields);

    /// <summary>Supplies every member sort field to a theory.</summary>
    /// <returns>One row per field.</returns>
    public static TheoryData<string> UserFieldCases() => ToData(UserFields);

    /// <summary>Supplies every module sort field to a theory.</summary>
    /// <returns>One row per field.</returns>
    public static TheoryData<string> ModuleFieldCases() => ToData(ModuleFields);

    /// <summary>Supplies every role-membership sort field to a theory.</summary>
    /// <returns>One row per field.</returns>
    public static TheoryData<string> RoleUserFieldCases() => ToData(RoleUserFields);

    // ------------------------------------------------------------------------
    // HELPERS
    // ------------------------------------------------------------------------

    /// <summary>Reflection flags selecting only the members a type declares itself.</summary>
    private const System.Reflection.BindingFlags BindingFlagsDeclaredInstance =
        System.Reflection.BindingFlags.Public
        | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Instance
        | System.Reflection.BindingFlags.DeclaredOnly;

    /// <summary>Wraps a field list as theory data.</summary>
    /// <param name="fields">The fields to supply.</param>
    /// <returns>One row per field.</returns>
    private static TheoryData<string> ToData(IEnumerable<string> fields)
    {
        TheoryData<string> data = [];
        foreach (string field in fields)
        {
            data.Add(field);
        }

        return data;
    }

    /// <summary>Composes the message a validator reports for a name outside its vocabulary.</summary>
    /// <param name="accepted">The vocabulary the validator applies.</param>
    /// <returns>The expected message, with the names in ordinal order.</returns>
    private static string ExpectedMessage(IEnumerable<string> accepted) =>
        "The sort field is not one that can be ordered by. Accepted fields: "
        + string.Join(", ", accepted.OrderBy(name => name, StringComparer.Ordinal))
        + ".";

    /// <summary>Reads the sort-field message one validator reports, using a name no collection declares.</summary>
    /// <typeparam name="TRequest">The request type the validator bounds.</typeparam>
    /// <param name="validator">The validator to interrogate.</param>
    /// <param name="request">A default request of that type.</param>
    /// <returns>The reported message.</returns>
    private static string MessageFor<TRequest>(IValidator<TRequest> validator, TRequest request)
        where TRequest : PagedRequest
    {
        request.SortBy = "AFieldNoCollectionDeclares";

        ValidationResult result = validator.Validate(request);

        return result.Errors
            .Single(failure => failure.PropertyName == nameof(PagedRequest.SortBy))
            .ErrorMessage;
    }

    /// <summary>Asserts that a validator admits a sort field.</summary>
    /// <typeparam name="TRequest">The request type the validator bounds.</typeparam>
    /// <param name="validator">The validator to exercise.</param>
    /// <param name="request">A default request of that type.</param>
    /// <param name="sortBy">The field the caller named.</param>
    private static void ShouldAcceptSort<TRequest>(
        IValidator<TRequest> validator,
        TRequest request,
        string? sortBy)
        where TRequest : PagedRequest
    {
        request.SortBy = sortBy;

        ValidationResult result = validator.Validate(request);

        result.Errors.Should().NotContain(
            failure => failure.PropertyName == nameof(PagedRequest.SortBy),
            "{0} names a field this listing honours end to end, so it must be admitted",
            sortBy ?? "<null>");
    }

    /// <summary>Asserts that a validator refuses a sort field and names its own vocabulary.</summary>
    /// <typeparam name="TRequest">The request type the validator bounds.</typeparam>
    /// <param name="validator">The validator to exercise.</param>
    /// <param name="request">A default request of that type.</param>
    /// <param name="sortBy">The field the caller named.</param>
    /// <param name="accepted">The vocabulary the validator must enumerate.</param>
    private static void ShouldRefuseSort<TRequest>(
        IValidator<TRequest> validator,
        TRequest request,
        string sortBy,
        IEnumerable<string> accepted)
        where TRequest : PagedRequest
    {
        request.SortBy = sortBy;

        ValidationResult result = validator.Validate(request);

        result.Errors.Should().Contain(
            failure => failure.PropertyName == nameof(PagedRequest.SortBy)
                && failure.ErrorMessage == ExpectedMessage(accepted),
            "\"{0}\" is not a field this listing orders by, so admitting it would mean answering 200 "
            + "while discarding the caller's request",
            sortBy);
    }

    /// <summary>Asserts that one validator applies the shared paging bounds.</summary>
    /// <typeparam name="TRequest">The request type the validator bounds.</typeparam>
    /// <param name="validator">The validator to exercise.</param>
    /// <param name="factory">Creates a fresh default request of that type.</param>
    private static void AssertPagingBounds<TRequest>(
        IValidator<TRequest> validator,
        Func<TRequest> factory)
        where TRequest : PagedRequest
    {
        TRequest negativeIndex = factory();
        negativeIndex.PageIndex = -1;
        validator.Validate(negativeIndex).Errors
            .Should().Contain(failure => failure.PropertyName == nameof(PagedRequest.PageIndex));

        TRequest zeroSize = factory();
        zeroSize.PageSize = 0;
        validator.Validate(zeroSize).Errors
            .Should().Contain(failure => failure.PropertyName == nameof(PagedRequest.PageSize));

        TRequest hugeSize = factory();
        hugeSize.PageSize = 100_000;
        validator.Validate(hugeSize).Errors
            .Should().Contain(failure => failure.PropertyName == nameof(PagedRequest.PageSize));

        TRequest undeclaredDirection = factory();
        undeclaredDirection.SortDir = (SortDirection)99;
        validator.Validate(undeclaredDirection).Errors
            .Should().Contain(failure => failure.PropertyName == nameof(PagedRequest.SortDir));

        validator.Validate(factory()).IsValid.Should().BeTrue(
            "a default request must be valid, so every bound above is attributable to the one member "
            + "the case altered");
    }
}
