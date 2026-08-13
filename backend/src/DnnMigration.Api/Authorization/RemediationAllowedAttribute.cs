namespace DnnMigration.Api.Authorization;

/// <summary>
/// Marks an endpoint that a caller with an outstanding mandatory credential or profile remediation may use.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class RemediationAllowedAttribute : Attribute
{
}
