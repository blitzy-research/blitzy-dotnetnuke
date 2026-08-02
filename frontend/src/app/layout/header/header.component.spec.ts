import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { RouterLink, provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { HeaderComponent } from './header.component';

/**
 * The component's protected surface, exposed to the specification under a
 * structural type.
 *
 * `onSignOutClick` is protected because no consumer should call it: the template is
 * its only legitimate caller. One behaviour cannot be reached through the template
 * at all, though — the guard that refuses a second emission while a sign-out is in
 * flight — because the same state that arms the guard also sets the control's
 * `disabled` attribute, and a disabled button dispatches no click. The guard is real
 * logic protecting against a direct or synthetic call, so it is exercised directly
 * here rather than left unasserted. A structural type is used in preference to
 * `as any` so the call remains type-checked.
 */
type HeaderInternals = {
  onSignOutClick(): void;
};

/**
 * A host that mounts the band through its element selector with the bare attribute
 * form of the in-flight input.
 *
 * The `booleanAttribute` transform is observable through `setInput` as well, but
 * only a real template proves that the ATTRIBUTE form — an attribute written with no
 * value at all — is understood, because that form has no equivalent in a programmatic
 * call.
 */
@Component({
  standalone: true,
  imports: [HeaderComponent],
  template: '<app-header signingOut userName="Grace Hopper" />',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class BareAttributeHostComponent {}

describe('HeaderComponent', () => {
  let fixture: ComponentFixture<HeaderComponent>;
  let component: HeaderComponent;

  /**
   * Sets one of the component's inputs and re-renders.
   *
   * Assigning to the instance field would NOT re-render: the component declares
   * on-push change detection, so a fixture-level `detectChanges` skips it unless it
   * has been marked dirty. `setInput` both marks it dirty and runs the input's
   * declared transform, which is exactly what a real template binding does.
   *
   * @param name The input to set.
   * @param value The value to set it to, before any transform.
   */
  function setInput(name: 'applicationName' | 'userName' | 'signingOut', value: unknown): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  /**
   * Returns the banner element.
   */
  function banner(): HTMLElement | null {
    return fixture.nativeElement.querySelector('header');
  }

  /**
   * Returns the identity affordance.
   */
  function brand(): HTMLAnchorElement | null {
    return fixture.nativeElement.querySelector('a.app-header__brand');
  }

  /**
   * Returns the trailing session cluster.
   */
  function actions(): HTMLElement | null {
    return fixture.nativeElement.querySelector('div.app-header__actions');
  }

  /**
   * Returns the signed-in account's display name element.
   */
  function userName(): HTMLElement | null {
    return fixture.nativeElement.querySelector('span.app-header__user');
  }

  /**
   * Returns the sign-out control.
   */
  function signOutButton(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('button.app-header__logout');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HeaderComponent],
      // The identity affordance is a router link, so a router must be present for
      // the directive to resolve. An empty route table is enough: the assertions are
      // about the rendered `href`, never about navigation actually occurring.
      providers: [provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(HeaderComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  describe('construction', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const definition = (
        HeaderComponent as unknown as {
          ɵcmp?: { onPush?: boolean };
        }
      ).ɵcmp;

      expect(definition).toBeDefined();
      expect(definition?.onPush).toBeTrue();
    });

    it('is standalone, so it can be imported directly by the shell', () => {
      const definition = (
        HeaderComponent as unknown as {
          ɵcmp?: { standalone?: boolean };
        }
      ).ɵcmp;

      expect(definition?.standalone).toBeTrue();
    });
  });

  describe('landmark structure', () => {
    it('renders a native header element, which is the banner landmark', () => {
      expect(banner()).not.toBeNull();
    });

    it('writes no explicit role, because a native header is already the banner landmark', () => {
      expect(banner()?.hasAttribute('role')).toBeFalse();
    });

    it('renders the identity affordance as the first child of the banner', () => {
      expect(banner()?.firstElementChild).toBe(brand()!);
    });

    it('renders the session cluster as the last child of the banner', () => {
      expect(banner()?.lastElementChild).toBe(actions()!);
    });
  });

  describe('identity affordance', () => {
    it('renders the build-time application name when nothing is bound', () => {
      expect(brand()?.textContent?.trim()).toBe(environment.applicationName);
    });

    it('renders a bound application name in preference to the default', () => {
      setInput('applicationName', 'Contoso Administration');

      expect(brand()?.textContent?.trim()).toBe('Contoso Administration');
    });

    it('resolves to the application root, so the affordance returns the operator home', () => {
      expect(brand()?.getAttribute('href')).toBe('/');
    });

    it('is a router link rather than a plain href, so activating it does not reload the document', () => {
      const linked = fixture.debugElement.queryAll(By.directive(RouterLink));

      // A plain `href` would take the browser out of the application and discard
      // every signal the running application holds. The presence of the directive on
      // this exact element is what makes activation a routed navigation instead.
      expect(linked.length).toBe(1);
      expect(linked[0].nativeElement).toBe(brand()!);
    });
  });

  describe('session cluster when no account is signed in', () => {
    it('renders the cluster element so the stylesheet can collapse it', () => {
      expect(actions()).not.toBeNull();
    });

    it('leaves the cluster with no element children, which is what the empty rule keys on', () => {
      expect(actions()?.children.length).toBe(0);
    });

    it('renders no display name', () => {
      expect(userName()).toBeNull();
    });

    it('renders no sign-out control, because there is no session to end', () => {
      expect(signOutButton()).toBeNull();
    });

    it('treats a whitespace-only display name as no account at all', () => {
      setInput('userName', '   ');

      expect(userName()).toBeNull();
      expect(signOutButton()).toBeNull();
      expect(actions()?.children.length).toBe(0);
    });

    it('treats an empty display name as no account at all', () => {
      setInput('userName', '');

      expect(userName()).toBeNull();
      expect(signOutButton()).toBeNull();
    });
  });

  describe('session cluster when an account is signed in', () => {
    beforeEach(() => {
      setInput('userName', 'Grace Hopper');
    });

    it('renders the display name it was given, verbatim', () => {
      expect(userName()?.textContent?.trim()).toBe('Grace Hopper');
    });

    it('renders the sign-out control', () => {
      expect(signOutButton()).not.toBeNull();
    });

    it('renders the display name ahead of the sign-out control', () => {
      const children = Array.from(actions()?.children ?? []);

      // The non-null assertions below are guarded by the count expectation: the
      // cluster renders these two elements and nothing else, so a cluster of two
      // children is a cluster containing both. Jasmine's `toBe` infers its expected
      // type from the actual, so a nullable argument would not type-check against a
      // non-nullable `Element`.
      expect(children.length).toBe(2);
      expect(children[0]).toBe(userName()!);
      expect(children[1]).toBe(signOutButton()!);
    });

    it('declares the control as a button rather than a submit, so it cannot post a form', () => {
      expect(signOutButton()?.getAttribute('type')).toBe('button');
    });

    it('leaves the control enabled while no sign-out is in flight', () => {
      expect(signOutButton()?.disabled).toBeFalse();
    });

    it('labels the control with text rather than an icon alone, so it is announced', () => {
      expect(signOutButton()?.textContent?.trim()).toBe('Sign out');
    });
  });

  describe('sign-out gesture', () => {
    beforeEach(() => {
      setInput('userName', 'Grace Hopper');
    });

    it('emits exactly once per activation', () => {
      let emissions = 0;
      component.signOut.subscribe(() => {
        emissions += 1;
      });

      signOutButton()?.click();

      expect(emissions).toBe(1);
    });

    it('emits no payload, because the session already knows whose it is', () => {
      const payloads: unknown[] = [];
      component.signOut.subscribe((value) => {
        payloads.push(value);
      });

      signOutButton()?.click();

      expect(payloads).toEqual([undefined]);
    });

    it('emits once per activation when activated repeatedly', () => {
      let emissions = 0;
      component.signOut.subscribe(() => {
        emissions += 1;
      });

      signOutButton()?.click();
      signOutButton()?.click();
      signOutButton()?.click();

      expect(emissions).toBe(3);
    });

    it('disables the control while a sign-out is in flight', () => {
      setInput('signingOut', true);

      expect(signOutButton()?.disabled).toBeTrue();
    });

    it('emits nothing when the control is activated while a sign-out is in flight', () => {
      setInput('signingOut', true);

      let emissions = 0;
      component.signOut.subscribe(() => {
        emissions += 1;
      });

      signOutButton()?.click();

      expect(emissions).toBe(0);
    });

    it('refuses a direct call while a sign-out is in flight, not merely a click', () => {
      setInput('signingOut', true);

      let emissions = 0;
      component.signOut.subscribe(() => {
        emissions += 1;
      });

      (component as unknown as HeaderInternals).onSignOutClick();

      expect(emissions).toBe(0);
    });

    it('accepts a direct call once the sign-out completes', () => {
      setInput('signingOut', true);
      setInput('signingOut', false);

      let emissions = 0;
      component.signOut.subscribe(() => {
        emissions += 1;
      });

      (component as unknown as HeaderInternals).onSignOutClick();

      expect(emissions).toBe(1);
    });
  });

  describe('in-flight input', () => {
    it('defaults to not in flight', () => {
      expect(component.signingOut).toBeFalse();
    });

    it('reads an empty string as true, through the boolean attribute transform', () => {
      // This is the programmatic equivalent of the bare attribute form: the browser
      // reports a valueless attribute as the empty string, and without the transform
      // the empty string would be falsy.
      setInput('userName', 'Grace Hopper');
      setInput('signingOut', '');

      expect(component.signingOut).toBeTrue();
      expect(signOutButton()?.disabled).toBeTrue();
    });

    it('reads the bare attribute form as true, in a real template', () => {
      const hosted = TestBed.createComponent(BareAttributeHostComponent);
      hosted.detectChanges();

      const hostedButton: HTMLButtonElement | null = hosted.nativeElement.querySelector(
        'button.app-header__logout',
      );

      expect(hostedButton).not.toBeNull();
      expect(hostedButton?.disabled).toBeTrue();
    });
  });
});
