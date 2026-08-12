using System.Reflection;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Security;

/// <summary>
/// Holds the diagnostics contract to the shape that makes credential material, request payloads and exception
/// text impossible to record through it.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE SHAPE IS THE GUARANTEE, AND WHY IT IS WORTH A TEST OF ITS OWN. This contract exists because the
/// Application layer has no logger and must still be able to report a security-relevant anomaly. The danger in
/// giving a layer such a route is obvious the moment it accepts a message: a free-form string is how a password,
/// a connection string, a request body, a rendered SQL statement or an exception's text ends up in a log, and no
/// amount of care at the call sites prevents the next one from doing it. The protection is therefore structural -
/// there is NO PARAMETER through which such a value could travel - and a structural guarantee deserves an
/// assertion, because it can be undone by a single well-meaning parameter added in a hurry.
/// </para>
/// <para>
/// An exact-set assertion is used in preference to a list of forbidden shapes, and it is the stronger of the two:
/// a denylist only rejects the mistakes somebody anticipated, whereas this fails the moment the surface changes
/// at all.
/// </para>
/// <para>
/// WHAT THIS FILE DOES NOT COVER, AND WHERE THAT COVERAGE LIVES. The implementation is out of reach from this
/// project, and deliberately so: AAP 0.5.2.2 fixes the unit-test project's only edge to the Application layer,
/// so nothing here can name a type in Infrastructure. What is asserted below is therefore the contract every
/// implementation is held to, which is exactly the layer at which a structural guarantee lives - and it is
/// blind, by construction, to everything the implementation DOES with what it is handed. A reason code
/// carrying a newline forging a second log line, an exception message written verbatim as though it were a
/// code, the wrong level, a template composed by interpolation instead of held constant, or a logging provider
/// whose failure escapes and fails the request: every one of those would leave this file green.
/// <c>DnnMigration.IntegrationTests.Services.SecurityDiagnosticsTests</c> is where the concrete recorder is
/// resolved and driven, and it covers each of those. Neither suite is sufficient alone, which is why both
/// exist rather than one standing in for the other.
/// </para>
/// </remarks>
public class SecurityDiagnosticsContractTests
{
    /// <summary>
    /// The contract declares exactly one operation.
    /// </summary>
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
    /// The one operation accepts a closed occurrence, two optional identifiers and a code, and nothing else.
    /// </summary>
    /// <remarks>
    /// The parameter types are pinned individually rather than merely counted, because the count is not what
    /// protects anything: a fourth parameter typed <c>object</c> or <c>Exception</c> would keep the count and
    /// destroy the guarantee.
    /// </remarks>
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

    /// <summary>
    /// No parameter can carry an exception, an arbitrary object or a collection of them.
    /// </summary>
    /// <remarks>
    /// The positive form above already implies this, and it is asserted separately anyway because it is the
    /// property a reader of this file is looking for: an exception parameter would admit
    /// <c>Exception.Message</c> and <c>ToString</c>, both of which routinely carry statement text, file paths
    /// and connection detail, and an <c>object</c> or format-argument parameter would admit anything at all.
    /// </remarks>
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
    /// Every occurrence the enumeration declares is distinctly valued, so none can be conflated with another.
    /// </summary>
    /// <remarks>
    /// Two members sharing a value would be indistinguishable in a log while looking distinct in source, which
    /// is the quietest possible way for one anomaly to be reported as another.
    /// </remarks>
    [Fact]
    public void Occurrences_AreDistinctlyValued()
    {
        SecurityDiagnosticEvent[] occurrences = Enum.GetValues<SecurityDiagnosticEvent>();

        occurrences.Should().NotBeEmpty();
        occurrences.Select(occurrence => (int)occurrence).Should().OnlyHaveUniqueItems();
        occurrences.Select(occurrence => occurrence.ToString()).Should().OnlyHaveUniqueItems();
    }
}
