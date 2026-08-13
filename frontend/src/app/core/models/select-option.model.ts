export interface SelectOption<TValue = string> {
  /** The value written into the bound form control when this option is chosen. */
  readonly value: TValue;

  /**
   * The text shown to the operator. Supplied already localised — the localisation mechanism the legacy
   * screens used is a Web Forms resource-provider feature that is not carried forward, so wording reaches
   * the client as data or as a literal in a template, never through a translation runtime.
   */
  readonly label: string;
}
