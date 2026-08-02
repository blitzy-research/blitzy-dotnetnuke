using System.Text.Json;
using System.Text.Json.Serialization;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Serialization;

/// <summary>
/// Serialises <see cref="PermissionKey"/> as its member name — <c>"VIEW"</c>, <c>"EDIT"</c>,
/// <c>"READ"</c> or <c>"WRITE"</c> — and reads the same form back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The member name is the contract and the ordinal is meaningless.</b> The enumeration says so
/// itself, and the schema is the reason: <c>Permission.PermissionKey</c> is declared
/// <c>varchar(20) NOT NULL</c> at
/// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.00.SqlDataProvider:L688</c>, widened to
/// <c>varchar(50)</c> at <c>04.06.00:L397-398</c>, and the <c>AddPermission</c> procedure accepts
/// <c>@PermissionKey varchar(20)</c>. No number for this concept is stored anywhere, so no member
/// carries an explicit value and the implicit ordinals are incidental artefacts of declaration
/// order. Left to the default treatment, <c>System.Text.Json</c> would put those incidental numbers
/// on the wire, so <c>0</c> would mean <c>VIEW</c> only until somebody reordered the members — and
/// reordering members is otherwise a harmless edit. This converter removes that hazard by making
/// the wire form the name.
/// </para>
/// <para>
/// <b>No response contract carries this enumeration today, and it is registered anyway.</b> The
/// permission surface publishes permission keys as plain strings — the endpoints under
/// <c>/api/v1/permissions</c> answer with <c>IReadOnlyList&lt;string&gt;</c> and <c>bool</c> — so the
/// enumeration is currently confined to server-side authorisation: the requirement, the policy names,
/// the permission repository and the evaluator. The converter is registered so that the FIRST
/// contract to carry the type is already correct, because the failure it prevents is silent: without
/// it the wire form is an incidental ordinal, and the ordinals move whenever a member is inserted.
/// The read half is kept for the same reason the write half exists — a converter that emits a name
/// has to accept one, or a client could not send back a value it was just given.
/// </para>
/// <para>
/// Note that the DOMAIN ENTITY deliberately keeps a plain <c>string</c>
/// property — <c>Permission.PermissionKey</c> — so that an installation carrying a key this codebase
/// has not seen still round-trips intact. There is consequently NO Entity Framework Core value
/// conversion for this type and none is needed; only the wire form is pinned here.
/// </para>
/// <para>
/// <b>A number is refused explicitly.</b> Accepting one would reintroduce exactly the ordinal
/// dependence the enumeration forbids, and it is the specific mistake a client would make after
/// reading a response that had been serialised without this converter registered. This is also why
/// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> is NOT used to resolve inbound
/// values: that method accepts numeric text, so <c>"3"</c> would quietly resolve to
/// <see cref="PermissionKey.WRITE"/>. The four names are matched explicitly instead.
/// </para>
/// <para>
/// <b>Case is parsed, not substituted.</b> Names are matched with
/// <see cref="StringComparison.OrdinalIgnoreCase"/> and always emitted in the canonical upper case,
/// which is both the framework's own posture for string-valued enumerations and safe here because no
/// two members differ only by case. Nothing differently cased ever reaches the database: what is
/// resolved is the member, and what is written is its canonical name. Should a member ever be added
/// that collides with another by case alone, this leniency becomes ambiguous and must be revisited.
/// </para>
/// <para>
/// <b>These four keys are the exhaustive set for this DotNetNuke generation.</b> Keys belonging to
/// later versions — DEPLOY, ADD, DELETE, MANAGE, FULLCONTROL and their kin — do not exist in this
/// codebase, so this converter neither accepts nor emits them and a request naming one is refused
/// with the accepted set named back.
/// </para>
/// <para>
/// This type holds no state, so a single instance is safe to share across every options object and
/// every thread.
/// </para>
/// </remarks>
public sealed class PermissionKeyJsonConverter : JsonConverter<PermissionKey>
{
    /// <summary>
    /// The accepted names, named in error messages so a caller learns the vocabulary without this
    /// converter echoing back whatever it was sent.
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
    /// Implemented so that a key used as a dictionary key produces the same name as a key used as a
    /// value. No contract in this assembly keys a dictionary by this enumeration today; leaving the
    /// inherited behaviour in place would make the first one that does fail with an unrelated "not
    /// supported" error instead of simply working.
    /// </remarks>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, PermissionKey value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(Format(value));
    }

    /// <inheritdoc />
    public override PermissionKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    /// <summary>
    /// Resolves one inbound textual value to its member, or reports why it cannot be resolved.
    /// </summary>
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
    /// <exception cref="JsonException">
    /// Thrown when <paramref name="value"/> names no declared member.
    /// </exception>
    /// <remarks>
    /// The names are produced by <see langword="nameof"/> rather than by
    /// <see cref="object.ToString"/> for two reasons: <see cref="object.ToString"/> renders an
    /// undeclared value as its NUMBER, which would place the very form this converter exists to
    /// prevent on the wire, and an exhaustive expression makes an undeclared value a visible failure
    /// instead. Reaching that failure means server code cast an arbitrary number into the
    /// enumeration, since the closed vocabulary is otherwise enforced by the compiler on the way in
    /// and by <see cref="Parse"/> on the way through.
    /// </remarks>
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
