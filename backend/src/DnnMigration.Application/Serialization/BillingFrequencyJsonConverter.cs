using System.Text.Json;
using System.Text.Json.Serialization;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Serialization;

// MIGRATION: this namespace holds the wire-form converters for the two enumerations whose external
// representation is fixed by legacy data rather than chosen by this migration. It sits in the
// Application layer and not in Domain because DnnMigration.Domain takes no dependency on any
// serialisation technology - a constraint each enumeration states on itself - and not in Api because
// the contracts these converters describe are the DTOs in this same assembly, so the converter
// travels with the contract it serves. The Api layer REGISTERS the policy; it does not define it.
// The registration surface is Serialization/DnnJsonConverters.cs.
//
// MIGRATION: no [JsonConverter] attribute is placed on any DTO member. Every DTO folder in this
// assembly records that the wire shape belongs to one central System.Text.Json policy configured at
// the Api edge, and attributes would put a second, competing declaration next to the property.

/// <summary>
/// Serialises <see cref="BillingFrequency"/> as the single legacy character its column stores —
/// <c>"N"</c>, <c>"O"</c>, <c>"D"</c>, <c>"W"</c>, <c>"M"</c> or <c>"Y"</c> — and reads the same
/// form back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this converter the wire form would be a number, and the wrong number.</b> The
/// enumeration declares <c>ushort</c> as its backing type and gives every member the code point of
/// its own legacy character, so the default <c>System.Text.Json</c> treatment of an
/// enumeration — emit the underlying numeric value — would put <c>77</c> on the wire where the
/// legacy contract, the database column and the Angular client all expect <c>"M"</c>. A caller
/// round-tripping a role would then send <c>77</c> back, and nothing in the pipeline would report a
/// problem. The failure is silent in both directions, which is why the wire form is pinned here
/// rather than left to a default.
/// </para>
/// <para>
/// <b>The generic string-enumeration converter is also wrong for this type.</b>
/// <see cref="JsonStringEnumConverter"/> emits the MEMBER NAME, producing <c>"Month"</c>, which is
/// not a value the legacy vocabulary contains: the <c>char(1)</c> column holds <c>'M'</c> and the
/// lookup row reads <c>'M', 'Month(s)'</c>. Registering a blanket string-enumeration policy would
/// therefore have to be ordered behind this converter to avoid claiming this type, and relying on
/// registration order for correctness is exactly the kind of implicit coupling this codebase
/// declines. <c>DnnJsonConverters</c> consequently registers one explicit converter per type and no
/// blanket policy.
/// </para>
/// <para>
/// <b>Both directions matter, because two of the six carrying contracts are inbound.</b>
/// <c>CreateRoleRequest</c> and <c>UpdateRoleRequest</c> each carry two nullable frequency members,
/// so this converter must accept <c>"M"</c> from a caller as well as emit it; the read half is not
/// symmetry for its own sake. The four members on <c>RoleListItemDto</c> and <c>RoleDetailDto</c>
/// are outbound only.
/// </para>
/// <para>
/// <b>An unrecognised inbound value is never COERCED, and the layer that refuses it is the validator
/// rather than this converter.</b> The persistence half —
/// <c>DnnMigration.Infrastructure.Persistence.ValueConverters.BillingFrequencyToStringConverter</c> —
/// carries an unrecognised stored character through losslessly, because AAP Rule T4 makes an existing
/// database authoritative: a row a legacy installation already holds must stay readable AND must
/// survive an edit to some unrelated column unchanged. Every installation holds two such rows
/// (<c>01.00.00.SqlDataProvider</c> L7192 and L7194). This converter therefore has to be able to WRITE
/// such a character, and — since the same contract types and the same converter serve both directions —
/// to READ it back, or the API could not deserialise its own output.
/// </para>
/// <para>
/// Caller input carries no authority, and it is still refused: <c>CreateRoleRequest</c> and
/// <c>UpdateRoleRequest</c> each declare an <c>IsInEnum</c> rule for both frequency members, so
/// <c>"Q"</c> from a caller becomes an RFC 7807 <c>ValidationProblemDetails</c> naming the field. That
/// is a strictly better contract than a <see cref="JsonException"/> raised mid-document, which
/// surfaces as a bare bad request naming nothing. Quietly reading <c>"Q"</c> as "no billing frequency"
/// remains forbidden, and neither half does it.
/// </para>
/// <para>
/// <b>Nullability is handled by the framework.</b> Every carrying member is declared
/// <c>BillingFrequency?</c>. <c>System.Text.Json</c> resolves a converter for a nullable
/// value type by locating the converter for the underlying type in the options and wrapping it, so
/// this converter is registered once for <see cref="BillingFrequency"/> and serves both forms; a
/// JSON <c>null</c> is consumed by that wrapper and never reaches <see cref="Read"/>.
/// </para>
/// <para>
/// <b>Case is parsed, not substituted, and only on the INBOUND side.</b> A one-character value read
/// from a document is upper-cased invariantly before resolution, so <c>"m"</c> resolves to
/// <see cref="BillingFrequency.Month"/> and the canonical <c>"M"</c> is what is emitted and stored.
/// This is parsing rather than the silent value substitution this codebase forbids elsewhere: the six
/// codes are six distinct letters, so no spelling is ambiguous and no other member <c>"m"</c> could
/// have meant exists. Should a member ever be added whose code differs from another only by case, this
/// leniency becomes ambiguous and must be revisited. The persistence half deliberately does NOT
/// up-case, because the legacy application compared stored codes case-sensitively and up-casing a
/// stored <c>'m'</c> would both change how the row reads and rewrite its byte on the next update.
/// </para>
/// <para>
/// This type holds no state, so a single instance is safe to share across every options object and
/// every thread.
/// </para>
/// </remarks>
public sealed class BillingFrequencyJsonConverter : JsonConverter<BillingFrequency>
{
    /// <summary>
    /// The accepted codes, named in error messages so a caller learns the vocabulary without this
    /// converter echoing back whatever it was sent.
    /// </summary>
    private const string AcceptedCodes = "\"N\", \"O\", \"D\", \"W\", \"M\" or \"Y\"";

