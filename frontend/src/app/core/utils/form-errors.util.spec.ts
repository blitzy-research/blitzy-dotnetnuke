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
//     `UserCreateStatus.Success` is 13, `UserLoginStatus.LOGIN_SUCCESS` is 1, and
//     the legacy `UserValidStatus.VALID` is 0. A "zero means success" assumption is
//     wrong two times out of three, and `UserCreateStatus.AddUser` at 0 is the
//     "no error yet" seed rather than an outcome.
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
  PASSWORD_UPDATE_STATUS_NAMES,
  REQUEST_REJECTED,
  SERVER_ERROR,
  TOO_MANY_ATTEMPTS,
  USER_CREATE_MESSAGE,
  USER_CREATE_STATUS_NAMES,
  USER_LOGIN_STATUS_NAMES,
  VALIDATION_REJECTED,
  advisoryMessage,
  authFailureMessage,
  conflictMessage,
  fieldErrorMessage,
  fieldErrorMessages,
  fieldMessages,
  formLevelMessages,
  isValidationProblemDetails,
  passwordUpdateMessage,
  problemMessage,
  problemSeverity,
  problemTraceId,
  resolveVerificationPrompt,
  statusMessage,
  stripLegacyBreakTags,
  summarizeProblem,
  userCreateMessage,
} from './form-errors.util';
import type { ProblemDetails, ValidationProblemDetails } from '../models/problem-details.model';

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

      expect(withNull.code).toBe('EnterCode');
      expect(withEmpty.code).toBe('EnterCode');
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
  // (e) THE TRACE IDENTIFIER IS SURFACED, AND ABSENCE IS SAFE
  // -------------------------------------------------------------------------
  describe('problemTraceId', () => {
    it('surfaces the identifier when the document carries one', () => {
      const problem: ProblemDetails = { status: 500, traceId: '00-abc-def-00' };

      expect(problemTraceId(problem)).toBe('00-abc-def-00');
      expect(summarizeProblem(problem).traceId).toBe('00-abc-def-00');
    });

    it('reports null rather than throwing when the document carries none', () => {
      expect(problemTraceId({ status: 500 })).toBeNull();
      expect(problemTraceId(null)).toBeNull();
      expect(problemTraceId(undefined)).toBeNull();
      expect(summarizeProblem(null).traceId).toBeNull();
    });

    it('treats a blank identifier as absent, because it joins nothing to nothing', () => {
      expect(problemTraceId({ status: 500, traceId: '   ' })).toBeNull();
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

    it('presents a rate-limit refusal as a warning, because nothing has failed', () => {
      expect(problemSeverity(429)).toBe('warning');
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
      expect(summary.traceId).toBe('00-trace-span-00');
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
      expect(summarizeProblem({ status: 429 }).severity).toBe('warning');
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

      expect(outcome.code).toBe('UserNotAuthorized');
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

      expect(outcome.code).toBe('EnterCode');
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

      expect(outcome.code).toBe('InvalidCode');
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
      ).toBe('EnterCode');
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
      ).toBe('InvalidCode');
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
        'EnterCode',
        'InvalidCode',
        'EnterCode',
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

    it('exposes exactly three codes and nothing else', () => {
      expect(AUTH_FAILURE_CODES).toEqual(['EnterCode', 'InvalidCode', 'UserNotAuthorized']);
      expect(AUTH_FAILURE_CODES.length).toBe(3);
      expect(authFailureMessage('EnterCode')).toBe('Enter Your Verification Code');
      expect(authFailureMessage('NotACode')).toBeNull();
      expect(authFailureMessage(null)).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // THE ORDINAL CORRECTION AND THE THREE SUCCESS CONVENTIONS
  // -------------------------------------------------------------------------
  describe('password-change vocabulary', () => {
    it('records the ordinals in DECLARATION ORDER, which is the authoritative order', () => {
      // PasswordUpdateStatus.vb:L23-L32 declares no explicit values, so position is
      // the ordinal. This assertion is what stops the incorrect ordering that
      // circulates in the requirements from creeping back in.
      expect([...PASSWORD_UPDATE_STATUS_NAMES]).toEqual([
        'Success',
        'PasswordMissing',
        'PasswordNotDifferent',
        'PasswordResetFailed',
        'PasswordInvalid',
        'PasswordMismatch',
        'InvalidPasswordAnswer',
        'InvalidPasswordQuestion',
      ]);
      expect(PASSWORD_UPDATE_STATUS_NAMES.indexOf('Success')).toBe(0);
      expect(PASSWORD_UPDATE_STATUS_NAMES.indexOf('PasswordMissing')).toBe(1);
      expect(PASSWORD_UPDATE_STATUS_NAMES.indexOf('PasswordNotDifferent')).toBe(2);
      expect(PASSWORD_UPDATE_STATUS_NAMES.indexOf('PasswordResetFailed')).toBe(3);
      expect(PASSWORD_UPDATE_STATUS_NAMES.indexOf('PasswordInvalid')).toBe(4);
      expect(PASSWORD_UPDATE_STATUS_NAMES.indexOf('PasswordMismatch')).toBe(5);
      expect(PASSWORD_UPDATE_STATUS_NAMES.indexOf('InvalidPasswordAnswer')).toBe(6);
      expect(PASSWORD_UPDATE_STATUS_NAMES.indexOf('InvalidPasswordQuestion')).toBe(7);
    });

    it('words each failure and reports null for the non-failure and the unknown', () => {
      expect(passwordUpdateMessage('PasswordMissing')).toBe(
        'You must provide your current password in order to change the password.',
      );
      expect(passwordUpdateMessage('InvalidPasswordAnswer')).toBe(
        'Password Answer must be provided',
      );
      expect(passwordUpdateMessage('Success')).toBeNull();
      expect(passwordUpdateMessage('NotAStatus')).toBeNull();
      expect(passwordUpdateMessage(null)).toBeNull();
    });

    it('keys on the string code and never on a numeric ordinal', () => {
      expect(passwordUpdateMessage('1')).toBeNull();
      expect(passwordUpdateMessage('5')).toBeNull();
    });
  });

  describe('user-creation vocabulary', () => {
    it('places Success at 13 and the AddUser seed at 0', () => {
      // UserCreateStatus.vb:L23-L42 assigns explicit values 0..17 in declaration
      // order, so position is the ordinal here too.
      expect(USER_CREATE_STATUS_NAMES.length).toBe(18);
      expect(USER_CREATE_STATUS_NAMES.indexOf('AddUser')).toBe(0);
      expect(USER_CREATE_STATUS_NAMES.indexOf('Success')).toBe(13);
      expect(USER_CREATE_STATUS_NAMES.indexOf('AddUserToPortal')).toBe(17);
    });

    it('proves the three success conventions differ, so zero never means success', () => {
      expect(USER_CREATE_STATUS_NAMES.indexOf('Success')).toBe(13);
      expect(USER_LOGIN_STATUS_NAMES.indexOf('LOGIN_SUCCESS')).toBe(1);
      expect(USER_CREATE_STATUS_NAMES[0]).toBe('AddUser');
      expect(USER_LOGIN_STATUS_NAMES[0]).toBe('LOGIN_FAILURE');
    });

    it('keeps the three username outcomes as three distinct members', () => {
      expect(USER_CREATE_STATUS_NAMES.indexOf('UsernameAlreadyExists')).toBe(1);
      expect(USER_CREATE_STATUS_NAMES.indexOf('DuplicateUserName')).toBe(5);
      expect(USER_CREATE_STATUS_NAMES.indexOf('InvalidUserName')).toBe(11);
      expect(Object.keys(USER_CREATE_MESSAGE)).toContain('UsernameAlreadyExists');
      expect(Object.keys(USER_CREATE_MESSAGE)).toContain('DuplicateUserName');
      expect(Object.keys(USER_CREATE_MESSAGE)).toContain('InvalidUserName');
    });

    it('reproduces the legacy grouping of outcomes onto shared wording', () => {
      // GetUserCreateStatus combines these in one Case arm.
      const nameTaken = userCreateMessage('UsernameAlreadyExists');

      expect(userCreateMessage('DuplicateUserName')).toBe(nameTaken);
      expect(userCreateMessage('UserAlreadyRegistered')).toBe(nameTaken);

      const registrationError = userCreateMessage('ProviderError');

      expect(userCreateMessage('DuplicateProviderUserKey')).toBe(registrationError);
      expect(userCreateMessage('InvalidProviderUserKey')).toBe(registrationError);
      expect(userCreateMessage('UnexpectedError')).toBe(registrationError);
    });

    it('gives InvalidUserName its own wording despite the shared username grouping', () => {
      expect(userCreateMessage('InvalidUserName')).toBe(
        'The username specified is invalid. Please specify a valid username.',
      );
      expect(userCreateMessage('InvalidUserName')).not.toBe(
        userCreateMessage('UsernameAlreadyExists'),
      );
    });

    it('returns null for the three outcomes that are not failures, instead of throwing', () => {
      // The legacy Case Else threw ArgumentException for exactly these.
      expect(userCreateMessage('AddUser')).toBeNull();
      expect(userCreateMessage('Success')).toBeNull();
      expect(userCreateMessage('AddUserToPortal')).toBeNull();
      expect(() => userCreateMessage('AnythingElse')).not.toThrow();
      expect(userCreateMessage('AnythingElse')).toBeNull();
    });

    it('omits the password-policy numbers rather than inventing them', () => {
      const message = userCreateMessage('InvalidPassword');

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
      expect(userCreateMessage('UsernameAlreadyExists')).toBe(
        'A User Already Exists For the Username Specified. Please Register Again Using A Different Username.',
      );
      expect(userCreateMessage('DuplicateEmail')).toBe(
        'A user already exists for the email address specified. Please login using the registered account of that email address.',
      );
      expect(userCreateMessage('ProviderError')).toBe(
        'An Unexpected Error Occurred During Registration. Please Contact The Portal Administrator For Further Information.',
      );
      expect(userCreateMessage('UserRejected')).toBe(
        'This user registration has been rejected. Please contact an administrator for more information.',
      );
      expect(passwordUpdateMessage('PasswordInvalid')).toBe(
        'You must enter a valid password. Please check with the Portal Administrator if you do not know the password requirements.',
      );
      expect(conflictMessage('TabExists')).toBe(
        'The Page Name you chose is already being used for another page at the same level of the page hierarchy.',
      );
      expect(conflictMessage('DuplicateRoleGroup')).toBe(
        'A role group with the same name already exists. The new group was not added.',
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
  // CONFLICT CODES, VERBATIM
  // -------------------------------------------------------------------------
  describe('conflict vocabulary', () => {
    it('carries all eleven codes, spelled exactly as they arrive', () => {
      expect([...CONFLICT_CODES]).toEqual([
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
      ]);
    });

    it('treats the dotted code as ONE string, not as a path', () => {
      expect(conflictMessage('Portal.LastPortal')).toBe(
        'You Can Not Delete The Last Portal In Your Database',
      );
      expect(conflictMessage('Portal')).toBeNull();
      expect(conflictMessage('LastPortal')).toBeNull();
    });

    it('words every code and leaves none blank', () => {
      for (const code of CONFLICT_CODES) {
        expect(conflictMessage(code)).toBe(CONFLICT_MESSAGE[code]);
        expect((conflictMessage(code) ?? '').length).toBeGreaterThan(0);
      }
    });

    it('reports null for a code it does not recognise', () => {
      expect(conflictMessage('DuplicateSomethingElse')).toBeNull();
      expect(conflictMessage(null)).toBeNull();
    });

    it('corrects the legacy misspelling in the page-name conflict wording', () => {
      expect(conflictMessage('TabExists')).toContain('hierarchy');
      expect(conflictMessage('TabExists')).not.toContain('heirarchy');
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
      expect(Object.isFrozen(USER_CREATE_MESSAGE)).toBeTrue();
      expect(Object.isFrozen(USER_CREATE_STATUS_NAMES)).toBeTrue();
      expect(Object.isFrozen(PASSWORD_UPDATE_STATUS_NAMES)).toBeTrue();
      expect(Object.isFrozen(USER_LOGIN_STATUS_NAMES)).toBeTrue();
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

    it('keeps the legacy SCREAMING_CASE spelling of the non-wire login statuses', () => {
      expect([...USER_LOGIN_STATUS_NAMES]).toEqual([
        'LOGIN_FAILURE',
        'LOGIN_SUCCESS',
        'LOGIN_SUPERUSER',
        'LOGIN_USERLOCKEDOUT',
        'LOGIN_USERNOTAPPROVED',
        'LOGIN_INSECUREADMINPASSWORD',
        'LOGIN_INSECUREHOSTPASSWORD',
      ]);
      // The lockout defect at Login.ascx.vb:L187 is only visible once these two
      // ordinals are known: 3 is not 0, so a lockout passed the legacy test.
      expect(USER_LOGIN_STATUS_NAMES.indexOf('LOGIN_USERLOCKEDOUT')).toBe(3);
      expect(USER_LOGIN_STATUS_NAMES.indexOf('LOGIN_FAILURE')).toBe(0);
    });
  });
});
