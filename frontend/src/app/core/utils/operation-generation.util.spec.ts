/**
 * Specification for `core/utils/operation-generation.util.ts`. The class is three short members, so what
 * is proven here is not its arithmetic but the four properties every call site depends on and no compiler
 * can check: 1.
 */
import { OperationGeneration } from './operation-generation.util';

describe('OperationGeneration', () => {
  let generation: OperationGeneration;

  beforeEach(() => {
    generation = new OperationGeneration();
  });

  describe('issuing tickets', () => {
    it('treats a freshly issued ticket as current', () => {
      const ticket = generation.begin();

      expect(generation.isCurrent(ticket)).toBeTrue();
    });

    it('never issues zero, so a zero-initialised ticket is refused', () => {
      // ⚠ THE LOAD-BEARING CASE FOR THE PRE-INCREMENT. A field declared `private ticket = 0` that was never
      // assigned must not be accepted as the first operation, because that would make a forgotten `begin()`
      // invisible: the commit would happen and look correct.
      expect(generation.isCurrent(0))
        .withContext('nothing holds ticket zero before any operation starts')
        .toBeFalse();

      generation.begin();

      expect(generation.isCurrent(0))
        .withContext('and it is still refused once operations have started')
        .toBeFalse();
    });

    it('issues one as the first ticket', () => {
      expect(generation.begin()).toBe(1);
    });

    it('issues a distinct ticket for every operation', () => {
      const tickets = [generation.begin(), generation.begin(), generation.begin()];

      expect(new Set(tickets).size)
        .withContext('two operations must never share a ticket')
        .toBe(tickets.length);
    });
  });

  describe('superseding an operation', () => {
    it('refuses an earlier ticket once a later operation starts', () => {
      // THE CENTRAL CASE. Read A, then read B; A answers second and must be refused, which is what
      // stops role A's record being committed while the route says role B.
      const readA = generation.begin();
      const readB = generation.begin();

      expect(generation.isCurrent(readA))
        .withContext('the superseded read may not commit, however late it answers')
        .toBeFalse();
      expect(generation.isCurrent(readB))
        .withContext('the newest read is the one being waited for')
        .toBeTrue();
    });

    it('keeps refusing a superseded ticket no matter how many operations follow', () => {
      const stale = generation.begin();

      for (let index = 0; index < 25; index += 1) {
        generation.begin();
      }

      // A ticket does not become current again by being far enough behind. Comparing "not equal"
      // rather than "less than" is what makes that true without any wrap-around reasoning.
      expect(generation.isCurrent(stale)).toBeFalse();
    });

    it('refuses a ticket from a re-read of the SAME record', () => {
      const firstRead = generation.begin();
      const secondRead = generation.begin();

      expect(generation.isCurrent(firstRead)).toBeFalse();
      expect(generation.isCurrent(secondRead)).toBeTrue();
    });
  });

  describe('invalidating without a replacement', () => {
    it('refuses an outstanding ticket after invalidation', () => {
      const outstanding = generation.begin();

      generation.invalidate();

      expect(generation.isCurrent(outstanding))
        .withContext('a response arriving after a clear must not repopulate the slice')
        .toBeFalse();
    });

    it('can be invalidated repeatedly, because the teardown paths overlap', () => {
      const outstanding = generation.begin();

      expect(() => {
        generation.invalidate();
        generation.invalidate();
      }).not.toThrow();

      expect(generation.isCurrent(outstanding)).toBeFalse();
    });

    it('can be invalidated before anything has started', () => {
      // A purge reaches a store whose screens were never opened, so invalidation must be safe on a
      // completely cold instance.
      expect(() => {
        generation.invalidate();
      }).not.toThrow();

      const afterwards = generation.begin();

      expect(generation.isCurrent(afterwards))
        .withContext('and the instance is still usable afterwards')
        .toBeTrue();
    });

    it('leaves a ticket issued after invalidation current', () => {
      generation.begin();
      generation.invalidate();

      const restarted = generation.begin();

      expect(generation.isCurrent(restarted))
        .withContext('invalidating does not poison the instance for future operations')
        .toBeTrue();
    });
  });

  describe('independence between instances', () => {
    it('does not let one slice invalidate another', () => {
      // Several of these are held side by side in one store — the module read, its settings and its
      // export each have their own. Reading the settings must not invalidate the module read.
      const moduleReads = new OperationGeneration();
      const settingsReads = new OperationGeneration();

      const moduleTicket = moduleReads.begin();

      settingsReads.begin();
      settingsReads.invalidate();

      expect(moduleReads.isCurrent(moduleTicket))
        .withContext('an unrelated slice must not cancel this one')
        .toBeTrue();
    });

    it('does not accept a ticket issued by a different instance', () => {
      const first = new OperationGeneration();
      const second = new OperationGeneration();

      const firstTicket = first.begin();

      second.begin();
      second.begin();

      expect(second.isCurrent(firstTicket))
        .withContext('tickets are not interchangeable across instances')
        .toBeFalse();
    });
  });
});
