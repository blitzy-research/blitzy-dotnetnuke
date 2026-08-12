using System.Text;
using System.Text.Json;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Serialization;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Serialization;

/// <summary>
/// Covers the JSON wire form of <see cref="SortDirection"/>, which exists so that the one contract member
/// bound from BOTH a query string and a request body has one vocabulary rather than two.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The defect these assertions pin.</strong> <c>PagedRequest.SortDir</c> is bound from the query
/// string on every collection endpoint and from a JSON body on <c>POST /api/v1/users/search</c> — the
/// compensating address an identifying account search uses so that personal data does not travel in a
/// request target. The query binder resolves an enumeration through its type converter and accepts the
/// member NAME; <c>System.Text.Json</c>, with no converter registered, accepted only the numeric
/// discriminator. Measured against the running API before the converter existed, a body carrying
/// <c>"sortDir":"Ascending"</c> was answered <c>400</c> with
/// <c>"$.sortDir": ["The JSON value could not be converted to …SortDirection."]</c> while the same search
/// carrying <c>"sortDir":0</c> was answered <c>200</c>. Both sides compiled and no test sent the member in
/// a body, so nothing detected it.
/// </para>
/// <para>
/// <strong>The accepted set is the query binder's set, deliberately.</strong> Measured on the same API,
/// <c>?sortDir=</c> accepts <c>Ascending</c>, <c>ascending</c> and <c>1</c>, and refuses <c>asc</c>. The
/// cases below assert exactly that surface plus a JSON number, so a body can express what a query string
/// could and nothing more. The number is admitted so the change stays purely additive for a caller written
/// against the previous behaviour.
/// </para>
/// <para>
/// <strong>An undeclared integer is carried, not refused, and that is asserted rather than assumed.</strong>
/// <c>?sortDir=5</c> binds and is then reported by <c>PagedRequestValidator</c>'s <c>IsInEnum</c> rule as a
/// field-level RFC 7807 failure. Refusing 5 in the converter would answer the same status with a
/// serialiser's wording, so the two transports would explain one mistake in two ways. Membership is checked
/// in exactly one place and this suite proves the converter does not check it a second time.
/// </para>
/// </remarks>
public sealed class SortDirectionJsonConverterTests
{
    /// <summary>The two declared members with the name each is written as.</summary>
    private static readonly (SortDirection Member, string Name)[] DeclaredNames =
    [
        (SortDirection.Ascending, "Ascending"),
        (SortDirection.Descending, "Descending"),
    ];

    /// <summary>The converter is part of the central policy, so nothing has to remember to add it.</summary>
    /// <remarks>
    /// The policy object is what the Api composition root, the problem-details output and the
    /// integration-test client all apply, so membership here is what makes the wire form uniform across
    /// every surface. A converter that existed but was unregistered would leave the original defect intact.
    /// </remarks>
    [Fact]
    public void ThePolicy_RegistersTheConverter()
    {
        DnnJsonConverters.All.Should().ContainSingle(converter => converter is SortDirectionJsonConverter);

        var options = new JsonSerializerOptions();
        DnnJsonConverters.AddTo(options);

        options.Converters.Should().ContainSingle(converter => converter is SortDirectionJsonConverter);
    }

    /// <summary>Each declared member is written as its own name, never as a number.</summary>
    [Fact]
    public void Write_EmitsTheMemberName()
    {
        foreach ((SortDirection member, string name) in DeclaredNames)
        {
            Serialise(member).Should().Be($"\"{name}\"");
        }

        Enum.GetValues<SortDirection>().Should().HaveCount(
            DeclaredNames.Length,
            "the vocabulary is closed at two members; a third would need a review of the validator's message too");
    }

    /// <summary>An undeclared value cannot be written, because emitting its number is the defect.</summary>
    [Fact]
    public void Write_RefusesAnUndeclaredValue()
    {
        Action writing = () => Serialise((SortDirection)7);

        writing.Should().Throw<JsonException>().WithMessage("*not one this contract declares*");
    }

