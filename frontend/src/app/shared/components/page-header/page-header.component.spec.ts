/**
 * Specification for `PageHeaderComponent` — the page title and action bar of the
 * in-repository shared component library (AAP 0.3.2, row 2).
 *
 * PROJECT STANDARDS: `review_rules` reports that NO user-specified rules exist
 * for this project, and completeness is established by the RANGES read rather
 * than by the number of calls: the default window, `[1, -1]`, `[2, -1]` and
 * `[250, 500]` all return the same single line, and the last two begin past
 * line 1, so a document with a body would have returned different text for
 * them. No rules document and no coding-standards document is assumed, invented
 * or implied here. This file instead holds to the AAP's own normative sections,
 * which AAP 0.8.2 gives rule-force, and to the enterprise baseline of AAP
 * 0.8.3 — strict typing with no escape hatches. The precedence order applied
 * throughout is AAP 0.3.5: design-system compliance, then visual continuity,
 * then accessibility, then responsive behaviour, then code quality.
 *
 * // MIGRATION: This specification is NET-NEW and has NO PREDECESSOR TO PORT.
 * The legacy tree contains ZERO automated tests of any kind (AAP 0.5.1.5: "The
 * legacy tree contains no automated tests of any kind, so every test file is a
 * CREATE"), so nothing here is a translation of an existing check. Its
 * assertions deliberately encode three divergences from measured legacy
 * behaviour:
 *
 *   (i)   NO LANDMARK IS EMITTED. The legacy title block was a plain `<div>` —
 *         `Website/controls/sectionheadcontrol.ascx` L2 opens `<div>`, L3 holds
 *         `<asp:imagebutton id="imgIcon" ... tabIndex="-1">` and L4 holds
 *         `<asp:label id="lblTitle" runat="server" enableviewstate="False">`.
 *         The target emits no banner, main, navigation or contentinfo landmark
 *         either, because landmarks belong exclusively to the application shell
 *         under `layout/`. This is continuity of markup semantics reached by a
 *         deliberate decision rather than by accident, and it is asserted below
 *         so that it cannot regress silently.
 *
 *   (ii)  THE TITLE IS PROMOTED to a real `<h1>`. Measured baseline across the
 *         39 in-scope admin `.ascx` files: heading elements `<h1>`–`<h6>` = 0,
 *         `aria-*` attributes = 0, `role=` attributes = 0, `<header>` elements
 *         = 0. The promotion is size-neutral, so it costs no visual change.
 *
 *   (iii) TITLE AND SUBTITLE ARE PLAIN TEXT ONLY, never markup. The legacy
 *         wording source cannot be trusted as markup: of the in-scope resource
 *         values, 76 contain an HTML tag and 29 begin with a leading `<br>`,
 *         and the decisive case is a value carrying a live Google syndication
 *         `<script>` block whose source is the remote host
 *         pagead2.googlesyndication.com — invisible to a naive search because
 *         the tags are stored HTML-escaped.
 *
 * The repository-root migration notes document is owned by another agent and is
 * deliberately NOT edited from here; these divergences are reported instead.
 *
 * HARNESS NOTES (AAP 0.9.2 resolved the test-runner choice in favour of Karma
 * with Jasmine, because Gate 4's command is
 * `ng test --watch=false --browsers=ChromeHeadless --code-coverage` and the
 * rejected alternative would have made that mandated command invalid. That
 * alternative is absent from the pinned 21-package surface and is therefore not
 * importable even by accident):
 *
 *   - The component under test is standalone, so it is registered through
 *     `imports`. A declaration array is neither used nor available.
 *   - NO providers are registered, and the array is omitted entirely. The
 *     component injects nothing, holds no state, performs no I/O and reads no
 *     ambient route or configuration value, so there is nothing to provide and
 *     nothing to verify afterwards. The deprecated HTTP and router testing
 *     modules are absent for the same reason.
 *   - No effect-flushing call appears anywhere. The component holds no signal
 *     state and registers no reactive effect, so flushing would be decoration.
 *     For the record, the installed `@angular/core` 19.2.25 exposes
 *     `TestBed.flushEffects()` and exposes no `TestBed.tick()` member — checked
 *     against the installed type definitions rather than assumed.
 *   - No URL of any kind appears, so there is no absolute-address hazard.
 */

