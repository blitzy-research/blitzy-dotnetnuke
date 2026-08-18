/**
 * The profile wire contract. Every declaration in this file mirrors, member for member, a data-transfer
 * object the API already serialises or binds, so that a change on either side of the boundary shows up as
 * a compilation failure rather than as an `undefined` at run time.
 */

import {
  arrayOf,
  decodeBoolean,
  decodeDateString,
  decodeInteger,
  decodeString,
  nullable,
  objectOf,
  type Decoder,
} from '../utils/decode.util';

/** How widely a profile value may be seen. */
export type ProfileVisibilityCode = number;

/**
 * The visibility codes the legacy profile editor offered, in the order it offered them. the legacy values
 * come from the membership settings' default profile visibility, whose shipped default is 2 — the most
 * private of the three.
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
 * A tenant's declaration of one profile property: what it is called, how it is grouped, how long it may
 * be, and whether it must be supplied.
 */
export interface ProfilePropertyDefinition {
  /** The declaration's identifier. */
  readonly propertyDefinitionId: number;

  /** The tenant the declaration belongs to. */
  readonly portalId: number;

  /** The module definition the declaration is scoped to, when it is scoped at all. */
  readonly moduleDefId: number | null;

  /** The declared data type, as a plain integer. */
  readonly dataType: number;

  /** The value applied when the account has recorded none. */
  readonly defaultValue: string | null;

  /** The grouping the property is displayed under. */
  readonly propertyCategory: string;

  /** The property's name, which is also its label. */
  readonly propertyName: string;

  /** The greatest number of characters a value may hold, or zero for no declared bound. */
  readonly length: number;

  /** Whether a value must be supplied. */
  readonly required: boolean;

  /** A regular expression a value must match, when one is declared. */
  readonly validationExpression: string | null;

  /** Where the property sits among its siblings. */
  readonly viewOrder: number;

  /** Whether the property is shown at all. */
  readonly visible: boolean;

  /** The visibility applied when the account has recorded none. */
  readonly visibility: ProfileVisibilityCode;
}

/**
 * One profile value, together with the declaration that describes it. The declaration is embedded rather
 * than referenced by identifier because the API projects it that way: a screen can render the whole
 * profile from one response without a second request and without a lookup table of its own.
 */
export interface UserProfileValue {
  /** The declaration this value was recorded against. */
  readonly propertyDefinitionId: number;

  /** The recorded value, which is the empty string when nothing was recorded. */
  readonly propertyValue: string;

  /** How widely this particular value may be seen. */
  readonly visibility: ProfileVisibilityCode;

  /**
   * When the value was last written, or `null` when it never has been. ⚠ THE NULL IS THE PRESENCE
   * DISCRIMINATOR, AND IT IS THE ONLY ONE ON THIS CONTRACT. The stored column is `LastUpdatedDate
   * datetime NOT NULL`, so a row that exists always carries a date; the API emits null exactly when it
   * synthesised this entry for a declared property the account has no row for.
   */
  readonly lastUpdatedDate: string | null;

  /** The declaration describing this value. */
  readonly definition: ProfilePropertyDefinition;
}

/**
 * An account's whole profile: one entry per property the tenant declares, whether or not the account has
 * recorded a value for it.
 */
export interface UserProfile {
  /** The account the profile belongs to. */
  readonly userId: number;

  /** One entry per declared property, in the tenant's declared display order. */
  readonly properties: readonly UserProfileValue[];

  readonly displayVisibilityEnabled: boolean;
}

/** One value in a profile submission. */
export interface UserProfileValueSubmission {
  /** The declaration being written. */
  readonly propertyDefinitionId: number;

  /** The value to write. */
  readonly propertyValue: string;

  /** The visibility to write. */
  readonly visibility: ProfileVisibilityCode;
}

/** A whole profile submission. */
export interface UserProfileSubmission {
  /** The account being written. */
  readonly userId: number;

  /** Every declared property's value and visibility. */
  readonly properties: readonly UserProfileValueSubmission[];
}

/**
 * The members a declaration write carries on BOTH verbs. Declared once and extended, mirroring the
 * server, which shares exactly these nine members through a common interface that both of its request
 * contracts implement.
 */
export interface ProfilePropertyDefinitionWriteMembers {
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
}

/**
 * The body of `POST /api/v1/profile-definitions`. Mirrors `CreateProfilePropertyDefinitionRequest`: the
 * nine shared members plus the module association, which only a create may decide.
 */
export interface CreateProfilePropertyDefinitionRequest
  extends ProfilePropertyDefinitionWriteMembers {
  /**
   * The module definition to scope the declaration to, or `null` for none. Nullable rather than optional,
   * because the server declares it as an optional integer with no validation rule and treats an absent
   * value as "no module association" - and `null` is what a screen offering no module picker has to send.
   */
  readonly moduleDefId: number | null;
}

/** The body of `PUT /api/v1/profile-definitions/{propertyDefinitionId}`. */
export type UpdateProfilePropertyDefinitionRequest = ProfilePropertyDefinitionWriteMembers;

/**
 * One declaration's requested position, as `PUT /api/v1/profile-definitions/order` carries it.
 *
 * ⚠ A PAIR, NOT A WHOLE DECLARATION, and deliberately so: an ordering request writes exactly one column,
 * and carrying the whole declaration would let a Move Up rename a property or change its data type as a
 * side effect.
 */
export interface ProfilePropertyDefinitionPosition {
  /** The declaration being positioned. */
  readonly propertyDefinitionId: number;

  /**
   * The declaration's new position.
   *
   * A sort key, not an index. The legacy grid EXCHANGED the stored values of two neighbours rather than
   * renumbering the list, so positions are expected to be sparse and the server never re-sequences them.
   */
  readonly viewOrder: number;
}

/**
 * Decodes one profile property declaration. `visibility` and `dataType` are decoded as plain integers
 * rather than closed code tables.
 */
export const decodeProfilePropertyDefinition: Decoder<ProfilePropertyDefinition> =
  objectOf<ProfilePropertyDefinition>({
    propertyDefinitionId: decodeInteger,
    portalId: decodeInteger,
    moduleDefId: nullable(decodeInteger),
    dataType: decodeInteger,
    defaultValue: nullable(decodeString),
    propertyCategory: decodeString,
    propertyName: decodeString,
    length: decodeInteger,
    required: decodeBoolean,
    validationExpression: nullable(decodeString),
    viewOrder: decodeInteger,
    visible: decodeBoolean,
    visibility: decodeInteger,
  });

export const decodeUserProfileValue: Decoder<UserProfileValue> = objectOf<UserProfileValue>({
  propertyDefinitionId: decodeInteger,
  propertyValue: decodeString,
  visibility: decodeInteger,
  lastUpdatedDate: nullable(decodeDateString),
  definition: decodeProfilePropertyDefinition,
});

/** Decodes one account's whole profile. */
export const decodeUserProfile: Decoder<UserProfile> = objectOf<UserProfile>({
  userId: decodeInteger,
  properties: arrayOf(decodeUserProfileValue),

  // REQUIRED rather than optional, for the same reason `properties` is. The API serialises with its ignore
  // condition set to never, so every declared member is always on the wire and an absent one is contract
  // drift.
  displayVisibilityEnabled: decodeBoolean,
});
