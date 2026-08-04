import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { ProblemDetails } from '../../../core/models/problem-details.model';
import {
  FORBIDDEN,
  NOT_FOUND,
  SERVER_ERROR,
  TOO_MANY_ATTEMPTS,
} from '../../../core/utils/form-errors.util';
import { ErrorBannerComponent } from './error-banner.component';

describe('ErrorBannerComponent', () => {
  let fixture: ComponentFixture<ErrorBannerComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ErrorBannerComponent] }).compileComponents();

    fixture = TestBed.createComponent(ErrorBannerComponent);
    fixture.detectChanges();
  });

  /** Assigns the problem through the component reference, as OnPush requires. */
  function setProblem(problem: ProblemDetails | null): void {
    fixture.componentRef.setInput('problem', problem);
    fixture.detectChanges();
  }

  /** The rendered summary text, or null when no banner is shown. */
  function summary(): string | null {
    const node = fixture.debugElement.query(By.css('.error-banner__message'));

    return node === null ? null : (node.nativeElement as HTMLElement).textContent!.trim();
  }

  /** The rendered title, or null when none is shown. */
  function title(): string | null {
    const node = fixture.debugElement.query(By.css('.error-banner__title'));

    return node === null ? null : (node.nativeElement as HTMLElement).textContent!.trim();
  }

  /** The rendered severity word, or null when no banner is shown. */
  function severityWord(): string | null {
    const node = fixture.debugElement.query(By.css('.error-banner__severity'));

    return node === null ? null : (node.nativeElement as HTMLElement).textContent!.trim();
  }

  /** The severity attribute on the banner, or null when no banner is shown. */
  function severityAttribute(): string | null {
    const node = fixture.debugElement.query(By.css('.error-banner'));

    return node === null ? null : (node.nativeElement as HTMLElement).getAttribute('data-severity');
  }

  /** The rendered trace reference, or null when none is shown. */
  function trace(): string | null {
    const node = fixture.debugElement.query(By.css('.error-banner__trace'));

    return node === null ? null : (node.nativeElement as HTMLElement).textContent!.trim();
  }

  /** The rendered field labels, in order. */
  function fieldLabels(): readonly string[] {
    return fixture.debugElement
      .queryAll(By.css('.error-banner__field'))
      .map((node) => (node.nativeElement as HTMLElement).textContent!.trim());
  }

  /** The rendered field messages, in order. */
  function fieldMessages(): readonly string[] {
    return fixture.debugElement
      .queryAll(By.css('.error-banner__detail'))
      .map((node) => (node.nativeElement as HTMLElement).textContent!.trim());
  }

  describe('the live region', () => {
    it('is present before any failure, so the first message is announced', () => {
      // A live region inserted at the same moment as its first message is announced
      // inconsistently, and the first message is the one that matters.
      expect(fixture.debugElement.query(By.css('.error-banner-live')))
        .withContext('the region exists with no problem bound')
        .not.toBeNull();
    });

    it('declares an assertive, atomic alert', () => {
      const region = fixture.debugElement.query(By.css('.error-banner-live'))
        .nativeElement as HTMLElement;

      expect(region.getAttribute('role')).toBe('alert');
      expect(region.getAttribute('aria-live')).toBe('assertive');
      expect(region.getAttribute('aria-atomic')).toBe('true');
    });

    it('remains the same element across a failure, so the region is never re-created', () => {
      const before = fixture.debugElement.query(By.css('.error-banner-live')).nativeElement;

      setProblem({ title: 'Not Found', status: 404 });

      expect(fixture.debugElement.query(By.css('.error-banner-live')).nativeElement).toBe(before);
    });
  });

  describe('with no problem', () => {
    it('renders no banner at all', () => {
      expect(fixture.debugElement.query(By.css('.error-banner'))).toBeNull();
      expect(summary()).toBeNull();
    });

    it('treats an explicit null as no problem', () => {
      setProblem(null);

      expect(fixture.debugElement.query(By.css('.error-banner'))).toBeNull();
    });

    it('reports no problem through its own state', () => {
      expect(fixture.componentInstance.hasProblem()).withContext('nothing bound').toBeFalse();
    });
  });

  describe('the summary', () => {
    it('prefers the detail, which describes this occurrence', () => {
      setProblem({ title: 'Not Found', status: 404, detail: 'Portal 42 does not exist.' });

      expect(summary()).toBe('Portal 42 does not exist.');
    });

    it('falls back to the title', () => {
      setProblem({ title: 'Not Found', status: 404 });

      expect(summary()).toBe('Not Found');
    });

    it('falls back to wording chosen from the status when the document carries no text', () => {
      // The wording is the shared utility's, not this component's. A caller-supplied
      // fallback input was removed deliberately: the utility already words every status
      // the API returns, so a generic caller string would have been a less specific
      // answer than the one available for free.
      setProblem({ status: 500 });

      expect(summary()).toBe(SERVER_ERROR);
    });

    it('words a rate-limit refusal calmly rather than as a failure', () => {
      setProblem({ status: 429 });

      expect(summary()).toBe(TOO_MANY_ATTEMPTS);
    });

    it('shows the title above the message when the two differ', () => {
      setProblem({ title: 'Not Found', status: 404, detail: 'Portal 42 does not exist.' });

      expect(title()).toBe('Not Found');
      expect(summary()).toBe('Portal 42 does not exist.');
    });

    it('does not repeat the title as a heading when it IS the message', () => {
      // The utility resolves the message as detail-then-title, so a document carrying
      // only a title would otherwise render the same sentence twice.
      setProblem({ title: 'Not Found', status: 404 });

      expect(summary()).toBe('Not Found');
      expect(title()).withContext('no duplicated heading').toBeNull();
    });

    it('never renders an empty heading line, even for an empty document', () => {
      setProblem({ status: 500 });

      expect(summary()!.length).toBeGreaterThan(0);
    });

    it('escapes markup in a message rather than rendering it', () => {
      // Measured across the legacy resource files, 76 values carry an HTML tag and one
      // carries a live script tag from a remote host. Interpolation is what makes such a
      // value inert.
      setProblem({ status: 400, detail: '<script>alert(1)</script>' });

      const node = fixture.debugElement.query(By.css('.error-banner__message'))
        .nativeElement as HTMLElement;

      expect(node.querySelector('script')).withContext('no element is created').toBeNull();
      expect(node.textContent).toContain('<script>alert(1)</script>');
    });
  });

  describe('per-field messages', () => {
    it('renders a term and a detail for each field', () => {
      setProblem({
        status: 400,
        title: 'One or more validation errors occurred.',
        errors: { PortalName: ['Portal name is required.'] },
      });

      expect(fieldLabels()).toEqual(['portalName']);
      expect(fieldMessages()).toEqual(['Portal name is required.']);
    });

    it('lower-cases only the first character, so the key still matches a control name', () => {
      // Lower-casing the whole key would turn `PageSize` into `pagesize` and stop it
      // matching anything.
      setProblem({ status: 400, errors: { PageSize: ['Out of range.'] } });

      expect(fieldLabels()).toEqual(['pageSize']);
    });

    it('renders every message for a field that has several', () => {
      setProblem({ status: 400, errors: { Password: ['Too short.', 'Too simple.'] } });

      expect(fieldMessages()).toEqual(['Too short.', 'Too simple.']);
    });

    it('drops a field whose messages are all blank, so no empty row is rendered', () => {
      setProblem({ status: 400, errors: { PortalName: ['   ', ''] } });

      expect(fieldLabels()).toEqual([]);
      expect(fixture.componentInstance.hasFieldErrors())
        .withContext('nothing renderable survived')
        .toBeFalse();
    });

    it('renders no field list when the failure carries none', () => {
      setProblem({ title: 'Not Found', status: 404 });

      expect(fixture.debugElement.query(By.css('.error-banner__fields'))).toBeNull();
    });

    it('uses a description list, so a message stays associated with its field', () => {
      setProblem({
        status: 400,
        errors: { PortalName: ['Required.'], Alias: ['Already used.'] },
      });

      const list = fixture.debugElement.query(By.css('.error-banner__fields'))
        .nativeElement as HTMLElement;

      expect(list.tagName).toBe('DL');
      expect(list.querySelectorAll('dt').length).toBe(2);
      expect(list.querySelectorAll('dd').length).toBe(2);
    });
  });

  describe('severity', () => {
    it('presents a permission refusal as a warning, not as an error', () => {
      // Measured from the legacy application twice over: AccessDenied.ascx.vb performs no
      // permission check and renders BOTH of its branches at YellowWarning, and the legacy
      // renderer gave YellowWarning the ordinary `Normal` heading class rather than the red
      // one. A refusal is the system working as configured.
      setProblem({ status: 403, detail: FORBIDDEN });

      expect(fixture.componentInstance.severity()).toBe('warning');
      expect(severityAttribute()).toBe('warning');
      expect(severityWord()).toBe('Warning');
    });

    it('presents a rate-limit refusal calmly', () => {
      // The compensating control for the deliberately removed legacy CAPTCHA. Nothing has
      // failed - the caller is early - so it must not read as a red failure.
      setProblem({ status: 429 });

      expect(fixture.componentInstance.severity()).toBe('calm');
      expect(severityAttribute()).toBe('calm');
      expect(severityWord()).toBe('Please wait');
    });

    it('presents a server fault as danger', () => {
      setProblem({ status: 500 });

      expect(fixture.componentInstance.severity()).toBe('danger');
      expect(severityWord()).toBe('Error');
    });

    it('presents a validation rejection as danger', () => {
      setProblem({ status: 400, errors: { PortalName: ['Required.'] } });

      expect(fixture.componentInstance.severity()).toBe('danger');
    });

    it('presents an unmapped status as danger, because an unanticipated failure matters most', () => {
      // The status is a plain number chosen by the server and is deliberately not narrowed
      // to a union, so an unrecognised value has to resolve to something.
      setProblem({ status: 418 });

      expect(fixture.componentInstance.severity()).toBe('danger');
    });

    it('resolves a severity even for a document carrying no status at all', () => {
      setProblem({ detail: 'Something went wrong.' });

      expect(fixture.componentInstance.severity()).toBe('danger');
    });

    it('carries the band as one attribute, so two bands can never apply at once', () => {
      setProblem({ status: 404, detail: NOT_FOUND });

      const classes = (
        fixture.debugElement.query(By.css('.error-banner')).nativeElement as HTMLElement
      ).className;

      expect(severityAttribute()).toBe('warning');
      expect(classes).withContext('the base class survives the attribute binding').toContain(
        'error-banner',
      );
    });

    it('states the severity as a word, so colour is never the only cue', () => {
      // --color-danger measures 4.0:1 on the background, below the 4.5:1 normal text needs,
      // and its token annotation requires a non-colour cue as well.
      setProblem({ status: 500 });

      expect(severityWord()!.length).toBeGreaterThan(0);
    });
  });

  describe('the trace reference', () => {
    it('shows the identifier the server supplied', () => {
      setProblem({ status: 500, traceId: '00-abc123-def456-01' });

      expect(trace()).toBe('Reference: 00-abc123-def456-01');
    });

    it('shows nothing when the document carried none', () => {
      setProblem({ status: 500 });

      expect(trace()).toBeNull();
      expect(fixture.componentInstance.hasTraceId()).toBeFalse();
    });

    it('treats a blank identifier as absent, because it joins nothing to nothing', () => {
      // A blank string is a legitimate value in this data rather than a synonym for
      // absent, so it is tested for explicitly rather than by truthiness.
      setProblem({ status: 500, traceId: '   ' });

      expect(fixture.componentInstance.traceId()).toBeNull();
      expect(trace()).toBeNull();
    });

    it('exposes no stack trace, exception or developer message of any kind', () => {
      // The legacy error container passed the exception's full string form - the whole
      // stack trace - as the displayed message whenever the caller was a super-user. The
      // problem document carries no such member and the banner surfaces none.
      setProblem({ status: 500, traceId: '00-abc123-def456-01' });

      const rendered = (
        fixture.debugElement.query(By.css('.error-banner')).nativeElement as HTMLElement
      ).textContent!;

      expect(rendered).not.toContain('at ');
      expect(rendered).not.toContain('Exception');
    });
  });

  describe('form-level messages', () => {
    it('shows a message the server keyed to the request beside the summary', () => {
      setProblem({ status: 400, errors: { '': ['The request is contradictory.'] } });

      expect(fieldLabels()).toEqual(['This form']);
      expect(fieldMessages()).toEqual(['The request is contradictory.']);
    });

    it('shows a message keyed to an unreadable body, which has no field either', () => {
      setProblem({ status: 400, errors: { $: ['The JSON value could not be converted.'] } });

      expect(fieldLabels()).toEqual(['This form']);
      expect(fieldMessages()).toEqual(['The JSON value could not be converted.']);
    });

    it('puts form-level messages first, above the per-field ones', () => {
      setProblem({
        status: 400,
        errors: { PortalName: ['Required.'], '': ['The request is contradictory.'] },
      });

      expect(fieldLabels()).toEqual(['This form', 'portalName']);
    });
  });

  describe('legacy break markup', () => {
    it('resolves a leading break in the summary rather than showing it', () => {
      // The legacy renderer appended `<br/>` to its heading and the signup screen appended
      // `<br>` inside a per-character validation loop, one per invalid character.
      setProblem({ status: 400, detail: '<br>You Must Enter a Valid Name' });

      expect(summary()).toBe('You Must Enter a Valid Name');
    });

    it('resolves repeated leading breaks in either spelling', () => {
      setProblem({ status: 400, detail: '<br><br/><BR />Invalid Name' });

      expect(summary()).toBe('Invalid Name');
    });

    it('resolves a leading break in a PER-FIELD message too', () => {
      // Nine validator ErrorMessage attributes in the legacy role editor markup open with
      // `<br>`, so breaks arrive in the dictionary values and not only in the summary.
      setProblem({ status: 400, errors: { ServiceFee: ['<br>Service Fee Value Entered Is Not Valid'] } });

      expect(fieldMessages()).toEqual(['Service Fee Value Entered Is Not Valid']);
    });
  });

  describe('conflict wording', () => {
    it('surfaces a dotted failure code verbatim, never split at the dot', () => {
      // `Portal.LastPortal` is ONE code string. Its wording is stored under the bare key
      // `LastPortal`, and any general "split on dot" rule would corrupt it.
      setProblem({
        status: 409,
        type: 'urn:dnn:error:Portal.LastPortal',
        detail: 'You Can Not Delete The Last Portal In Your Database',
      });

      expect(summary()).toBe('You Can Not Delete The Last Portal In Your Database');
      expect(fixture.componentInstance.severity())
        .withContext('a conflict is actionable, and the legacy surfaced it in red')
        .toBe('danger');
    });
  });

  describe('rendered appearance', () => {
    /**
     * The banner's paint, snapshot as plain strings.
     *
     * The Karma target loads `src/styles.scss`, so the global token sheet is present and
     * every `var()` resolves here exactly as it does in the application. The values are
     * copied out immediately rather than held as a live declaration, which would read
     * whichever element was still attached by the end of the spec.
     */
    function paintOf(status: number): { color: string; border: string; background: string } {
      setProblem({ status, detail: 'A failure occurred.' });

      const node = fixture.debugElement.query(By.css('.error-banner')).nativeElement as HTMLElement;
      const style = getComputedStyle(node);

      return {
        color: style.color,
        border: style.borderTopColor,
        background: style.backgroundColor,
      };
    }

    it('paints only the danger band in the error colour', () => {
      // Measured provenance: the legacy renderer gave `RedError` the `NormalRed` heading
      // class - the sole #ff0000 declaration, sitting under the comment "text style used
      // for error messages" - and gave `YellowWarning` the ordinary `Normal` class. The
      // legacy withheld the red for a refusal, and so does this.
      expect(paintOf(500).color).toBe('rgb(255, 0, 0)');
      expect(paintOf(500).border).toBe('rgb(255, 0, 0)');
      expect(paintOf(403).color).not.toBe('rgb(255, 0, 0)');
      expect(paintOf(429).color).not.toBe('rgb(255, 0, 0)');
    });

    it('gives the three bands mutually distinct surfaces', () => {
      // rgb(255, 255, 153) is --color-surface-hint, which stands in for the legacy yellow
      // warning icon; rgb(238, 238, 238) is --color-surface.
      const danger = paintOf(500);
      const warning = paintOf(403);
      const calm = paintOf(429);

      expect(warning.background).toBe('rgb(255, 255, 153)');
      expect(calm.background).toBe('rgb(238, 238, 238)');
      expect(danger.background).not.toBe(warning.background);
      expect(warning.background).not.toBe(calm.background);
    });

    it('resolves every token it references, so no property falls back to an initial value', () => {
      setProblem({ status: 500 });

      const style = getComputedStyle(
        fixture.debugElement.query(By.css('.error-banner')).nativeElement as HTMLElement,
      );

      expect(style.borderTopWidth).toBe('1px');
      expect(style.borderTopStyle).toBe('solid');
      expect(style.borderTopLeftRadius).toBe('4px');
      expect(style.display).toBe('flex');
      expect(parseFloat(style.rowGap)).toBeGreaterThan(0);
      expect(parseFloat(style.paddingTop)).toBeGreaterThan(0);
    });

    it('shows the severity word as visible text, not as a hidden label', () => {
      // --color-danger measures 4.0:1 on the background, below the 4.5:1 normal text
      // needs, so the band must be legible without perceiving the colour. A visually
      // hidden word would satisfy a screen reader and fail a sighted reader.
      setProblem({ status: 500 });

      const word = fixture.debugElement.query(By.css('.error-banner__severity'))
        .nativeElement as HTMLElement;

      expect(getComputedStyle(word).display).not.toBe('none');
      expect(word.getBoundingClientRect().height).toBeGreaterThan(0);
    });

    it('de-emphasises the trace reference rather than letting it inherit the band colour', () => {
      setProblem({ status: 500, traceId: '00-abc-def-01' });

      const traceStyle = getComputedStyle(
        fixture.debugElement.query(By.css('.error-banner__trace')).nativeElement as HTMLElement,
      );
      const messageStyle = getComputedStyle(
        fixture.debugElement.query(By.css('.error-banner__message')).nativeElement as HTMLElement,
      );

      // rgb(105, 105, 105) is --color-text-muted. A red reference string would compete
      // with the message for attention.
      expect(traceStyle.color).toBe('rgb(105, 105, 105)');
      expect(parseFloat(traceStyle.fontSize)).toBeLessThan(parseFloat(messageStyle.fontSize));
    });

    it('keeps the empty live region at zero height, so it costs no space', () => {
      const region = fixture.debugElement.query(By.css('.error-banner-live'))
        .nativeElement as HTMLElement;

      expect(region.getBoundingClientRect().height).toBe(0);
    });
  });

  describe('clearing', () => {
    it('removes the banner when the problem is withdrawn', () => {
      setProblem({ title: 'Not Found', status: 404 });
      expect(fixture.debugElement.query(By.css('.error-banner'))).not.toBeNull();

      setProblem(null);

      expect(fixture.debugElement.query(By.css('.error-banner')))
        .withContext('the region stays, the banner goes')
        .toBeNull();
    });
  });

  describe('change detection', () => {
    it('declares the on-push strategy the migration plan mandates', () => {
      // Read from the compiled definition rather than from the decorator source, so the
      // expectation reflects what the framework actually uses.
      const definition = (ErrorBannerComponent as unknown as { ɵcmp: { onPush: boolean } }).ɵcmp;

      expect(definition.onPush).withContext('OnPush is mandated for every component').toBeTrue();
    });
  });
});
