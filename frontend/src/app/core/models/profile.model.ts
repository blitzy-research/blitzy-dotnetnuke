/**
 * The profile wire contract.
 *
 * Every declaration in this file mirrors, member for member, a data-transfer object
 * the API already serialises, so that a change on either side of the boundary shows
 * up as a compilation failure rather than as an `undefined` at run time. The
 * backend types are `ProfilePropertyDefinitionDto`, `UserProfileValueDto` and
 * `UserProfileDto` in `DnnMigration.Application.Dtos.User`.
 *
 * NAMING
 * ------
 * Member names are camel-cased because that is what the API emits: its JSON options
 * take the framework's web defaults, which apply the camel-case naming policy. The
 * backend property `PropertyDefinitionId` therefore arrives as `propertyDefinitionId`
 * and is spelled that way here.
 *
 * TYPES ONLY, DELIBERATELY
 * ------------------------
 * This file declares no class, no constant and no function — nothing that executes.
 * That is why it has no paired specification: there is no behaviour to assert, and a
 * specification that constructed an object literal and read its members back would
 * be testing the TypeScript compiler rather than this application. The type checks
 * that matter are performed by `tsc` over every consumer.
 *
 * MIGRATION: the legacy profile surface was split across two namespaces —
 * `DotNetNuke.Entities.Users` owned `UserProfile.vb` while
 * `DotNetNuke.Entities.Profile` owned `ProfilePropertyDefinition.vb` — and the two
 * are consolidated here, matching the consolidation already performed on the
 * backend.
 */

/**
 * How widely a profile value may be seen.
 *
 * The backend carries this as a plain integer rather than an enumeration, so it is
 * modelled as a number here rather than as a string union: inventing a client-side
 * enumeration would imply the API validates against a closed set that it does not.
 * The three values the legacy administration screens offered are published as named
 * constants below so that a template never spells a bare number.
 */
export type ProfileVisibilityCode = number;

/**
 * The visibility codes the legacy profile editor offered, in the order it offered
 * them.
 *
 * MIGRATION: the legacy values come from the membership settings' default profile
 * visibility, whose shipped default is 2 — the most private of the three. That
 * default is preserved by the backend and is therefore not restated here.
 */
export const PROFILE_VISIBILITY = {
  /** Visible to anyone who can view the profile, including anonymous visitors. */
  allUsers: 0,
  /** Visible to members of the tenant. */
  membersOnly: 1,
  /** Visible only to the account itself and to administrators. */
  adminOnly: 2,
} as const;

/**
 * A tenant's declaration of one profile property: what it is called, how it is
 * grouped, how long it may be, and whether it must be supplied.
 */
export interface ProfilePropertyDefinition {
  /** The declaration's identifier. */
  readonly propertyDefinitionId: number;

  /** The tenant the declaration belongs to. */
  readonly portalId: number;

  /** The module definition the declaration is scoped to, when it is scoped at all. */
  readonly moduleDefId: number | null;

  /**
   * The declared data type, as a plain integer.
   *
   * The backend does not narrow this to an enumeration, so neither does this
   * contract. The screens that render a control for a property treat every type as
   * text, which is what the legacy editor did for every type it had no specialised
   * control for.
   */
  readonly dataType: number;

  /** The value applied when the account has recorded none. */
  readonly defaultValue: string | null;

  /** The grouping the property is displayed under. */
  readonly propertyCategory: string;

  /** The property's name, which is also its label. */
  readonly propertyName: string;

  /**
   * The greatest number of characters a value may hold, or zero for no declared
   * bound.
   */
  readonly length: number;

  /** Whether a value must be supplied. */
  readonly required: boolean;

  /**
   * A regular expression a value must match, when one is declared.
   *
   * Applied as a client-side pattern in addition to the server's check, never
   * instead of it.
   */
  readonly validationExpression: string | null;

  /** Where the property sits among its siblings. */
  readonly viewOrder: number;

  /** Whether the property is shown at all. */
  readonly visible: boolean;

  /** The visibility applied when the account has recorded none. */
  readonly visibility: ProfileVisibilityCode;
}

