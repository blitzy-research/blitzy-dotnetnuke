/**
 * A monotonic ticket that says whether the asynchronous operation now answering is still the one the
 * application is waiting for. THE DEFECT THIS CLOSES Every screen in this application that addresses a
 * record by an identifier in the URL shares one route configuration with every other visit to that
 * screen.
 */
export class OperationGeneration {
  /**
   * The ticket most recently issued. Starts at zero, which is a value {@link OperationGeneration.begin}
   * never returns.
   */
  private issued = 0;

  /**
   * Issues the ticket for an operation that is starting, invalidating every earlier one. A PRE-increment,
   * so the first ticket ever issued is 1 and never 0.
   *
   * @returns The ticket the operation must present to {@link OperationGeneration.isCurrent}.
   */
  begin(): number {
    this.issued += 1;

    return this.issued;
  }

  /**
   * Whether the operation holding this ticket is still the one being waited for. ⚠ A NON-POSITIVE TICKET
   * IS ALWAYS REFUSED, and the test is explicit rather than a consequence of the counter's value.
   *
   * @param ticket The value {@link OperationGeneration.begin} returned at dispatch.
   * @returns True only when the ticket was genuinely issued, no later operation has started, and nothing
   * has been invalidated since.
   */
  isCurrent(ticket: number): boolean {
    return ticket > 0 && this.issued === ticket;
  }

  /**
   * Abandons whatever is outstanding, so that no ticket already issued can still be current. For the
   * cases where an operation stops being wanted without a replacement being started: a slice being
   * cleared, and the whole store being purged when a session ends.
   */
  invalidate(): void {
    this.issued += 1;
  }
}
