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
/// <b>An unrecognised inbound value is rejected, not coerced.</b> This is the deliberate opposite of
/// the persistence half —
/// <c>DnnMigration.Infrastructure.Persistence.ValueConverters.BillingFrequencyToStringConverter</c>
/// resolves an unrecognised stored character to <see cref="BillingFrequency.None"/> because AAP Rule
/// T4 makes an existing database authoritative and a row a legacy installation already accepted must
/// stay readable. Caller input carries no such authority. Quietly reading <c>"Q"</c> as "no billing
/// frequency" would create a role the caller did not ask for, so the value is refused and surfaces
/// as a field-level 400 through the deserialisation failure path rather than as a successful write
/// of something else.
/// </para>
/// <para>
/// <b>Nullability is handled by the framework.</b> Every carrying member is declared
/// <c>BillingFrequency?</c>. <c>System.Text.Json</c> resolves a converter for a nullable
/// value type by locating the converter for the underlying type in the options and wrapping it, so
/// this converter is registered once for <see cref="BillingFrequency"/> and serves both forms; a
/// JSON <c>null</c> is consumed by that wrapper and never reaches <see cref="Read"/>.
/// </para>
/// <para>
/// <b>Case is parsed, not substituted.</b> A one-character value is upper-cased invariantly before
/// resolution, so <c>"m"</c> resolves to <see cref="BillingFrequency.Month"/> and the canonical
/// <c>"M"</c> is what is emitted and stored. This is parsing rather than the silent value
/// substitution this codebase forbids elsewhere: the six codes are six distinct letters, so no
/// spelling is ambiguous and no other member <c>"m"</c> could have meant exists. Should a member
/// ever be added whose code differs from another only by case, this leniency becomes ambiguous and
/// must be revisited.
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
    public override void Write(Utf8JsonWriter writer, BillingFrequency value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // A one-character span rather than a string keeps the write allocation-free on a path that
        // runs for every role in every page of results.
        Span<char> code = stackalloc char[1];
        code[0] = RequireDeclaredCode(value);
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
        code[0] = RequireDeclaredCode(value);
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
    /// Thrown when the value is absent, is not exactly one character long, or is a character the
    /// enumeration does not declare.
    /// </exception>
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
        BillingFrequency candidate = (BillingFrequency)char.ToUpperInvariant(text[0]);

        if (!Enum.IsDefined(candidate))
        {
            throw new JsonException(
                "The billing frequency is not one this contract recognises. Accepted values are " +
                AcceptedCodes + ".");
        }

        return candidate;
    }

    /// <summary>
    /// Returns the character a member is written as, refusing a value the enumeration does not
    /// declare.
    /// </summary>
    /// <param name="value">The member being written.</param>
    /// <returns>The member's one-character legacy code.</returns>
    /// <exception cref="JsonException">
    /// Thrown when <paramref name="value"/> names no declared member.
    /// </exception>
    /// <remarks>
    /// Unreachable through either supported path: the persistence converter resolves every
    /// unrecognised stored character to <see cref="BillingFrequency.None"/>, and
    /// <see cref="Parse"/> refuses one on the way in. Reaching it therefore means server code cast
    /// an arbitrary number into the enumeration, and emitting the corresponding character would put
    /// a value on the wire that no client can interpret and that the vocabulary does not contain.
    /// Failing visibly is the better outcome for a defect that only server code can introduce.
    /// </remarks>
    private static char RequireDeclaredCode(BillingFrequency value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new JsonException(
                "A billing frequency outside the declared vocabulary cannot be serialised. Accepted values are " +
                AcceptedCodes + ".");
        }

        return (char)value;
    }
}