    /// <summary>The member name resolves, in the casing the query string documents and in any other.</summary>
    /// <param name="json">The JSON value to read.</param>
    /// <param name="expected">The direction it must resolve to.</param>
    /// <remarks>
    /// Case-insensitivity is not a courtesy: the query binder parses case-insensitively, and a body that
    /// refused <c>"ascending"</c> while a query string accepted it would be a second divergence of the same
    /// kind the converter exists to remove.
    /// </remarks>
    [Theory]
    [InlineData("\"Ascending\"", SortDirection.Ascending)]
    [InlineData("\"Descending\"", SortDirection.Descending)]
    [InlineData("\"ascending\"", SortDirection.Ascending)]
    [InlineData("\"DESCENDING\"", SortDirection.Descending)]
    public void Read_ResolvesTheMemberName(string json, SortDirection expected) =>
        Deserialise(json).Should().Be(expected);

    /// <summary>Numeric text resolves, because the query string's type converter accepts it.</summary>
    /// <param name="json">The JSON value to read.</param>
    /// <param name="expected">The direction it must resolve to.</param>
    [Theory]
    [InlineData("\"0\"", SortDirection.Ascending)]
    [InlineData("\"1\"", SortDirection.Descending)]
    public void Read_ResolvesNumericText(string json, SortDirection expected) =>
        Deserialise(json).Should().Be(expected);

    /// <summary>A JSON number resolves, which is what keeps the change additive.</summary>
    /// <param name="json">The JSON value to read.</param>
    /// <param name="expected">The direction it must resolve to.</param>
    [Theory]
    [InlineData("0", SortDirection.Ascending)]
    [InlineData("1", SortDirection.Descending)]
    public void Read_ResolvesTheDiscriminator(string json, SortDirection expected) =>
        Deserialise(json).Should().Be(expected);

    /// <summary>An undeclared integer is carried through for the request validator to report.</summary>
    /// <param name="json">The JSON value to read.</param>
    /// <remarks>
    /// Both spellings are asserted because both transports can produce one: a query string sends the digit
    /// as text and a body can send it as a number.
    /// </remarks>
    [Theory]
    [InlineData("5")]
    [InlineData("\"5\"")]
    public void Read_CarriesAnUndeclaredIntegerRatherThanRefusingIt(string json)
    {
        SortDirection carried = Deserialise(json);

        ((int)carried).Should().Be(5);
        Enum.IsDefined(carried).Should().BeFalse(
            "the validator's IsInEnum rule is the one place membership is decided");
    }

    /// <summary>A value that is neither a name nor an integer is refused, exactly as the binder refuses it.</summary>
    /// <param name="json">The JSON value to read.</param>
    [Theory]
    [InlineData("\"asc\"")]
    [InlineData("\"desc\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Read_RefusesAValueThatNamesNoDirection(string json)
    {
        Action reading = () => Deserialise(json);

        reading.Should().Throw<JsonException>();
    }

    /// <summary>A fractional number is refused rather than truncated to a neighbouring direction.</summary>
    [Fact]
    public void Read_RefusesAFractionalNumber()
    {
        Action reading = () => Deserialise("1.5");

        reading.Should().Throw<JsonException>().WithMessage("*32-bit integer*");
    }

    /// <summary>
    /// The member round-trips inside the request contract the account search actually binds, through the
    /// policy rather than through the converter directly.
    /// </summary>
    /// <remarks>
    /// This is the assertion closest to the reproduced defect: the failure was not in the enumeration but in
    /// binding a BODY that declares it, so the contract is deserialised whole here with the same policy the
    /// Api applies.
    /// </remarks>
    [Fact]
    public void ThePagedContract_BindsTheMemberNameFromABody()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        DnnJsonConverters.AddTo(options);

        PagedRequest? bound = JsonSerializer.Deserialize<PagedRequest>(
            """{"pageIndex":0,"pageSize":10,"sortBy":"username","sortDir":"Descending"}""",
            options);

        bound.Should().NotBeNull();
        bound!.SortDir.Should().Be(SortDirection.Descending);
        bound.SortBy.Should().Be("username");

        JsonSerializer.Serialize(bound, options).Should().Contain("\"sortDir\":\"Descending\"");
    }

    /// <summary>Serialises one value through the production converter.</summary>
    /// <param name="value">The value to write.</param>
    /// <returns>The JSON the converter produced.</returns>
    private static string Serialise(SortDirection value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            new SortDirectionJsonConverter().Write(writer, value, new JsonSerializerOptions());
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Deserialises one JSON value through the production converter.</summary>
    /// <param name="json">The JSON to read.</param>
    /// <returns>The value the converter resolved.</returns>
    private static SortDirection Deserialise(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();

        return new SortDirectionJsonConverter().Read(
            ref reader,
            typeof(SortDirection),
            new JsonSerializerOptions());
    }
}
