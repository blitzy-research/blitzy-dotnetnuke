import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AppComponent } from './app.component';
import { environment } from '../environments/environment';

describe('AppComponent', () => {
  let fixture: ComponentFixture<AppComponent>;
  let component: AppComponent;

  /**
   * Returns the root component's host element.
   */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      // The shell renders a router outlet and the banner inside it renders a router
      // link, so a router must be present. An empty route table is sufficient: no
      // assertion here concerns navigation.
      providers: [provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(AppComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  describe('construction', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const definition = (
        AppComponent as unknown as {
          ɵcmp?: { onPush?: boolean };
        }
      ).ɵcmp;

      expect(definition).toBeDefined();
      expect(definition?.onPush).toBeTrue();
    });

    it('is standalone, so it can be bootstrapped without a module', () => {
      const definition = (
        AppComponent as unknown as {
          ɵcmp?: { standalone?: boolean };
        }
      ).ɵcmp;

      expect(definition?.standalone).toBeTrue();
    });

    it('is selected by the element name the document declares', () => {
      const definition = (
        AppComponent as unknown as {
          ɵcmp?: { selectors?: unknown[][] };
        }
      ).ɵcmp;

      expect(definition?.selectors?.[0]?.[0]).toBe('app-root');
    });
  });

  describe('composition', () => {
    it('mounts exactly one element, as its stylesheet is written to expect', () => {
      expect(host().children.length).toBe(1);
    });

    it('mounts the shell', () => {
      expect(host().firstElementChild?.tagName.toLowerCase()).toBe('app-shell');
    });

    it('renders the shell with the grid class, so the layout engages end to end', () => {
      const shell = host().querySelector('app-shell');

      expect(shell?.classList.contains('shell')).toBeTrue();
    });
  });

  describe('regions reachable through the shell', () => {
    it('renders the skip link, the banner, the main region and the footer', () => {
      expect(host().querySelector('a.shell__skip-link')).not.toBeNull();
      expect(host().querySelector('app-header header')).not.toBeNull();
      expect(host().querySelector('main#main-content')).not.toBeNull();
      expect(host().querySelector('app-footer footer')).not.toBeNull();
    });

    it('emits each singular landmark exactly once across the whole application', () => {
      // Presence is not the interesting property; uniqueness is. A document with two
      // banners or two contentinfo landmarks is a genuine accessibility defect, and
      // the root is the only scope at which the duplication would be observable.
      for (const landmark of ['header', 'main', 'footer']) {
        expect(host().querySelectorAll(landmark).length)
          .withContext(`the application must render exactly one <${landmark}>`)
          .toBe(1);
      }
    });

    it('renders exactly one router outlet, and renders it inside the main landmark', () => {
      const main = host().querySelector('main');

      expect(main).not.toBeNull();
      expect(host().querySelectorAll('router-outlet').length).toBe(1);

      // An outlet at the root level would compile, render, and put every routed view
      // outside the main landmark and outside the shell's page gutter — a defect that
      // is invisible until someone reads the accessibility tree.
      expect(main?.querySelectorAll('router-outlet').length).toBe(1);
    });

    it('exposes a skip link whose fragment resolves from the real root', () => {
      const link = host().querySelector<HTMLAnchorElement>('a.shell__skip-link');

      expect(link).not.toBeNull();

      const fragment = link?.getAttribute('href') ?? '';

      expect(fragment.startsWith('#')).toBeTrue();

      // Resolved against the FULL document tree rather than against the shell's
      // subtree. The fragment a browser follows is resolved document-wide, so this is
      // the scope at which a duplicate id or a stale fragment would actually bite.
      const resolved = host().querySelector(fragment);

      expect(resolved).not.toBeNull();
      expect(resolved).toBe(host().querySelector('main'));
    });

    it('renders no chrome of its own', () => {
      // The root owns no data, no navigation and no chrome; the shell renders all of
      // it. Any of these appearing at this level would be a second definition of
      // something that already exists exactly once.
      expect(host().querySelectorAll(':scope > header').length).toBe(0);
      expect(host().querySelectorAll(':scope > nav').length).toBe(0);
      expect(host().querySelectorAll(':scope > main').length).toBe(0);
      expect(host().querySelectorAll(':scope > footer').length).toBe(0);
    });

    it('renders the banner with the build-time application name, no binding required', () => {
      const brand = host().querySelector('a.app-header__brand');

      expect(brand?.textContent?.trim()).toBe(environment.applicationName);
    });
  });

  describe('session', () => {
    it('binds no session, so the banner renders no session cluster', () => {
      // This is an assertion about a deliberate scope decision, not about an
      // unfinished one: no authentication service exists in this workspace, the
      // shell forwards an absent display name unchanged, and the banner treats an
      // absent name as no account signed in. Should a session ever be bound in the
      // root template without the banner being updated, this expectation fails and
      // says why.
      expect(host().querySelector('span.app-header__user')).toBeNull();
      expect(host().querySelector('button.app-header__logout')).toBeNull();
    });

    it('leaves the secondary navigation region empty, so the stylesheet collapses it', () => {
      const region = host().querySelector('div.shell__sidebar');

      expect(region).not.toBeNull();
      expect(region?.children.length).toBe(0);
    });
  });
});