    /// <inheritdoc />
    public override BillingFrequency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            // Reported separately from the general shape failure because a number is the one wrong
            // form a caller can arrive at honestly: it is what a client would send after receiving a
            // response serialised without this converter registered.
            throw new JsonException(
                "A billing frequency is a one-character code, not a number. Accepted values are " +
                AcceptedCodes + ".");
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                "A billing frequency must be a JSON string holding one character. Accepted values are " +
                AcceptedCodes + ".");
        }

        return Parse(reader.GetString());
    }

    /// <inheritdoc />
    /// <remarks>
    /// The outbound direction is deliberately TOLERANT while <see cref="Parse"/> stays strict, and the
    /// asymmetry is the point. What is written here is a value the SERVER holds, which for this
    /// enumeration may legitimately be a character no vocabulary declares: the persistence conversion
    /// carries a stored character through losslessly so that an unrelated edit cannot rewrite it, and
    /// the installation seed of every DotNetNuke database contains two such rows. Refusing to write it
    /// would turn one legacy row into a failed response and take every other role in the same listing
    /// down with it, and reporting a substitute would tell the client something the database does not
    /// say. What a CALLER may send is a different question with a different answer, and
    /// <see cref="Parse"/> gives it.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, BillingFrequency value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // A one-character span rather than a string keeps the write allocation-free on a path that
        // runs for every role in every page of results.
        Span<char> code = stackalloc char[1];
        code[0] = CodeOf(value);
        writer.WriteStringValue(code);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implemented so that a frequency used as a dictionary key produces the same one-character form
    /// as a frequency used as a value. No contract in this assembly keys a dictionary by this
    /// enumeration today; leaving the inherited behaviour in place would make the first one that
    /// does fail with an unrelated "not supported" error instead of simply working.
    /// </remarks>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, BillingFrequency value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        Span<char> code = stackalloc char[1];
        code[0] = CodeOf(value);
        writer.WritePropertyName(code);
    }

    /// <inheritdoc />
    public override BillingFrequency ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    /// <summary>
    /// Resolves one inbound textual value to its member, or reports why it cannot be resolved.
    /// </summary>
    /// <param name="text">The value read from the JSON document.</param>
    /// <returns>The member the value names.</returns>
    /// <exception cref="JsonException">
    /// Thrown when the value is absent or is not exactly one character long.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>SHAPE is enforced here; VOCABULARY is enforced by the request validators.</b> An empty value
    /// and a value of the wrong length are refused, because neither can be resolved to a code at all and
    /// there is nothing a later layer could say about them that this one cannot say better. A
    /// single character the enumeration does not declare is accepted and carried, and that division is
    /// deliberate for three reasons.
    /// </para>
    /// <para>
    /// First, the response contract has to be readable back. The persistence conversion carries a stored
    /// character through losslessly so an unrelated edit cannot rewrite it, and the installation seed of
    /// every DotNetNuke database contains two roles whose frequency is outside the vocabulary
    /// (<c>01.00.00.SqlDataProvider</c> L7192 and L7194). A converter that writes <c>"4"</c> and then
    /// refuses to read <c>"4"</c> would make the API unable to deserialise its own output - which is not
    /// a hypothetical, since the same contract types and the same converter serve both directions.
    /// </para>
    /// <para>
    /// Second, the vocabulary is still closed on the only side a caller can widen. Both write contracts
    /// that carry a frequency - <c>CreateRoleRequest</c> and <c>UpdateRoleRequest</c> - declare an
    /// <c>IsInEnum</c> rule for it, so an undeclared code submitted by a caller is refused before any
    /// service sees it.
    /// </para>
    /// <para>
    /// Third, refusing it THERE is a better contract than refusing it here. A validator failure becomes
    /// an RFC 7807 <c>ValidationProblemDetails</c> naming the offending field, whereas a
    /// <see cref="JsonException"/> raised mid-document surfaces as a bare bad request with no field
    /// information and no indication of which of several frequencies was at fault.
    /// </para>
    /// <para>
    /// An inbound character is UPPER-CASED before it is resolved, which the persistence read
    /// deliberately does not do: caller text is not stored data, and the legacy application compared
    /// stored codes case-sensitively, so up-casing a stored <c>'m'</c> would change how an existing row
    /// reads and would rewrite its byte on the next update.
    /// </para>
    /// </remarks>
    private static BillingFrequency Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new JsonException(
                "A billing frequency may not be empty. Accepted values are " + AcceptedCodes + ".");
        }

        if (text.Length != 1)
        {
            // Length is checked before the cast so that a longer value is reported as the wrong
            // shape rather than silently resolving on its first character, which would accept
            // "Monthly" as "M".
            throw new JsonException(
                "A billing frequency is exactly one character. Accepted values are " +
                AcceptedCodes + ".");
        }

        // The member's value IS its character, so the cast is the whole resolution and there is no
        // lookup table to keep in step with the enumeration.
        return (BillingFrequency)char.ToUpperInvariant(text[0]);
    }

    /// <summary>
    /// Returns the character a value is written as, whether or not the enumeration declares it.
    /// </summary>
    /// <param name="value">The value being written.</param>
    /// <returns>The one-character legacy code the value carries.</returns>
    /// <remarks>
    /// <para>
    /// The member's value IS its character, so the cast is the whole conversion for a declared member
    /// and equally for an undeclared one — which is what lets this direction be lossless. An earlier
    /// revision threw for anything undeclared, on the reasoning that such a value could only come from
    /// server code casting an arbitrary number. That reasoning no longer holds and was in fact the
    /// weaker half of a pair of defects: the persistence conversion used to normalise an unrecognised
    /// stored character to <see cref="BillingFrequency.None"/> precisely so that it would never reach
    /// here, and that normalisation destroyed the stored byte on the next update of the row. Making the
    /// read lossless is the fix; making this write lossless is what allows it, because the two roles
    /// every DotNetNuke installation ships with store characters outside the vocabulary
    /// (<c>01.00.00.SqlDataProvider</c> L7192 and L7194).
    /// </para>
    /// <para>
    /// A client reading such a value therefore sees exactly what the database holds, which is more
    /// informative than a substitute and strictly more truthful. It may not usefully send one back, but
    /// note WHERE that is decided, because an earlier revision of this remark named the wrong place:
    /// <see cref="Parse"/> does NOT refuse an undeclared character - it enforces SHAPE only, a non-empty
    /// single character, and casts whatever that character is. It has to, or the API could not
    /// deserialise its own output. The closed vocabulary is enforced entirely by the
    /// <c>IsInEnum</c> rules on the two write contracts that carry a frequency,
    /// <c>CreateRoleRequest</c> and <c>UpdateRoleRequest</c>, so a caller's undeclared code is refused
    /// before any service sees it - and refused as an RFC 7807 document naming the field, rather than as
    /// a bare <see cref="JsonException"/> raised mid-parse.
    /// </para>
    /// <para>
    /// That is the asymmetry, stated plainly so neither half is mistaken for the other: this direction is
    /// LOSSLESS by design, the read direction is SHAPE-CHECKED ONLY by design, and the vocabulary lives
    /// in the validators. Reintroducing a vocabulary check in either direction of this converter would
    /// break the round trip on the two roles every installation ships with. <see cref="Parse"/>'s own
    /// remarks set the same division out from the reading side.
    /// </para>
    /// </remarks>
    private static char CodeOf(BillingFrequency value) => (char)value;
}
