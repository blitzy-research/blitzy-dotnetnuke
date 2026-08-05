namespace DnnMigration.Api.Authorization;

/// <summary>
/// Marks an endpoint that a caller with an outstanding mandatory credential or profile remediation may use.
/// </summary>
/// <remarks>
/// The attribute grants nothing by itself. Normal authentication and authorisation still run; it only tells
/// <c>RestrictedSessionMiddleware</c> that the endpoint is part of the narrow remediation surface.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class RemediationAllowedAttribute : Attribute
{
}
