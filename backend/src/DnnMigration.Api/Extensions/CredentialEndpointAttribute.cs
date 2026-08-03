namespace DnnMigration.Api.Extensions;

/// <summary>
/// Marks an endpoint that handles a credential, bringing it under the credential window and the
/// process-wide credential concurrency bound.
/// </summary>
/// <remarks>
/// <para>
/// WHY A MARKER EXISTS AT ALL. The credential limits were originally applied by matching whole segments of
/// the request PATH against a word list, and a path is not a statement about what an action does. The list
/// missed three endpoints that hash a credential - account creation on <c>/users</c>, tenant provisioning on
/// <c>/portals</c>, which hashes the administrator credential it creates, and the administrative credential
/// reset, whose <c>password-reset</c> segment is not equal to <c>password</c> and therefore matched nothing
/// under whole-segment comparison. All three were unlimited, so the expensive half of the work this control
/// exists to bound was reachable without any bound at all.
/// </para>
/// <para>
/// WHY THE OPT-IN POLICY WAS NOT THE ANSWER. A named policy did exist and was documented as the way to bring
/// such an endpoint under a window explicitly, but its partitioner asked the same path matcher and returned
/// the shared no-limit partition for exactly the paths the policy was written to cover - so declaring it on
/// any of the three would have changed nothing. That is fixed too: the named policy now applies its window
/// unconditionally, because an author who declares it has already stated what the heuristic was guessing at.
/// </para>
/// <para>
/// WHY BOTH MECHANISMS SURVIVE. This attribute is endpoint metadata, read by the global limiter's classifier,
/// so a marked action is bounded by BOTH limiters - the window that makes guessing slow and the concurrency
/// bound that keeps processor and memory consumption independent of the caller. The path matcher is retained
/// underneath it and is deliberately not narrowed: it is what still catches an endpoint whose author forgets
/// this attribute, and it is the only classifier available for a request that matched no endpoint at all.
/// Being classified by both costs nothing, because the classifier answers one question and the two sources
/// only ever agree by making the answer <see langword="true"/>.
/// </para>
/// <para>
/// WHAT QUALIFIES. An action that hashes a credential, verifies one, or exchanges or revokes a token derived
/// from one. Hashing is the load-bearing case: it is deliberately expensive, so an unbounded hashing endpoint
/// lets a caller choose how much of this process's time is spent. An action that merely records a FLAG about
/// a credential - requiring a change at next sign-in, unlocking an account - does not qualify, because it
/// performs no cryptographic work and bounding it would spend a shared budget on an administrative click.
/// </para>
/// <para>
/// Applied to an action, never to a controller. Every controller that owns a credential action also owns
/// actions that are ordinary reads, and a class-level mark would spend the credential budget on those.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
internal sealed class CredentialEndpointAttribute : Attribute
{
}
