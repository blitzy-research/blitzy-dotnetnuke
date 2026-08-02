/**
 * A single choice offered by a `<select>` control.
 *
 * The legacy administration screens bound their `asp:DropDownList` controls to
 * server-side lookup collections — pages within the site, administrator accounts,
 * currency codes, payment processors, installed languages, time zones. Every one
 * of those lists is data the API supplies, not something a presentational
 * component can know, so each screen accepts its lists as inputs and this type is
 * the shape they arrive in.
 *
 * Declared generically over the value because the lists are not all the same
 * kind: a page list carries identifiers (`number`), a currency list carries codes
 * (`string`). Keeping the value parameterised means a screen's form control type
 * and its option list type are checked against each other by the compiler rather
 * than agreeing by convention.
 *
 * Both members are `readonly`: an option list is a projection of server state and
 * a component must never edit one in place. A caller that needs a different list
 * supplies a different list.
 *
 * This file declares types only — no class, no function, no constant carrying
 * behaviour — so it has no paired spec. There is nothing here that can be true or
 * false at runtime; the compiler is the whole of its verification.
 */
export interface SelectOption<TValue = string> {
  /**
   * The value written into the bound form control when this option is chosen.
   *
   * Bound with `[ngValue]` rather than `[value]` wherever `TValue` is not
   * `string`. `SelectControlValueAccessor` compares and writes back `[value]` as
   * text, so a numeric identifier bound that way would reach the request payload
   * as a string and stop matching the numeric contract the API declares.
   */
  readonly value: TValue;

  /**
   * The text shown to the operator.
   *
   * Supplied already localised — the localisation mechanism the legacy screens
   * used is a Web Forms resource-provider feature that is not carried forward, so
   * wording reaches the client as data or as a literal in a template, never
   * through a translation runtime.
   */
  readonly label: string;
}
