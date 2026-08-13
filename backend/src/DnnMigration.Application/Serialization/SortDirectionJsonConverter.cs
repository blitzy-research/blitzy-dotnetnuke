using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Serialization;

/// <summary>
/// Reads and writes <see cref="SortDirection"/> in a JSON body using the SAME vocabulary the query-string
/// binder accepts, so one contract member has one wire form on both transports.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this closes.</b> <see cref="PagedRequest.SortDir"/> is bound from the query string on
/// every collection endpoint and from a JSON BODY on <c>POST /api/v1/users/search</c> — the compensating
/// address an identifying account search uses so that personal data does not travel in a request target.
/// </para>
/// <para>
/// <b>Why the fix belongs here rather than on the client.</b> The alternative — having the client write the
/// number into the body while continuing to write the name into the query string — would leave one member
/// with two spellings that a reader of either side has to remember, and would make the body's form depend
/// on declaration order in this assembly: the members carry explicit values today, but a numeric wire form
/// is exactly the coupling the sibling <see cref="PermissionKeyJsonConverter"/> documents as a hazard.
/// </para>
/// </remarks>
public sealed class SortDirectionJsonConverter : JsonConverter<SortDirection>
{
    /// <summary>
    /// The accepted names, quoted in a refusal so a caller learns the vocabulary without this converter
    /// echoing back whatever it was sent.
    /// </summary>
    private const string AcceptedNames = "\"Ascending\" or \"Descending\"";

    /// <inheritdoc />
    /// <exception cref="JsonException">
    /// Thrown when the token is neither a string nor a number, or when a string names no member and is not
    /// an integer.
    /// </exception>
    public override SortDirection Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Parse(reader.GetString());

            case JsonTokenType.Number:
                // The underlying representation, admitted so that a body remains compatible with the
                // behaviour that applied before this converter was registered.
                if (!reader.TryGetInt32(out int discriminator))
                {
                    throw new JsonException(
                        "A sort direction expressed as a number must be a 32-bit integer. Accepted values are "
                        + AcceptedNames + ".");
                }

                // Deliberately unvalidated: see the note on this type about where membership is decided.
                return (SortDirection)discriminator;

            default:
                throw new JsonException(
                    "A sort direction must be a JSON string or number. Accepted values are "
                    + AcceptedNames + ".");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SortDirection value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Format(value));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implemented so that a direction used as a dictionary key produces the same name as one used as a
    /// value. No contract keys a dictionary by this enumeration today; leaving the inherited behaviour in
    /// place would make the first one that does fail with an unrelated "not supported" error rather than
    /// simply working.
    /// </remarks>
    public override void WriteAsPropertyName(
        Utf8JsonWriter writer,
        SortDirection value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(Format(value));
    }

    /// <inheritdoc />
    public override SortDirection ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        Parse(reader.GetString());

    /// <summary>Resolves one inbound textual value to a direction, or reports why it cannot be resolved.</summary>
    /// <param name="text">The value read from the JSON document.</param>
    /// <returns>The direction the value expresses.</returns>
    /// <exception cref="JsonException">
    /// Thrown when the value is absent, empty, or names neither a member nor an integer.
    /// </exception>
    /// <remarks>
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> is used precisely BECAUSE it accepts
    /// numeric text as well as a member name: that is the query binder's behaviour and this method's
    /// contract is to reproduce it.
    /// </remarks>
    private static SortDirection Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new JsonException(
                "A sort direction may not be empty. Accepted values are " + AcceptedNames + ".");
        }

        if (!Enum.TryParse(text, ignoreCase: true, out SortDirection parsed))
        {
            throw new JsonException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' is not a sort direction. Accepted values are {1}.",
                    text,
                    AcceptedNames));
        }

        return parsed;
    }

    /// <summary>
    /// Returns the canonical name a direction is written as, refusing one the enumeration does not declare.
    /// </summary>
    /// <param name="value">The direction being written.</param>
    /// <returns>The member's name.</returns>
    /// <exception cref="JsonException">Thrown when <paramref name="value"/> names no declared member.</exception>
    private static string Format(SortDirection value) => value switch
    {
        SortDirection.Ascending => nameof(SortDirection.Ascending),
        SortDirection.Descending => nameof(SortDirection.Descending),
        _ => throw new JsonException(
            "The sort direction is not one this contract declares. Accepted values are "
            + AcceptedNames + "."),
    };
}
