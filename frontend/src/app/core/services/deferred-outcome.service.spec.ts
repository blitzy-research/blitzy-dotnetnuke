import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';

import { DeferredOutcomeService } from './deferred-outcome.service';
import type { DeferredOutcome } from './deferred-outcome.service';
import { NotificationService } from './notification.service';

/**
 * THE SUBJECT: reporting the outcome of a write whose screen has already gone.
 *
 * ⚠ WHAT THIS SERVICE EXISTS FOR IS A LIFETIME, WHICH IS WHY THE CASES BELOW LEAN ON WHERE THE WATCH IS
 * CREATED RATHER THAN ON WHAT IT SAYS. Every editing screen composes and announces its own outcome from an
 * `effect` in its own injection context, so the announcement dies with the component. A browser audit
 * measured the consequence on the role creation form: submit, immediately click elsewhere, and the request
 * answers `201`, the record is genuinely created, the destination screen is healthy - and the operator is
 * never told the write committed, because the only party that was going to tell them no longer exists.
 *
 * This service is provided at the application root, so what it injects as an `Injector` IS the root
 * environment injector and an effect created with it outlives every screen. The first case pins exactly
 * that property, by registering from OUTSIDE any injection context at all.
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

  /**
   * Drains the root effect queue.
   *
   * The watch is a ROOT effect - created with the root environment injector, which is the entire point of
   * this service - so nothing drains it on a component's behalf. `flushEffects()` is the API this version
   * of the framework publishes and it was verified against the installed `@angular/core` rather than
   * assumed: `flushEffects(): void` is declared on the testing surface, while the `tick()` that supersedes
   * it in a later major version is not. Everything here is synchronous, so no timer is involved.
   */
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
      service.announceWhenSettled(verdict, () => 'The role was created.');
    }).not.toThrow();
  });

  it('says nothing while the write is still in flight', () => {
    service.announceWhenSettled(verdict, () => 'The role was created.');
    flush();

    expect(queuedMessages()).withContext('nothing has settled yet').toEqual([]);
  });

  it('states the outcome once the write settles successfully', () => {
    service.announceWhenSettled(verdict, () => 'The role was created.');
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

    service.announceWhenSettled(verdict, () => `Account ${stored ?? '?'} was created.`);
    flush();

    stored = 'qa7probe';
    verdict.set('succeeded');
    flush();

    expect(queuedMessages()).toEqual(['Account qa7probe was created.']);
  });

  it('says nothing when the caller declines to word the outcome', () => {
    service.announceWhenSettled(verdict, () => null);
    flush();

    verdict.set('succeeded');
    flush();

    expect(queuedMessages())
      .withContext('a caller returning null states the outcome silently')
      .toEqual([]);
  });

  it('relays NO refusal, because a refusal without its screen is worse than silence', () => {
    // ⚠ DELIBERATE, AND NOT AN OVERSIGHT. A failure in this application is a document - a title, a detail,
    // per-field messages and the support reference an operator quotes - and its home is the banner ON the
    // screen that attempted the write. That screen is gone: there is no field for a field message to sit
    // beside and no form to correct, so a decontextualised sentence thrown at whatever screen the operator
    // moved to would report a problem without showing it or letting them fix it. The store still holds the
    // failure, so returning to the screen presents it in full.
    service.announceWhenSettled(verdict, () => 'The role was created.');
    flush();

    verdict.set('failed');
    flush();

    expect(queuedMessages()).toEqual([]);
  });

  it('speaks exactly ONCE, and stops watching after it has spoken', () => {
    service.announceWhenSettled(verdict, () => 'The role was created.');
    flush();

    verdict.set('succeeded');
    flush();

    // A further settle - another write publishing to the same slot the caller derived its verdict from -
    // must not be reported as this one. The watch released itself, and the one-shot flag holds even if the
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

    service.announceWhenSettled(verdict, () => 'The role was created.');
    service.announceWhenSettled(second, () => 'The account was created.');
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
    service.announceWhenSettled(verdict, () => 'The role was created.');
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
