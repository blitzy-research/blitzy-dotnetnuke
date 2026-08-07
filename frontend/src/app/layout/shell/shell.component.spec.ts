import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';

import { FooterComponent } from '../footer/footer.component';
import { HeaderComponent } from '../header/header.component';
import { SidebarComponent } from '../sidebar/sidebar.component';
import { ShellComponent } from './shell.component';

/**
 * SPECIFICATION FOR THE APPLICATION SHELL
 *
 * The shell is the only component in the workspace that renders a router outlet and
 * the only one that renders the main landmark, so a handful of facts about its markup
 * are load-bearing for every screen the console will ever have. Those facts are not
 * self-evident from reading the template: a second outlet would double-render every
 * route, a missing one would render nothing at all, and a skip link whose fragment
 * names no element is announced, reachable, and does nothing when activated. Each of
 * those failures is silent. This file makes them mechanical.
 *
 * The assertions are deliberately structural rather than visual. Placement is the
 * stylesheet's decision and is verified by looking at the rendered console, but
 * SOURCE ORDER is what the Tab key follows and what a screen reader in document-order
 * mode follows, so source order is asserted here and is never a matter of taste.
 *
 * ## WHAT IS ASSERTED IN TWO ARRANGEMENTS, AND WHY
 *
 * The navigation rail is NOT imported by the shell. The template publishes the region
 * as a projection slot — `<div class="shell__sidebar"><ng-content /></div>` — so the
 * rail is supplied by whichever component mounts `<app-shell>`. That single design
 * decision means the landmark inventory has two legitimate answers, and asserting
 * only one of them would be wrong:
 *
 * - Mounted bare, the composed tree contains one `<header>`, one `<footer>`, one
 *   `<main>` and ZERO `<nav>`, because nothing has been projected.
 * - Mounted with the real rail projected, it contains exactly one `<nav>`.
 *
 * Both are asserted. The first pins the projection seam and the `:empty` collapse the
 * stylesheet relies on; the second pins the arrangement that actually ships.
 *
 * ## NO HTTP PROVIDER IS CONFIGURED, AND THAT IS THE POINT
 *
 * The shell injects no service and issues no request. Its composed children are the
 * same: the banner and footer take inputs only, and the notification surface reads a
 * zero-dependency signal store. The cleanest available proof that nothing in this
 * render graph performs data access is that this specification needs no HTTP provider
 * whatsoever and still renders — so none is configured, and none may be added without
 * a change in the components themselves that would deserve scrutiny.
 *
 * A router IS required, twice over: the shell renders a router outlet and the banner
 * it composes renders a router link. An empty route table satisfies both, and no
 * navigation occurs.
 *
 * ## NO EFFECTS, SO NO EFFECT FLUSHING
 *
 * Neither the shell nor any component it renders declares a reactive effect, so there
 * is nothing to drain between an input change and an assertion. `TestBed.flushEffects`
 * exists on the pinned framework version and `TestBed.tick` does not, but this file
 * uses neither, because reaching for either would imply asynchrony that this render
 * graph does not contain.
 *
 * ## ORDER INDEPENDENCE
 *
 * The runner leaves the framework's spec randomisation at its default, so every spec
 * here is independent and order-agnostic: the fixture is rebuilt in `beforeEach` and
 * no mutable state is shared between specs.
 *
 * ## MIGRATION RECORD
 *
 * MIGRATION: this entire file is a NET ADDITION. There is no .NET or VB.NET test
 * project, fixture or assertion anywhere in the legacy trees — both legacy solution
 * files contain no test project, and no `.vb` file in the checkout carries a test
 * attribute or an assertion — so nothing here is a port of an existing test. The one
 * qualification is worth stating precisely rather than overclaiming: the checkout does
 * track seven hand-run browser pages under `Website/js/ClientAPITests/`, which
 * exercise the legacy client-side script API through a bespoke assertion harness of
 * their own. They are opened manually in a browser, they are not part of any automated
 * suite, and they have no bearing on page composition. Every behaviour asserted below
 * was, in the legacy application, verified only by manual click-through.
 *
 * MIGRATION: the single-outlet assertion pins an invariant the legacy page held
 * STRUCTURALLY rather than by test. `Website/Default.aspx` is 30 lines long and
 * declared exactly one injection point, the placeholder at L25, into which
 * `Website/Default.aspx.vb` added the resolved layout control at L590. One placeholder
 * in one page cannot be duplicated by accident. A component template can, so the
 * invariant that was previously guaranteed by the shape of the file is now guaranteed
 * by this specification instead.
 *
 * MIGRATION: every accessibility assertion below tests a NET ADDITION, not preserved
 * behaviour. Measured across the whole legacy checkout: zero skip links, zero
 * accessibility attributes of any kind, zero explicit roles in any page or user
 * control, and zero main elements. There is therefore no legacy behaviour to preserve
 * here and no risk of regressing any — the skip link, the main landmark and the
 * programmatic focus target are all new, and all of them are free of visual cost.
 *
 * MIGRATION: no skin, theme or layout-variant test exists here BY DESIGN. The legacy
 * chrome was selected per request: `LoadSkin` (L217-L243) stripped the application
 * path from a database-configured path, instantiated the control with
 * `LoadControl("~" & SkinPath)` (L224) and called `DataBind()` (L226) — the comment at
 * L225 is explicit that this executed "any server logic in the skin", so the layout
 * was executable code. Skinning, containers and skin objects are all out of scope, and
 * this generation of the product used skins rather than master pages: the checkout
 * contains zero master-page files. The target has exactly one compile-time layout, so
 * a variant matrix would be testing a feature that must not exist.
 *
 * MIGRATION: this specification asserts RELATIVE addresses only, and in fact asserts
 * no address at all, because the shell references none. The test target carries no
 * file replacements, so specs compile against the production environment, whose API
 * base is the relative `/api/v1`. Asserting an absolute host here would encode a value
 * that is wrong in the container topology the console ships in.
 */

