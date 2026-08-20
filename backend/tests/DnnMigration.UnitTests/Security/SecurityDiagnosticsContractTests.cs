using System.Reflection;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Security;

/// <summary>
/// Holds the diagnostics contract to the shape that makes credential material, request payloads and
/// exception text impossible to record through it.
/// </summary>
/// <remarks>
/// WHY THE SHAPE IS THE GUARANTEE, AND WHY IT IS WORTH A TEST OF ITS OWN. This contract exists because the
/// Application layer has no logger and must still be able to report a security-relevant anomaly.
/// </remarks>
public class SecurityDiagnosticsContractTests
{
    /// <summary>The contract declares exactly one operation.</summary>
    [Fact]
    public void Contract_DeclaresExactlyOneOperation()
    {
        typeof(ISecurityDiagnostics).GetMethods().Select(operation => operation.Name)
            .Should()
            .BeEquivalentTo([nameof(ISecurityDiagnostics.Record)]);

        typeof(ISecurityDiagnostics).GetProperties().Should().BeEmpty(
            "state on a diagnostics contract would be state a caller could read back");
        typeof(ISecurityDiagnostics).GetEvents().Should().BeEmpty();
    }

    /// <summary>
    /// The one operation accepts a closed occurrence, two optional identifiers and a code, and nothing
    /// else.
    /// </summary>
    [Fact]
    public void Record_AcceptsOnlyAClosedOccurrenceIdentifiersAndACode()
    {
        MethodInfo record = typeof(ISecurityDiagnostics).GetMethod(nameof(ISecurityDiagnostics.Record))!;

        record.ReturnType.Should().Be(typeof(void), "recording is fire-and-forget and awaits nothing");

        record.GetParameters().Select(parameter => (parameter.Name, parameter.ParameterType))
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    ("occurrence", typeof(SecurityDiagnosticEvent)),
                    ("portalId", typeof(int?)),
                    ("userId", typeof(int?)),
                    ("reasonCode", typeof(string)),
                },
                options => options.WithStrictOrdering());
    }

    /// <summary>No parameter can carry an exception, an arbitrary object or a collection of them.</summary>
    [Fact]
    public void Record_AdmitsNoExceptionObjectOrFormatArgument()
    {
        ParameterInfo[] parameters = typeof(ISecurityDiagnostics)
            .GetMethod(nameof(ISecurityDiagnostics.Record))!
            .GetParameters();

        parameters.Should().OnlyContain(
            parameter => parameter.ParameterType != typeof(object)
                && parameter.ParameterType != typeof(object[])
                && !typeof(Exception).IsAssignableFrom(parameter.ParameterType));

        parameters.Should().OnlyContain(
            parameter => !parameter.IsDefined(typeof(ParamArrayAttribute), false),
            "a params array is the usual way a logging-shaped surface acquires an unbounded payload");
    }

    /// <summary>
    /// Every occurrence the enumeration declares is distinctly valued, so none can be conflated with
    /// another.
    /// </summary>
    [Fact]
    public void Occurrences_AreDistinctlyValued()
    {
        SecurityDiagnosticEvent[] occurrences = Enum.GetValues<SecurityDiagnosticEvent>();

        occurrences.Should().NotBeEmpty();
        occurrences.Select(occurrence => (int)occurrence).Should().OnlyHaveUniqueItems();
        occurrences.Select(occurrence => occurrence.ToString()).Should().OnlyHaveUniqueItems();
    }
}
