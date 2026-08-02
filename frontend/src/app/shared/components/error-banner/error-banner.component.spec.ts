import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { ProblemDetails } from '../../../core/models/problem-details.model';
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

    it('falls back to the supplied fallback when the document carries no text', () => {
      fixture.componentRef.setInput('fallbackMessage', 'The portal could not be saved.');
      setProblem({ status: 500 });

      expect(summary()).toBe('The portal could not be saved.');
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

    it('labels a form-level message rather than rendering a blank term', () => {
      setProblem({ status: 400, errors: { '': ['The request is contradictory.'] } });

      expect(fieldLabels()).toEqual(['This form']);
      expect(fieldMessages()).toEqual(['The request is contradictory.']);
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
