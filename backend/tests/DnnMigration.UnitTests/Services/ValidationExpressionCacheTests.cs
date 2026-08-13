using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Common;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Services;

/// <summary>
/// Guards the eviction bound on the process-wide cache of tenant-authored validation expressions.
/// </summary>
/// <remarks>
/// <para>
/// INFO-01. A profile property's validation expression, and the membership e-mail rule, are TENANT DATA -
/// authored by any account entitled to write those definitions - and the compiled-expression cache that holds
/// them is static, so it is shared by every tenant the process serves. At its ceiling the cache used to call
/// <c>Clear()</c>. Nothing grew without bound, which is why this was a hardening note rather than a defect,
/// but the cost of reaching the ceiling fell on the wrong party: one privileged writer introducing distinct
/// expressions could discard every compiled expression belonging to OTHER tenants, each of which then paid a
/// recompilation on its next validation.
/// </para>
/// <para>
/// The fix removes exactly one entry per newly admitted expression, so a writer can displace no more than the
/// work it brought. THAT PROPORTIONALITY IS WHAT THESE FACTS MEASURE, and they are written to fail loudly
/// against the old behaviour: a wholesale clear leaves the cache holding a single entry, so the observed count
/// after an admission at capacity is the discriminator.
/// </para>
/// <para>
/// The cache is reached by reflection because it is deliberately private static implementation detail - the
/// alternative, widening it for a test, would make an internal bound part of the service's surface. The
/// ceiling is read from the constant rather than restated, so the facts cannot drift from the implementation.
/// Every key these facts introduce is removed again in a <see langword="finally"/> block, because the cache
/// outlives the test class and a test that permanently occupied a shared cache would slow every later
/// validation in the run.
/// </para>
/// </remarks>
public sealed class ValidationExpressionCacheTests
{
    /// <summary>Prefix on every expression these facts introduce, so the additions can be withdrawn again.</summary>
    private const string KeyPrefix = "^info01-";

    /// <summary>The cache itself.</summary>
    private static readonly ConcurrentDictionary<string, Regex> Cache = ReadCache();

    /// <summary>The declared ceiling, read from the implementation rather than restated here.</summary>
    private static readonly int Ceiling = ReadCeiling();

    /// <summary>The private compile-and-cache entry point under test.</summary>
    private static readonly MethodInfo GetValidationExpression = ReadMethod();

    /// <summary>
    /// Admitting an expression while the cache is at its ceiling displaces at most ONE entry.
    /// </summary>
    /// <remarks>
    /// This is the proportionality property stated as a measurement. Under the previous wholesale clear the
    /// count would fall from the ceiling to 1, so the delta is the whole difference between the two
    /// behaviours.
    /// </remarks>
    [Fact]
    public void AdmittingAnExpressionAtCapacityDisplacesAtMostOneEntry()
    {
        List<string> introduced = new();

        try
        {
            FillToCeiling(introduced);

            int before = Cache.Count;
            before.Should().BeGreaterOrEqualTo(
                Ceiling,
                "the cache must actually be at its ceiling for this fact to be measuring anything");

            string admitted = Admit(introduced, "at-capacity");

            Cache.Count.Should().BeGreaterOrEqualTo(
                before - 1,
                "one newly admitted expression may displace at most one existing entry; a wholesale clear " +
                $"would have left roughly a single entry in place of {before}");

            Cache.ContainsKey(admitted).Should().BeTrue(
                "the expression whose admission triggered the eviction must itself be retained, or the " +
                "eviction would have achieved nothing");
        }
        finally
        {
            Withdraw(introduced);
        }
    }