import { Component, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { PageHeaderComponent } from './page-header.component';

const TITLE_PORTALS = 'Portals';

const TITLE_EDIT_SECURITY_ROLES = 'Edit Security Roles';

const SUBTITLE_BASIC_SETTINGS =
  'In this section, you can set up the basic settings for this role.';

const SUBTITLE_LEADING_BREAK = '<br>Billing Period Must Be Greater Than Zero';

const MARKUP_PROBE = '<b>x</b>';

const HEADING_TAG_NAMES: readonly string[] = ['h1', 'h2', 'h3', 'h4', 'h5', 'h6'];

/**
 * A concrete stand-in for the `--space-4` spacing token, in pixels.
 *
 * Used only by the geometry expectation that measures the collapsed action
 * wrapper. The token itself is declared in the global stylesheet, which a
 * component-level test does not load, so a value has to be supplied locally for
 * the flex gap under measurement to resolve to anything at all. The specific
 * number is immaterial — the expectation compares two measured heights rather
 * than asserting this figure — it only has to be large enough that a residual gap
 * would be unmistakable.
 */
const MEASURED_GAP_PX = 16;

/**
 * Landmark selectors this component must NEVER emit.
 *
 * Semantic landmarks belong exclusively to `frontend/src/app/layout/**`, where
 * the application shell owns them together with the routed outlet. This
 * component always renders inside the main region, so a second banner landmark
 * would be an accessibility defect rather than an improvement, and none of the
 * nine components in this shared library may emit one.
 */
const FORBIDDEN_LANDMARK_SELECTORS: readonly string[] = [
  'header',
  'main',
  'nav',
  'footer',
  '[role="banner"]',
];

@Component({
  standalone: true,
  imports: [PageHeaderComponent],
  template: `
    <app-page-header [title]="headingText">
      <button type="button">Add New Portal</button>
      <button type="button">Export Portal Template</button>
      <button type="button">Delete Expired Portals</button>
    </app-page-header>
  `,
})
class ThreeActionHostComponent {
  readonly headingText = TITLE_PORTALS;
}

@Component({
  standalone: true,
  imports: [PageHeaderComponent],
  template: `
    <app-page-header [title]="headingText" [subtitle]="supportingText">
      <button type="button">Manage Roles for this User</button>
      <button type="button">Add New User</button>
      <button type="button">Manage User's Profile</button>
      <button type="button">Manage User's Password</button>
      <button type="button">Manage Profile Properties</button>
    </app-page-header>
  `,
})
class FiveActionHostComponent {
  readonly headingText = TITLE_EDIT_SECURITY_ROLES;
  readonly supportingText = SUBTITLE_BASIC_SETTINGS;
}

@Component({
  standalone: true,
  imports: [PageHeaderComponent],
  template: ` <app-page-header [title]="headingText"></app-page-header> `,
})
class ZeroActionHostComponent {
  readonly headingText = TITLE_PORTALS;
}

@Component({
  standalone: true,
  imports: [PageHeaderComponent],
  template: ` <app-page-header title="Portals"></app-page-header> `,
})
class StaticTitleAttributeHostComponent {}

describe('PageHeaderComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        PageHeaderComponent,
        ThreeActionHostComponent,
        FiveActionHostComponent,
        ZeroActionHostComponent,
        StaticTitleAttributeHostComponent,
      ],
    }).compileComponents();
  });

  describe('landmark prohibition', () => {
    let fixture: ComponentFixture<PageHeaderComponent>;

    beforeEach(() => {
      fixture = TestBed.createComponent(PageHeaderComponent);
    });

    it('emits no semantic landmark element of any kind', () => {
      fixture.componentRef.setInput('title', TITLE_PORTALS);
      fixture.componentRef.setInput('subtitle', SUBTITLE_BASIC_SETTINGS);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;

      for (const selector of FORBIDDEN_LANDMARK_SELECTORS) {
        expect(root.querySelector(selector)).toBeNull();
      }
    });

    it('emits no landmark even when actions are projected into the slot', () => {
      const hostFixture = TestBed.createComponent(FiveActionHostComponent);
      hostFixture.detectChanges();

      const root: HTMLElement = hostFixture.nativeElement;

      for (const selector of FORBIDDEN_LANDMARK_SELECTORS) {
        expect(root.querySelector(selector)).toBeNull();
      }
    });

    it('carries no landmark role on its own host element', () => {
      fixture.componentRef.setInput('title', TITLE_PORTALS);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;

      expect(root.getAttribute('role')).toBeNull();
    });
  });

  describe('title rendering', () => {
    let fixture: ComponentFixture<PageHeaderComponent>;

    beforeEach(() => {
      fixture = TestBed.createComponent(PageHeaderComponent);
    });

    // Every direct-fixture input change in this file goes through
    // `componentRef.setInput`, never a property assignment: assigning a property
    // does not mark an OnPush component for check, so the rendered DOM can stay
    // stale and an assertion would silently pass against the previous render.
    it('renders the title inside a real h1 element', () => {
      fixture.componentRef.setInput('title', TITLE_PORTALS);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;
      const heading = root.querySelector('h1');

      expect(heading).not.toBeNull();
      if (heading === null) {
        return;
      }

      expect((heading.textContent ?? '').trim()).toBe(TITLE_PORTALS);
    });

    it('renders exactly one h1, so the title is a heading and not a styled div', () => {
      fixture.componentRef.setInput('title', TITLE_PORTALS);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;

      expect(root.querySelectorAll('h1').length).toBe(1);
    });

    it('is mode-agnostic and renders whatever pre-resolved title it is given', () => {
      fixture.componentRef.setInput('title', TITLE_EDIT_SECURITY_ROLES);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;
      const heading = root.querySelector('h1');

      expect(heading).not.toBeNull();
      if (heading === null) {
        return;
      }

      expect((heading.textContent ?? '').trim()).toBe(TITLE_EDIT_SECURITY_ROLES);
    });
  });

  describe('subtitle rendering', () => {
    let fixture: ComponentFixture<PageHeaderComponent>;

    beforeEach(() => {
      fixture = TestBed.createComponent(PageHeaderComponent);
    });

    it('renders nothing at all in place of an absent subtitle', () => {
      fixture.componentRef.setInput('title', TITLE_PORTALS);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;

      expect(root.querySelector('p')).toBeNull();
      expect(fixture.componentInstance.subtitle).toBeUndefined();
    });

    it('renders a supplied subtitle as supporting text', () => {
      fixture.componentRef.setInput('title', TITLE_EDIT_SECURITY_ROLES);
      fixture.componentRef.setInput('subtitle', SUBTITLE_BASIC_SETTINGS);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;
      const supporting = root.querySelector('p');

      expect(supporting).not.toBeNull();
      if (supporting === null) {
        return;
      }

      expect((supporting.textContent ?? '').trim()).toBe(SUBTITLE_BASIC_SETTINGS);
    });

    it('never places the subtitle in the document outline', () => {
      fixture.componentRef.setInput('title', TITLE_EDIT_SECURITY_ROLES);
      fixture.componentRef.setInput('subtitle', SUBTITLE_BASIC_SETTINGS);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;

      expect(root.querySelector('h2')).toBeNull();

      const supporting = root.querySelector('p');
      expect(supporting).not.toBeNull();
      if (supporting === null) {
        return;
      }

      expect(HEADING_TAG_NAMES.includes(supporting.tagName.toLowerCase())).toBe(false);
    });
  });

  describe('projected actions', () => {
    function actionLabelsOf(root: HTMLElement): readonly string[] {
      return Array.from(root.querySelectorAll('button')).map((action) =>
        (action.textContent ?? '').trim(),
      );
    }

    it('renders all three actions of the measured portal-listing screen', () => {
      const hostFixture = TestBed.createComponent(ThreeActionHostComponent);
      hostFixture.detectChanges();

      const root: HTMLElement = hostFixture.nativeElement;

      expect(actionLabelsOf(root)).toEqual([
        'Add New Portal',
        'Export Portal Template',
        'Delete Expired Portals',
      ]);
    });

    it('renders all five actions of the measured maximum screen', () => {
      const hostFixture = TestBed.createComponent(FiveActionHostComponent);
      hostFixture.detectChanges();

      const root: HTMLElement = hostFixture.nativeElement;

      expect(actionLabelsOf(root)).toEqual([
        'Manage Roles for this User',
        'Add New User',
        "Manage User's Profile",
        "Manage User's Password",
        'Manage Profile Properties',
      ]);
    });

    it('generates no box at all in the action area when no action is projected', () => {
      const hostFixture = TestBed.createComponent(ZeroActionHostComponent);
      hostFixture.detectChanges();

      const root: HTMLElement = hostFixture.nativeElement;

      expect(actionLabelsOf(root)).toEqual([]);

      // Collapse is asserted as COMPUTED GEOMETRY, not merely as DOM sanity. An
      // earlier revision of this spec asserted only the latter, on the stated
      // reasoning that an actions wrapper carrying no padding, border or
      // background occupies no space — which is true of the wrapper's own box and
      // does not follow for the header, because the space-4 gap belongs to the
      // flex CONTAINER and is generated between adjacent items regardless of
      // their size. A zero-sized item is still an item, so the empty wrapper was
      // emitting a full gap after the title on every one of the 29 measured
      // screens that carry no action. Nothing short of a resolved `display`
      // detects that, which is why it is checked here directly.
      const actions = root.querySelector('.page-header__actions');
      expect(actions).not.toBeNull();
      if (actions === null) {
        return;
      }

      // The wrapper is ALWAYS emitted, so its emptiness is the precondition the
      // stylesheet rule keys on. Asserting an absence of buttons alone would pass
      // with the full gap still intact, which is exactly the false confidence
      // being removed here.
      expect(actions.matches(':empty')).toBeTrue();

      // The mechanism: the stylesheet suppresses the empty wrapper outright, so
      // it leaves the flex formatting context and contributes no gap. Asserted as
      // "generates no box" rather than as a measured dimension, because the box
      // count is what determines whether the parent counts it as an item — and
      // because `display: none` is a literal keyword in the rule rather than a
      // design token, this assertion is independent of the global token
      // stylesheet, which is not this component's contract to uphold.
      expect(actions.getClientRects().length).toBe(0);
      expect(getComputedStyle(actions).display).toBe('none');

      const heading = root.querySelector('h1');
      expect(heading).not.toBeNull();
      if (heading === null) {
        return;
      }

      expect((heading.textContent ?? '').trim()).toBe(TITLE_PORTALS);
    });

    it('keeps the action area laid out whenever any action is projected', () => {
      // POSITIVE CONTROL for the suppression above, and the reason it is not
      // vacuous. Without this, the previous test would pass just as happily
      // against a stylesheet that suppressed the wrapper UNCONDITIONALLY — which
      // would hide every action on every screen while looking, in that one test,
      // like a clean collapse.
      // Typed as the framework's own component type rather than inferred: the
      // two hosts declare different string-literal title types, so an inferred
      // union is not assignable to a single component type. Widening here is
      // honest — the loop needs nothing from either instance.
      const actionCarryingHosts: readonly Type<unknown>[] = [
        ThreeActionHostComponent,
        FiveActionHostComponent,
      ];

      for (const host of actionCarryingHosts) {
        const hostFixture = TestBed.createComponent(host);
        hostFixture.detectChanges();

        const root: HTMLElement = hostFixture.nativeElement;
        const actions = root.querySelector('.page-header__actions');

        expect(actions).not.toBeNull();
        if (actions === null) {
          return;
        }

        expect(actions.matches(':empty'))
          .withContext(`${host.name} projects actions, so the wrapper is not empty`)
          .toBeFalse();
        expect(getComputedStyle(actions).display)
          .withContext(`${host.name} must keep the wrapper laid out as a flex row`)
          .toBe('flex');
        expect(actions.getClientRects().length).toBeGreaterThan(0);

        hostFixture.destroy();
      }
    });

    it('removes the action wrapper from layout when no action is projected', () => {
      // The regression this guards against is specific and was measured, not
      // imagined: the wrapper is a flex ITEM of `.page-header`, and a flex
      // container allocates its `gap` between adjacent items whether or not
      // either item holds any content. An empty-but-present wrapper therefore
      // still pushed a full `--space-4` of dead space beneath the title on the 29
      // in-scope screens that project no action.
      //
      // Sizing to zero cannot fix that, because the gap is allocated for the
      // item's existence rather than for its size. Only removing the box from
      // layout removes the gap, which is what the stylesheet's `:empty` rule
      // does — and computed geometry is the only honest way to assert it.
      const hostFixture = TestBed.createComponent(ZeroActionHostComponent);
      hostFixture.detectChanges();

      const root: HTMLElement = hostFixture.nativeElement;
      const actions = root.querySelector<HTMLElement>('.page-header__actions');

      expect(actions).not.toBeNull();
      if (actions === null) {
        return;
      }

      expect(window.getComputedStyle(actions).display)
        .withContext('an action wrapper with nothing projected must not occupy a flex slot')
        .toBe('none');
    });

    it('keeps the action wrapper in layout when an action is projected', () => {
      // The other half of the contract. A collapse rule that also hid populated
      // wrappers would be a far worse defect than the gap it set out to remove,
      // so the positive case is asserted alongside the negative one.
      const hostFixture = TestBed.createComponent(ThreeActionHostComponent);
      hostFixture.detectChanges();

      const root: HTMLElement = hostFixture.nativeElement;
      const actions = root.querySelector<HTMLElement>('.page-header__actions');

      expect(actions).not.toBeNull();
      if (actions === null) {
        return;
      }

      expect(window.getComputedStyle(actions).display)
        .withContext('a populated action wrapper must still lay its actions out as a flex row')
        .toBe('flex');
    });

    it('leaves no residual gap beneath the title when no action is projected', () => {
      // This is the measured symptom the collapse rule exists to remove, asserted
      // as real geometry: an empty-but-present wrapper made the header one whole
      // spacing step taller than the content it actually rendered.
      //
      // The spacing token is supplied on the host here rather than relied upon.
      // The component's gap is declared as `gap: var(--space-4)`, and the custom
      // property that resolves it lives in the GLOBAL token stylesheet, which a
      // component-level test does not load. Without the token the gap would
      // compute to `normal` — that is, to zero — and this expectation would pass
      // whether or not the wrapper occupied a slot, which would make it decorative
      // rather than load-bearing. Declaring the token locally reproduces the
      // runtime cascade for the one property under measurement, and nothing else.
      const hostFixture = TestBed.createComponent(ZeroActionHostComponent);
      hostFixture.detectChanges();

      const root: HTMLElement = hostFixture.nativeElement;
      const header = root.querySelector<HTMLElement>('.page-header');
      const heading = root.querySelector<HTMLElement>('h1');

      expect(header).not.toBeNull();
      expect(heading).not.toBeNull();
      if (header === null || heading === null) {
        return;
      }

      header.style.setProperty('--space-4', `${MEASURED_GAP_PX}px`);
      header.style.setProperty('margin-block-end', '0');

      // Compared against the rendered title rather than against an absolute pixel
      // figure, so the expectation survives a change to the token's value while
      // still failing the moment the wrapper starts consuming a flex slot again.
      expect(header.getBoundingClientRect().height)
        .withContext('a collapsed action wrapper must add no height to the header')
        .toBeCloseTo(heading.getBoundingClientRect().height, 0);
    });

    it('projects every action through a single unnamed content slot', () => {
      const hostFixture = TestBed.createComponent(FiveActionHostComponent);
      hostFixture.detectChanges();

      const root: HTMLElement = hostFixture.nativeElement;
      const actions = Array.from(root.querySelectorAll('button'));

      expect(actions.length).toBe(5);

      const firstAction = root.querySelector('button');
      expect(firstAction).not.toBeNull();
      if (firstAction === null) {
        return;
      }

      const slotWrapper = firstAction.parentElement;
      expect(slotWrapper).not.toBeNull();
      if (slotWrapper === null) {
        return;
      }

      for (const action of actions) {
        expect(action.parentElement).toBe(slotWrapper);
      }

      expect(HEADING_TAG_NAMES.includes(slotWrapper.tagName.toLowerCase())).toBe(false);
      expect(slotWrapper.tagName.toLowerCase()).not.toBe('p');
    });
  });

  describe('plain-text rendering', () => {
    let fixture: ComponentFixture<PageHeaderComponent>;

    beforeEach(() => {
      fixture = TestBed.createComponent(PageHeaderComponent);
    });

    // Wording carried over from the legacy portal can arrive already carrying
    // markup, up to and including an HTML-escaped remote script element, so these
    // two inputs must never reach a raw-markup sink or a sanitiser bypass.
    it('renders a markup-bearing title as literal text and creates no element', () => {
      fixture.componentRef.setInput('title', MARKUP_PROBE);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;

      expect(root.querySelector('h1 b')).toBeNull();

      const heading = root.querySelector('h1');
      expect(heading).not.toBeNull();
      if (heading === null) {
        return;
      }

      expect(heading.textContent ?? '').toContain(MARKUP_PROBE);
    });

    it('renders a markup-bearing subtitle as literal text and creates no element', () => {
      fixture.componentRef.setInput('title', TITLE_PORTALS);
      fixture.componentRef.setInput('subtitle', MARKUP_PROBE);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;

      expect(root.querySelector('p b')).toBeNull();

      const supporting = root.querySelector('p');
      expect(supporting).not.toBeNull();
      if (supporting === null) {
        return;
      }

      expect(supporting.textContent ?? '').toContain(MARKUP_PROBE);
    });

    it('renders a real shipped leading-break value literally', () => {
      fixture.componentRef.setInput('title', TITLE_EDIT_SECURITY_ROLES);
      fixture.componentRef.setInput('subtitle', SUBTITLE_LEADING_BREAK);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;

      expect(root.querySelector('p br')).toBeNull();

      const supporting = root.querySelector('p');
      expect(supporting).not.toBeNull();
      if (supporting === null) {
        return;
      }

      expect((supporting.textContent ?? '').trim()).toBe(SUBTITLE_LEADING_BREAK);
    });
  });

  describe('component contract', () => {
    let fixture: ComponentFixture<PageHeaderComponent>;

    beforeEach(() => {
      fixture = TestBed.createComponent(PageHeaderComponent);
    });

    // REPLACES a bare fixture-construction truthiness assertion. That assertion
    // proved only that the harness could instantiate the class — a fact every
    // other test in this file already depends on, and one that cannot fail on its
    // own without failing everything else first. The two tests below assert the
    // closed contracts the paired template and stylesheet state verbatim and
    // that nothing else here covers: the exact class vocabulary, and the promise
    // that this template contributes no styling of its own.
    it('emits only the five class names its stylesheet declares, and no modifier', () => {
      fixture.componentRef.setInput('title', TITLE_PORTALS);
      fixture.componentRef.setInput('subtitle', SUBTITLE_BASIC_SETTINGS);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;
      const rendered = new Set<string>();
      root.querySelectorAll('[class]').forEach((element: Element): void => {
        element.classList.forEach((token: string): void => {
          rendered.add(token);
        });
      });

      // The class-name contract is stated verbatim and identically in both the
      // template header and the stylesheet header: the block plus exactly four
      // elements, "nothing else is emitted and no modifier class exists, because
      // the component declares no variants". Restated here as an independent
      // literal set so that adding a sixth class — or a `--modifier` the
      // stylesheet has no rule for — fails rather than passing unnoticed.
      expect(Array.from(rendered).sort()).toEqual([
        'page-header',
        'page-header__actions',
        'page-header__subtitle',
        'page-header__text',
        'page-header__title',
      ]);

      // A modifier would mean a variant, and this component declares none.
      for (const token of rendered) {
        expect(token.includes('--'))
          .withContext(`"${token}" is a modifier, and this component declares no variants`)
          .toBeFalse();
      }
    });

    it('contributes no styling of its own and no presentational attribute', () => {
      fixture.componentRef.setInput('title', TITLE_PORTALS);
      fixture.componentRef.setInput('subtitle', SUBTITLE_BASIC_SETTINGS);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;

      // Every value the selectors resolve to must be a design token, which is
      // only enforceable if the markup cannot smuggle a literal past the
      // stylesheet. An inline style attribute would do exactly that, and an
      // identifier would give a consumer a scope-piercing hook that bypasses the
      // component's own encapsulation.
      expect(root.querySelectorAll('[style]').length).toBe(0);
      expect(root.querySelectorAll('[id]').length).toBe(0);

      // The legacy admin markup carried its layout in table attributes of exactly
      // this kind — measured across the in-scope screens — so their absence is a
      // migration guarantee rather than a stylistic preference.
      for (const attribute of [
        'align',
        'valign',
        'bgcolor',
        'width',
        'height',
        'border',
        'cellpadding',
        'cellspacing',
        'nowrap',
      ]) {
        expect(root.querySelectorAll(`[${attribute}]`).length)
          .withContext(`presentational attribute "${attribute}" must not survive the migration`)
          .toBe(0);
      }

      // And no table element either: the title block is a flex layout, not the
      // legacy nested-table arrangement it replaces.
      expect(root.querySelectorAll('table').length).toBe(0);
    });

    it('renders on the app-page-header host tag at a consumer call site', () => {
      const hostFixture = TestBed.createComponent(ThreeActionHostComponent);
      hostFixture.detectChanges();

      const root: HTMLElement = hostFixture.nativeElement;
      const element = root.querySelector('app-page-header');

      expect(element).not.toBeNull();
      if (element === null) {
        return;
      }

      expect(element.tagName.toLowerCase()).toBe('app-page-header');
    });

    it('exposes title and subtitle as public inputs', () => {
      fixture.componentRef.setInput('title', TITLE_PORTALS);
      fixture.detectChanges();

      // Reading both members from a spec is itself part of the proof that they
      // are public. The stronger, compile-time half of that proof is the host
      // component above, which binds `[title]` AND `[subtitle]` from a template:
      // `strictInputAccessModifiers` is enabled, so a `private` or `protected`
      // input would break this file's own compilation.
      //
      // `title` is asserted here as a SUPPLIED value rather than as a default.
      // An earlier revision of this spec asserted that it defaulted to the empty
      // string, which documented the very defect the component now forbids: the
      // heading is emitted unconditionally, so an empty default meant an unnamed
      // <h1> was a legal, silent state. There is no default to assert now.
      // `subtitle` genuinely is optional — it renders nothing when absent — so
      // its undefined default is still the correct expectation.
      expect(fixture.componentInstance.title).toBe(TITLE_PORTALS);
      expect(fixture.componentInstance.subtitle).toBeUndefined();
    });

    it('normalises surrounding white space out of a supplied title', () => {
      // Normalisation, not validation: HTML collapses leading and trailing white
      // space, so a padded title always rendered identically. Trimming makes a
      // whitespace-only title indistinguishable from an empty one so that both
      // hit the rejection below — without it, `' '` would satisfy a length
      // check and still produce an unnamed heading.
      fixture.componentRef.setInput('title', `  ${TITLE_PORTALS}\n`);
      fixture.detectChanges();

      expect(fixture.componentInstance.title).toBe(TITLE_PORTALS);
    });

    it('rejects a blank title rather than rendering an unnamed heading', () => {
      // The component emits the page's single <h1> unconditionally, so a blank
      // title yields a heading with no accessible name — announced as a heading
      // and then silent, and an unlabelled top-level entry in the document
      // outline. Omission is already a compile-time error because the input is
      // required; this covers the case the compiler cannot see, a bound value
      // that is present but empty at runtime.
      for (const blank of ['', ' ', '\t', '\n', '   \n  ']) {
        expect(() =>
          fixture.componentRef.setInput('title', blank),
        ).toThrowError(/must be a non-blank string/);
      }
    });

    it('strips the colliding global title attribute from its host element', () => {
      fixture.componentRef.setInput('title', TITLE_PORTALS);
      fixture.detectChanges();

      const root: HTMLElement = fixture.nativeElement;

      expect(root.getAttribute('title')).toBeNull();
    });

    it('strips the title attribute at a static-attribute consumer call site', () => {
      const hostFixture = TestBed.createComponent(StaticTitleAttributeHostComponent);
      hostFixture.detectChanges();

      const root: HTMLElement = hostFixture.nativeElement;
      const element = root.querySelector('app-page-header');

      expect(element).not.toBeNull();
      if (element === null) {
        return;
      }

      expect(element.getAttribute('title')).toBeNull();

      const heading = element.querySelector('h1');
      expect(heading).not.toBeNull();
      if (heading === null) {
        return;
      }

      expect((heading.textContent ?? '').trim()).toBe(TITLE_PORTALS);
    });
  });
});
