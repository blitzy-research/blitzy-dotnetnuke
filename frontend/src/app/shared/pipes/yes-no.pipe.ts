import { Pipe, PipeTransform } from '@angular/core';

/**
 * Display text for the affirmative state.
 *
 * Sourced, not invented. `Website/App_GlobalResources/SharedResources.resx`
 * L126-L128 declares `<data name="Yes.Text">` with the value `Yes`. The legacy
 * wording authority is the resource value, never a markup attribute, because
 * DotNetNuke assigned control text from resources at run time and several
 * markup attributes in the legacy tree contradict their own resource entry.
 */
const AFFIRMATIVE_TEXT = 'Yes';

/**
 * Display text for the negative state.
 *
 * Sourced, not invented. `Website/App_GlobalResources/SharedResources.resx`
 * L129-L131 declares `<data name="No.Text">` with the value `No`.
 */
const NEGATIVE_TEXT = 'No';

// MIGRATION: localisation is not ported. The legacy labels were resolved from
// resource files at run time; no translation runtime exists in the target, so
// the two display strings above are authored directly in this file and the
// resource entries they cite serve only as the wording authority.

// MIGRATION: the `~/images/checked.gif` and `~/images/unchecked.gif` glyph pair
// is reduced to text, in both states. Neither legacy asset is carried into the
// SPA, whose only static image is the application icon. The legacy
// `<asp:Image>` tags at users.ascx L76-L77 and roles.ascx L68-L69 and L74-L75
// also declared no AlternateText, so a screen reader announced nothing at all;
// rendering the state as text is therefore a net accessibility gain. A consumer
// that wants a glyph supplies its own inline SVG or CSS, because this pipe emits
// neither markup nor an asset reference.

/**
 * Renders a boolean flag as human-readable affirmative or negative display
 * text: `Yes` for `true` and `No` for `false`.
 *
 * Centralises the boolean display formatting that the legacy Web Forms grids
 * expressed inline, as a pair of mutually exclusive images. That ancestry is a
 * closed set of six cells in exactly two files, and the pipe covers all of it:
 *
 * - user list, the `approved` flag — `Website/admin/Users/users.ascx` L76-L77,
 *   column header `Authorized`.
 * - role list, the `isPublic` flag — `Website/admin/Security/roles.ascx`
 *   L68-L69, column header `Public`.
 * - role list, the `autoAssignment` flag — `Website/admin/Security/roles.ascx`
 *   L74-L75, column header `Auto`.
 *
 * The profile-definition list reuses the pipe for read-only rendering of its
 * two boolean flag columns. Its legacy cells were editable checkbox columns
 * with postback, which is the shared data table's cell-template concern; this
 * pipe is display-only and deliberately exposes no editing, no two-way binding
 * and no output.
 *
 * The input is a non-nullable `boolean` by design, and that is measured rather
 * than stylistic. In the legacy model the "absent" marker for a Boolean was
 * literally `False`: `Library/Components/Shared/Null.vb` L76-L80 returns
 * `False` from `NullBoolean`, and its `IsNull` helper at L227-L228 reports a
 * `False` field as null. A nullable input would therefore be indistinguishable
 * from a legitimate `false`. Every flag this pipe formats is declared
 * `As Boolean` at source over a plain backing field with no coercion —
 * `UserMembership.Approved` L83, `RoleInfo.IsPublic` L248,
 * `RoleInfo.AutoAssignment` L263 — and the API always serialises booleans, so
 * the value is always present. Accordingly the transform branches on an
 * identity comparison and never tests truthiness.
 *
 * Both branches yield non-empty text. The legacy grids showed a visible glyph
 * in either state and never a blank cell, so emitting an empty string for
 * `false` would change observable behaviour.
 *
 * The return value is plain text. It is interpolated and escaped by Angular,
 * carries no markup, and must never be bound as raw HTML.
 *
 * @example
 * ```html
 * <td>{{ user.approved | yesNo }}</td>
 * <td>{{ role.isPublic | yesNo }}</td>
 * ```
 */
@Pipe({
  // Camel-case pipe name. The workspace selector prefix `app` configured in
  // angular.json governs component and directive selectors only, not pipes.
  name: 'yesNo',
  // Standalone: consumers import this class directly into their own `imports`
  // array, so the pipe never needs a declaring module.
  standalone: true,
  // `pure` is deliberately left unset, which keeps Angular's default of true.
  // The input is a primitive, so an impure pipe would re-evaluate on every
  // change-detection cycle and undermine the OnPush strategy its consumers use.
})
export class YesNoPipe implements PipeTransform {
  /**
   * Maps a boolean flag onto its display text.
   *
   * @param value The flag to render. `false` is meaningful data, not an absent
   * value, and is rendered as the negative text rather than as empty output.
   * @returns `Yes` when the flag is `true`, otherwise `No`. Never empty.
   */
  transform(value: boolean): string {
    // MIGRATION: the legacy string-versus-boolean coercion is now explicit.
    // The two legacy grids compared the same kind of flag against different
    // literal types. `Website/admin/Users/users.ascx` L76-L77 casts the bound
    // item with `CType(Container.DataItem, ...UserInfo)`, so
    // `Membership.Approved` is an early-bound Boolean compared against the
    // Boolean literals true and false. `Website/admin/Security/roles.ascx`
    // L68-L69 and L74-L75 read their flags through untyped
    // `DataBinder.Eval(...)`, which yields Object, and compare that against the
    // string literals "true" and "false". Both grids bind an ArrayList
    // (Roles.ascx.vb L70 and L77, Users.ascx.vb L52 and L282), so the
    // divergence lives in the markup rather than in the data source. Both pages
    // compiled with Option Strict off (Website/release.config L125,
    // Website/development.config L123) while the class library compiled Option
    // Strict on under Option Compare Binary
    // (Library/DotNetNuke.Library.vbproj L22-L24). Under Option Compare Binary
    // a string comparison of "True" against "true" is False, because VB renders
    // a Boolean with a capital T. The roles.ascx expressions worked only
    // because VB resolved that late-bound Object-against-String comparison by
    // converting the string to Boolean rather than the Boolean to a string. Had
    // precedence gone the other way, both glyphs would have been hidden and the
    // Public and Auto columns would have rendered blank. The legacy result was
    // correct by accident of late-binding rules, so the conversion is pinned
    // here instead: the parameter is a real boolean and the test below is an
    // identity comparison. No string form of the flag is accepted or produced.
    if (value === true) {
      return AFFIRMATIVE_TEXT;
    }

    // `false` reaches here as data, never as a stand-in for a missing value, so
    // it renders the negative text rather than empty output.
    return NEGATIVE_TEXT;
  }
}
