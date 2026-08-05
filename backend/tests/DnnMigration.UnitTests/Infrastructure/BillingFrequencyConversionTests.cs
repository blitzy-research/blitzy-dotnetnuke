using System.Text;
using System.Text.Json;
using DnnMigration.Application.Serialization;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Infrastructure;

/// <summary>
/// Covers the two halves of the legacy billing-frequency conversion — the persistence half that reads and
/// writes the <c>char(1)</c> column, and the wire half that reads and writes the JSON code.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the two halves are tested together.</strong> They are separate types in separate assemblies
/// that happen to agree on the same vocabulary, and the defect they were both changed to resolve was a
/// consequence of that split: the persistence half normalised an unrecognised stored character to
/// <see cref="BillingFrequency.None"/> so that it would never reach the wire half, which refused to write
/// anything undeclared. Reading fed writing, so an edit to a role's description rewrote a stored <c>'4'</c>
/// as <c>'N'</c> and destroyed data AAP Rule T4 makes authoritative. Asserting one half in isolation cannot
/// express that, because each was internally consistent.
/// </para>
/// <para>
/// <strong>The vocabulary is closed where a caller can widen it and open where the database can.</strong>
/// Both halves now carry an undeclared character rather than substituting for it, and the closed vocabulary
/// is enforced on inbound REQUESTS by the <c>IsInEnum</c> rules on <c>CreateRoleRequest</c> and
/// <c>UpdateRoleRequest</c>, which produce a field-level RFC 7807 failure rather than a bare
/// deserialisation error. The characters exercised here are the ones every DotNetNuke installation ships:
/// <c>'4'</c> on the Administrators role and <c>'0'</c> on the Registered Users role
/// (<c>01.00.00.SqlDataProvider</c> L7192 and L7194).
/// </para>
/// <para>
/// The persistence converter is <c>internal</c> to the Infrastructure assembly, so it is exercised here
/// through the public JSON half plus the enumeration's own arithmetic, which is what the persistence half is
/// built from: each member's value IS its code point, and that identity is the whole reason a lossless
/// conversion is possible. The persistence half's behaviour against a real column is proven by the
/// integration suites, which write and re-read the stored bytes.
/// </para>
/// </remarks>
public sealed class BillingFrequencyConversionTests
{
    /// <summary>The six codes the legacy application declared, in declaration order.</summary>
    private static readonly (BillingFrequency Member, char Code)[] DeclaredCodes =
    [
        (BillingFrequency.None, 'N'),
        (BillingFrequency.OneTime, 'O'),
        (BillingFrequency.Day, 'D'),
        (BillingFrequency.Week, 'W'),
        (BillingFrequency.Month, 'M'),
        (BillingFrequency.Year, 'Y'),
    ];

    /// <summary>Every declared member's value is the code point of its own legacy character.</summary>
    /// <remarks>
    /// This is the fact everything else here depends on. If a member were ever given an ordinal instead of
    /// its code point, a lossless conversion would become impossible and both halves would silently begin
    /// storing and emitting the wrong byte.
    /// </remarks>
    [Fact]
    public void EveryDeclaredMember_CarriesTheCodePointOfItsOwnCharacter()
    {
        foreach ((BillingFrequency member, char code) in DeclaredCodes)
        {
            ((char)member).Should().Be(code);
            ((ushort)member).Should().Be(code);
        }

        Enum.GetValues<BillingFrequency>().Should().HaveCount(
            DeclaredCodes.Length,
            "the vocabulary is closed at six members; a seventh would need a code and a review of both halves");
    }

    /// <summary>An undeclared character is representable exactly, which is what makes the read lossless.</summary>
    /// <param name="code">A character an installation is known to store.</param>
    [Theory]
    [InlineData('4')]
    [InlineData('0')]
    [InlineData('m')]
    [InlineData('Z')]
    public void AnUndeclaredCharacter_RoundTripsThroughTheEnumerationExactly(char code)
    {
        var carried = (BillingFrequency)code;

        Enum.IsDefined(carried).Should().BeFalse("the character is deliberately outside the vocabulary");
        ((char)carried).Should().Be(code, "the cast back is the whole of the write direction");
    }