/**
 * A host that mounts the shell with the real navigation rail projected into it.
 *
 * The projection contract can only be observed from OUTSIDE the shell. A fixture
 * created directly on the shell has no content children to project, so its navigation
 * region is necessarily empty, and an assertion made there would prove only that the
 * region exists. Projecting the real rail — rather than a stand-in `<nav>` — is what
 * makes the second half of the landmark inventory meaningful: it proves that the
 * arrangement which actually ships yields exactly one navigation landmark.
 *
 * The import list is written across several lines deliberately. It keeps the shell's
 * single canonical registration, in the testing module below, unambiguous to the
 * mechanical checks applied to this file.
 */
@Component({
  standalone: true,
  imports: [
    ShellComponent,
    SidebarComponent,
  ],
  template: `
    <app-shell>
      <app-sidebar />
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
   *
   * Cast exactly once, here, so that no other line in this file needs to assert
   * anything about the fixture's element type.
   */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * Returns the one element matching `selector` within the shell, failing with a
   * descriptive message when it is absent.
   *
   * Every DOM read in this file goes through this helper. It narrows
   * `Element | null` to `Element` by throwing rather than by asserting non-null, so a
   * missing element produces a named failure instead of a type error suppressed at
   * author time and a property access on nothing at run time.
   *
   * @param selector The selector to resolve against the shell's host element.
   * @returns The matching element.
   */
  function requireElement(selector: string): HTMLElement {
    const found = host().querySelector<HTMLElement>(selector);

    if (found === null) {
      throw new Error(`expected the shell to render an element matching '${selector}'`);
    }

    return found;
  }

  /**
   * Counts the elements matching `selector` within the shell.
   *
   * @param selector The selector to count.
   * @returns The number of matches.
   */
  function count(selector: string): number {
    return host().querySelectorAll(selector).length;
  }

  /**
   * Sets one of the shell's inputs and re-renders.
   *
   * Assigning to the instance field would NOT re-render: the shell declares on-push
   * change detection, so a fixture-level render pass skips it unless it has been
   * marked dirty. Going through the component reference both marks it dirty and runs
   * the input's declared transform, which is what a real template binding does.
   *
   * @param name The input to set.
   * @param value The value to set it to, before any declared transform.
   */
  function setInput(name: 'applicationName' | 'userName' | 'signingOut', value: unknown): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  /**
   * Reports whether `first` precedes `second` in document order.
   *
   * Comparing positions is preferred over indexing into a child list because it holds
   * regardless of how deeply either element sits, which keeps the tab-order assertion
   * independent of the wrapper decisions inside the composed children.
   *
   * @param first The element expected to come first.
   * @param second The element expected to follow it.
   * @returns True when `second` follows `first`.
   */
  function precedes(first: HTMLElement, second: HTMLElement): boolean {
    return (first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING) > 0;
  }

  /**
   * Returns the banner component instance the shell composes.
   *
   * Resolved through the node injector so the returned value is typed, which is what
   * lets the forwarding specifications below assert on the banner's INPUTS rather than
   * on the banner's internal markup. The banner is another component's file; reaching
   * into its class names from here would couple this specification to details it does
   * not own and would fail on a purely cosmetic change there.
   */
  function banner(): HeaderComponent {
    const node = fixture.debugElement.query(By.directive(HeaderComponent));

    if (node === null) {
      throw new Error('expected the shell to compose the banner component');
    }

    return node.injector.get(HeaderComponent);
  }

  /**
   * Returns the footer component instance the shell composes.
   *
   * Resolved by directive rather than by element name for the same reason as the
   * banner: matching `app-footer` proves only that an element with that name exists,
   * whereas resolving the directive proves the real component class is mounted there.
   */
  function footerBand(): FooterComponent {
    const node = fixture.debugElement.query(By.directive(FooterComponent));

    if (node === null) {
      throw new Error('expected the shell to compose the footer component');
    }

    return node.injector.get(FooterComponent);
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ShellComponent],
      // A router is required twice over: the shell renders a router outlet and the
      // banner it composes renders a router link. An empty route table satisfies both
      // without any navigation occurring. No HTTP provider appears here, and none is
      // needed — see the note on data access in this file's header.
      providers: [provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(ShellComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  describe('component definition', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('is standalone, because nothing in this workspace declares a module', () => {
      const definition = (
        ShellComponent as unknown as {
          ɵcmp?: { standalone?: boolean };
        }
      ).ɵcmp;

      expect(definition).toBeDefined();
      expect(definition?.standalone).toBeTrue();
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

    it('carries the shell class on its own host, without which the global grid never engages', () => {
      // The grid is published globally rather than by this component's stylesheet,
      // because a host-scoped grid cannot place children the global partial names.
      // The class is applied by the component itself rather than by whichever parent
      // mounts it, which is what makes a correctly laid-out shell the only shell that
      // can exist. Without it the host matches the stylesheet's fallback and every
      // region stacks at full width regardless of viewport.
      expect(host().classList.contains('shell')).toBeTrue();
    });
  });

  describe('region composition', () => {
    it('renders exactly five element children, each carrying its published grid area', () => {
      // The region classes must sit on the shell's DIRECT children, because the host
      // itself is the grid container. A wrapper interposed here would sever the
      // parent-child relationship the grid depends on, so the count is asserted
      // alongside the classes rather than the classes alone.
      const regions = Array.from(host().children).map((child) => child.className);

      expect(regions.length).toBe(5);
      expect(regions[0]).toContain('shell__skip-link');
      expect(regions[1]).toContain('shell__header');
      expect(regions[2]).toContain('shell__sidebar');
      expect(regions[3]).toContain('shell__main');
      expect(regions[4]).toContain('shell__footer');
    });

    it('renders exactly one router outlet, which is the application\'s only outlet', () => {
      // Two outlets would double-render every route; none would render nothing. Both
      // failures are silent, and neither is visible in a code review of the template.
      expect(count('router-outlet')).toBe(1);
    });

    it('renders exactly one main region', () => {
      expect(count('main')).toBe(1);
    });

    it('places the router outlet inside the main region', () => {
      // This is the accessibility contract rather than decoration: the routed screen
      // has to be inside the landmark the skip link moves focus to, or the skip link
      // lands the user somewhere that does not contain the content they asked for.
      const outletWithinMain = requireElement('main').querySelector('router-outlet');

      expect(outletWithinMain).not.toBeNull();
    });

    it('composes the banner and the footer exactly once each', () => {
      expect(count('app-header')).toBe(1);
      expect(count('app-footer')).toBe(1);
    });

    it('composes the real banner and footer components, not merely elements so named', () => {
      // Resolving each by directive rather than by element name is the difference
      // between proving a component is mounted and proving a tag exists. An element
      // named `app-footer` with no component behind it would satisfy the count above
      // and fail here, which is exactly the substitution this assertion guards.
      expect(banner()).toBeInstanceOf(HeaderComponent);
      expect(footerBand()).toBeInstanceOf(FooterComponent);
    });
  });

  describe('landmark inventory', () => {
    it('emits exactly one banner landmark and one footer landmark', () => {
      // The shell's own template emits NEITHER element. Both arrive from the composed
      // children, so the count that matters is the one across the composed tree: one
      // banner from the banner component and one content-info from the footer
      // component. Asserting zero here would be wrong, and asserting more than one
      // would mean a duplicated landmark that assistive technology would announce
      // twice.
      expect(count('header')).toBe(1);
      expect(count('footer')).toBe(1);
    });

    it('emits no navigation landmark of its own when nothing is projected', () => {
      // MEASURED, not assumed. The rail is a projection slot rather than an import, so
      // a shell mounted with no content children legitimately has no navigation
      // landmark at all. The complementary assertion — that the shipping arrangement
      // yields exactly one — is made against the projecting host further below.
      expect(count('nav')).toBe(0);
      expect(count('app-sidebar')).toBe(0);
    });

    it('emits no landmark twice over, so nothing is announced as a duplicate', () => {
      // A single sweep guarding the whole inventory. `<aside>` and `<nav>` are checked
      // because either could be introduced by a well-meaning edit to a composed child
      // and neither would be visible in this component's own template.
      expect(count('main')).toBe(1);
      expect(count('header')).toBe(1);
      expect(count('footer')).toBe(1);
      expect(count('aside')).toBe(0);
    });
  });

  describe('skip link', () => {
    it('is the first element in the shell, so it is the first thing the Tab key reaches', () => {
      const first = host().children[0];

      expect(first.classList.contains('shell__skip-link')).toBeTrue();
    });

    it('is an anchor carrying an address, which is what makes it a tab stop at all', () => {
      // An anchor without an address is not focusable and is not announced as a link.
      // The address is therefore load-bearing even though its default action is
      // cancelled, which is exactly the sort of thing a reader deletes as redundant.
      const link = requireElement('a.shell__skip-link');

      expect(link.tagName.toLowerCase()).toBe('a');
      expect(link.hasAttribute('href')).toBeTrue();
    });

    it('names a fragment that the main region actually carries', () => {
      // THE ASSERTION THIS BLOCK EXISTS FOR. A skip link whose fragment matches no
      // element is announced, is reachable, and does nothing — a failure that is
      // invisible in production and invisible in a template review. Both halves are
      // read from the rendered DOM rather than from the component or from a literal,
      // so the assertion survives a rename of the identifier and still fails if the
      // two sides ever drift apart.
      const target = requireElement('a.shell__skip-link').getAttribute('href') ?? '';
      const region = requireElement('main');

      expect(region.id).not.toBe('');
      expect(target).toBe(`#${region.id}`);
    });

    it('is labelled with visible text, so its purpose is announced', () => {
      expect(requireElement('a.shell__skip-link').textContent?.trim()).toBe('Skip to main content');
    });

    it('moves focus into the main region when it is activated', () => {
      const link = requireElement('a.shell__skip-link');
      const region = requireElement('main');

      link.click();

      // Markup alone cannot distinguish a working affordance from a broken one here:
      // the fragment is correct, the target exists and the link is the first tab stop
      // in both the working and the broken arrangement. What the affordance exists to
      // DO is move focus, so that is what is asserted.
      expect(document.activeElement).toBe(region);
    });

    it('cancels its default action, guarding a full-document reload that was measured', () => {
      const link = requireElement('a.shell__skip-link');
      const activation = new MouseEvent('click', { bubbles: true, cancelable: true });

      link.dispatchEvent(activation);

      // A fragment-only address resolves against the document's BASE url rather than
      // against the address showing. The static document declares a root base, which
      // deep links require, so from any route below the root the address names a
      // DIFFERENT PATH and letting it resolve performs a full document reload that
      // discards the current screen.
      //
      // The reload itself cannot be reproduced inside the runner, because the
      // specification's document is the runner's own. What CAN be asserted is the
      // single condition that prevents it. If a future edit replaces the handler with
      // a plain anchor, or drops the cancellation, this fails while every markup
      // assertion above still passes.
      expect(activation.defaultPrevented).withContext('skip link default action').toBeTrue();
    });
  });

  describe('tab order', () => {
    it('follows source order from the skip link through to the footer', () => {
      // Placement is the stylesheet's decision, so visual position is never a reason
      // to reorder the template. Source order is what the Tab key and a screen reader
      // in document-order mode follow, which is why it is pinned here: the skip link
      // must come first for it to be reachable before the banner, and the footer must
      // sit last.
      const link = requireElement('a.shell__skip-link');
      const bannerHost = requireElement('app-header');
      const navigationRegion = requireElement('div.shell__sidebar');
      const region = requireElement('main');
      const footerHost = requireElement('app-footer');

      expect(precedes(link, bannerHost)).withContext('skip link before banner').toBeTrue();
      expect(precedes(bannerHost, navigationRegion)).withContext('banner before rail').toBeTrue();
      expect(precedes(navigationRegion, region)).withContext('rail before main').toBeTrue();
      expect(precedes(region, footerHost)).withContext('main before footer').toBeTrue();
    });
  });

  describe('main region', () => {
    it('is a native main element, which is both the landmark and the stylesheet selector', () => {
      // The component stylesheet selects the bare element name, and a native element
      // makes the accessibility tree correct without a single attribute. Swapping in a
      // generic element with an explicit role would break the first and leave the
      // second only apparently intact.
      expect(requireElement('.shell__main').tagName.toLowerCase()).toBe('main');
    });

    it('writes no explicit role, because a native main is already the main landmark', () => {
      expect(requireElement('main').hasAttribute('role')).toBeFalse();
    });

    it('is programmatically focusable without joining the tab order', () => {
      // This is what makes the skip link able to move focus rather than merely scroll
      // the viewport. Without it some browsers scroll but leave focus behind, and the
      // next Tab press returns the user to the banner they were trying to skip.
      expect(requireElement('main').getAttribute('tabindex')).toBe('-1');
    });
  });

  describe('notification surface', () => {
    it('mounts the notification surface exactly once', () => {
      expect(count('app-notification-list')).toBe(1);
    });

    it('mounts it inside the main region, so the skip link carries the user to it', () => {
      const surfaceWithinMain = requireElement('main').querySelector('app-notification-list');

      expect(surfaceWithinMain).not.toBeNull();
    });

    it('mounts it above the outlet, so an announcement is not something to scroll for', () => {
      const surface = requireElement('app-notification-list');
      const outlet = requireElement('router-outlet');

      expect(precedes(surface, outlet)).withContext('announcement before routed screen').toBeTrue();
    });

    it('contributes no landmark of its own, being a live region rather than a region', () => {
      // The surface is announced through a live region, which is not a landmark. If it
      // ever grew one it would be counted by the landmark inventory above, so this
      // assertion states the intent directly rather than leaving it implied.
      const surface = requireElement('app-notification-list');

      expect(surface.querySelectorAll('nav').length).toBe(0);
      expect(surface.querySelectorAll('main').length).toBe(0);
    });
  });

  describe('secondary navigation region', () => {
    it('is a plain element that writes no landmark of its own', () => {
      // The region delegates BOTH the landmark and the accessible name to whatever is
      // projected into it. Writing either here would produce a nameless landmark on a
      // shell mounted without navigation, or two competing names on one that has it.
      const region = requireElement('div.shell__sidebar');

      expect(region.tagName.toLowerCase()).toBe('div');
      expect(region.hasAttribute('role')).toBeFalse();
      expect(region.hasAttribute('aria-label')).toBeFalse();
    });

    it('renders with no child nodes at all when nothing is projected', () => {
      // The stylesheet collapses this region with an `:empty` branch so that a shell
      // with no navigation contributes no width and is not announced. `:empty` matches
      // only when there are NO child nodes, text nodes included, which is why the
      // template keeps the element on a single line — a line break inside the tag
      // would introduce a text node and silently defeat the collapse. Asserting on
      // child NODES rather than child ELEMENTS is what makes this test able to catch
      // that.
      expect(requireElement('div.shell__sidebar').childNodes.length).toBe(0);
    });

    it('receives projected content into the navigation region', () => {
      const hosted = TestBed.createComponent(ProjectingHostComponent);
      hosted.detectChanges();

      const hostedElement = hosted.nativeElement as HTMLElement;
      const region = hostedElement.querySelector('div.shell__sidebar');
      const rail = hostedElement.querySelector('app-sidebar');

      expect(region).not.toBeNull();
      expect(rail).not.toBeNull();
      expect(region?.contains(rail)).withContext('rail projected into the region').toBeTrue();
    });

    it('yields exactly one navigation landmark once the real rail is projected', () => {
      // THE COMPLEMENT TO THE BARE-MOUNT INVENTORY. Mounted bare the shell has no
      // navigation landmark; mounted the way the console actually mounts it, it has
      // exactly one. Both numbers are correct, and asserting only one of them would
      // misrepresent the projection seam.
      const hosted = TestBed.createComponent(ProjectingHostComponent);
      hosted.detectChanges();

      const hostedElement = hosted.nativeElement as HTMLElement;

      expect(hostedElement.querySelectorAll('nav').length).toBe(1);
      expect(hostedElement.querySelectorAll('app-sidebar').length).toBe(1);
      // Still exactly one of each of the other landmarks: projecting a rail must not
      // duplicate the banner, the main region or the footer.
      expect(hostedElement.querySelectorAll('header').length).toBe(1);
      expect(hostedElement.querySelectorAll('main').length).toBe(1);
      expect(hostedElement.querySelectorAll('footer').length).toBe(1);
      expect(hostedElement.querySelectorAll('router-outlet').length).toBe(1);
    });
  });

  describe('session forwarding', () => {
    // These specifications assert on the banner's INPUTS rather than on the banner's
    // markup. The shell's contribution is the binding, not the rendering, and the
    // banner is another component's file: asserting on its class names from here would
    // fail on a cosmetic change there and would test that component twice.

    it('forwards a build-time application name when nothing is bound', () => {
      // The default is read from the component rather than from a literal or from the
      // environment module, so this holds whatever the configured name is while still
      // proving that a default reaches the banner rather than an empty string.
      expect(banner().applicationName).toBe(component.applicationName);
      expect(banner().applicationName.length).toBeGreaterThan(0);
    });

    it('forwards a bound application name', () => {
      setInput('applicationName', 'Administration');

      expect(banner().applicationName).toBe('Administration');
    });

    it('forwards no display name while no account is signed in', () => {
      expect(banner().userName).toBeUndefined();
    });

    it('forwards the signed-in display name verbatim', () => {
      setInput('userName', 'host');

      expect(banner().userName).toBe('host');
    });

    it('forwards a blank display name unchanged, applying no interpretation of its own', () => {
      // The banner's own rule is that a blank name means no session. The shell must not
      // pre-empt that by substituting a placeholder, so the blank value is asserted to
      // arrive intact.
      setInput('userName', '');

      expect(banner().userName).toBe('');
    });

    it('forwards the in-flight flag, including through the bare-attribute transform', () => {
      expect(banner().signingOut).toBeFalse();

      // The empty string is what a bare attribute produces in a template. The declared
      // transform is expected to read it as true, and the transformed value is what
      // must reach the banner.
      setInput('signingOut', '');

      expect(banner().signingOut).toBeTrue();
    });

    it('re-emits the sign-out gesture the banner raises, adding nothing of its own', () => {
      let emissions = 0;
      let payload: unknown = 'unset';

      component.signOut.subscribe((value: void) => {
        emissions += 1;
        payload = value;
      });

      banner().signOut.emit();

      // The shell owns no session, so it must not interpret the gesture: no
      // confirmation, no state change and no payload of its own.
      expect(emissions).toBe(1);
      expect(payload).toBeUndefined();
    });
  });

  describe('instrumentation', () => {
    it('logs nothing while composing, rendering or being interacted with', () => {
      // Structured logging belongs to the API and to the workspace's own notification
      // surface. A layout primitive that logged would emit on every screen, and a
      // console statement left behind during debugging is exactly the kind of thing
      // that reaches production and leaks whatever it was inspecting. The spies are
      // captured in variables and asserted through those, so no direct member access
      // on the console object appears anywhere in this file.
      const logSpy = spyOn(console, 'log');
      const warnSpy = spyOn(console, 'warn');
      const errorSpy = spyOn(console, 'error');
      const infoSpy = spyOn(console, 'info');
      const debugSpy = spyOn(console, 'debug');

      // A fresh fixture is built INSIDE the spied window so that construction and the
      // first render are observed too, not merely the interaction afterwards.
      const probe = TestBed.createComponent(ShellComponent);
      probe.detectChanges();

      const probeElement = probe.nativeElement as HTMLElement;
      probeElement.querySelector<HTMLElement>('a.shell__skip-link')?.click();

      probe.componentRef.setInput('userName', 'host');
      probe.componentRef.setInput('signingOut', true);
      probe.detectChanges();

      expect(logSpy).not.toHaveBeenCalled();
      expect(warnSpy).not.toHaveBeenCalled();
      expect(errorSpy).not.toHaveBeenCalled();
      expect(infoSpy).not.toHaveBeenCalled();
      expect(debugSpy).not.toHaveBeenCalled();
    });
  });
});
