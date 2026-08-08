using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Serialization;

/// <summary>
/// Reads and writes <see cref="SortDirection"/> in a JSON body using the SAME vocabulary the
/// query-string binder accepts, so one contract member has one wire form on both transports.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this closes.</b> <see cref="PagedRequest.SortDir"/> is bound from the query string on
/// every collection endpoint and from a JSON BODY on
/// <c>POST /api/v1/users/search</c> — the compensating address an identifying account search uses so
/// that personal data does not travel in a request target. The framework's query-string binder resolves
/// an enumeration through its type converter, which accepts the MEMBER NAME; the body was bound by
/// <c>System.Text.Json</c> with no converter registered for this type, which accepts only the numeric
/// discriminator. One member therefore had two incompatible wire forms, and the client — reasonably —
/// implemented the one the query string documents. Measured against the running API before this
/// converter existed: <c>{"pageIndex":0,"pageSize":10,"sortDir":"Ascending"}</c> was answered
/// <c>400 Bad Request</c> carrying
/// <c>"$.sortDir": ["The JSON value could not be converted to …SortDirection."]</c>, while the identical
/// search with <c>"sortDir":0</c> was answered <c>200 OK</c>. Nothing on either side of the wire could
/// detect that: both are internally well typed, and no test sent the member in a body.
/// </para>
/// <para>
/// <b>Why the fix belongs here rather than on the client.</b> The alternative — having the client write
/// the number into the body while continuing to write the name into the query string — would leave one
/// member with two spellings that a reader of either side has to remember, and would make the body's
/// form depend on declaration order in this assembly: the members carry explicit values today, but a
/// numeric wire form is exactly the coupling the sibling <see cref="PermissionKeyJsonConverter"/>
/// documents as a hazard. Pinning the body to the name the query string already uses removes the
/// divergence at its source and leaves the client's <c>'Ascending' | 'Descending'</c> union correct for
/// both transports.
/// </para>
/// <para>
/// <b>The accepted set mirrors the query binder exactly, and that is deliberate.</b> Measured against
/// the running API, <c>?sortDir=</c> accepts <c>Ascending</c>, <c>ascending</c> (the type converter
/// parses case-insensitively) and <c>1</c> (it also parses numeric text), and refuses <c>asc</c>. This
/// converter accepts the same three shapes plus a JSON number, so a body can express anything a query
/// string could and nothing more. Admitting the number is what keeps the change purely additive: a
/// caller written against the previous behaviour keeps working.
/// </para>
/// <para>
/// <b>An out-of-range integer is passed through rather than refused, so that the rule about which
/// directions exist is stated in one place.</b> Carried, <c>5</c> reaches
/// <c>PagedRequestValidator</c>'s <c>IsInEnum</c> rule and is reported as
/// <c>"The sort direction must be either Ascending or Descending."</c> — a field-level RFC 7807 failure
/// keyed <c>SortDir</c> that names the domain rule. Refusing it here would answer the same status keyed
/// <c>$.sortDir</c> with a serialiser's account of the same fact, and membership would then be decided
/// both here and in the validator.
/// </para>
/// <para>
/// <b>The two transports refuse the same inputs but do not word the refusal alike, and that was measured
/// rather than assumed.</b> Against the running API, <c>?sortDir=5</c> answers <c>400</c> keyed
/// <c>SortDir</c> reading <c>"The value '5' is invalid."</c> and <c>?sortDir=asc</c> answers
/// <c>"The value 'asc' is not valid for SortDir."</c> — both from the framework's enum model binder,
/// which stops an undeclared value before any validator runs and whose wording no code here controls. On
/// the body transport the equivalents are the validator message above and this converter's
/// <c>"'asc' is not a sort direction…"</c>. Every case is a <c>400</c> naming the same member, so no
/// caller is admitted on one transport and refused on the other; only the sentence differs.
/// </para>
/// <para>
/// <b>The write half emits the name.</b> No response contract carries this enumeration today —
/// <c>PagedResponse&lt;T&gt;</c>'s metadata publishes the total, the index and the size, not the
/// ordering — so the write half exists for the same reason the sibling converter's does: a converter
/// that accepts a name must be able to produce one, or a caller could not send back a value it was
/// given. An undeclared value is refused when written, because emitting its number would put on the
/// wire the very form this converter removes.
/// </para>
/// <para>
/// This type holds no state, so one instance is shared across every options object and every thread.
/// </para>
/// </remarks>
public sealed class SortDirectionJsonConverter : JsonConverter<SortDirection>
{
    /// <summary>
    /// The accepted names, quoted in a refusal so a caller learns the vocabulary without this
    /// converter echoing back whatever it was sent.
    /// </summary>
    private const string AcceptedNames = "\"Ascending\" or \"Descending\"";

    /// <inheritdoc />
    /// <exception cref="JsonException">
    /// Thrown when the token is neither a string nor a number, or when a string names no member and is
    /// not an integer.
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
                // behaviour that applied before this converter was registered. Read as Int32 because
                // that is the enumeration's underlying type; a fractional or out-of-Int32 number fails
                // here and is reported as a conversion failure on this member.
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
    /// value. No contract keys a dictionary by this enumeration today; leaving the inherited behaviour
    /// in place would make the first one that does fail with an unrelated "not supported" error rather
    /// than simply working.
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

    /// <summary>
    /// Resolves one inbound textual value to a direction, or reports why it cannot be resolved.
    /// </summary>
    /// <param name="text">The value read from the JSON document.</param>
    /// <returns>The direction the value expresses.</returns>
    /// <exception cref="JsonException">
    /// Thrown when the value is absent, empty, or names neither a member nor an integer.
    /// </exception>
    /// <remarks>
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> is used precisely BECAUSE it accepts
    /// numeric text as well as a member name: that is the query binder's behaviour and this method's
    /// contract is to reproduce it. The sibling permission-key converter avoids the same method for the
    /// opposite reason — its ordinals are incidental and must never appear on the wire, whereas these
    /// carry no meaning beyond ordering and the query string already admits them.
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

        // Deliberately unvalidated, exactly as the numeric branch is: 'Ascending' and '5' both reach the
        // request validator, which is the single place deciding which directions exist and reports an
        // undeclared one as a field-level failure naming the domain rule.
        return parsed;
    }

    /// <summary>
    /// Returns the canonical name a direction is written as, refusing one the enumeration does not
    /// declare.
    /// </summary>
    /// <param name="value">The direction being written.</param>
    /// <returns>The member's name.</returns>
    /// <exception cref="JsonException">
    /// Thrown when <paramref name="value"/> names no declared member.
    /// </exception>
    /// <remarks>
    /// Produced by <see langword="nameof"/> through an exhaustive expression rather than by
    /// <see cref="object.ToString"/>, which renders an undeclared value as its number — the one form
    /// this converter exists to keep off the wire. Reaching the failure arm means server code cast an
    /// arbitrary number into the enumeration, because every inbound path is either compiler-checked or
    /// reported by the request validator.
    /// </remarks>
    private static string Format(SortDirection value) => value switch
    {
        SortDirection.Ascending => nameof(SortDirection.Ascending),
        SortDirection.Descending => nameof(SortDirection.Descending),
        _ => throw new JsonException(
            "The sort direction is not one this contract declares. Accepted values are "
            + AcceptedNames + "."),
    };
}