/**
 * One profile value, together with the declaration that describes it.
 *
 * The declaration is embedded rather than referenced by identifier because the API
 * projects it that way: a screen can render the whole profile from one response
 * without a second request and without a lookup table of its own.
 */
export interface UserProfileValue {
  /** The declaration this value was recorded against. */
  readonly propertyDefinitionId: number;

  /** The recorded value, which is the empty string when nothing was recorded. */
  readonly propertyValue: string;

  /** How widely this particular value may be seen. */
  readonly visibility: ProfileVisibilityCode;

  /** When the value was last written, or `null` when it never has been. */
  readonly lastUpdatedDate: string | null;

  /** The declaration describing this value. */
  readonly definition: ProfilePropertyDefinition;
}

/**
 * An account's whole profile: one entry per property the tenant declares, whether or
 * not the account has recorded a value for it.
 */
export interface UserProfile {
  /** The account the profile belongs to. */
  readonly userId: number;

  /** One entry per declared property, in the tenant's declared display order. */
  readonly properties: readonly UserProfileValue[];
}

/**
 * One value in a profile submission.
 */
export interface UserProfileValueSubmission {
  /** The declaration being written. */
  readonly propertyDefinitionId: number;

  /** The value to write. */
  readonly propertyValue: string;

  /** The visibility to write. */
  readonly visibility: ProfileVisibilityCode;
}

/**
 * A whole profile submission.
 *
 * The API replaces rather than merges — a write carries every value, and a property
 * omitted from the payload is a property cleared — so this contract carries the
 * complete set rather than a difference. Modelling it as a difference would invite a
 * caller to send one changed field and silently erase the rest.
 */
export interface UserProfileSubmission {
  /** The account being written. */
  readonly userId: number;

  /** Every declared property's value and visibility. */
  readonly values: readonly UserProfileValueSubmission[];
}

/**
 * A declaration submission, used for both creating and replacing a declaration.
 *
 * `propertyDefinitionId` is absent when creating; the screen that raises the
 * submission reports which of the two it means through a separate event rather than
 * by the presence of an identifier, so that an accidental omission cannot turn an
 * edit into a create.
 *
 * MIGRATION: the API binds TWO distinct contracts behind the two verbs, because the
 * terminal stored procedures do not honour the same member set -
 * `AddPropertyDefinition` declares a module-definition key that
 * `UpdatePropertyDefinition` does not. This interface is deliberately the
 * INTERSECTION of the two rather than a mirror of either: the screen offers no
 * module association, so the member that distinguishes them is one this form could
 * not fill in. Anything sent from here is therefore valid on both verbs.
 *
 * MIGRATION: `visibility` is carried on this shape for the form's own use and is NOT
 * part of either server write contract. There is no `Visibility` column on
 * `ProfilePropertyDefinition` at any point in the schema's upgrade history - the
 * stored per-account counterpart lives on `UserProfile` - so the value a definition
 * reports is a default hint the API derives from a module setting, not something a
 * definition write can persist. A service wiring this submission to the API must
 * omit the member rather than expect it to round-trip.
 */
export interface ProfilePropertyDefinitionSubmission {
  /** The property's name, which is also its label. */
  readonly propertyName: string;

  /** The grouping the property is displayed under. */
  readonly propertyCategory: string;

  /** The declared data type. */
  readonly dataType: number;

  /** The value applied when an account has recorded none. */
  readonly defaultValue: string | null;

  /** The greatest number of characters a value may hold, or zero for no bound. */
  readonly length: number;

  /** Whether a value must be supplied. */
  readonly required: boolean;

  /** A regular expression a value must match, when one is declared. */
  readonly validationExpression: string | null;

  /** Where the property sits among its siblings. */
  readonly viewOrder: number;

  /** Whether the property is shown at all. */
  readonly visible: boolean;

  /**
   * The visibility applied when an account has recorded none.
   *
   * Held for the form's own use only. The definition write endpoints do not accept
   * this member and could not store it if they did; see the note on this interface.
   */
  readonly visibility: ProfileVisibilityCode;
}
