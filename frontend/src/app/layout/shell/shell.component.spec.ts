import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { ShellComponent } from './shell.component';

/**
 * A host that mounts the shell with projected navigation content.
 *
 * The projection contract can only be observed from outside: a fixture created
 * directly on the shell has no content children to project, so the region is
 * necessarily empty and an assertion made there would prove only that the region
 * exists.
 */
@Component({
  standalone: true,
  imports: [ShellComponent],
  template: `
    <app-shell>
      <nav aria-label="Administration" class="projected-navigation">
        <a href="/portals">Portals</a>
      </nav>
    </app-shell>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class ProjectingHostComponent {}

describe('ShellComponent', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let component: ShellComponent;

  /**
   * Returns the shell's host element.
   */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * Sets one of the shell's inputs and re-renders.
   *
   * Assigning to the instance field would NOT re-render: the shell declares on-push
   * change detection, so a fixture-level `detectChanges` skips it unless it has been
   * marked dirty. `setInput` both marks it dirty and runs the input's declared
   * transform, which is what a real template binding does.
   *
   * @param name The input to set.
   * @param value The value to set it to, before any transform.
   */
  function setInput(name: 'applicationName' | 'userName' | 'signingOut', value: unknown): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  /**
   * Returns the skip link.
   */
  function skipLink(): HTMLAnchorElement | null {
    return host().querySelector('a.shell__skip-link');
  }

  /**
   * Returns the banner component's host element.
   */
  function header(): HTMLElement | null {
    return host().querySelector('app-header');
  }

  /**
   * Returns the secondary navigation region.
   */
  function sidebar(): HTMLElement | null {
    return host().querySelector('div.shell__sidebar');
  }

  /**
   * Returns the main region.
   */
  function main(): HTMLElement | null {
    return host().querySelector('main');
  }

  /**
   * Returns the footer component's host element.
   */
  function footer(): HTMLElement | null {
    return host().querySelector('app-footer');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ShellComponent],
      // A router is required twice over: the shell renders a router outlet and the
      // banner it composes renders a router link. An empty route table satisfies
      // both without any navigation occurring.
      providers: [provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(ShellComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  describe('construction', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const definition = (
        ShellComponent as unknown as {
          ɵcmp?: { onPush?: boolean };
        }
      ).ɵcmp;

      expect(definition).toBeDefined();
      expect(definition?.onPush).toBeTrue();
    });

    it('is standalone', () => {
      const definition = (
        ShellComponent as unknown as {
          ɵcmp?: { standalone?: boolean };
        }
      ).ɵcmp;

      expect(definition?.standalone).toBeTrue();
    });
  });

  describe('host class', () => {
    it('carries the shell class, without which the global grid never engages', () => {
      expect(host().classList.contains('shell')).toBeTrue();
    });
  });

  describe('region composition', () => {
    it('renders all five regions', () => {
      expect(skipLink()).not.toBeNull();
      expect(header()).not.toBeNull();
      expect(sidebar()).not.toBeNull();
      expect(main()).not.toBeNull();
      expect(footer()).not.toBeNull();
    });

    it('renders them in the source order the grid track list and the tab order require', () => {
      const children = Array.from(host().children);

      // The non-null assertions below are guarded both by the count expectation and
      // by the preceding test, which proves every region is present. Jasmine's
      // `toBe` infers its expected type from the actual, so a nullable argument
      // would not type-check against a non-nullable `Element`.
      expect(children.length).toBe(5);
      expect(children[0]).toBe(skipLink()!);
      expect(children[1]).toBe(header()!);
      expect(children[2]).toBe(sidebar()!);
      expect(children[3]).toBe(main()!);
      expect(children[4]).toBe(footer()!);
    });

    it('places each region in its published grid area by class', () => {
      expect(header()?.classList.contains('shell__header')).toBeTrue();
      expect(sidebar()?.classList.contains('shell__sidebar')).toBeTrue();
      expect(main()?.classList.contains('shell__main')).toBeTrue();
      expect(footer()?.classList.contains('shell__footer')).toBeTrue();
    });

    it('renders exactly one router outlet, and renders it inside the main region', () => {
      expect(host().querySelectorAll('router-outlet').length).toBe(1);
      expect(main()?.querySelector('router-outlet')).not.toBeNull();
    });
  });

  describe('skip link', () => {
    it('is the first element in the shell, so it is the first focus stop', () => {
      expect(host().firstElementChild).toBe(skipLink());
    });

    it('targets the main region by fragment', () => {
      expect(skipLink()?.getAttribute('href')).toBe('#main-content');
    });

    it('names a fragment that the main region actually carries', () => {
      const target = skipLink()?.getAttribute('href') ?? '';

      expect(target.startsWith('#')).toBeTrue();
      expect(main()?.id).toBe(target.slice(1));
    });

    it('is labelled with text, so its purpose is announced', () => {
      expect(skipLink()?.textContent?.trim()).toBe('Skip to main content');
    });

    it('exposes the same target through the component, so the two cannot drift', () => {
      expect(component.skipLinkTarget).toBe(`#${component.mainRegionId}`);
    });

    it('moves focus to the main region when it is activated', () => {
      const link = skipLink();
      const region = main();

      expect(link).not.toBeNull();
      expect(region).not.toBeNull();

      link?.click();

      // THE POINT OF THIS TEST. Every other assertion in this block inspects
      // markup, and markup alone cannot distinguish a working affordance from a
      // broken one here: the fragment is correct, the target exists, and the link
      // is the first tab stop in both the working and the broken arrangement. What
      // the affordance exists to DO is move focus, so that is what is asserted.
      expect(document.activeElement).toBe(region);
    });

    it('does not let its default action resolve the fragment', () => {
      const link = skipLink();

      expect(link).not.toBeNull();

      const activation = new MouseEvent('click', { bubbles: true, cancelable: true });
      link?.dispatchEvent(activation);

      // THIS GUARDS A DEFECT THAT WAS REAL, NOT HYPOTHETICAL. A fragment-only href
      // resolves against the document's BASE url rather than against the address
      // showing. `index.html` declares `<base href="/">`, which deep links require,
      // so from any route below the root the href names a DIFFERENT PATH and
      // letting it resolve performs a full document reload that discards the
      // current screen. That was measured in a real browser against the production
      // bundle before this guard existed.
      //
      // The reload cannot be reproduced inside Karma, because the spec's own
      // document is the test runner's. What CAN be asserted is the single condition
      // that prevents it: the default action must be cancelled. If a future edit
      // replaces the handler with a plain anchor, or drops the `preventDefault`,
      // this fails while every markup assertion above still passes.
      expect(activation.defaultPrevented).withContext('skip link default action').toBeTrue();
    });
  });

  describe('main region', () => {
    it('is a native main element, which is both the landmark and the stylesheet selector', () => {
      expect(main()?.tagName.toLowerCase()).toBe('main');
    });

    it('writes no explicit role, because a native main is already the main landmark', () => {
      expect(main()?.hasAttribute('role')).toBeFalse();
    });

    it('is programmatically focusable without joining the tab order', () => {
      expect(main()?.getAttribute('tabindex')).toBe('-1');
    });
  });

  describe('secondary navigation region', () => {
    it('is empty when nothing is projected, which is what the collapse rule keys on', () => {
      expect(sidebar()?.children.length).toBe(0);
    });

    it('writes no landmark of its own, leaving that to whatever is projected', () => {
      expect(sidebar()?.tagName.toLowerCase()).toBe('div');
      expect(sidebar()?.hasAttribute('role')).toBeFalse();
      expect(sidebar()?.hasAttribute('aria-label')).toBeFalse();
    });

    it('receives projected content', () => {
      const hosted = TestBed.createComponent(ProjectingHostComponent);
      hosted.detectChanges();

      const projectedInto: HTMLElement | null = hosted.nativeElement.querySelector(
        'div.shell__sidebar',
      );
      const projected: HTMLElement | null = hosted.nativeElement.querySelector(
        'nav.projected-navigation',
      );

      expect(projected).not.toBeNull();
      expect(projectedInto?.contains(projected as Node)).toBeTrue();
    });
  });

  describe('banner forwarding', () => {
    /**
     * Returns the banner's identity affordance.
     */
    function brand(): HTMLAnchorElement | null {
      return host().querySelector('a.app-header__brand');
    }

    /**
     * Returns the banner's display-name element.
     */
    function userName(): HTMLElement | null {
      return host().querySelector('span.app-header__user');
    }

    /**
     * Returns the banner's sign-out control.
     */
    function signOutButton(): HTMLButtonElement | null {
      return host().querySelector('button.app-header__logout');
    }

    it('forwards the build-time application name when nothing is bound', () => {
      expect(brand()?.textContent?.trim()).toBe(environment.applicationName);
    });

    it('forwards a bound application name', () => {
      setInput('applicationName', 'Contoso Administration');

      expect(brand()?.textContent?.trim()).toBe('Contoso Administration');
    });

    it('renders no session cluster while no account is signed in', () => {
      expect(userName()).toBeNull();
      expect(signOutButton()).toBeNull();
    });

    it('forwards the signed-in display name', () => {
      setInput('userName', 'Grace Hopper');

      expect(userName()?.textContent?.trim()).toBe('Grace Hopper');
      expect(signOutButton()).not.toBeNull();
    });

    it('forwards a blank display name unchanged, so the banner still reads it as no session', () => {
      setInput('userName', '   ');

      expect(userName()).toBeNull();
      expect(signOutButton()).toBeNull();
    });

    it('forwards the in-flight flag, disabling the banner control', () => {
      setInput('userName', 'Grace Hopper');
      setInput('signingOut', true);

      expect(signOutButton()?.disabled).toBeTrue();
    });

    it('re-emits the sign-out gesture the banner raises', () => {
      setInput('userName', 'Grace Hopper');

      let emissions = 0;
      component.signOut.subscribe(() => {
        emissions += 1;
      });

      signOutButton()?.click();

      expect(emissions).toBe(1);
    });

    it('adds nothing of its own to the gesture, emitting no payload', () => {
      setInput('userName', 'Grace Hopper');

      const payloads: unknown[] = [];
      component.signOut.subscribe((value) => {
        payloads.push(value);
      });

      signOutButton()?.click();

      expect(payloads).toEqual([undefined]);
    });

    it('emits nothing when the banner control is activated while a sign-out is in flight', () => {
      setInput('userName', 'Grace Hopper');
      setInput('signingOut', true);

      let emissions = 0;
      component.signOut.subscribe(() => {
        emissions += 1;
      });

      signOutButton()?.click();

      expect(emissions).toBe(0);
    });
  });

  describe('footer', () => {
    it('renders the shared footer band', () => {
      expect(footer()?.querySelector('footer')).not.toBeNull();
    });

    it('renders it as the last region, after the routed content', () => {
      expect(host().lastElementChild).toBe(footer());
    });
  });
});
