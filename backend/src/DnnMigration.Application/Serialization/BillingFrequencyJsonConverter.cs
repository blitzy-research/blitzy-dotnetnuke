using System.Text.Json;
using System.Text.Json.Serialization;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Serialization;

// This namespace holds the wire-form converters for the two enumerations whose external representation is
// fixed by legacy data rather than chosen by this migration.

/// <summary>
/// Serialises <see cref="BillingFrequency"/> as the single legacy character its column stores — <c>"N"</c>,
/// <c>"O"</c>, <c>"D"</c>, <c>"W"</c>, <c>"M"</c> or <c>"Y"</c> — and reads the same form back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this converter the wire form would be a number, and the wrong number.</b> The enumeration
/// declares <c>ushort</c> as its backing type and gives every member the code point of its own legacy
/// character, so the default <c>System.Text.Json</c> treatment of an enumeration — emit the underlying
/// numeric value — would put <c>77</c> on the wire where the legacy contract, the database column and the
/// Angular client all expect <c>"M"</c>.
/// </para>
/// <para>
/// <b>Case is parsed, not substituted, and only on the INBOUND side.</b> A one-character value read from a
/// document is upper-cased invariantly before resolution, so <c>"m"</c> resolves to <see
/// cref="BillingFrequency.Month"/> and the canonical <c>"M"</c> is what is emitted and stored.
/// </para>
/// </remarks>
public sealed class BillingFrequencyJsonConverter : JsonConverter<BillingFrequency>
{
    /// <summary>
    /// The accepted codes, named in error messages so a caller learns the vocabulary without this converter
    /// echoing back whatever it was sent.
    /// </summary>
    private const string AcceptedCodes = "\"N\", \"O\", \"D\", \"W\", \"M\" or \"Y\"";

    /// <inheritdoc />
    public override BillingFrequency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            // Reported separately from the general shape failure because a number is the one wrong form a
            // caller can arrive at honestly: it is what a client would send after receiving a response
            // serialised without this converter registered.
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
        code[0] = CodeOf(value);
        writer.WriteStringValue(code);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implemented so that a frequency used as a dictionary key produces the same one-character form as a
    /// frequency used as a value. No contract in this assembly keys a dictionary by this enumeration today;
    /// leaving the inherited behaviour in place would make the first one that does fail with an unrelated
    /// "not supported" error instead of simply working.
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

    /// <summary>Resolves one inbound textual value to its member, or reports why it cannot be resolved.</summary>
    /// <param name="text">The value read from the JSON document.</param>
    /// <returns>The member the value names.</returns>
    /// <exception cref="JsonException">
    /// Thrown when the value is absent or is not exactly one character long.
    /// </exception>
    /// <remarks>
    /// First, the response contract has to be readable back. The persistence conversion carries a stored
    /// character through losslessly so an unrelated edit cannot rewrite it, and the installation seed of
    /// every DotNetNuke database contains two roles whose frequency is outside the vocabulary
    /// (<c>01.00.00.SqlDataProvider</c> L7192 and L7194).
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
            // Length is checked before the cast so that a longer value is reported as the wrong shape
            // rather than silently resolving on its first character, which would accept "Monthly" as "M".
            throw new JsonException(
                "A billing frequency is exactly one character. Accepted values are " +
                AcceptedCodes + ".");
        }

        // The member's value IS its character, so the cast is the whole resolution and there is no
        // lookup table to keep in step with the enumeration.
        return (BillingFrequency)char.ToUpperInvariant(text[0]);
    }

    /// <summary>Returns the character a value is written as, whether or not the enumeration declares it.</summary>
    /// <param name="value">The value being written.</param>
    /// <returns>The one-character legacy code the value carries.</returns>
    private static char CodeOf(BillingFrequency value) => (char)value;
}