    /// <summary>
    /// The cache saturates at its ceiling and stays there, rather than emptying and refilling.
    /// </summary>
    /// <remarks>
    /// Sustained pressure is the case the old behaviour handled worst: every admission past the ceiling
    /// emptied the cache, so a writer introducing many expressions in succession repeatedly reduced every
    /// other tenant to a cold cache. Here the occupancy is expected to hold at the ceiling throughout.
    /// </remarks>
    [Fact]
    public void SustainedAdmissionsHoldTheCacheAtItsCeilingRatherThanEmptyingIt()
    {
        List<string> introduced = new();

        try
        {
            FillToCeiling(introduced);

            for (int i = 0; i < 16; i++)
            {
                Admit(introduced, FormattableString.Invariant($"sustained-{i}"));

                Cache.Count.Should().BeGreaterThan(
                    Ceiling / 2,
                    "occupancy must hold near the ceiling under sustained pressure; a wholesale clear would " +
                    "collapse it to a single entry on every admission past the ceiling");
            }

            Cache.Count.Should().BeLessOrEqualTo(
                Ceiling,
                "the ceiling must still bound the cache - proportionate eviction must not become no eviction");
        }
        finally
        {
            Withdraw(introduced);
        }
    }

    /// <summary>Admits distinct expressions until the cache sits at its ceiling.</summary>
    /// <param name="introduced">Collects every key added, so it can be withdrawn afterwards.</param>
    private static void FillToCeiling(List<string> introduced)
    {
        for (int i = 0; Cache.Count < Ceiling && i < Ceiling * 2; i++)
        {
            Admit(introduced, FormattableString.Invariant($"fill-{i}"));
        }
    }

    /// <summary>Compiles and caches one distinct, valid expression.</summary>
    /// <param name="introduced">Collects the key added.</param>
    /// <param name="discriminator">Makes the expression unique.</param>
    /// <returns>The expression admitted.</returns>
    private static string Admit(List<string> introduced, string discriminator)
    {
        string expression = string.Concat(KeyPrefix, discriminator, "[a-z]{1,3}$");

        var result = (Result<Regex>)GetValidationExpression.Invoke(null, new object[] { expression })!;

        result.IsSuccess.Should().BeTrue(
            "the fact needs a VALID expression so that admission is the only thing being measured");

        introduced.Add(expression);

        return expression;
    }

    /// <summary>Removes every key these facts introduced, leaving the shared cache as it was found.</summary>
    /// <param name="introduced">The keys to withdraw.</param>
    private static void Withdraw(IEnumerable<string> introduced)
    {
        foreach (string key in introduced)
        {
            Cache.TryRemove(key, out _);
        }
    }

    /// <summary>Reads the private static cache.</summary>
    /// <returns>The cache.</returns>
    /// <exception cref="InvalidOperationException">The field has been renamed or removed.</exception>
    private static ConcurrentDictionary<string, Regex> ReadCache()
    {
        FieldInfo field = typeof(UserService).GetField(
            "ValidationExpressionCache",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "UserService no longer declares a static ValidationExpressionCache field. If the cache moved, " +
                "move this guard with it - the eviction bound it protects is INFO-01 and is not optional.");

        return (ConcurrentDictionary<string, Regex>)field.GetValue(null)!;
    }

    /// <summary>Reads the declared ceiling.</summary>
    /// <returns>The ceiling.</returns>
    /// <exception cref="InvalidOperationException">The constant has been renamed or removed.</exception>
    private static int ReadCeiling()
    {
        FieldInfo field = typeof(UserService).GetField(
            "ValidationExpressionCacheMaximum",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "UserService no longer declares ValidationExpressionCacheMaximum. The cache must keep a " +
                "declared ceiling: without one the eviction has no trigger and the cache grows unbounded.");

        return Convert.ToInt32(field.GetRawConstantValue(), CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the private compile-and-cache method.</summary>
    /// <returns>The method.</returns>
    /// <exception cref="InvalidOperationException">The method has been renamed or removed.</exception>
    private static MethodInfo ReadMethod()
    {
        return typeof(UserService).GetMethod(
            "GetValidationExpression",
            BindingFlags.NonPublic | BindingFlags.Static,
            new[] { typeof(string) })
            ?? throw new InvalidOperationException(
                "UserService no longer declares GetValidationExpression(string). It is the only route into " +
                "the expression cache, so this guard cannot measure the eviction bound without it.");
    }
}
