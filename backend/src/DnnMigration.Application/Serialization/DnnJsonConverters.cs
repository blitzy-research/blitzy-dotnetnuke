using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnnMigration.Application.Serialization;

/// <summary>
/// The central <c>System.Text.Json</c> converter policy for the contracts in this assembly: one
/// explicit converter per enumeration whose external representation is fixed by legacy data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a registration surface exists at all.</b> Two enumerations need a pinned wire form and
/// both failures are silent — an unregistered converter produces a plausible-looking number instead
/// of an error — so registering one and forgetting the other is a mistake nothing downstream would
/// report. Exposing the pair as a single unit makes the composition root's obligation one call it
/// cannot half-perform, rather than a list it has to keep in step with this folder.
/// </para>
/// <para>
/// <b>The policy is DEFINED here and APPLIED at the Api edge.</b> Every DTO folder in this assembly
/// records that the wire shape belongs to one central policy configured at the Api boundary, and
/// none carries a <c>[JsonConverter]</c> attribute. This type is the definition half: it names the
/// converters and adds them to an options object. The Api layer's service registration calls
/// <see cref="AddTo(JsonSerializerOptions)"/> for the MVC input and output formatters and for any
/// other options object that must round-trip these contracts — problem-details output and the
/// integration-test client among them. Placing the converters in this assembly rather than in Api
/// keeps them with the contracts they describe, so anything that serialises a DTO can apply the same
/// policy without depending on the web host.
/// </para>
/// <para>
/// <b>There is deliberately no blanket enumeration policy.</b>
/// <see cref="JsonStringEnumConverter"/> is NOT registered. It would claim every enumeration,
/// including <c>BillingFrequency</c>, whose legacy wire form is a single character and not its
/// member name — so correctness would depend on this list being ordered ahead of it, and a
/// reordering would silently change a wire contract. Registering exactly the types that need pinning
/// also leaves the remaining enumerations' wire form an explicit decision at the Api boundary rather
/// than one taken accidentally here.
/// </para>
/// <para>
/// <b>Adding twice is harmless.</b> <see cref="AddTo(IList{JsonConverter})"/> skips a converter whose
/// exact type is already present, so an options object configured by more than one path — the MVC
/// pipeline and a test fixture, say — ends up with one instance of each rather than duplicates.
/// Duplicates would not change behaviour, because the first match wins, but they make a converter
/// list harder to read when diagnosing a serialisation problem.
/// </para>
/// </remarks>
public static class DnnJsonConverters
{
    /// <summary>
    /// The converters this policy applies, in the order they are added.
    /// </summary>
    /// <remarks>
    /// Each converter claims exactly one type, so the order carries no meaning and no converter can
    /// shadow another. The instances are stateless and are shared rather than constructed per
    /// options object.
    /// </remarks>
    public static IReadOnlyList<JsonConverter> All { get; } = Array.AsReadOnly(new JsonConverter[]
    {
        new BillingFrequencyJsonConverter(),
        new PermissionKeyJsonConverter(),
    });

    /// <summary>
    /// Applies this policy to a serialiser options object.
    /// </summary>
    /// <param name="options">The options object to configure.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    public static void AddTo(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        AddTo(options.Converters);
    }

    /// <summary>
    /// Applies this policy to a converter collection.
    /// </summary>
    /// <param name="converters">The collection to add the converters to.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="converters"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Offered alongside the options overload because some hosting surfaces expose only the
    /// collection. A converter whose exact type is already present is skipped.
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

    /// <summary>
    /// Reports whether a converter of the given exact type is already registered.
    /// </summary>
    /// <param name="converters">The collection to inspect.</param>
    /// <param name="converterType">The converter type to look for.</param>
    /// <returns>
    /// <see langword="true"/> when the collection already holds a converter of that exact type.
    /// </returns>
    /// <remarks>
    /// Matched on the exact runtime type rather than on the type it converts, because
    /// <see cref="JsonConverter.CanConvert(Type)"/> would also match a caller's own converter for the
    /// same enumeration — and a caller that has deliberately registered one should keep it rather
    /// than have this policy decide it is a duplicate. An index loop rather than a LINQ predicate
    /// keeps the check allocation-free on a startup path that may run once per options object.
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