    /// <summary>Each declared member is written as its one-character code.</summary>
    /// <param name="member">The member being written.</param>
    /// <param name="code">The character it must produce.</param>
    [Theory]
    [MemberData(nameof(DeclaredCodeCases))]
    public void Write_EmitsTheDeclaredCode(BillingFrequency member, char code)
    {
        Serialise(member).Should().Be(FormattableString.Invariant($"\"{code}\""));
    }

    /// <summary>
    /// A value carrying an undeclared character is written as that character rather than refused.
    /// </summary>
    /// <param name="code">The character the value carries.</param>
    /// <remarks>
    /// The outbound direction has to tolerate this or a single legacy row would fail the response that
    /// contains it — and, worse, the persistence half would have to keep normalising to avoid the failure,
    /// which is what destroyed the stored byte.
    /// </remarks>
    [Theory]
    [InlineData('4')]
    [InlineData('0')]
    [InlineData('Z')]
    public void Write_EmitsAnUndeclaredCharacterRatherThanRefusingIt(char code)
    {
        Serialise((BillingFrequency)code).Should().Be(FormattableString.Invariant($"\"{code}\""));
    }

    /// <summary>Each declared code resolves to its member, in either case.</summary>
    /// <param name="member">The member the code names.</param>
    /// <param name="code">The character to read.</param>
    [Theory]
    [MemberData(nameof(DeclaredCodeCases))]
    public void Read_ResolvesEachDeclaredCode(BillingFrequency member, char code)
    {
        Deserialise(FormattableString.Invariant($"\"{code}\"")).Should().Be(member);
        Deserialise(FormattableString.Invariant($"\"{char.ToLowerInvariant(code)}\"")).Should().Be(
            member,
            "an inbound code is upper-cased before resolution, unlike a stored one");
    }

    /// <summary>
    /// An undeclared inbound character is carried rather than refused, so the API can read back what it
    /// wrote.
    /// </summary>
    /// <param name="code">The character to read.</param>
    /// <remarks>
    /// The same contract types and the same converter serve both directions, so a converter that emitted
    /// <c>"4"</c> and refused to read it would leave the API unable to deserialise its own output. The
    /// vocabulary a caller may submit is constrained by the role request validators instead, which report a
    /// field-level failure rather than a bare deserialisation error.
    /// </remarks>
    [Theory]
    [InlineData('4')]
    [InlineData('0')]
    [InlineData('Z')]
    public void Read_CarriesAnUndeclaredCharacterSoTheContractRoundTrips(char code)
    {
        BillingFrequency carried = Deserialise(FormattableString.Invariant($"\"{code}\""));

        carried.Should().Be((BillingFrequency)code);
        Serialise(carried).Should().Be(
            FormattableString.Invariant($"\"{code}\""),
            "the round trip through the wire contract is byte-for-byte");
    }

    /// <summary>A value of the wrong SHAPE is still refused, because no code can be resolved from it.</summary>
    /// <param name="json">A document whose frequency value cannot name a code at all.</param>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"Monthly\"")]
    [InlineData("\"MM\"")]
    [InlineData("77")]
    [InlineData("true")]
    public void Read_RefusesAValueThatCannotNameACode(string json)
    {
        Action reading = () => Deserialise(json);

        reading.Should().Throw<JsonException>(
            "shape is this converter's concern even though vocabulary is the validators'");
    }

    /// <summary>The declared members paired with their characters, as theory data.</summary>
    /// <returns>One row per declared member.</returns>
    public static TheoryData<BillingFrequency, char> DeclaredCodeCases()
    {
        var data = new TheoryData<BillingFrequency, char>();

        foreach ((BillingFrequency member, char code) in DeclaredCodes)
        {
            data.Add(member, code);
        }

        return data;
    }

    /// <summary>Serialises one value through the production converter.</summary>
    /// <param name="value">The value to write.</param>
    /// <returns>The JSON the converter produced.</returns>
    private static string Serialise(BillingFrequency value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            new BillingFrequencyJsonConverter().Write(writer, value, new JsonSerializerOptions());
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Deserialises one JSON value through the production converter.</summary>
    /// <param name="json">The JSON to read.</param>
    /// <returns>The value the converter resolved.</returns>
    private static BillingFrequency Deserialise(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();

        return new BillingFrequencyJsonConverter().Read(
            ref reader,
            typeof(BillingFrequency),
            new JsonSerializerOptions());
    }
}
