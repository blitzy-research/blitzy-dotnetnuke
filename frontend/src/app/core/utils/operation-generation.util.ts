/**
 * A monotonic ticket that says whether the asynchronous operation now answering is still the one
 * the application is waiting for.
 *
 * ---------------------------------------------------------------------------------------------------
 * THE DEFECT THIS CLOSES
 *
 * Every screen in this application that addresses a record by an identifier in the URL shares one
 * route configuration with every other visit to that screen. Moving from `/roles/5` to `/roles/9`
 * therefore does NOT recreate the component: the route parameter changes, an effect re-reads, and the
 * same component instance and the same root-provided store serve both records in turn.
 *
 * That makes the following ordinary and undetectable:
 *
 *     read role 5  ──────────────────────────────────────────►  answers SECOND
 *     read role 9  ────────────────►  answers FIRST
 *
 * Nothing about a slower first request is unusual — a cold cache, a larger row set, a retried
 * connection. But with an unconditional commit, role 5's response lands last and WINS, so the screen
 * holds role 5's data while the route, the page heading and the submit button all say role 9. The
 * operator then presses save, and the request combines role 9's route key with role 5's field values:
 * a cross-record overwrite, executed with full authority, reported as a success, and invisible in the
 * audit trail because every individual request was well-formed.
 *
 * The same shape reaches destructive and exfiltrating sinks. A membership row belonging to role 5 can
 * be rendered under role 9 and then removed with role 9's key; an export produced for module 5 can be
 * delivered under module 9's filename.
 *
 * ---------------------------------------------------------------------------------------------------
 * WHY CANCELLATION ALONE IS NOT ENOUGH
 *
 * Releasing the previous request's handle before starting the next one — which every store here now
 * does — closes the common case, and it is the stronger fix where it applies, because it removes the
 * possibility of a late commit rather than testing for one. It does not close all of them:
 *
 * - A handle is per-SLICE, but a screen may hold several slices whose reads were started by different
 *   commands. Cancelling one does not invalidate what another has already scheduled to commit.
 * - Clearing a slice, or purging the store at sign-out, changes what a pending response MEANS without
 *   there necessarily being a newer request whose dispatch would have cancelled it.
 * - A component that reads a shared store observes whatever that store most recently published. It has
 *   no handle to release and cannot cancel anything, so it needs a way to ask "is what I am looking at
 *   the answer to the question I asked?"
 * - Cancellation is a promise about a transport. This is a promise about a COMMIT, which is the thing
 *   that actually causes the damage.
 *
 * So the two are layered rather than alternatives: cancel to prevent the delivery, and check the ticket
 * before acting on any delivery that happens anyway.
 *
 * ---------------------------------------------------------------------------------------------------
 * WHY A TICKET AND NOT AN IDENTIFIER COMPARISON
 *
 * Comparing the answered record's identifier against the wanted one is a useful SECOND check and is
 * applied alongside this wherever the response carries an identifier. It cannot replace the ticket:
 *
 * - Not every response carries one. A settings bag, an export document and an empty `204` answer to a
 *   write carry no key at all, so there is nothing to compare.
 * - Re-reading the SAME record legitimately produces two responses with identical identifiers, and only
 *   the newer may win. An identifier comparison cannot tell them apart; a ticket can.
 * - An identifier comparison cannot express "this operation was abandoned", which is what a clear or a
 *   sign-out means.
 *
 * ---------------------------------------------------------------------------------------------------
 * PROVENANCE
 *
 * The mechanism is not invented here. It is the same shape as
 * `core/services/token-storage.service.ts`, whose session generation every late authentication callback
 * tests itself against, and as the phase ticket in `core/state/auth.store.ts`. This class exists so
 * that the five sites needing it share one implementation instead of five subtly different ones — the
 * failure mode being an off-by-one in one copy that admits exactly the response it was meant to refuse.
 *
 * It is a plain class rather than an injectable service: an instance belongs to ONE slice of ONE store
 * or component, several are held side by side, and none of them wants a lifetime managed by the
 * injector. Nothing here touches the router, the DOM or the transport, so every branch is directly
 * testable with no test bed at all.
 *
 * @example
 * ```ts
 * private readonly roleReads = new OperationGeneration();
 *
 * selectRole(roleId: number): void {
 *   const ticket = this.roleReads.begin();
 *
 *   this.service.getRole(roleId).subscribe((role) => {
 *     // Refused when a newer read started, when the slice was cleared, or at sign-out.
 *     if (!this.roleReads.isCurrent(ticket)) {
 *       return;
 *     }
 *
 *     this._selectedRole.set(role);
 *   });
 * }
 * ```
 */
export class OperationGeneration {
  /**
   * The ticket most recently issued.
   *
   * Starts at zero, which is a value {@link OperationGeneration.begin} never returns. Zero is refused
   * explicitly by {@link OperationGeneration.isCurrent} rather than merely by arithmetic, because on a
   * cold instance the counter IS zero and a bare comparison would accept a ticket nobody was issued.
   * The same "the first ticket is 1, and 0 matches nothing" reasoning governs the phase ticket in
   * `core/state/auth.store.ts`.
   */
  private issued = 0;

  /**
   * Issues the ticket for an operation that is starting, invalidating every earlier one.
   *
   * A PRE-increment, so the first ticket ever issued is 1 and never 0.
   *
   * Call this at DISPATCH — beside the request, before anything is awaited — and hold the result in a
   * local. Holding it in a field would defeat the whole mechanism, because the next dispatch would
   * overwrite the value the earlier callback is going to compare against.
   *
   * @returns The ticket the operation must present to {@link OperationGeneration.isCurrent}.
   */
  begin(): number {
    this.issued += 1;

    return this.issued;
  }

  /**
   * Whether the operation holding this ticket is still the one being waited for.
   *
   * ⚠ A NON-POSITIVE TICKET IS ALWAYS REFUSED, and the test is explicit rather than a consequence of
   * the counter's value. Relying on the counter alone was wrong in exactly one state and it is the
   * state every instance starts in: before the first {@link OperationGeneration.begin}, `issued` is
   * still 0, so a comparison against 0 alone would answer TRUE and accept a ticket nobody had been
   * issued. That is the failure this guard exists to prevent — a call site that forgot to call `begin`
   * and left its ticket field at its zero initialiser would have had its commit ACCEPTED on a cold
   * instance, which makes the omission invisible instead of loud.
   *
   * @param ticket The value {@link OperationGeneration.begin} returned at dispatch.
   * @returns True only when the ticket was genuinely issued, no later operation has started, and
   * nothing has been invalidated since.
   */
  isCurrent(ticket: number): boolean {
    return ticket > 0 && this.issued === ticket;
  }

  /**
   * Abandons whatever is outstanding, so that no ticket already issued can still be current.
   *
   * For the cases where an operation stops being wanted without a replacement being started: a slice
   * being cleared, and the whole store being purged when a session ends. Without this, a response
   * arriving after a clear would find its ticket still current and would repopulate precisely what the
   * clear had just discarded.
   *
   * Idempotent in effect — calling it twice simply invalidates twice — which is what lets the
   * overlapping teardown paths each call it without checking whether another already had.
   */
  invalidate(): void {
    this.issued += 1;
  }
}
