import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';

import { DeferredOutcomeService } from './deferred-outcome.service';
import type { DeferredOutcome } from './deferred-outcome.service';
import { NotificationService } from './notification.service';

/**
 * THE SUBJECT: reporting the outcome of a write whose screen has already gone. ⚠ WHAT THIS SERVICE EXISTS
 * FOR IS A LIFETIME, WHICH IS WHY THE CASES BELOW LEAN ON WHERE THE WATCH IS CREATED RATHER THAN ON WHAT
 * IT SAYS. Every editing screen composes and announces its own outcome from an `effect` in its own
 * injection context, so the announcement dies with the component.
 */
describe('DeferredOutcomeService', () => {
  let service: DeferredOutcomeService;
  let notifications: NotificationService;
  let verdict: WritableSignal<DeferredOutcome>;

  /** The severities currently queued, newest last. */
  function queuedSeverities(): readonly string[] {
    return notifications.notifications().map((entry) => entry.severity);
  }

  /** The messages currently queued, newest last. */
  function queuedMessages(): readonly string[] {
    return notifications.notifications().map((entry) => entry.message);
  }

  /** Declines to word a refusal. */
  function declineFailure(): null {
    return null;
  }

  function flush(): void {
    TestBed.flushEffects();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(DeferredOutcomeService);
    notifications = TestBed.inject(NotificationService);
    verdict = signal<DeferredOutcome>('pending');
  });

  it('is a single root-provided instance', () => {
    // Two instances would give two screens two different watchers, and a service re-created per screen
    // would be destroyed by exactly the navigation this exists to survive.
    expect(TestBed.inject(DeferredOutcomeService))
      .withContext('one instance is shared by every caller')
      .toBe(service);
  });

  it('registers from outside any injection context, which is the property the whole mechanism rests on', () => {
    // ⚠ NOT A STYLE POINT. The caller registers from a destroy hook, by which time its own injection
    // context is gone; a watch that needed one would throw exactly when it was needed. Called here as a
    // plain method on an already-resolved instance, with no `runInInjectionContext` anywhere.
    expect(() => {
      service.announceWhenSettled(verdict, () => 'The role was created.', declineFailure);
    }).not.toThrow();
  });

  it('says nothing while the write is still in flight', () => {
    service.announceWhenSettled(verdict, () => 'The role was created.', declineFailure);
    flush();

    expect(queuedMessages()).withContext('nothing has settled yet').toEqual([]);
  });

  it('states the outcome once the write settles successfully', () => {
    service.announceWhenSettled(verdict, () => 'The role was created.', declineFailure);
    flush();

    verdict.set('succeeded');
    flush();

    expect(queuedMessages()).toEqual(['The role was created.']);
    expect(queuedSeverities()).withContext('a completed write is a success').toEqual(['success']);
  });

  it('composes the sentence at announce time, so it may name what arrived with the response', () => {
    // The create confirmation names the account the SERVER stored rather than what the form held, and that
    // value is not known when the watch is registered. A string captured at registration could not carry it.
    let stored: string | null = null;

    service.announceWhenSettled(verdict, () => `Account ${stored ?? '?'} was created.`, declineFailure);
    flush();

    stored = 'qa7probe';
    verdict.set('succeeded');
    flush();

    expect(queuedMessages()).toEqual(['Account qa7probe was created.']);
  });

  it('says nothing when the caller declines to word the outcome', () => {
    service.announceWhenSettled(verdict, () => null, declineFailure);
    flush();

    verdict.set('succeeded');
    flush();

    expect(queuedMessages())
      .withContext('a caller returning null states the outcome silently')
      .toEqual([]);
  });

  it('relays a refusal as one bounded sentence and its support reference', () => {
    service.announceWhenSettled(verdict, () => 'The role was created.', () => ({
      message: 'The role could not be created',
      reference: '4d19ae7c1b8f4e2a9d6c3f5b7a091e2d',
    }));
    flush();

    verdict.set('failed');
    flush();

    expect(queuedMessages()).toEqual([
      'The role could not be created Reference: 4d19ae7c1b8f4e2a9d6c3f5b7a091e2d',
    ]);
    expect(queuedSeverities()).withContext('the write did not happen').toEqual(['error']);

    const queued = notifications.notifications();

    expect(queued[0]?.reference)
      .withContext('the reference is carried as a reference, so it renders like every other one')
      .toBe('4d19ae7c1b8f4e2a9d6c3f5b7a091e2d');
  });

  it('relays a refusal that carried no reference, rather than inventing one', () => {
    // A transport failure has no document and therefore no reference. The sentence is still owed.
    service.announceWhenSettled(verdict, () => 'The role was created.', () => ({
      message: 'The role could not be created',
      reference: null,
    }));
    flush();

    verdict.set('failed');
    flush();

    expect(queuedMessages()).toEqual(['The role could not be created']);
    expect(notifications.notifications()[0]?.reference).toBeNull();
  });

  it('says nothing when the caller declines to word a refusal', () => {
    // The symmetry with the success path is deliberate: a caller that has already redirected, or that does
    // not want a particular refusal relayed, keeps the option of silence by returning null.
    service.announceWhenSettled(verdict, () => 'The role was created.', declineFailure);
    flush();

    verdict.set('failed');
    flush();

    expect(queuedMessages()).toEqual([]);
  });

  it('composes the refusal at announce time, so it may name what arrived with the response', () => {
    // The reference is read from the store's record of THIS write, which does not exist when the watch is
    // registered. A value captured at registration could not carry it.
    let reference: string | null = null;

    service.announceWhenSettled(verdict, () => 'The role was created.', () => ({
      message: 'The role could not be created',
      reference,
    }));
    flush();

    reference = '764349256f59ef77af077f6619f899ee';
    verdict.set('failed');
    flush();

    expect(notifications.notifications()[0]?.reference).toBe('764349256f59ef77af077f6619f899ee');
  });

  it('does not word a refusal on a write that succeeded, or a success on one that failed', () => {
    // Each describer is evaluated only on its own outcome. Calling both, or the wrong one, would put a
    // failure sentence on a completed write - the worst possible reading of a trail an operator acts on.
    const successCalls: number[] = [];
    const failureCalls: number[] = [];

    service.announceWhenSettled(
      verdict,
      () => {
        successCalls.push(1);
        return 'The role was created.';
      },
      () => {
        failureCalls.push(1);
        return { message: 'The role could not be created', reference: null };
      },
    );
    flush();

    verdict.set('succeeded');
    flush();

    expect(successCalls.length).toBe(1);
    expect(failureCalls.length).withContext('the write succeeded').toBe(0);
  });

  it('speaks exactly ONCE, and stops watching after it has spoken', () => {
    service.announceWhenSettled(verdict, () => 'The role was created.', declineFailure);
    flush();

    verdict.set('succeeded');
    flush();

    // A further settle - another write publishing to the same slot the caller derived its verdict from must
    // not be reported as this one. The watch released itself, and the one-shot flag holds even if the
    // release has not taken effect yet.
    verdict.set('pending');
    flush();
    verdict.set('succeeded');
    flush();

    expect(queuedMessages())
      .withContext('one write, one statement')
      .toEqual(['The role was created.']);
  });

  it('watches two writes independently', () => {
    const second: WritableSignal<DeferredOutcome> = signal<DeferredOutcome>('pending');

    service.announceWhenSettled(verdict, () => 'The role was created.', declineFailure);
    service.announceWhenSettled(second, () => 'The account was created.', declineFailure);
    flush();

    second.set('succeeded');
    flush();

    expect(queuedMessages())
      .withContext('the one that settled, and only that one')
      .toEqual(['The account was created.']);

    verdict.set('succeeded');
    flush();

    expect(queuedMessages()).toEqual(['The account was created.', 'The role was created.']);
  });

  it('does not claim to outlive a navigation, because the operator has already made theirs', () => {
    // The navigation the operator chose completed before the write settled, so there is no departure left
    // for the statement to outlive. Claiming one would spend the exemption on their NEXT change of screen
    // and leave the confirmation sitting over something it has nothing to do with.
    service.announceWhenSettled(verdict, () => 'The role was created.', declineFailure);
    flush();

    verdict.set('succeeded');
    flush();

    const queued = notifications.notifications();

    expect(queued.length).toBe(1);
    expect(queued[0]?.survivesNavigation)
      .withContext('it is read where it is raised')
      .toBeFalse();
  });
});
