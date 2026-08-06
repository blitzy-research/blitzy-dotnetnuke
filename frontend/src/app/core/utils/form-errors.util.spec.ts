//
// Specification for the error-presentation helpers of the dnn-migration
// administration front end.
//
// ---------------------------------------------------------------------------
// WHY THIS SUITE EXISTS, AND WHY IT IS PLAIN JASMINE
// ---------------------------------------------------------------------------
// The module under test is a set of pure functions over immutable values. It takes
// no dependency, issues no request and touches no browser API, so nothing here
// needs a testing module, a fixture or a component harness, and importing one
// would make the suite slower and its failures harder to read for no gain. Every
// expectation below is a direct call.
//
// The suite also serves a structural purpose. `tsconfig.app.json` compiles by
// IMPORT GRAPH - it declares `files: ["src/main.ts"]` and includes only
// declaration files - so a utility that nothing imports yet is silently NOT
// type-checked by a production build. `tsconfig.spec.json` includes
// `src/**/*.spec.ts`, so this file is what puts the module under test into a gated
// compile. A clean production build alone would prove nothing about it.
//
// ---------------------------------------------------------------------------
// WHAT IS ACTUALLY AT RISK, MEASURED
// ---------------------------------------------------------------------------
// Three things, each of which the obvious implementation gets wrong:
//
//  1. LEGACY MESSAGE TEXT CARRIES EMBEDDED LINE-BREAK MARKUP, in three spellings.
//     Across the 37 in-scope legacy administration resource files the escaped
//     forms occur as `<br>` 73 times, `<br/>` 13 times and `<br />` twice, and the
//     code-behinds add their own: Signup.ascx.vb:L193 prepends `"<br>"` INSIDE A
//     PER-CHARACTER LOOP, L323 wraps the accumulation as
//     `"<br>" & strMessage & "<br><br>"`, and User.ascx.vb:L187 prepends the
//     other spelling. The API reproduces all of it byte for byte, so a naive
//     display shows a person the characters `<br>`.
//
//  2. THE LEGACY NULL CONTRACT MAKES `null` AND `""` THE SAME VALUE.
//     Null.vb:L71-L75 defines the string "absent" marker with a body of literally
//     `Return ""`, and Signup.ascx.vb tests one variable for emptiness two
//     different ways in one file - `= ""` at L227 and `= Null.NullString` at L315.
//     Anything that treats them differently changes a branch outcome.
//
//  3. THREE NEIGHBOURING ENUMERATIONS USE THREE DIFFERENT SUCCESS CONVENTIONS.
//     `UserCreateStatus.Success` is 13, the sign-in success outcome is 1 - spelled
//     `LOGIN_SUCCESS` in the legacy source and `UserLoginStatus.Success` in the target
//     enumeration, same value either way - and the legacy `UserValidStatus.VALID` is 0.
//     A "zero means success" assumption is wrong two times out of three, and
//     `UserCreateStatus.AddUser` at 0 is the "no error yet" seed rather than an outcome.
//
//  4. THE DISCRIMINATION GUARD IS ONLY AS SOUND AS THE SHAPE TEST IT DELEGATES TO.
//     `isValidationProblemDetails` narrows an UNTRUSTED body, and it does so by
//     calling `isProblemDetails` first so that the two guards cannot disagree about
//     what a problem document is. That makes the delegated shape test part of this
//     module's contract rather than someone else's detail: if it admits a document
//     whose members carry the wrong types, this module hands a caller a narrowed
//     value that lies, and the caller then reads a number as though it were a
//     string. The block at (d2) below exercises the delegated test directly, and
//     covers the two admissions - an empty per-field dictionary, and a DOM
//     `ProgressEvent` - that `error.interceptor.ts` depends on and that must
//     therefore survive any future tightening.
//
// The legacy tree shipped no automated tests of any kind, so there is no assertion
// to port; every expectation below is derived from reading the source it cites.
//

import {
  ADVISORY_MESSAGE,
  AUTH_FAILURE_CODES,
  CONFLICT,
  CONFLICT_CODES,
  CONFLICT_MESSAGE,
  FORBIDDEN,
  NOT_AUTHENTICATED,
  NOT_FOUND,
  PASSWORD_UPDATE_CODES,
  PASSWORD_UPDATE_MESSAGE,
  REQUEST_REJECTED,
  SERVER_ERROR,
  TOO_MANY_ATTEMPTS,
  USER_CREATE_MESSAGE,
  USER_CREATE_CODES,
  VALIDATION_REJECTED,
  advisoryMessage,
  authFailureMessage,
  conflictMessage,
  failureCode,
  fieldErrorMessage,
  fieldErrorMessages,
  fieldMessages,
  formLevelMessages,
  isConflictCode,
  isRefusalStatus,
  isAliasInUseCode,
  isDuplicateAliasCode,
  isValidationProblemDetails,
  passwordUpdateMessage,
  problemMessage,
  problemSeverity,
  problemSupportReference,
  resolveVerificationPrompt,
  statusMessage,
  stripLegacyBreakTags,
  summarizeProblem,
  userCreateMessage,
} from './form-errors.util';
import {
  isProblemDetails,
  problemDetailsFieldErrors,
  problemDetailsMessage,
} from '../models/problem-details.model';
import type { ProblemDetails, ValidationProblemDetails } from '../models/problem-details.model';
import { UserLoginStatus } from '../models/auth.model';
import { UserCreateStatus } from '../models/user.model';

