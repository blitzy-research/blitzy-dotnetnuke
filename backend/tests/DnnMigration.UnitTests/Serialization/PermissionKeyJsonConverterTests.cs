using System.Text;
using System.Text.Json;
using DnnMigration.Application.Serialization;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Serialization;

/// <summary>
/// Covers the JSON wire form of <see cref="PermissionKey"/>, which exists so that the wire carries the
/// member NAME rather than an incidental ordinal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a converter with no current caller still needs a test.</strong> No response contract carries
/// this enumeration today - the permission endpoints publish plain strings - and the converter is registered
/// anyway, so that the first contract to carry the type is already correct. That is the right decision and it
/// is also exactly what makes the converter dangerous to leave unasserted: nothing exercises it, so a
/// regression in it is invisible until the day a contract starts using it, at which point the wire form is
/// wrong in production rather than in a test. The failure mode is silent by construction, because the
/// incorrect form - a bare number - is still valid JSON and still deserialises.
/// </para>
/// <para>
/// <strong>What is actually being protected.</strong> <c>Permission.PermissionKey</c> is a
/// <c>varchar</c> column: no number for this concept is stored anywhere, so no member carries an explicit
/// value and the implicit ordinals are artefacts of declaration order. Left to the default treatment those
/// artefacts would go on the wire, and <c>0</c> would mean <c>VIEW</c> only until somebody reordered the
/// members - which is otherwise a harmless edit. Every fact below is anchored to a NAME for that reason, and
/// the ordinal-independence is asserted directly rather than implied.
/// </para>
/// <para>
/// The converter is exercised through <see cref="JsonSerializer"/> rather than by calling
/// <c>Read</c> and <c>Write</c> directly, because the registration and the reader-state handling are part of
/// what has to work: a converter that mishandled the reader would pass a direct call and fail a real
/// deserialisation.
/// </para>
/// </remarks>
public class PermissionKeyJsonConverterTests
{
    /// <summary>Options carrying the converter, exactly as the application registers it.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new PermissionKeyJsonConverter() },
    };

    /// <summary>Every member is written as its canonical upper-case name.</summary>
    /// <remarks>
    /// A theory over the members rather than four facts, so that a member added later is written and refused
    /// deliberately rather than silently acquiring the default numeric form.
    /// </remarks>
    [Theory]
    [InlineData(PermissionKey.VIEW, "\"VIEW\"")]
    [InlineData(PermissionKey.EDIT, "\"EDIT\"")]
    [InlineData(PermissionKey.READ, "\"READ\"")]
    [InlineData(PermissionKey.WRITE, "\"WRITE\"")]
    public void Write_EmitsTheCanonicalName(PermissionKey key, string expected)
    {
        JsonSerializer.Serialize(key, Options).Should().Be(
            expected,
            "the name is the contract because the column is textual, and the ordinal is an artefact of "
            + "declaration order");
    }

    /// <summary>No member is ever written as a number.</summary>
    /// <remarks>
    /// Asserted across every member at once, because this is the single property the converter exists for and
    /// it must hold for the whole enumeration rather than for the four values that happen to be listed above.
    /// </remarks>
    [Fact]
    public void Write_NeverEmitsAnOrdinal()
    {
        foreach (PermissionKey key in Enum.GetValues<PermissionKey>())
        {
            string written = JsonSerializer.Serialize(key, Options);

            written.Should().StartWith("\"").And.EndWith("\"");
            written.Should().NotBe(
                ((int)key).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "an ordinal on the wire would mean 0 stands for VIEW only until somebody reordered the "
                + "members, which is otherwise a harmless edit");
        }
    }

    /// <summary>Every canonical name is read back as its member.</summary>
    [Theory]
    [InlineData("\"VIEW\"", PermissionKey.VIEW)]
    [InlineData("\"EDIT\"", PermissionKey.EDIT)]
    [InlineData("\"READ\"", PermissionKey.READ)]
    [InlineData("\"WRITE\"", PermissionKey.WRITE)]
    public void Read_ResolvesTheCanonicalName(string json, PermissionKey expected)
    {
        JsonSerializer.Deserialize<PermissionKey>(json, Options).Should().Be(expected);
    }

    /// <summary>Every member round-trips through the wire form unchanged.</summary>
    /// <remarks>
    /// The round trip is the property a client depends on: it must be able to send back a value it was just
    /// given. A write half without a matching read half would make that impossible while both halves looked
    /// individually correct.
    /// </remarks>
    [Fact]
    public void EveryMember_RoundTrips()
    {
        foreach (PermissionKey key in Enum.GetValues<PermissionKey>())
        {
            string written = JsonSerializer.Serialize(key, Options);

            JsonSerializer.Deserialize<PermissionKey>(written, Options).Should().Be(key);
        }
    }

    /// <summary>Case is parsed rather than substituted.</summary>
    /// <remarks>
    /// Leniency on the way in is the framework's own posture for string-valued enumerations and is safe here
    /// because no two members differ only by case. What is resolved is the MEMBER, so nothing differently
    /// cased can reach the database - which the write assertion below is what establishes.
    /// </remarks>
    [Theory]
    [InlineData("\"view\"")]
    [InlineData("\"View\"")]
    [InlineData("\"vIeW\"")]
    public void Read_AcceptsAnyCasingAndCanonicalisesIt(string json)
    {
        PermissionKey parsed = JsonSerializer.Deserialize<PermissionKey>(json, Options);

        parsed.Should().Be(PermissionKey.VIEW);
        JsonSerializer.Serialize(parsed, Options).Should().Be(
            "\"VIEW\"",
            "leniency applies to reading only: what is written is always the canonical name the column holds");
    }

    /// <summary>A number is refused, and the refusal names the accepted vocabulary.</summary>
    /// <remarks>
    /// This is the specific mistake a client makes after reading a response that was serialised WITHOUT this
    /// converter registered, and accepting it would reintroduce exactly the ordinal dependence the enumeration
    /// forbids. It is also why the framework's own parse helper is not used: that helper accepts numeric text,
    /// so <c>"3"</c> would quietly resolve to a member.
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData("-1")]
    public void Read_RefusesANumber(string json)
    {
        Action read = () => JsonSerializer.Deserialize<PermissionKey>(json, Options);

        read.Should().Throw<JsonException>()
            .WithMessage("*not a number*");
    }

    /// <summary>Numeric TEXT is refused too.</summary>
    /// <remarks>
    /// The distinct and more dangerous case: a JSON string containing a number satisfies the string check and
    /// would be accepted by the framework's parse helper, so it is the value that would slip through an
    /// implementation written the obvious way. It must be refused as an unrecognised name.
    /// </remarks>
    [Theory]
    [InlineData("\"0\"")]
    [InlineData("\"3\"")]
    public void Read_RefusesNumericText(string json)
    {
        Action read = () => JsonSerializer.Deserialize<PermissionKey>(json, Options);

        read.Should().Throw<JsonException>()
            .WithMessage("*not one this contract recognises*");
    }

    /// <summary>A comma-separated list is refused: these keys are not flags.</summary>
    /// <remarks>
    /// The framework's parse helper accepts a list and combines the members, which for a non-flags enumeration
    /// produces a value that names no member at all - and the write half would then refuse to serialise it,
    /// turning a bad request into a server fault.
    /// </remarks>
    [Theory]
    [InlineData("\"VIEW,EDIT\"")]
    [InlineData("\"VIEW, EDIT\"")]
    public void Read_RefusesACombinedList(string json)
    {
        Action read = () => JsonSerializer.Deserialize<PermissionKey>(json, Options);

        read.Should().Throw<JsonException>()
            .WithMessage("*not one this contract recognises*");
    }

    /// <summary>An empty string is refused as empty rather than as unrecognised.</summary>
    /// <remarks>
    /// Distinguished from an unrecognised name deliberately: an empty value is a caller that sent no key,
    /// whereas an unrecognised one is a caller that sent the wrong key, and the two are fixed differently.
    /// </remarks>
    [Fact]
    public void Read_RefusesAnEmptyString()
    {
        Action read = () => JsonSerializer.Deserialize<PermissionKey>("\"\"", Options);

        read.Should().Throw<JsonException>()
            .WithMessage("*may not be empty*");
    }

    /// <summary>A key belonging to a later DotNetNuke generation is refused.</summary>
    /// <remarks>
    /// These four are the exhaustive set for this generation. The later keys are the ones a developer familiar
    /// with a newer DotNetNuke would reach for, so refusing them by name - with the accepted set reported back -
    /// is the difference between a clear rejection and a silent mis-authorisation.
    /// </remarks>
    [Theory]
    [InlineData("\"DEPLOY\"")]
    [InlineData("\"ADD\"")]
    [InlineData("\"DELETE\"")]
    [InlineData("\"MANAGE\"")]
    [InlineData("\"FULLCONTROL\"")]
    [InlineData("\"VIEWS\"")]
    [InlineData("\" VIEW\"")]
    public void Read_RefusesAKeyOutsideTheClosedVocabulary(string json)
    {
        Action read = () => JsonSerializer.Deserialize<PermissionKey>(json, Options);

        read.Should().Throw<JsonException>()
            .WithMessage("*not one this contract recognises*");
    }

    /// <summary>The refusal names the accepted vocabulary and echoes nothing the caller sent.</summary>
    /// <remarks>
    /// Naming the four accepted values is how a caller learns the vocabulary from the refusal. Echoing the
    /// supplied value back is what a refusal must not do: this message travels to the structured log through
    /// the problem-details edge, and a value a caller chose is a value a caller can use to inject content
    /// into it.
    /// </remarks>
    [Fact]
    public void Read_NamesTheAcceptedVocabularyWithoutEchoingTheSuppliedValue()
    {
        const string supplied = "SUPER-SECRET-VALUE";

        Action read = () => JsonSerializer.Deserialize<PermissionKey>(
            "\"" + supplied + "\"",
            Options);

        string message = read.Should().Throw<JsonException>().Which.Message;

        foreach (string accepted in new[] { "VIEW", "EDIT", "READ", "WRITE" })
        {
            message.Should().Contain(accepted, "a caller learns the vocabulary from the refusal");
        }

        message.Should().NotContain(
            supplied,
            "a value the caller chose must not be reflected into a message that reaches the log");
    }

    /// <summary>A non-string token is refused as a shape fault rather than parsed.</summary>
    /// <remarks>
    /// Each of these reaches the converter with a different token type, and an implementation that only
    /// guarded against numbers would fault with an unrelated reader exception instead of reporting the shape.
    /// </remarks>
    [Theory]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Read_RefusesANonStringToken(string json)
    {
        Action read = () => JsonSerializer.Deserialize<PermissionKey>(json, Options);

        read.Should().Throw<JsonException>()
            .WithMessage("*must be a JSON string*");
    }

    /// <summary>A JSON null is refused.</summary>
    /// <remarks>
    /// Asserted separately from the token shapes above because the converter never sees it: the enumeration is
    /// a value type and the converter does not declare that it handles null, so the framework refuses it before
    /// dispatching. That is the correct division of labour, and it is worth pinning - a converter that DID
    /// declare it handled null would have to choose a member to stand in for absence, and there is none.
    /// </remarks>
    [Fact]
    public void Read_RefusesAJsonNull()
    {
        Action read = () => JsonSerializer.Deserialize<PermissionKey>("null", Options);

        read.Should().Throw<JsonException>(
            "there is no member that could stand in for an absent key");
    }

    /// <summary>A key used as a dictionary key produces the same name as one used as a value.</summary>
    /// <remarks>
    /// No contract keys a dictionary by this enumeration today. The property-name half is implemented so that
    /// the first one that does simply works, instead of failing with a "not supported" error that names
    /// nothing about permission keys - and it is asserted here for the same reason the value half is.
    /// </remarks>
    [Fact]
    public void AsADictionaryKey_TheNameIsTheSame()
    {
        Dictionary<PermissionKey, bool> grants = new()
        {
            [PermissionKey.VIEW] = true,
            [PermissionKey.WRITE] = false,
        };

        string written = JsonSerializer.Serialize(grants, Options);

        written.Should().Be("{\"VIEW\":true,\"WRITE\":false}");

        Dictionary<PermissionKey, bool>? read =
            JsonSerializer.Deserialize<Dictionary<PermissionKey, bool>>(written, Options);

        read.Should().NotBeNull();
        read![PermissionKey.VIEW].Should().BeTrue();
        read[PermissionKey.WRITE].Should().BeFalse();
    }

    /// <summary>A differently cased dictionary key resolves to its member.</summary>
    [Fact]
    public void AsADictionaryKey_CaseIsParsed()
    {
        Dictionary<PermissionKey, bool>? read =
            JsonSerializer.Deserialize<Dictionary<PermissionKey, bool>>("{\"edit\":true}", Options);

        read.Should().NotBeNull();
        read!.Should().ContainKey(PermissionKey.EDIT);
    }

    /// <summary>A dictionary key outside the vocabulary is refused.</summary>
    [Fact]
    public void AsADictionaryKey_AnUnrecognisedNameIsRefused()
    {
        Action read = () => JsonSerializer.Deserialize<Dictionary<PermissionKey, bool>>(
            "{\"FULLCONTROL\":true}",
            Options);

        read.Should().Throw<JsonException>();
    }

    /// <summary>A value the enumeration does not declare cannot be serialised.</summary>
    /// <remarks>
    /// Reaching this refusal means server code cast an arbitrary number into the enumeration, since the closed
    /// vocabulary is otherwise enforced by the compiler on the way in and by the read half on the way through.
    /// The alternative - rendering the undeclared value through <c>ToString</c> - would put a NUMBER on the
    /// wire, which is precisely the form this converter exists to prevent, and it would do so silently.
    /// </remarks>
    [Fact]
    public void Write_RefusesAValueTheEnumerationDoesNotDeclare()
    {
        PermissionKey undeclared = (PermissionKey)99;

        Action write = () => JsonSerializer.Serialize(undeclared, Options);

        write.Should().Throw<JsonException>()
            .WithMessage("*outside the declared vocabulary*");
    }

    /// <summary>The converter is stateless, so one instance is safe to share.</summary>
    /// <remarks>
    /// The registration shares a single instance across every options object and every thread, which is only
    /// safe because the type holds nothing. Asserted structurally, because a field added here would make the
    /// shared registration a data race that no functional test would reveal.
    /// </remarks>
    [Fact]
    public void TheConverter_HoldsNoState()
    {
        typeof(PermissionKeyJsonConverter)
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Should()
            .BeEmpty("one instance is shared across every options object and every thread");

        typeof(PermissionKeyJsonConverter).Should().BeSealed(
            "there is nothing here worth specialising, and a subclass could reintroduce the ordinal form");
    }

    /// <summary>The converter accepts exactly the type it declares and nothing else.</summary>
    [Fact]
    public void TheConverter_ConvertsOnlyPermissionKey()
    {
        PermissionKeyJsonConverter converter = new();

        converter.CanConvert(typeof(PermissionKey)).Should().BeTrue();
        converter.CanConvert(typeof(string)).Should().BeFalse();
        converter.CanConvert(typeof(int)).Should().BeFalse();
        converter.CanConvert(typeof(PermissionKey?)).Should().BeFalse(
            "the nullable form is handled by the framework's own wrapper around this converter, not by it");
    }

    /// <summary>A key nested in a document round-trips, which is how a contract will actually carry it.</summary>
    /// <remarks>
    /// The standalone assertions above exercise the converter in isolation. This one proves it composes: a
    /// converter registered only for the top-level type would pass every fact above and fail the first
    /// contract that carried the enumeration as a member.
    /// </remarks>
    [Fact]
    public void NestedInADocument_TheKeyRoundTrips()
    {
        string written = JsonSerializer.Serialize(
            new GrantProbe { Key = PermissionKey.EDIT, Allowed = true },
            Options);

        written.Should().Contain("\"Key\":\"EDIT\"");

        GrantProbe? read = JsonSerializer.Deserialize<GrantProbe>(written, Options);

        read.Should().NotBeNull();
        read!.Key.Should().Be(PermissionKey.EDIT);
        read.Allowed.Should().BeTrue();
    }

    /// <summary>The reader is left positioned correctly, so a following member still binds.</summary>
    /// <remarks>
    /// A converter that consumed the wrong number of tokens would deserialise its own value correctly and
    /// corrupt whatever followed it, which is the failure a single-member probe cannot detect.
    /// </remarks>
    [Fact]
    public void Read_LeavesTheReaderPositionedForTheNextMember()
    {
        const string json = "{\"Key\":\"WRITE\",\"Allowed\":true}";

        Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));

        GrantProbe? read = JsonSerializer.Deserialize<GrantProbe>(ref reader, Options);

        read.Should().NotBeNull();
        read!.Key.Should().Be(PermissionKey.WRITE);
        read.Allowed.Should().BeTrue("the member after the key must still bind");
    }

    /// <summary>A document member carrying a permission key, used only by this suite.</summary>
    private sealed class GrantProbe
    {
        /// <summary>Gets or sets the key.</summary>
        public PermissionKey Key { get; set; }

        /// <summary>Gets or sets whether the grant is allowed.</summary>
        public bool Allowed { get; set; }
    }
}
