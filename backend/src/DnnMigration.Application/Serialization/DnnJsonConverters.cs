using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnnMigration.Application.Serialization;

/// <summary>
/// The central <c>System.Text.Json</c> converter policy for the contracts in this assembly: one explicit
/// converter per enumeration whose external representation is pinned rather than incidental.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two of the three are pinned by legacy DATA and the third by a sibling TRANSPORT.</b>
/// <c>BillingFrequency</c> and <c>PermissionKey</c> carry forms the schema stores, so their wire
/// representation is not this codebase's to choose. <c>SortDirection</c> stores nothing: its converter
/// exists because the same contract member is also bound from a query string, where the framework's type
/// converter accepts the member NAME, so a body that accepted only the number gave one member two
/// incompatible spellings.
/// </para>
/// <para>
/// <b>The policy is DEFINED here and APPLIED at the Api edge.</b> Every DTO folder in this assembly records
/// that the wire shape belongs to one central policy configured at the Api boundary, and none carries a
/// <c>[JsonConverter]</c> attribute. This type is the definition half: it names the converters and adds
/// them to an options object.
/// </para>
/// </remarks>
public static class DnnJsonConverters
{
    /// <summary>The converters this policy applies, in the order they are added.</summary>
    /// <remarks>
    /// Each converter claims exactly one type, so the order carries no meaning and no converter can shadow
    /// another. The instances are stateless and are shared rather than constructed per options object.
    /// </remarks>
    public static IReadOnlyList<JsonConverter> All { get; } = Array.AsReadOnly(new JsonConverter[]
    {
        new BillingFrequencyJsonConverter(),
        new PermissionKeyJsonConverter(),
        new SortDirectionJsonConverter(),
    });

    /// <summary>Applies this policy to a serialiser options object.</summary>
    /// <param name="options">The options object to configure.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    public static void AddTo(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        AddTo(options.Converters);
    }

    /// <summary>Applies this policy to a converter collection.</summary>
    /// <param name="converters">The collection to add the converters to.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="converters"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Offered alongside the options overload because some hosting surfaces expose only the collection. A
    /// converter whose exact type is already present is skipped.
    /// </remarks>
    public static void AddTo(IList<JsonConverter> converters)
    {
        ArgumentNullException.ThrowIfNull(converters);

        foreach (JsonConverter converter in All)
        {
            if (Contains(converters, converter.GetType()))
            {
                continue;
            }

            converters.Add(converter);
        }
    }

    /// <summary>Reports whether a converter of the given exact type is already registered.</summary>
    /// <param name="converters">The collection to inspect.</param>
    /// <param name="converterType">The converter type to look for.</param>
    /// <returns><see langword="true"/> when the collection already holds a converter of that exact type.</returns>
    /// <remarks>
    /// Matched on the exact runtime type rather than on the type it converts, because <see
    /// cref="JsonConverter.CanConvert(Type)"/> would also match a caller's own converter for the same
    /// enumeration — and a caller that has deliberately registered one should keep it rather than have this
    /// policy decide it is a duplicate.
    /// </remarks>
    private static bool Contains(IList<JsonConverter> converters, Type converterType)
    {
        for (int index = 0; index < converters.Count; index++)
        {
            if (converters[index].GetType() == converterType)
            {
                return true;
            }
        }

        return false;
    }
}
