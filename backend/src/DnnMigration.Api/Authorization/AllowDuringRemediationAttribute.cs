namespace DnnMigration.Api.Authorization;

/// <summary>
/// Identifies an endpoint that may be reached while an authenticated account is completing blocking
/// credential or profile remediation.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AllowDuringRemediationAttribute : Attribute
{
    /// <summary>Initialises a new instance of the attribute.</summary>
    /// <param name="kind">The remediation purpose served by the endpoint.</param>
    public AllowDuringRemediationAttribute(RemediationEndpointKind kind)
    {
        Kind = kind;
    }

    /// <summary>Gets the remediation purpose served by the endpoint.</summary>
    public RemediationEndpointKind Kind { get; }
}

/// <summary>The narrowly permitted endpoint purposes while remediation is active.</summary>
public enum RemediationEndpointKind
{
    /// <summary>Authentication lifecycle operations such as refresh, logout and the caller snapshot.</summary>
    Authentication = 0,

    /// <summary>The account owner's credential-change operation.</summary>
    Password = 1,

    /// <summary>The account owner's profile read and update operations.</summary>
    Profile = 2,
}
