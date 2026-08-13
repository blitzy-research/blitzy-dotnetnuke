namespace DnnMigration.Api.Extensions;

/// <summary>
/// Marks an endpoint that handles a credential, bringing it under the credential window and the
/// process-wide credential concurrency bound.
/// </summary>
/// <remarks>
/// <para>
/// WHY A MARKER EXISTS AT ALL. The credential limits were originally applied by matching whole segments of
/// the request PATH against a word list, and a path is not a statement about what an action does.
/// </para>
/// <para>
/// WHY THE OPT-IN POLICY WAS NOT THE ANSWER. A named policy did exist and was documented as the way to
/// bring such an endpoint under a window explicitly, but its partitioner asked the same path matcher and
/// returned the shared no-limit partition for exactly the paths the policy was written to cover - so
/// declaring it on any of the three would have changed nothing.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
internal sealed class CredentialEndpointAttribute : Attribute
{
}
