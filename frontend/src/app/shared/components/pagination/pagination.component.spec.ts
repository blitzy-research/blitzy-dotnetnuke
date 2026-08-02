import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { PaginationComponent } from './pagination.component';

describe('PaginationComponent', () => {
  let fixture: ComponentFixture<PaginationComponent>;
  let emitted: number[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [PaginationComponent] }).compileComponents();

    fixture = TestBed.createComponent(PaginationComponent);
    emitted = [];
    fixture.componentInstance.pageChange.subscribe((page) => emitted.push(page));
  });

  /** Binds the three envelope members, as OnPush requires. */
  function bind(page: number, pageSize: number, totalCount: number, disabled = false): void {
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('pageSize', pageSize);
    fixture.componentRef.setInput('totalCount', totalCount);
    fixture.componentRef.setInput('disabled', disabled);
    fixture.detectChanges();
  }

  /** The four navigation buttons, in rendered order. */
  function buttons(): readonly HTMLButtonElement[] {
    return fixture.debugElement
      .queryAll(By.css('.pagination__button'))
      .map((node) => node.nativeElement as HTMLButtonElement);
  }

  function statusText(): string | null {
    const node = fixture.debugElement.query(By.css('.pagination__status'));

    return node === null ? null : (node.nativeElement as HTMLElement).textContent!.replace(/\s+/g, ' ').trim();
  }

  describe('visibility', () => {
    it('renders nothing when everything fits on one page', () => {
      // Navigation between one page and itself is not an affordance.
      bind(0, 10, 7);

      expect(fixture.debugElement.query(By.css('.pagination'))).toBeNull();
    });

    it('renders nothing for an empty result set', () => {
      bind(0, 10, 0);

      expect(fixture.debugElement.query(By.css('.pagination'))).toBeNull();
    });

    it('renders once there is more than one page', () => {
      bind(0, 10, 11);

      expect(fixture.debugElement.query(By.css('.pagination'))).not.toBeNull();
    });

    it('renders nothing when the page size requests every match', () => {
      // A page size of zero is a deliberate request for every match in one page.
      bind(0, 0, 5000);

      expect(fixture.debugElement.query(By.css('.pagination'))).toBeNull();
    });
  });

  describe('page arithmetic', () => {
    it('reports one page for an empty result set rather than zero', () => {
      // Returning zero would render "page 1 of 0" and make the next-page test false for
      // the only page that exists.
      bind(0, 10, 0);

      expect(fixture.componentInstance.totalPages).toBe(1);
    });

    it('rounds a partial final page up', () => {
      bind(0, 10, 21);

      expect(fixture.componentInstance.totalPages).toBe(3);
    });

    it('reports one page when the page size requests every match, rather than dividing by zero', () => {
      bind(0, 0, 5000);

      expect(fixture.componentInstance.totalPages).toBe(1);
    });

    it('renders the position one-based, because a person counts from one', () => {
      bind(2, 10, 100);

      expect(fixture.debugElement.query(By.css('.pagination__position')).nativeElement.textContent)
        .toContain('3 / 10');
    });
  });

  describe('the range summary', () => {
    it('names the range shown and the total', () => {
      bind(1, 10, 34);

      expect(statusText()).toBe('11–20 of 34');
    });

    it('clamps the last item to the total on a short final page', () => {
      bind(3, 10, 34);

      expect(statusText()).toBe('31–34 of 34');
    });

    it('is announced politely, because a page change replaces the list without moving focus', () => {
      bind(0, 10, 34);

      expect(
        (fixture.debugElement.query(By.css('.pagination__status')).nativeElement as HTMLElement)
          .getAttribute('aria-live'),
      )
        .withContext('a status update must not interrupt')
        .toBe('polite');
    });
  });

  describe('availability', () => {
    it('disables the backward steps on the first page', () => {
      bind(0, 10, 100);

      const [first, previous, next, last] = buttons();
      expect(first!.disabled).withContext('already on the first page').toBeTrue();
      expect(previous!.disabled).toBeTrue();
      expect(next!.disabled).toBeFalse();
      expect(last!.disabled).toBeFalse();
    });

    it('disables the forward steps on the last page', () => {
      bind(9, 10, 100);

      const [first, previous, next, last] = buttons();
      expect(first!.disabled).toBeFalse();
      expect(previous!.disabled).toBeFalse();
      expect(next!.disabled).withContext('already on the last page').toBeTrue();
      expect(last!.disabled).toBeTrue();
    });

    it('disables every step while a page is loading', () => {
      // Otherwise a person can queue three page changes and end up on one they did not
      // ask for last.
      bind(5, 10, 100, true);

      expect(buttons().every((button) => button.disabled))
        .withContext('all four steps are unavailable')
        .toBeTrue();
    });

    it('keeps the disabled steps in the document, so the control does not reflow under the pointer', () => {
      bind(0, 10, 100);

      expect(buttons().length).toBe(4);
    });
  });

  describe('emitting a page change', () => {
    it('emits the following page index', () => {
      bind(0, 10, 100);

      buttons()[2]!.click();

      expect(emitted).toEqual([1]);
    });

    it('emits the previous page index', () => {
      bind(4, 10, 100);

      buttons()[1]!.click();

      expect(emitted).toEqual([3]);
    });

    it('emits zero for the first page', () => {
      bind(4, 10, 100);

      buttons()[0]!.click();

      expect(emitted).toEqual([0]);
    });

    it('emits the last page index', () => {
      bind(4, 10, 100);

      buttons()[3]!.click();

      expect(emitted).toEqual([9]);
    });

    it('emits nothing for a step that is unavailable', () => {
      bind(0, 10, 100);

      fixture.componentInstance.goPrevious();
      fixture.componentInstance.goFirst();

      expect(emitted).toEqual([]);
    });

    it('emits nothing while loading', () => {
      bind(5, 10, 100, true);

      fixture.componentInstance.goNext();
      fixture.componentInstance.goPrevious();

      expect(emitted).toEqual([]);
    });

    it('does not change its own page, so the pager cannot claim a page whose request failed', () => {
      bind(3, 10, 100);

      buttons()[2]!.click();
      fixture.detectChanges();

      expect(fixture.componentInstance.page)
        .withContext('the consumer rebinds this once the page has actually arrived')
        .toBe(3);
    });
  });

  describe('accessibility', () => {
    it('names the navigation landmark', () => {
      bind(0, 10, 100);

      const nav = fixture.debugElement.query(By.css('.pagination')).nativeElement as HTMLElement;
      expect(nav.tagName).toBe('NAV');
      expect(nav.getAttribute('aria-label')).toBe('Pagination');
    });

    it('names every step, so none is announced as a punctuation character', () => {
      bind(4, 10, 100);

      expect(buttons().map((button) => button.getAttribute('aria-label'))).toEqual([
        'First page',
        'Previous page',
        'Next page',
        'Last page',
      ]);
    });

    it('hides the glyphs from assistive technology', () => {
      bind(4, 10, 100);

      for (const button of buttons()) {
        expect(button.querySelector('span')!.getAttribute('aria-hidden')).toBe('true');
      }
    });

    it('declares an explicit button type, so a pager inside a form cannot submit it', () => {
      bind(4, 10, 100);

      expect(buttons().every((button) => button.getAttribute('type') === 'button'))
        .withContext('without it, a button in a form defaults to submit')
        .toBeTrue();
    });

    it('renders the current position as text rather than as a disabled control', () => {
      bind(4, 10, 100);

      const position = fixture.debugElement.query(By.css('.pagination__position'))
        .nativeElement as HTMLElement;

      expect(position.tagName)
        .withContext('a disabled button would put an unusable stop in the tab order')
        .toBe('SPAN');
    });
  });

  describe('change detection', () => {
    it('declares the on-push strategy the migration plan mandates', () => {
      const definition = (PaginationComponent as unknown as { ɵcmp: { onPush: boolean } }).ɵcmp;

      expect(definition.onPush).toBeTrue();
    });
  });
});
