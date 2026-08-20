using System.Globalization;
using System.Reflection;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Holds the failure-code classification table to the property that makes it worth having: every reason code
/// the solution can raise has a status DECLARED for it, rather than one inferred from how the code is spelled.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS SUITE EXISTS. The classification was previously a set of substring tests over the final dotted
/// segment of a code - a code containing <c>in_use</c> became a 409, one containing <c>notfound</c> became a
/// 404. Every code the solution actually emitted classified correctly, so no response was wrong; the defect
/// was that correctness depended on spelling. A code named <c>portal.alias_in_use_check_failed</c> would have
/// answered 409 where it meant 500, and a genuinely new failure class would have become a silent 400.
/// </para>
/// <para>
/// A lookup table removes that whole class of accident and introduces exactly one of its own: a code can be
/// added to a service and forgotten here, and it then answers the unclassified default. These facts are what
/// make that impossible to ship. They enumerate the reason-code constants of all four production assemblies
/// by reflection - the convention is a <c>const string</c> whose name ends in <c>Code</c>, and the solution
/// has no literal failure code at any call site - and require each one to be classified.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FailureCodeClassificationTests
{
    /// <summary>
    /// Constant values that end in <c>Code</c> and are NOT failure codes, each excluded for its own reason.
    /// </summary>
    /// <remarks>
    /// Excluded by VALUE rather than by declaration site, so moving a constant between files cannot silently
    /// reinstate it, and <see cref="EveryExclusion_StillNamesAConstantThatExists"/> fails if one of these
    /// stops being declared - which is what stops the list outliving its reasons.
    /// <list type="bullet">
    /// <item><c>en-US</c> - the default language tag on the portal options, a BCP-47 code and not a failure.</item>
    /// <item><c>SYSTEM_TAB</c>, <c>SYSTEM_MODULE_DEFINITION</c> - permission-scope labels naming what a grant
    /// is scoped to.</item>
    /// <item><c>credential_replacement_store_failure</c> - the <c>FailureCode</c> written onto an audit
    /// record when a credential replacement faults. The request itself answers under a code of its own, so
    /// this value never reaches a problem document.</item>
    /// <item><c>unrecognised-code</c> - what the security diagnostics substitute for a value that is not
    /// code-shaped, so it is a redaction rather than an outcome.</item>
    /// </list>
    /// </remarks>
    private static readonly string[] NonOutcomeCodeValues =
    {
        "en-US", "SYSTEM_TAB", "SYSTEM_MODULE_DEFINITION",
        "credential_replacement_store_failure", "unrecognised-code",
    };

    /// <summary>The four production assemblies, reached through one type each.</summary>
    private static readonly Assembly[] ProductionAssemblies =
    {
        typeof(Result).Assembly,
        typeof(IAuthService).Assembly,
        typeof(DnnMigration.Infrastructure.DependencyInjection).Assembly,
        typeof(ApiResults).Assembly,
    };

    /// <summary>Every reason-code constant in the solution has a declared status.</summary>
    /// <remarks>
    /// The assertion is <see cref="ApiResults.IsClassified"/> rather than a status comparison, because
    /// <c>400</c> is both a declared classification and the answer for a code nobody classified.
    /// </remarks>
    [Fact]
    public void EveryReasonCodeConstant_IsClassified()
    {
        IReadOnlyList<(string Owner, string Name, string Value)> codes = ReasonCodeConstants();

        codes.Should().HaveCountGreaterThan(
            100,
            "the reflection convention must actually be finding the constants; a near-empty set would make "
            + "this suite pass by finding nothing");

        List<string> unclassified = codes
            .Where(candidate => !ApiResults.IsClassified(candidate.Value))
            .Select(candidate => string.Format(
                CultureInfo.InvariantCulture,
                "{0}.{1} = \"{2}\"",
                candidate.Owner,
                candidate.Name,
                candidate.Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        unclassified.Should().BeEmpty(
            "a failure code with no entry in the classification table answers the unclassified default of "
            + "400, whatever it means. Add each code listed here to the group for the status its endpoint "
            + "publishes, or - if it is not an HTTP outcome at all - to this suite's exclusion list with the "
            + "reason it is not one");
    }

    /// <summary>An exclusion cannot outlive the constant it was written for.</summary>
    [Fact]
    public void EveryExclusion_StillNamesAConstantThatExists()
    {
        HashSet<string> declared = AllCodeConstants()
            .Select(candidate => candidate.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string excluded in NonOutcomeCodeValues)
        {
            declared.Should().Contain(
                excluded,
                "an exclusion for a constant that no longer exists is a licence nobody asked for, and the "
                + "next code that happens to carry this value would inherit it");
        }
    }

    /// <summary>No excluded value is classified, which is the other half of the exclusion being honest.</summary>
    [Fact]
    public void NoExcludedValue_IsAlsoClassified()
    {
        foreach (string excluded in NonOutcomeCodeValues)
        {
            ApiResults.IsClassified(excluded).Should().BeFalse(
                "a value cannot be both 'not an HTTP outcome' and classified with a status; one of the two "
                + "statements is wrong");
        }
    }

    /// <summary>
    /// The statuses that carry a contract obligation are pinned by whole code, so a table edit cannot move one
    /// quietly.
    /// </summary>
    /// <param name="failureCode">A code the solution raises.</param>
    /// <param name="expectedStatus">The status its endpoint publishes.</param>
    /// <remarks>
    /// Each row is a code that IS raised in production, deliberately: pinning a hypothetical code would only
    /// have tested the classifier's spelling rules, which is the mechanism this change removed.
    /// </remarks>
    [Theory]
    [InlineData("auth.invalid_credentials", StatusCodes.Status401Unauthorized)]
    [InlineData("auth.locked_out", StatusCodes.Status401Unauthorized)]
    [InlineData("role.protected", StatusCodes.Status403Forbidden)]
    [InlineData("user.delete.superuser-protected", StatusCodes.Status403Forbidden)]
    [InlineData("portal.not_found", StatusCodes.Status404NotFound)]
    [InlineData("user.not-found", StatusCodes.Status404NotFound)]
    [InlineData("role.name_duplicate", StatusCodes.Status409Conflict)]
    [InlineData("portal.last_remaining", StatusCodes.Status409Conflict)]
    [InlineData("user.approval.unchanged", StatusCodes.Status409Conflict)]
    [InlineData("portal.creation_failed", StatusCodes.Status500InternalServerError)]
    [InlineData("module.export_failed", StatusCodes.Status500InternalServerError)]
    [InlineData("auth.approval_store_unavailable", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("TOKEN_STORE_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("user.password.invalid", StatusCodes.Status400BadRequest)]
    [InlineData("module.content_type_mismatch", StatusCodes.Status400BadRequest)]
    public void ADeclaredCode_AnswersItsDeclaredStatus(string failureCode, int expectedStatus)
    {
        ApiResults.MapStatusCode(failureCode).Should().Be(expectedStatus);
    }

    /// <summary>
    /// The spelling of a code no longer decides anything, which is the whole point of the table.
    /// </summary>
    /// <param name="unknownCode">A code shaped like a classified one but declared nowhere.</param>
    /// <remarks>
    /// <c>portal.alias_in_use_check_failed</c> is the case that prompted the change: under substring matching
    /// it answered 409 because its name contains <c>in_use</c>, while it describes a check that failed. Each
    /// row here would have been mis-classified by resemblance and now answers the unclassified default.
    /// </remarks>
    [Theory]
    [InlineData("portal.alias_in_use_check_failed")]
    [InlineData("module.notfound_probe_unavailable")]
    [InlineData("role.duplicate_check_failed")]
    [InlineData("user.protected_flag_read_failed")]
    public void AnUndeclaredCode_IsNotClassifiedByResemblance(string unknownCode)
    {
        ApiResults.IsClassified(unknownCode).Should().BeFalse();
        ApiResults.MapStatusCode(unknownCode).Should().Be(
            StatusCodes.Status400BadRequest,
            "an unclassified code answers the default, and the default says 'correct the request' rather "
            + "than borrowing a meaning from a substring of its name");
    }

    /// <summary>A blank or absent code answers the default rather than throwing.</summary>
    /// <param name="code">The value a failed outcome carried.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankCode_AnswersTheDefault(string? code)
    {
        ApiResults.MapStatusCode(code).Should().Be(StatusCodes.Status400BadRequest);
    }

    /// <summary>Spelling variants of one code resolve to one classification.</summary>
    [Fact]
    public void SpellingVariantsOfOneCode_ClassifyIdentically()
    {
        int canonical = ApiResults.MapStatusCode("user.not_found");

        ApiResults.MapStatusCode("user.not-found").Should().Be(canonical);
        ApiResults.MapStatusCode("USER.NOT_FOUND").Should().Be(canonical);
        ApiResults.MapStatusCode("  user.not_found  ").Should().Be(canonical);
    }

    /// <summary>The code constants that are failure codes: every declared one, less the named exclusions.</summary>
    /// <returns>The owning type name, the field name and the value, for each failure code found.</returns>
    private static IReadOnlyList<(string Owner, string Name, string Value)> ReasonCodeConstants() =>
        AllCodeConstants()
            .Where(candidate => !NonOutcomeCodeValues.Contains(candidate.Value, StringComparer.Ordinal))
            .ToList();

    /// <summary>Collects every <c>const string</c> whose name ends in <c>Code</c>, from all four assemblies.</summary>
    /// <returns>The owning type name, the field name and the value, for each constant found.</returns>
    private static IReadOnlyList<(string Owner, string Name, string Value)> AllCodeConstants()
    {
        const BindingFlags flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        List<(string, string, string)> found = new();

        foreach (Assembly assembly in ProductionAssemblies)
        {
            foreach (Type type in assembly.GetTypes())
            {
                foreach (FieldInfo field in type.GetFields(flags))
                {
                    if (!field.IsLiteral
                        || field.IsInitOnly
                        || field.FieldType != typeof(string)
                        || !field.Name.EndsWith("Code", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (field.GetRawConstantValue() is string value && value.Length > 0)
                    {
                        found.Add((type.Name, field.Name, value));
                    }
                }
            }
        }

        return found;
    }
}
