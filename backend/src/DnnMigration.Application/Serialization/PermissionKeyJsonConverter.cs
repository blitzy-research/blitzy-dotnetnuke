using System.Text.Json;
using System.Text.Json.Serialization;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Serialization;

/// <summary>
/// Serialises <see cref="PermissionKey"/> as its member name — <c>"VIEW"</c>, <c>"EDIT"</c>, <c>"READ"</c>
/// or <c>"WRITE"</c> — and reads the same form back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The member name is the contract and the ordinal is meaningless.</b> The enumeration says so itself,
/// and the schema is the reason: <c>Permission.PermissionKey</c> is <c>varchar(50) NOT NULL</c> in the
/// terminal schema, widened from the <c>varchar(20)</c> baseline by
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.06.00.SqlDataProvider</c> L398, and
/// <c>AddPermission</c> accepts <c>@PermissionKey varchar(50)</c> from L407 onward.
/// </para>
/// <para>
/// <b>No response contract carries this enumeration today, and it is registered anyway.</b> The permission
/// surface publishes permission keys as plain strings — the endpoints under <c>/api/v1/permissions</c>
/// answer with <c>IReadOnlyList&lt;string&gt;</c> and <c>bool</c> — so the enumeration is currently
/// confined to server-side authorisation: the requirement, the policy names, the permission repository and
/// the evaluator.
/// </para>
/// </remarks>
public sealed class PermissionKeyJsonConverter : JsonConverter<PermissionKey>
{
    /// <summary>
    /// The accepted names, named in error messages so a caller learns the vocabulary without this converter
    /// echoing back whatever it was sent.
    /// </summary>
    private const string AcceptedNames = "\"VIEW\", \"EDIT\", \"READ\" or \"WRITE\"";

    /// <inheritdoc />
    public override PermissionKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            throw new JsonException(
                "A permission key is a name, not a number. Accepted values are " + AcceptedNames + ".");
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                "A permission key must be a JSON string. Accepted values are " + AcceptedNames + ".");
        }

        return Parse(reader.GetString());
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PermissionKey value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Format(value));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implemented so that a key used as a dictionary key produces the same name as a key used as a value.
    /// No contract in this assembly keys a dictionary by this enumeration today; leaving the inherited
    /// behaviour in place would make the first one that does fail with an unrelated "not supported" error
    /// instead of simply working.
    /// </remarks>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, PermissionKey value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(Format(value));
    }

    /// <inheritdoc />
    public override PermissionKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    /// <summary>Resolves one inbound textual value to its member, or reports why it cannot be resolved.</summary>
    /// <param name="text">The value read from the JSON document.</param>
    /// <returns>The member the value names.</returns>
    /// <exception cref="JsonException">
    /// Thrown when the value is absent or names no key in the closed vocabulary.
    /// </exception>
    private static PermissionKey Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new JsonException(
                "A permission key may not be empty. Accepted values are " + AcceptedNames + ".");
        }

        // Matched name by name rather than through Enum.TryParse, which would also accept numeric
        // text and comma-separated lists. The keys are not flags and must never be combined.
        if (string.Equals(text, nameof(PermissionKey.VIEW), StringComparison.OrdinalIgnoreCase))
        {
            return PermissionKey.VIEW;
        }

        if (string.Equals(text, nameof(PermissionKey.EDIT), StringComparison.OrdinalIgnoreCase))
        {
            return PermissionKey.EDIT;
        }

        if (string.Equals(text, nameof(PermissionKey.READ), StringComparison.OrdinalIgnoreCase))
        {
            return PermissionKey.READ;
        }

        if (string.Equals(text, nameof(PermissionKey.WRITE), StringComparison.OrdinalIgnoreCase))
        {
            return PermissionKey.WRITE;
        }

        throw new JsonException(
            "The permission key is not one this contract recognises. Accepted values are " +
            AcceptedNames + ".");
    }

    /// <summary>
    /// Returns the canonical name a member is written as, refusing a value the enumeration does not
    /// declare.
    /// </summary>
    /// <param name="value">The member being written.</param>
    /// <returns>The member's upper-case name, exactly as the database stores it.</returns>
    /// <exception cref="JsonException">Thrown when <paramref name="value"/> names no declared member.</exception>
    private static string Format(PermissionKey value) => value switch
    {
        PermissionKey.VIEW => nameof(PermissionKey.VIEW),
        PermissionKey.EDIT => nameof(PermissionKey.EDIT),
        PermissionKey.READ => nameof(PermissionKey.READ),
        PermissionKey.WRITE => nameof(PermissionKey.WRITE),
        _ => throw new JsonException(
            "A permission key outside the declared vocabulary cannot be serialised. Accepted values are " +
            AcceptedNames + "."),
    };
}