describe('form-errors.util', () => {
  // -------------------------------------------------------------------------
  // (a) LEADING BREAKS - ALL THREE SPELLINGS, CASE-INSENSITIVE, REPEATED
  // -------------------------------------------------------------------------
  describe('stripLegacyBreakTags - leading breaks', () => {
    it('strips the unclosed spelling that Signup.ascx.vb:L193 prepends', () => {
      expect(stripLegacyBreakTags('<br>You Must Enter a Valid Name')).toBe(
        'You Must Enter a Valid Name',
      );
    });

    it('strips the self-closing spelling that User.ascx.vb:L187 prepends', () => {
      expect(stripLegacyBreakTags('<br/>The username specified is invalid.')).toBe(
        'The username specified is invalid.',
      );
    });

    it('strips the spaced spelling, which the resource data really does contain', () => {
      expect(stripLegacyBreakTags('<br />Password Is Required.')).toBe('Password Is Required.');
    });

    it('is case-insensitive and tolerates whitespace inside the tag', () => {
      expect(stripLegacyBreakTags('<BR>upper')).toBe('upper');
      expect(stripLegacyBreakTags('<Br >mixed')).toBe('mixed');
      expect(stripLegacyBreakTags('<br  />padded')).toBe('padded');
      expect(stripLegacyBreakTags('< br >spaced open')).toBe('spaced open');
    });

    it('strips a REPEATED run of leading breaks in any mixture of spellings', () => {
      expect(stripLegacyBreakTags('<br><br/><br />Portal Name Is Required.')).toBe(
        'Portal Name Is Required.',
      );
    });

    it('strips whitespace around the leading run as well', () => {
      expect(stripLegacyBreakTags('  <br>  \n Email Is Required.')).toBe('Email Is Required.');
    });

    it('does not mistake an unrelated element for a break tag', () => {
      expect(stripLegacyBreakTags('<brochure>kept')).toBe('<brochure>kept');
      expect(stripLegacyBreakTags('<br-x>kept')).toBe('<br-x>kept');
    });
  });

  // -------------------------------------------------------------------------
  // (b) INTERIOR BREAKS BECOME NEWLINES  +  (c) TRAILING BREAKS ARE REMOVED
  // -------------------------------------------------------------------------
  describe('stripLegacyBreakTags - interior and trailing breaks', () => {
    it('turns an interior break into a plain-text newline, never into markup', () => {
      const result = stripLegacyBreakTags('First fragment<br>Second fragment');

      expect(result).toBe('First fragment\nSecond fragment');
      expect(result).not.toContain('<');
    });

    it('maps each interior break to one newline, so a double break stays a blank line', () => {
      expect(stripLegacyBreakTags('One<br><br>Two')).toBe('One\n\nTwo');
    });

    it('absorbs horizontal whitespace hugging an interior break', () => {
      expect(stripLegacyBreakTags('One \t<br/> \tTwo')).toBe('One\nTwo');
    });

    it('removes the trailing pair that Signup.ascx.vb:L323 appends', () => {
      expect(stripLegacyBreakTags('<br>Something went wrong<br><br>')).toBe(
        'Something went wrong',
      );
    });

    it('reproduces the whole Signup.ascx.vb:L323 shape with a repeated loop fragment', () => {
      // L191-L195 appends the identical fragment once per invalid character, then
      // L323 wraps the accumulation in a leading break and a trailing pair.
      const accumulated = '<br>' + '<br>Invalid Name<br>Invalid Name' + '<br><br>';

      expect(stripLegacyBreakTags(accumulated)).toBe('Invalid Name\nInvalid Name');
    });

    it('collapses a value that is nothing but break tags to the empty string', () => {
      expect(stripLegacyBreakTags('<br><br/><br />')).toBe('');
    });

    it('leaves non-break markup as literal text rather than parsing it', () => {
      // Deliberate: stripping these would mean parsing untrusted markup. They stay
      // visible-but-inert, and the template escapes them.
      expect(stripLegacyBreakTags('<b>Note:</b><br>Detail')).toBe('<b>Note:</b>\nDetail');
    });

    it('leaves text with no break markup untouched apart from trimming', () => {
      expect(stripLegacyBreakTags('  A plain sentence.  ')).toBe('A plain sentence.');
    });
  });

  // -------------------------------------------------------------------------
  // (f) NULL AND THE EMPTY STRING BEHAVE IDENTICALLY  (Rule T7)
  // -------------------------------------------------------------------------
  describe('null and the empty string are one value', () => {
    it('yields the same result from stripLegacyBreakTags for null, undefined and ""', () => {
      expect(stripLegacyBreakTags(null)).toBe('');
      expect(stripLegacyBreakTags(undefined)).toBe('');
      expect(stripLegacyBreakTags('')).toBe('');
      expect(stripLegacyBreakTags(null)).toBe(stripLegacyBreakTags(''));
    });

    it('takes the same ladder branch for a null code as for an empty code', () => {
      const withNull = resolveVerificationPrompt({
        verificationVisible: true,
        verificationCode: null,
        verifiedRegistration: true,
      });
      const withEmpty = resolveVerificationPrompt({
        verificationVisible: true,
        verificationCode: '',
        verifiedRegistration: true,
      });

      expect(withNull.code).toBe('auth.verification_required');
      expect(withEmpty.code).toBe('auth.verification_required');
      expect(withNull).toEqual(withEmpty);
    });

    it('resolves a field lookup identically for a null and an empty control name', () => {
      const problem: ValidationProblemDetails = { status: 400, errors: { Email: ['Required.'] } };

      expect(fieldErrorMessages(problem, null)).toEqual([]);
      expect(fieldErrorMessages(problem, '')).toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // (d) THE DISCRIMINATION GUARD
  // -------------------------------------------------------------------------
  describe('isValidationProblemDetails', () => {
    it('rejects a problem document that carries no per-field dictionary', () => {
      const problem: ProblemDetails = { status: 500, title: 'Error', detail: 'Nope.' };

      expect(isValidationProblemDetails(problem)).toBeFalse();
    });

    it('accepts a problem document that carries one, and narrows it for the compiler', () => {
      const body: unknown = { status: 400, title: 'Bad Request', errors: { Email: ['Required.'] } };

      expect(isValidationProblemDetails(body)).toBeTrue();

      if (isValidationProblemDetails(body)) {
        // Reading `errors` without a presence test is the whole point of the
        // narrowing, and bracket access is the only syntax the workspace permits
        // on an index-signature member.
        expect(body.errors['Email']).toEqual(['Required.']);
      }
    });

    it('accepts an EMPTY dictionary, because presence and content are different questions', () => {
      expect(isValidationProblemDetails({ status: 400, errors: {} })).toBeTrue();
    });

    it('rejects a null dictionary, an array dictionary, an array body and a non-object', () => {
      expect(isValidationProblemDetails({ status: 400, errors: null })).toBeFalse();
      expect(isValidationProblemDetails({ status: 400, errors: ['Required.'] })).toBeFalse();
      expect(isValidationProblemDetails([{ status: 400, errors: {} }])).toBeFalse();
      expect(isValidationProblemDetails('a string')).toBeFalse();
      expect(isValidationProblemDetails(null)).toBeFalse();
      expect(isValidationProblemDetails(undefined)).toBeFalse();
    });

    it('rejects an arbitrary object that is not a problem document at all', () => {
      expect(isValidationProblemDetails({ items: [], totalCount: 0 })).toBeFalse();
    });
  });

  // -------------------------------------------------------------------------
  // (d2) THE DELEGATED SHAPE TEST THE GUARD IS BUILT ON
  //
  // `isValidationProblemDetails` calls `isProblemDetails` before it looks at the
  // per-field dictionary, so every judgement made here propagates into the guard
  // above. The subject lives in `../models/problem-details.model`, which carries no
  // spec of its own by design - it is a declarations file - so the module that
  // DEPENDS on the predicate is where the predicate's behaviour is pinned.
  //
  // The question the predicate answers is deliberately asymmetric: absent members
  // are fine, because RFC 7807 makes every member optional and the API genuinely
  // emits subsets; a PRESENT member carrying the wrong type is not, because the
  // narrowing then asserts a declaration the value does not satisfy and the caller
  // reads it as though it did.
  // -------------------------------------------------------------------------
  describe('isProblemDetails - the delegated shape test', () => {
    const FALLBACK = 'The request could not be completed.';

    // The read every consumer performs: gate on the predicate, then extract. Writing
    // it once keeps the expectations below about the CALLER'S outcome rather than
    // about the predicate's return value, which is the property that actually
    // matters and the one that survives any future hardening of the extractors.
    const readGuarded = (body: unknown): string =>
      isProblemDetails(body) ? problemDetailsMessage(body, FALLBACK) : FALLBACK;

    const fieldsGuarded = (body: unknown): Readonly<Record<string, readonly string[]>> =>
      isProblemDetails(body) ? problemDetailsFieldErrors(body) : {};

    it('refuses a well-typed member vouching for a malformed one', () => {
      // The exact body that motivated the tightening. `status` is a number, so a
      // test asking whether SOME member matched passed, narrowed to a shape
      // declaring `detail?: string`, and the message extractor then called `.trim()`
      // on the number 42.
      const body: unknown = { status: 400, detail: 42 };

      expect(isProblemDetails(body))
        .withContext('a numeric detail contradicts the declared string')
        .toBeFalse();
      expect(isValidationProblemDetails(body))
        .withContext('the delegating guard inherits the refusal')
        .toBeFalse();
      expect(() => readGuarded(body)).not.toThrow();
      expect(readGuarded(body)).toBe(FALLBACK);
    });

    it('accepts the same document once every present member carries its declared type', () => {
      const body: unknown = { status: 400, detail: 'The portal name is already in use.' };

      expect(isProblemDetails(body)).toBeTrue();
      expect(readGuarded(body)).toBe('The portal name is already in use.');
    });

    it('refuses a per-field dictionary that is null or an array', () => {
      // `typeof null === 'object'` and `typeof [] === 'object'`, so neither is
      // excluded by a type-of test alone. A document declaring `errors` while
      // holding nothing iterable is what the interceptor would then walk.
      expect(isProblemDetails({ title: 'Bad Request', errors: null })).toBeFalse();
      expect(isProblemDetails({ title: 'Bad Request', errors: [] })).toBeFalse();
      expect(isProblemDetails({ title: 'Bad Request', errors: ['Required.'] })).toBeFalse();
      expect(fieldsGuarded({ title: 'Bad Request', errors: null })).toEqual({});
    });

    it('refuses a dictionary whose values are not arrays of strings', () => {
      expect(isProblemDetails({ errors: { Email: 'Required.' } })).toBeFalse();
      expect(isProblemDetails({ errors: { Email: [1, 2] } })).toBeFalse();
      expect(isProblemDetails({ errors: { Email: ['Required.'], PortalName: null } })).toBeFalse();
      expect(fieldsGuarded({ errors: { Email: 'Required.' } })).toEqual({});
    });

    it('ADMITS an empty dictionary and counts it as a recognised member', () => {
      // Load-bearing, not incidental. The API writes `errors: {}` whenever model
      // state carries no entries, and `error.interceptor.ts` distinguishes "a
      // per-field dictionary is present" from "anything renderable was reported" on
      // exactly that basis. Tightening this away would silently change which branch
      // a validation response takes.
      expect(isProblemDetails({ errors: {} })).toBeTrue();
      expect(isProblemDetails({ title: 'Bad Request', errors: {} })).toBeTrue();
      expect(isValidationProblemDetails({ status: 400, errors: {} })).toBeTrue();
      expect(fieldsGuarded({ title: 'Bad Request', errors: {} })).toEqual({});
    });

    it('ADMITS a value shaped like a DOM ProgressEvent', () => {
      // Also load-bearing. A transport failure delivers a `ProgressEvent`, which
      // carries a string `type` and none of the other standard members. That is why
      // `error.interceptor.ts` resolves a status of zero BEFORE it reads the body;
      // the ordering there depends on this admission rather than compensating for it.
      expect(isProblemDetails({ type: 'error' })).toBeTrue();
      expect(readGuarded({ type: 'error' })).toBe(FALLBACK);
    });

    it('refuses every standard member that is present with the wrong type', () => {
      const refused: readonly unknown[] = [
        { type: 7, title: 'Bad Request' },
        { title: 12345 },
        { status: '400' },
        { detail: { message: 'nested' } },
        { instance: {}, title: 'Bad Request' },
        { traceId: 99, title: 'Bad Request' },
      ];

      for (const body of refused) {
        expect(isProblemDetails(body))
          .withContext(`refused: ${JSON.stringify(body)}`)
          .toBeFalse();
        expect(readGuarded(body))
          .withContext(`the caller falls back for: ${JSON.stringify(body)}`)
          .toBe(FALLBACK);
      }
    });

    it('refuses a value carrying no recognised member, and every non-object', () => {
      const refused: readonly unknown[] = [
        { items: [], totalCount: 0 },
        {},
        [],
        [{ status: 400 }],
        'a string',
        42,
        true,
        null,
        undefined,
      ];

      for (const body of refused) {
        expect(isProblemDetails(body))
          .withContext(`refused: ${JSON.stringify(body) ?? String(body)}`)
          .toBeFalse();
        expect(fieldsGuarded(body)).toEqual({});
      }
    });

    it('accepts a complete document and reads every member through the narrowing', () => {
      const body: unknown = {
        type: 'https://httpstatuses.io/400',
        title: 'One or more validation errors occurred.',
        status: 400,
        detail: 'The portal name is required.',
        instance: '/api/v1/portals',
        traceId: '00-abc-def-00',
        errors: { PortalName: ['The portal name is required.'] },
      };

      expect(isProblemDetails(body)).toBeTrue();
      expect(isValidationProblemDetails(body)).toBeTrue();
      expect(readGuarded(body)).toBe('The portal name is required.');
      expect(fieldsGuarded(body)).toEqual({ portalName: ['The portal name is required.'] });
    });
  });

  // -------------------------------------------------------------------------
  // (e) THE TRACE IDENTIFIER IS SURFACED, AND ABSENCE IS SAFE
  // (e) THE SUPPORT REFERENCE IS SURFACED, THE CORRELATION IDENTIFIER WINS,
  //     AND ABSENCE IS SAFE
  // -------------------------------------------------------------------------
  describe('problemSupportReference', () => {
    it('prefers the correlation identifier, which is the value the server can find', () => {
      const problem: ProblemDetails = {
        status: 500,
        correlationId: 'c0rrelation-id',
        traceId: '00-abc-def-00',
      };

      expect(problemSupportReference(problem)).toBe('c0rrelation-id');
      expect(summarizeProblem(problem).supportReference).toBe('c0rrelation-id');
    });

    it('falls back to the trace identifier only when no correlation identifier was published', () => {
      const problem: ProblemDetails = { status: 500, traceId: '00-abc-def-00' };

      expect(problemSupportReference(problem)).toBe('00-abc-def-00');
      expect(summarizeProblem(problem).supportReference).toBe('00-abc-def-00');
    });

    it('reports null rather than throwing when the document carries none', () => {
      expect(problemSupportReference({ status: 500 })).toBeNull();
      expect(problemSupportReference(null)).toBeNull();
      expect(problemSupportReference(undefined)).toBeNull();
      expect(summarizeProblem(null).supportReference).toBeNull();
    });

    it('treats a blank identifier as absent, because it joins nothing to nothing', () => {
      expect(problemSupportReference({ status: 500, traceId: '   ' })).toBeNull();
      expect(problemSupportReference({ status: 500, correlationId: '  ' })).toBeNull();
    });

    it('falls back past a blank correlation identifier to a usable trace identifier', () => {
      const problem: ProblemDetails = { status: 500, correlationId: ' ', traceId: '00-a-b-00' };

      expect(problemSupportReference(problem)).toBe('00-a-b-00');
    });
  });

  // -------------------------------------------------------------------------
  // (g) SEVERITY AND THE DISTINCT RATE-LIMIT WORDING
  // -------------------------------------------------------------------------
  describe('problemSeverity', () => {
    it('presents a permission refusal as a WARNING, per the legacy YellowWarning', () => {
      // AccessDenied.ascx.vb has no permission check; both of its branches - L43 for
      // the query-string message and L45 for the localised default - use the warning
      // message type.
      expect(problemSeverity(403)).toBe('warning');
    });

    it('presents a rate-limit refusal as INFORMATIONAL, quieter than the other refusals', () => {
      // The quietest of the three severities, and the reason the informational member
      // exists at all. Nothing was rejected on its merits and nothing is misconfigured -
      // the caller is early, and the only action is to wait. This is also the single
      // authority the shared error banner used to override with a local test of its own,
      // which is how the same status came to be announced as a warning and painted as a
      // calm notice on one screen.
      expect(problemSeverity(429)).toBe('info');
    });

    it('is the only classifier, so no surface may re-derive or override its answer', () => {
      // The banner maps three severities onto three bands and tests no status itself, and
      // the interceptor announces at whatever this returns. Both are asserted in their own
      // specifications; what is asserted here is that the informational member is REACHABLE
      // from a real status, because an unreachable member is what invited the override.
      expect(problemSeverity(429)).toBe('info');
      expect(problemSeverity(403)).toBe('warning');
      expect(problemSeverity(500)).toBe('error');
    });

    it('presents an unauthenticated and a not-found response as warnings', () => {
      expect(problemSeverity(401)).toBe('warning');
      expect(problemSeverity(404)).toBe('warning');
    });

    it('presents a validation refusal, a conflict and a server fault as errors', () => {
      expect(problemSeverity(400)).toBe('error');
      expect(problemSeverity(422)).toBe('error');
      expect(problemSeverity(409)).toBe('error');
      expect(problemSeverity(500)).toBe('error');
    });

    it('falls through to error for the statuses only the server vocabulary lists', () => {
      for (const status of [405, 406, 415, 501, 503]) {
        expect(problemSeverity(status)).toBe('error');
      }
    });

    it('falls through to error for an absent status and for the no-response zero', () => {
      expect(problemSeverity(null)).toBe('error');
      expect(problemSeverity(undefined)).toBe('error');
      expect(problemSeverity(0)).toBe('error');
      expect(problemSeverity(418)).toBe('error');
    });
  });

  describe('isRefusalStatus', () => {
    it('reports every status at which the server refuses rather than fails', () => {
      // The six the API actually refuses with. Declared beside the severity rule so a
      // reader deciding how to treat a failure sees both classifications together, and
      // exported so the response path consumes it rather than keeping a private copy -
      // which is what made the interceptor look like a second classification authority.
      for (const status of [400, 403, 404, 409, 422, 429]) {
        expect(isRefusalStatus(status))
          .withContext(`${status} is a refusal the operator provoked`)
          .toBeTrue();
      }
    });

    it('reports a fault and an unanticipated status as NOT a refusal', () => {
      for (const status of [500, 502, 503, 405, 406, 415, 418]) {
        expect(isRefusalStatus(status))
          .withContext(`${status} is a fault worth a diagnostic identifier`)
          .toBeFalse();
      }
    });

    it('excludes an unauthenticated response, which no surface words at all', () => {
      expect(isRefusalStatus(401)).toBeFalse();
    });

    it('reports the no-response zero as NOT a refusal', () => {
      // Nothing was rejected, because nothing was received.
      expect(isRefusalStatus(0)).toBeFalse();
    });

    it('reports an absent status as NOT a refusal, matching the severity rule', () => {
      expect(isRefusalStatus(null)).toBeFalse();
      expect(isRefusalStatus(undefined)).toBeFalse();
    });

    it('is a DIFFERENT question from severity, and the two deliberately disagree', () => {
      // A validation refusal and a conflict are refusals here while resolving to the error
      // severity, on the measured legacy evidence. Deriving either rule from the other
      // would force one of them to be wrong.
      expect(isRefusalStatus(400)).toBeTrue();
      expect(problemSeverity(400)).toBe('error');
      expect(isRefusalStatus(409)).toBeTrue();
      expect(problemSeverity(409)).toBe('error');
    });
  });

  describe('statusMessage', () => {
    it('words a rate-limit refusal calmly and distinctly from every other status', () => {
      expect(statusMessage(429)).toBe(TOO_MANY_ATTEMPTS);
      expect(TOO_MANY_ATTEMPTS).toBe('Too many attempts. Wait a moment and try again.');
      expect(statusMessage(429)).not.toBe(statusMessage(403));
      expect(statusMessage(429)).not.toBe(statusMessage(500));
    });

    it('words the statuses the interceptor also words, using the same sentences', () => {
      expect(statusMessage(403)).toBe(FORBIDDEN);
      expect(statusMessage(404)).toBe(NOT_FOUND);
      expect(statusMessage(409)).toBe(CONFLICT);
      expect(statusMessage(422)).toBe(VALIDATION_REJECTED);
      expect(statusMessage(401)).toBe(NOT_AUTHENTICATED);
    });

    it('gives every status it recognises a distinct sentence', () => {
      const worded = [401, 403, 404, 409, 422, 429, 500].map((status) => statusMessage(status));

      expect(new Set(worded).size).toBe(worded.length);
    });

    it('does not disclose which half of a credential pair was wrong', () => {
      const message = statusMessage(401);

      expect(message).toBe(
        'Either you are not currently logged in, or you do not have access to this content.',
      );
      expect(message.toLowerCase()).not.toContain('username');
      expect(message.toLowerCase()).not.toContain('does not exist');
    });

    it('words every 5xx as a server fault and anything else as a rejected request', () => {
      expect(statusMessage(500)).toBe(SERVER_ERROR);
      expect(statusMessage(503)).toBe(SERVER_ERROR);
      expect(statusMessage(400)).toBe(REQUEST_REJECTED);
      expect(statusMessage(415)).toBe(REQUEST_REJECTED);
      expect(statusMessage(null)).toBe(REQUEST_REJECTED);
    });
  });

  // -------------------------------------------------------------------------
  // SUMMARY TEXT PRECEDENCE
  // -------------------------------------------------------------------------
  describe('problemMessage', () => {
    it('prefers detail over title, and strips the break markup from it', () => {
      const problem: ProblemDetails = {
        title: 'Bad Request',
        detail: '<br>You Must Enter a Valid Name',
      };

      expect(problemMessage(problem, 'fallback')).toBe('You Must Enter a Valid Name');
    });

    it('falls back to title when detail is absent or blank', () => {
      expect(problemMessage({ title: 'Conflict', detail: '   ' }, 'fallback')).toBe('Conflict');
      expect(problemMessage({ title: 'Conflict' }, 'fallback')).toBe('Conflict');
    });

    it('STRIPS BEFORE TESTING FOR BLANKNESS, so a break-only detail cannot hide a title', () => {
      // The reason this module re-expresses the precedence instead of delegating:
      // a raw blankness test would select "<br>", strip it to nothing, and lose the
      // title entirely.
      expect(problemMessage({ title: 'Conflict', detail: '<br>' }, 'fallback')).toBe('Conflict');
      expect(problemMessage({ title: 'Conflict', detail: '<br/> <br />' }, 'fallback')).toBe(
        'Conflict',
      );
    });

    it('falls back when the document carries nothing usable, or is null', () => {
      expect(problemMessage({ status: 500 }, 'fallback')).toBe('fallback');
      expect(problemMessage(null, 'fallback')).toBe('fallback');
      expect(problemMessage(undefined, 'fallback')).toBe('fallback');
    });
  });

  // -------------------------------------------------------------------------
  // FIELD RESOLUTION
  // -------------------------------------------------------------------------
  describe('field resolution', () => {
    const problem: ValidationProblemDetails = {
      status: 400,
      title: 'One or more validation errors occurred.',
      errors: {
        Email: ['<br>Email Is Required.', 'The email address specified is invalid.'],
        UserName: ['<br/>Username Is Required.'],
        'Alias.HttpAlias': ['The Portal Alias already exists.'],
        '$.portalName': ['Could not be read.'],
        'request.PortalName': ['Portal Name Is Required.'],
        '': ['A form-level rule failed.'],
        $: ['The request body could not be read.'],
      },
    };

    it('lists one entry per field, excluding the keys that are not field names', () => {
      const fields = fieldMessages(problem).map((entry) => entry.field);

      expect(fields).not.toContain('');
      expect(fields).not.toContain('$');
      expect(fields.length).toBe(5);
    });

    it('strips break markup from every per-field message', () => {
      expect(fieldErrorMessages(problem, 'email')).toEqual([
        'Email Is Required.',
        'The email address specified is invalid.',
      ]);
    });

    it('matches case-insensitively, since the server does not re-case its keys', () => {
      // The keys are Pascal-cased .NET model names; a form control is named in the
      // client's own casing.
      expect(fieldErrorMessages(problem, 'userName')).toEqual(['Username Is Required.']);
      expect(fieldErrorMessages(problem, 'USERNAME')).toEqual(['Username Is Required.']);
    });

    it('tolerates the JSON-path prefix the body binder produces', () => {
      expect(
        fieldErrorMessages(
          { status: 400, errors: { '$.portalName': ['Could not be read.'] } },
          'portalName',
        ),
      ).toEqual(['Could not be read.']);
    });

    it('tolerates the binder prefix a parameter named `request` adds', () => {
      expect(
        fieldErrorMessages({ status: 400, errors: { 'request.Name': ['Bad.'] } }, 'name'),
      ).toEqual(['Bad.']);
    });

    it('AGGREGATES two differently-prefixed keys that name the SAME field', () => {
      // `$.portalName` and `request.PortalName` differ only by the prefix the server
      // happened to attach and by casing, so both describe one control and both
      // messages belong to it. Returning only one would hide a real failure from the
      // person who has to fix it.
      expect(fieldErrorMessages(problem, 'portalName')).toEqual([
        'Could not be read.',
        'Portal Name Is Required.',
      ]);
      expect(fieldErrorMessage(problem, 'portalName')).toBe('Could not be read.');
    });

    it('matches a nested validator path by its leaf', () => {
      expect(fieldErrorMessages(problem, 'httpAlias')).toEqual([
        'The Portal Alias already exists.',
      ]);
    });

    it('prefers an exact key over a nested key that merely shares its leaf', () => {
      const ambiguous: ValidationProblemDetails = {
        status: 400,
        errors: { Name: ['Exact.'], 'Portal.Name': ['By leaf.'] },
      };

      expect(fieldErrorMessages(ambiguous, 'name')).toEqual(['Exact.']);
    });

    it('returns the FIRST message for the single-string form-field input', () => {
      expect(fieldErrorMessage(problem, 'email')).toBe('Email Is Required.');
      expect(fieldErrorMessage(problem, 'userName')).toBe('Username Is Required.');
    });

    it('returns null from the single-string form for a field with no message', () => {
      expect(fieldErrorMessage(problem, 'nothingHere')).toBeNull();
      expect(fieldErrorMessage(null, 'email')).toBeNull();
    });

    it('reports the form-level keys separately, in the documented order', () => {
      expect(formLevelMessages(problem)).toEqual([
        'A form-level rule failed.',
        'The request body could not be read.',
      ]);
    });

    it('drops an entry whose messages are all blank rather than showing an empty one', () => {
      const blank: ValidationProblemDetails = {
        status: 400,
        errors: { Email: ['   ', '<br>'], UserName: ['Required.'] },
      };

      expect(fieldMessages(blank).map((entry) => entry.field)).toEqual(['userName']);
      expect(fieldErrorMessages(blank, 'email')).toEqual([]);
    });

    it('survives a document with no dictionary at all', () => {
      expect(fieldMessages({ status: 500 })).toEqual([]);
      expect(formLevelMessages({ status: 500 })).toEqual([]);
      expect(fieldMessages(null)).toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // THE ASSEMBLED SUMMARY
  // -------------------------------------------------------------------------
  describe('summarizeProblem', () => {
    it('assembles severity, plain-text title and message, fields, trace and status', () => {
      const summary = summarizeProblem({
        status: 400,
        title: '<br>One or more validation errors occurred.',
        detail: '<br/>See the fields below.',
        traceId: '00-trace-span-00',
        errors: { Email: ['<br>Email Is Required.'] },
      });

      expect(summary.severity).toBe('error');
      expect(summary.title).toBe('One or more validation errors occurred.');
      expect(summary.message).toBe('See the fields below.');
      expect(summary.fieldMessages).toEqual([{ field: 'email', messages: ['Email Is Required.'] }]);
      expect(summary.formMessages).toEqual([]);
      expect(summary.supportReference).toBe('00-trace-span-00');
      expect(summary.status).toBe(400);
      expect(summary.hasFieldMessages).toBeTrue();
    });

    it('marks a refusal as a warning and reports no field messages', () => {
      const summary = summarizeProblem({ status: 403, detail: 'Refused.' });

      expect(summary.severity).toBe('warning');
      expect(summary.hasFieldMessages).toBeFalse();
      expect(summary.message).toBe('Refused.');
    });

    it('sets hasFieldMessages from a form-level message as well as from a field', () => {
      const formLevelOnly = summarizeProblem({ status: 400, errors: { '': ['Nope.'] } });

      expect(formLevelOnly.hasFieldMessages).toBeTrue();
      expect(summarizeProblem({ status: 400, errors: {} }).hasFieldMessages).toBeFalse();
    });

    it('uses the status wording when the document carries no text', () => {
      expect(summarizeProblem({ status: 429 }).message).toBe(TOO_MANY_ATTEMPTS);
      // Informational rather than a warning: the wording for this status has always been
      // annotated "calm on purpose", and the severity now says the same thing.
      expect(summarizeProblem({ status: 429 }).severity).toBe('info');
    });

    it('prefers a supplied fallback over the status wording, and ignores a blank one', () => {
      expect(summarizeProblem({ status: 500 }, 'Could not save the portal.').message).toBe(
        'Could not save the portal.',
      );
      expect(summarizeProblem({ status: 500 }, '   ').message).toBe(SERVER_ERROR);
      expect(summarizeProblem({ status: 500 }, null).message).toBe(SERVER_ERROR);
    });

    it('produces a usable summary for a null document, with an empty title not "null"', () => {
      const summary = summarizeProblem(null);

      expect(summary.title).toBe('');
      expect(summary.message).toBe(REQUEST_REJECTED);
      expect(summary.severity).toBe('error');
      expect(summary.status).toBeNull();
      expect(summary.hasFieldMessages).toBeFalse();
    });

    it('never returns markup in any string member', () => {
      const summary = summarizeProblem({
        status: 400,
        title: '<br>T',
        detail: '<br><br>D<br>E<br><br>',
        errors: { Email: ['<br/>M'] },
      });

      expect(summary.title).toBe('T');
      expect(summary.message).toBe('D\nE');
      expect(summary.fieldMessages[0]?.messages).toEqual(['M']);
      expect(summary.title + summary.message).not.toContain('<br');
    });
  });

  // -------------------------------------------------------------------------
  // THE PROGRESSIVE, STATEFUL VERIFICATION LADDER
  // -------------------------------------------------------------------------
  describe('resolveVerificationPrompt', () => {
    it('refuses outright when the portal does not verify registration by code', () => {
      // Login.ascx.vb:L184.
      const outcome = resolveVerificationPrompt({
        verificationVisible: false,
        verificationCode: null,
        verifiedRegistration: false,
      });

      expect(outcome.code).toBe('auth.account_not_approved');
      expect(outcome.revealVerification).toBeFalse();
      expect(outcome.message).toBe('You are not currently authorized to login to this site.');
    });

    it('reveals the field and asks for a code on the first refusal', () => {
      // Login.ascx.vb:L171-L175.
      const outcome = resolveVerificationPrompt({
        verificationVisible: false,
        verificationCode: null,
        verifiedRegistration: true,
      });

      expect(outcome.code).toBe('auth.verification_required');
      expect(outcome.revealVerification).toBeTrue();
      expect(outcome.message).toBe('Enter Your Verification Code');
    });

    it('judges a submitted code as invalid once the field is already showing', () => {
      // Login.ascx.vb:L177-L178.
      const outcome = resolveVerificationPrompt({
        verificationVisible: true,
        verificationCode: 'WRONG',
        verifiedRegistration: true,
      });

      expect(outcome.code).toBe('auth.verification_code_invalid');
      expect(outcome.revealVerification).toBeFalse();
      expect(outcome.message).toBe('Invalid Verification Code');
    });

    it('asks again, rather than judging, when nothing was submitted', () => {
      // Login.ascx.vb:L180.
      expect(
        resolveVerificationPrompt({
          verificationVisible: true,
          verificationCode: '',
          verifiedRegistration: true,
        }).code,
      ).toBe('auth.verification_required');
    });

    it('DOES NOT TRIM the emptiness test, because the legacy `<> ""` did not', () => {
      // A code of spaces was non-empty in the legacy comparison and produced
      // InvalidCode. Trimming here would silently reroute it to EnterCode.
      expect(
        resolveVerificationPrompt({
          verificationVisible: true,
          verificationCode: '   ',
          verifiedRegistration: true,
        }).code,
      ).toBe('auth.verification_code_invalid');
    });

    it('walks the whole progression in order, which a flat code map could not', () => {
      const first = resolveVerificationPrompt({
        verificationVisible: false,
        verificationCode: null,
        verifiedRegistration: true,
      });
      const second = resolveVerificationPrompt({
        verificationVisible: first.revealVerification,
        verificationCode: 'BADCODE',
        verifiedRegistration: true,
      });
      const third = resolveVerificationPrompt({
        verificationVisible: true,
        verificationCode: null,
        verifiedRegistration: true,
      });

      expect([first.code, second.code, third.code]).toEqual([
        'auth.verification_required',
        'auth.verification_code_invalid',
        'auth.verification_required',
      ]);
    });

    it('presents every refusal at warning severity, never as an error', () => {
      for (const verifiedRegistration of [true, false]) {
        for (const verificationVisible of [true, false]) {
          expect(
            resolveVerificationPrompt({
              verificationVisible,
              verificationCode: 'x',
              verifiedRegistration,
            }).severity,
          ).toBe('warning');
        }
      }
    });

    it('exposes exactly three codes, spelled as the API publishes them', () => {
      // The legacy member names - EnterCode, InvalidCode, UserNotAuthorized - are the
      // PROVENANCE of these three, not their wire spelling. A vocabulary keyed on the
      // legacy names could never match a value taken off the wire, so each legacy
      // spelling is asserted to resolve to nothing, alongside the code that does.
      expect(AUTH_FAILURE_CODES).toEqual([
        'auth.verification_required',
        'auth.verification_code_invalid',
        'auth.account_not_approved',
      ]);
      expect(AUTH_FAILURE_CODES.length).toBe(3);
      expect(authFailureMessage('auth.verification_required')).toBe(
        'Enter Your Verification Code',
      );
      expect(authFailureMessage('EnterCode')).toBeNull();
      expect(authFailureMessage('InvalidCode')).toBeNull();
      expect(authFailureMessage('UserNotAuthorized')).toBeNull();
      expect(authFailureMessage('NotACode')).toBeNull();
      expect(authFailureMessage(null)).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // THE FAILURE CODE, AND WHY EVERY VOCABULARY IS KEYED ON IT
  // -------------------------------------------------------------------------
  describe('failureCode', () => {
    it('strips the namespace the API puts in front of every code it publishes', () => {
      expect(failureCode({ type: 'urn:dnnmigration:error:role.name_duplicate' })).toBe(
        'role.name_duplicate',
      );
    });

    it('folds a hyphen onto an underscore, exactly as the server does before publishing', () => {
      // The services disagree about the separator inside a reason token, and the
      // server declines to keep two spellings of one code, so it normalises before
      // publishing. Doing it again here is idempotent and lets a caller pass a code
      // quoted from a service as well as one read off the wire.
      expect(failureCode({ type: 'urn:dnnmigration:error:user.create.duplicate-email' })).toBe(
        'user.create.duplicate_email',
      );
      expect(failureCode({ type: 'urn:dnnmigration:error:USER.PASSWORD.NOT-DIFFERENT' })).toBe(
        'user.password.not_different',
      );
    });

    it('recognises the namespace whatever case the scheme arrives in', () => {
      // A URI scheme is case-insensitive by specification, so a gateway that re-cased
      // the prefix must still be understood.
      expect(failureCode({ type: 'URN:DNNMigration:Error:portal.not_found' })).toBe(
        'portal.not_found',
      );
    });

    it('reports null for a framework problem type, which is NOT an application code', () => {
      // The framework writes a specification URL for a status it mapped without
      // reaching an action. Treating that as a failure code would key a vocabulary on
      // a documentation link.
      expect(failureCode({ type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5' })).toBeNull();
      expect(failureCode({ type: 'about:blank' })).toBeNull();
    });

    it('reports null when there is no type, and never returns the empty string', () => {
      expect(failureCode(null)).toBeNull();
      expect(failureCode(undefined)).toBeNull();
      expect(failureCode({})).toBeNull();
      expect(failureCode({ type: '' })).toBeNull();
      expect(failureCode({ type: '   ' })).toBeNull();
      expect(failureCode({ type: 'urn:dnnmigration:error:' })).toBeNull();
      expect(failureCode({ type: 'urn:dnnmigration:error:   ' })).toBeNull();
    });

    it('hands a value every vocabulary can actually resolve', () => {
      // The point of the function, asserted end to end: a document as the server
      // writes it, through the parser, into a wording table.
      const problem: ProblemDetails = {
        type: 'urn:dnnmigration:error:portal.alias_duplicate',
        title: 'Conflict',
        status: 409,
      };

      expect(conflictMessage(failureCode(problem))).toBe(
        'The Portal Alias Name You Specified Already Exists. Please Choose A Different Portal Alias.',
      );
    });
  });

  // -------------------------------------------------------------------------
  // THE PASSWORD VOCABULARY, AND THE THREE LEGACY OUTCOMES THAT HAVE NO CODE
  // -------------------------------------------------------------------------
  describe('password-change vocabulary', () => {
    it('carries the five codes that have legacy wording, in legacy ordinal order', () => {
      // PasswordUpdateStatus.vb:L23-L32 declared eight members with no explicit
      // values, so declaration order was the ordinal. Five of the eight map onto a
      // code the service emits, and the order below is theirs: 1, 2, 3, 4, 5.
      expect([...PASSWORD_UPDATE_CODES]).toEqual([
        'user.password.missing',
        'user.password.not_different',
        'user.password.reset_failed',
        'user.password.invalid',
        'user.password.mismatch',
      ]);
      expect(PASSWORD_UPDATE_CODES.length).toBe(5);
    });

    it('words each failure and reports null for a code it does not word', () => {
      expect(passwordUpdateMessage('user.password.missing')).toBe(
        'You must provide your current password in order to change the password.',
      );
      expect(passwordUpdateMessage('user.password.not_different')).toBe(
        'The new password is the same as the old password. Please enter a different password',
      );
      expect(passwordUpdateMessage('user.password.current-incorrect')).toBeNull();
      expect(passwordUpdateMessage('NotAStatus')).toBeNull();
      expect(passwordUpdateMessage(null)).toBeNull();
    });

    it('accepts either separator spelling of a code', () => {
      expect(passwordUpdateMessage('user.password.reset-failed')).toBe(
        'There was an error setting the password. The password has not been changed.',
      );
      expect(passwordUpdateMessage('user.password.reset_failed')).toBe(
        'There was an error setting the password. The password has not been changed.',
      );
    });

    it('words NONE of the three legacy outcomes that have no code', () => {
      // Success is not a failure. The two question-and-answer outcomes have no
      // counterpart because the password question requirement is not carried
      // forward - the target hashes credentials one way, so there is no question to
      // be wrong about. Their legacy wording is reproduced under no key at all.
      expect(passwordUpdateMessage('Success')).toBeNull();
      expect(passwordUpdateMessage('InvalidPasswordAnswer')).toBeNull();
      expect(passwordUpdateMessage('InvalidPasswordQuestion')).toBeNull();

      for (const message of Object.values(PASSWORD_UPDATE_MESSAGE)) {
        expect(message).not.toContain('Password Answer');
        expect(message).not.toContain('Password Question');
      }
    });

    it('keys on the code and never on a legacy member name or a numeric ordinal', () => {
      expect(passwordUpdateMessage('PasswordMissing')).toBeNull();
      expect(passwordUpdateMessage('PasswordInvalid')).toBeNull();
      expect(passwordUpdateMessage('1')).toBeNull();
      expect(passwordUpdateMessage('5')).toBeNull();
    });
  });

  describe('user-creation vocabulary', () => {
    it('carries the ten codes the creation path emits', () => {
      expect([...USER_CREATE_CODES]).toEqual([
        'user.create.username_already_exists',
        'user.create.user_already_registered',
        'user.create.duplicate_email',
        'user.create.duplicate_username',
        'user.create.invalid_email',
        'user.create.invalid_password',
        'user.create.invalid_username',
        'user.create.provider_error',
        'user.create.password_mismatch',
        'user.create.portal_assignment_failed',
      ]);
      expect(USER_CREATE_CODES.length).toBe(10);
    });

    it('proves the three success conventions differ, so zero never means success', () => {
      // Read from the authoritative enumerations, which remain declared on the client
      // as legacy contracts even though neither ever crossed the wire. Creation seeds
      // at AddUser 0 and succeeds at 13; sign-in fails at 0 and succeeds at 1. Neither
      // value is a failure code, which is why no vocabulary here is keyed on one.
      expect(UserCreateStatus.Success).toBe(13);
      expect(UserCreateStatus.AddUser).toBe(0);
      expect(UserCreateStatus.AddUserToPortal).toBe(17);
      expect(UserLoginStatus.Success).toBe(1);
      expect(UserLoginStatus.Failure).toBe(0);
    });

    it('keeps the three name outcomes as three distinct codes', () => {
      // Sharing wording is not the same as being the same outcome: the server emits
      // three codes, so three keys are carried.
      expect(Object.keys(USER_CREATE_MESSAGE)).toContain('user.create.username_already_exists');
      expect(Object.keys(USER_CREATE_MESSAGE)).toContain('user.create.duplicate_username');
      expect(Object.keys(USER_CREATE_MESSAGE)).toContain('user.create.invalid_username');
    });

    it('reproduces the legacy grouping of outcomes onto shared wording', () => {
      // GetUserCreateStatus combined the name outcomes in one Case arm, and combined
      // all four provider faults in another - which the server has taken one step
      // further by emitting a single code for the latter.
      const nameTaken = userCreateMessage('user.create.username_already_exists');

      expect(userCreateMessage('user.create.duplicate_username')).toBe(nameTaken);
      expect(userCreateMessage('user.create.user_already_registered')).toBe(nameTaken);

      const registrationError = userCreateMessage('user.create.provider_error');

      expect(registrationError).toBe(
        'An Unexpected Error Occurred During Registration. Please Contact The Portal ' +
          'Administrator For Further Information.',
      );
      expect(userCreateMessage('user.create.portal_assignment_failed')).toBe(registrationError);
    });

    it('gives the invalid-name outcome its own wording despite the shared grouping', () => {
      expect(userCreateMessage('user.create.invalid_username')).toBe(
        'The username specified is invalid. Please specify a valid username.',
      );
      expect(userCreateMessage('user.create.invalid_username')).not.toBe(
        userCreateMessage('user.create.username_already_exists'),
      );
    });

    it('accepts either separator spelling, because the server folds one onto the other', () => {
      expect(userCreateMessage('user.create.duplicate-email')).toBe(
        userCreateMessage('user.create.duplicate_email'),
      );
      expect(userCreateMessage('user.create.duplicate-email')).not.toBeNull();
    });

    it('words no legacy member name, and never throws on one', () => {
      // Every legacy spelling resolves to nothing. That is the defect this vocabulary
      // was carrying: keyed on these names, not one lookup could ever have succeeded.
      for (const legacyName of [
        'AddUser',
        'Success',
        'AddUserToPortal',
        'UsernameAlreadyExists',
        'DuplicateUserName',
        'InvalidUserName',
        'ProviderError',
        'UnexpectedError',
        'DuplicateProviderUserKey',
        'InvalidProviderUserKey',
        'UserRejected',
        'InvalidAnswer',
        'InvalidQuestion',
      ]) {
        expect(userCreateMessage(legacyName))
          .withContext(`${legacyName} is provenance, not a wire code`)
          .toBeNull();
      }

      // The legacy Case Else threw ArgumentException. A display adapter that throws on
      // an unrecognised input converts a cosmetic gap into a broken screen.
      expect(() => userCreateMessage('AnythingElse')).not.toThrow();
      expect(userCreateMessage('AnythingElse')).toBeNull();
      expect(userCreateMessage(null)).toBeNull();
    });

    it('omits the password-policy numbers rather than inventing them', () => {
      const message = userCreateMessage('user.create.invalid_password');

      expect(message).toBe('The password specified is invalid. Please specify a valid password.');
      expect(message).not.toContain('[PasswordLength]');
      expect(message).not.toContain('[NoneAlphabet]');
    });

    it('carries no unresolved substitution token in any entry', () => {
      for (const message of Object.values(USER_CREATE_MESSAGE)) {
        expect(message ?? '').not.toContain('[');
        expect(message ?? '').not.toContain('{0}');
      }
    });

    it('pins the exact text of every message assembled from concatenated parts', () => {
      // Several of these are written as two joined literals to stay inside the
      // workspace column limit. Joining is exactly where a space goes missing, so the
      // full sentence is asserted character for character rather than by substring.
      expect(userCreateMessage('user.create.username_already_exists')).toBe(
        'A User Already Exists For the Username Specified. Please Register Again Using A Different Username.',
      );
      expect(userCreateMessage('user.create.duplicate_email')).toBe(
        'A user already exists for the email address specified. Please login using the ' +
          'registered account of that email address.',
      );
      expect(userCreateMessage('user.create.provider_error')).toBe(
        'An Unexpected Error Occurred During Registration. Please Contact The Portal ' +
          'Administrator For Further Information.',
      );
      expect(passwordUpdateMessage('user.password.invalid')).toBe(
        'You must enter a valid password. Please check with the Portal Administrator if you ' +
          'do not know the password requirements.',
      );
      expect(conflictMessage('role_group.name_duplicate')).toBe(
        'A role group with the same name already exists. The new group was not added.',
      );
      expect(conflictMessage('role_assignment.protected')).toBe(
        'You Can Not Remove The Portal Administrator Or The Registered Users Role',
      );
    });

    it('contains no word-joining defect from a dropped separator', () => {
      // A lost space produces a run of letters with an internal capital, such as
      // "youDo" or "AUser". Every authored message is checked for one.
      const authored = [
        ...Object.values(USER_CREATE_MESSAGE),
        ...Object.values(CONFLICT_MESSAGE),
      ];

      for (const message of authored) {
        expect(message ?? '').not.toMatch(/[a-z]{2}[A-Z]/);
      }
    });
  });

  // -------------------------------------------------------------------------
  // STATE-REFUSAL CODES, SPELLED AS THE API PUBLISHES THEM
  // -------------------------------------------------------------------------
  describe('conflict vocabulary', () => {
    it('carries the eleven codes a screen can act on, in publication order', () => {
      // ⚠️ ELEVEN, AND TWO OF THEM ARE WORTH EXPLAINING. Nine of these have legacy
      // wording behind them and are listed for that reason. `role_group.in_use` has NONE - the
      // legacy screen deleted a role group without consulting the roles classified by it, so the
      // refusal did not exist to be worded - and it is listed anyway because the server publishes
      // it and an operator can act on it. Omitting it was not neutral: the code resolved to no
      // conflict at all, so the role-listing screen kept a private copy of the wording keyed off
      // the bare `409`, which is a second vocabulary living in a feature folder. The order is the
      // publication order and is asserted as a sequence so a code cannot be appended silently.
      expect([...CONFLICT_CODES]).toEqual([
        'portal.alias_duplicate',
        'portal.alias_in_use.conflict',
        'portal.last_remaining',
        'role.name_duplicate',
        'role_group.name_duplicate',
        'role_group.in_use',
        'role_assignment.protected',
        'tab.name_reserved',
        'module.content_invalid',
        'module.content_type_mismatch',
        'module.not_portable',
      ]);
      expect(CONFLICT_CODES.length).toBe(11);
    });

    it('recognises the group-in-use refusal and words the operator\'s next step', () => {
      // The server publishes this on `DELETE /api/v1/role-groups/{roleGroupId}`; the `in_use`
      // token in the code is what the shared status translator reads to answer `409`. The wording
      // is authored rather than measured, and it says what the server's own detail does not: what
      // to do next. Asserted verbatim, because a screen renders it verbatim.
      expect(isConflictCode('role_group.in_use')).toBeTrue();
      expect(conflictMessage('role_group.in_use')).toBe(
        'That role group still contains roles, so it was not removed. Move or delete its roles first.',
      );
    });

    it('keeps the two role-group refusals apart', () => {
      // They arrive at the same status from the same resource and mean opposite things: one says
      // the name is taken, the other that the group is still doing its job. Collapsing them onto
      // one message would tell an operator to rename a group they were trying to delete.
      expect(conflictMessage('role_group.in_use')).not.toBe(
        conflictMessage('role_group.name_duplicate'),
      );
      expect(conflictMessage('role_group.name_duplicate')).toBe(
        'A role group with the same name already exists. The new group was not added.',
      );
    });

    it('tells the two portal-alias refusals apart by code and not by status', () => {
      // MIGRATION 17: both alias refusals arrive as 409, so a screen that branched on the
      // status alone would show the duplicate wording for the active-alias refusal. The
      // predicates exist so that no screen has to write a code literal, and they are
      // asserted against EACH OTHER rather than only against themselves: a pair that both
      // answered true would be worse than useless.
      expect(isDuplicateAliasCode('portal.alias_duplicate')).toBeTrue();
      expect(isDuplicateAliasCode('portal.alias_in_use.conflict')).toBeFalse();

      expect(isAliasInUseCode('portal.alias_in_use.conflict')).toBeTrue();
      expect(isAliasInUseCode('portal.alias_duplicate')).toBeFalse();

      // Absence is not either refusal. A failure the store did not recognise arrives with
      // no code, and neither predicate may claim it.
      expect(isDuplicateAliasCode(null)).toBeFalse();
      expect(isAliasInUseCode(null)).toBeFalse();

      // Nor is an unrelated refusal that happens to share the vocabulary.
      expect(isDuplicateAliasCode('portal.last_remaining')).toBeFalse();
      expect(isAliasInUseCode('portal.last_remaining')).toBeFalse();
    });

    it('words the active-alias refusal, which has no legacy antecedent', () => {
      // The legacy screen HID the affordance instead of refusing the request, so there is
      // no resource key to be faithful to. The sentence must still name the recovery,
      // because the refusal is actionable.
      const worded = conflictMessage('portal.alias_in_use.conflict') ?? '';

      expect(worded.length).toBeGreaterThan(0);
      expect(worded).toContain('other host names');
    });

    it('treats a dotted code as ONE string, not as a path', () => {
      expect(conflictMessage('portal.last_remaining')).toBe(
        'You Can Not Delete The Last Portal In Your Database',
      );
      expect(conflictMessage('portal')).toBeNull();
      expect(conflictMessage('last_remaining')).toBeNull();
    });

    it('words every code and leaves none blank', () => {
      for (const code of CONFLICT_CODES) {
        expect(conflictMessage(code)).toBe(CONFLICT_MESSAGE[code]);
        expect((conflictMessage(code) ?? '').length).toBeGreaterThan(0);
      }
    });

    it('reports null for a code it does not recognise, and for every legacy name', () => {
      expect(conflictMessage('DuplicateSomethingElse')).toBeNull();
      expect(conflictMessage(null)).toBeNull();

      for (const legacyName of [
        'DuplicatePortalAlias',
        'Portal.LastPortal',
        'DuplicateAlias',
        'DuplicateRole',
        'DuplicateRoleGroup',
        'RoleRemoveError',
        'TabExists',
        'InvalidTabName',
        'NotValidXml',
        'NotCorrectType',
        'ImportNotSupported',
      ]) {
        expect(conflictMessage(legacyName))
          .withContext(`${legacyName} is provenance, not a wire code`)
          .toBeNull();
      }
    });

    it('carries no key for the two legacy refusals the API cannot report', () => {
      // The terser duplicate-alias wording collapses onto the same code as the
      // actionable one, so only the actionable sentence survives. And the
      // duplicate-page-name refusal has no code at all: the legacy guard belonged to
      // the create path (ManageTabs.ascx.vb:L279 versus the edit branch at L304) and
      // this API exposes no page create, so wording it would describe a refusal no
      // request can elicit.
      const wording = Object.values(CONFLICT_MESSAGE);

      expect(wording).not.toContain('The Portal Alias already exists.');

      for (const message of wording) {
        expect(message).not.toContain('page hierarchy');
        expect(message).not.toContain('page heirarchy');
      }
    });

    it('keeps the page-name key on the refusal that IS reachable', () => {
      // The reserved-device-name check survives on the update verb, and its legacy
      // wording with it.
      expect(conflictMessage('tab.name_reserved')).toBe('This is an invalid Page Name');
    });

    it('accepts either separator spelling of a code', () => {
      expect(conflictMessage('portal.alias-duplicate')).toBe(
        conflictMessage('portal.alias_duplicate'),
      );
      expect(conflictMessage('portal.alias-duplicate')).not.toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // ADVISORY WORDING
  // -------------------------------------------------------------------------
  describe('advisoryMessage', () => {
    it('substitutes an already-formatted date into the expiry wording', () => {
      expect(advisoryMessage('PasswordExpired', '3 May 2026')).toBe(
        'Your password expired on 3 May 2026. Please update your password before continuing.',
      );
      expect(advisoryMessage('PasswordExpiring', '3 May 2026')).toBe(
        'Your password will expire on 3 May 2026. Please update your password before continuing.',
      );
    });

    it('never shows the placeholder or a gap when no date is available', () => {
      expect(advisoryMessage('PasswordExpired')).toBe(ADVISORY_MESSAGE.PasswordUpdate);
      expect(advisoryMessage('PasswordExpiring', null)).toBe(ADVISORY_MESSAGE.PasswordUpdate);
      expect(advisoryMessage('PasswordExpired', '   ')).not.toContain('{0}');
      expect(advisoryMessage('PasswordExpired', '   ')).not.toContain(' . ');
    });

    it('returns the undated advisories unchanged', () => {
      expect(advisoryMessage('PasswordUpdate')).toBe(
        'Please update your password before continuing.',
      );
      expect(advisoryMessage('ProfileUpdate')).toBe(
        'Please update your profile before continuing.',
      );
    });
  });

  // -------------------------------------------------------------------------
  // THE VOCABULARIES ARE IMMUTABLE AND PLAIN TEXT
  // -------------------------------------------------------------------------
  describe('module invariants', () => {
    it('freezes every lookup table, so no consumer can edit the vocabulary', () => {
      expect(Object.isFrozen(AUTH_FAILURE_CODES)).toBeTrue();
      expect(Object.isFrozen(CONFLICT_CODES)).toBeTrue();
      expect(Object.isFrozen(CONFLICT_MESSAGE)).toBeTrue();
      expect(Object.isFrozen(USER_CREATE_CODES)).toBeTrue();
      expect(Object.isFrozen(USER_CREATE_MESSAGE)).toBeTrue();
      expect(Object.isFrozen(PASSWORD_UPDATE_CODES)).toBeTrue();
      expect(Object.isFrozen(PASSWORD_UPDATE_MESSAGE)).toBeTrue();
      expect(Object.isFrozen(ADVISORY_MESSAGE)).toBeTrue();
    });

    it('carries no break markup in any authored message', () => {
      const authored = [
        ...Object.values(CONFLICT_MESSAGE),
        ...Object.values(USER_CREATE_MESSAGE),
        ...Object.values(ADVISORY_MESSAGE),
        REQUEST_REJECTED,
        NOT_AUTHENTICATED,
        FORBIDDEN,
        VALIDATION_REJECTED,
        TOO_MANY_ATTEMPTS,
        SERVER_ERROR,
      ];

      for (const message of authored) {
        expect(message ?? '').not.toContain('<');
      }
    });

    it('carries no double space, so the migrated wording renders as the legacy did', () => {
      for (const message of Object.values(USER_CREATE_MESSAGE)) {
        expect(message ?? '').not.toContain('  ');
      }
    });

    it('preserves every legacy login ordinal under the renamed members', () => {
      // This module no longer restates the login vocabulary - one definition, in
      // auth.model.ts. What still has to hold is that the rename changed only the
      // spellings: UserLoginStatus.vb:L23-L31 declares LOGIN_FAILURE 0, LOGIN_SUCCESS 1,
      // LOGIN_SUPERUSER 2, LOGIN_USERLOCKEDOUT 3, LOGIN_USERNOTAPPROVED 4,
      // LOGIN_INSECUREADMINPASSWORD 5 and LOGIN_INSECUREHOSTPASSWORD 6.
      expect(UserLoginStatus.Failure).toBe(0);
      expect(UserLoginStatus.Success).toBe(1);
      expect(UserLoginStatus.SuperUser).toBe(2);
      expect(UserLoginStatus.UserLockedOut).toBe(3);
      expect(UserLoginStatus.UserNotApproved).toBe(4);
      expect(UserLoginStatus.InsecureAdminPassword).toBe(5);
      expect(UserLoginStatus.InsecureHostPassword).toBe(6);

      // The lockout defect at Login.ascx.vb:L187 is only visible once these two ordinals
      // are known: 3 is not 0, so a lockout passed the legacy test.
      expect(UserLoginStatus.UserLockedOut).not.toBe(UserLoginStatus.Failure);
    });

    it('preserves every legacy creation ordinal on the authoritative enumeration', () => {
      // This module no longer derives a vocabulary from the enumeration, because the
      // vocabulary is now the failure codes the API publishes and those are not
      // derivable from legacy member names. The enumeration itself is still a legacy
      // contract worth pinning, and it is pinned directly here: eighteen members with
      // explicit values 0 to 17 in declaration order.
      const fromEnum = Object.values(UserCreateStatus).filter(
        (member): member is string => typeof member === 'string',
      );

      expect(fromEnum.length).toBe(18);
      expect(fromEnum[0]).toBe('AddUser');

      for (let ordinal = 0; ordinal < fromEnum.length; ordinal += 1) {
        const name = fromEnum[ordinal] as keyof typeof UserCreateStatus;
        expect(UserCreateStatus[name])
          .withContext(`${name} must sit at ordinal ${ordinal}`)
          .toBe(ordinal);
      }
    });

    it('keys every vocabulary on the published code, never on a legacy member name', () => {
      // The single assertion that would have caught the defect these vocabularies
      // carried. Every key of every table must be a code the server could publish:
      // lower case, dot-separated, and free of the legacy PascalCase spelling.
      const keys = [
        ...AUTH_FAILURE_CODES,
        ...PASSWORD_UPDATE_CODES,
        ...USER_CREATE_CODES,
        ...CONFLICT_CODES,
      ];

      // Twenty-nine across the four tables: the conflict vocabulary carries ELEVEN, having
      // gained both the group-in-use refusal and the active-alias refusal.
      expect(keys.length).toBe(29);

      for (const key of keys) {
        expect(key).withContext(`${key} must be a published failure code`).toMatch(/^[a-z0-9_]+(\.[a-z0-9_]+)+$/);
      }

      // And each is reachable through the parser from a document as the server writes
      // it, which is the property that makes the tables usable at all.
      for (const key of keys) {
        expect(failureCode({ type: `urn:dnnmigration:error:${key}` }))
          .withContext(`${key} must round-trip through failureCode`)
          .toBe(key);
      }
    });
  });
});
