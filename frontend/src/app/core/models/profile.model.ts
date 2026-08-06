/**
 * The profile wire contract.
 *
 * Every declaration in this file mirrors, member for member, a data-transfer object
 * the API already serialises or binds, so that a change on either side of the
 * boundary shows up as a compilation failure rather than as an `undefined` at run
 * time. The backend types, all in `DnnMigration.Application.Dtos.User`, are
 * `ProfilePropertyDefinitionDto`, `UserProfileValueDto` and `UserProfileDto` for the
 * reads, and `CreateProfilePropertyDefinitionRequest` and
 * `UpdateProfilePropertyDefinitionRequest` for the two declaration writes. Note that
 * `UserProfileDto` is BOTH the read projection and the replace request body, which is
 * why the profile submission below is named the way that type names its members
 * rather than the way a form happens to group them.
 *
 * NAMING
 * ------
 * Member names are camel-cased because that is what the API emits: its JSON options
 * take the framework's web defaults, which apply the camel-case naming policy. The
 * backend property `PropertyDefinitionId` therefore arrives as `propertyDefinitionId`
 * and is spelled that way here.
 *
 * TYPES, ONE CODE TABLE, AND ONE DECODER PER CONTRACT THAT IS READ
 * ---------------------------------------------------------------
 * A TypeScript interface is erased at compile time, so `http.get<UserProfile>(…)` compiles
 * to `http.get(…)`: nothing inspects the body, and a renamed member arrives as `undefined`
 * behind a 200 to surface as a blank field on a profile screen, far from the response that
 * caused it. Each contract that is READ therefore carries a runtime decoder declared FROM
 * its interface, so that forgetting a member is a compile error rather than a silent hole,
 * and the transport refuses a response that does not match. Violations name the member path
 * and the expected type and NEVER the value — which matters more here than anywhere else in
 * the workspace, because a profile value is personal data and a violation must not copy it
 * into a log.
 *
 * The write contracts carry no decoder: this client composes them, so there is nothing
 * untrusted to check. The decoders are verified through `core/services/user.service.spec.ts`,
 * which exercises each against a malformed payload, rather than through a spec paired with
 * this module.
 *
 * MIGRATION: the legacy profile surface was split across two namespaces —
 * `DotNetNuke.Entities.Users` owned `UserProfile.vb` while
 * `DotNetNuke.Entities.Profile` owned `ProfilePropertyDefinition.vb` — and the two
 * are consolidated here, matching the consolidation already performed on the
 * backend.
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

  /**
   * When the value was last written, or `null` when it never has been.
   *
   * ⚠ THE NULL IS THE PRESENCE DISCRIMINATOR, AND IT IS THE ONLY ONE ON THIS CONTRACT. The stored column
   * is `LastUpdatedDate datetime NOT NULL`, so a row that exists always carries a date; the API emits null
   * exactly when it synthesised this entry for a declared property the account has no row for. That is
   * what lets a consumer tell "never recorded" from "recorded as empty", which the legacy code could not
   * do — `UserProfile.vb` L507-L517 returned the empty string for both.
   *
   * A consumer must NOT treat it as the only evidence of presence, though. A value that arrives with
   * content is present whatever the metadata beside it says, and deciding on the timestamp alone means
   * replacing that content with a declaration default — which a screen that submits every property then
   * persists.
   */
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
 *
 * The two members are named exactly as `UserProfileDto` names them, because
 * `PUT /api/v1/users/{userId}/profile` binds THAT type as its request body — the
 * read projection and the replace payload are one contract on the server, not two.
 * The member is therefore `properties`, matching {@link UserProfile.properties}, and
 * a submission spelling it any other way is refused with `400` naming the offending
 * member: the API's request deserialiser is configured to reject an undeclared
 * member rather than to discard it silently, so a near-miss fails loudly at the
 * boundary instead of writing a partial profile.
 */
export interface UserProfileSubmission {
  /** The account being written. */
  readonly userId: number;

  /** Every declared property's value and visibility. */
  readonly properties: readonly UserProfileValueSubmission[];
}

/**
 * The members a declaration write carries on BOTH verbs.
 *
 * Declared once and extended, mirroring the server, which shares exactly these nine
 * members through a common interface that both of its request contracts implement.
 * A member added here therefore reaches create and replace together, which is the
 * only way the two can be kept from drifting apart.
 *
 * MIGRATION: `visibility` is NOT a member of any write contract, and its absence is
 * enforced here rather than merely documented. There is no `Visibility` column on
 * `ProfilePropertyDefinition` at any point in the schema's 88-script upgrade history
 * - the stored per-account counterpart lives on `UserProfile` - so the value a
 * definition REPORTS is a default hint the API derives from the "User Accounts"
 * module setting `Profile_DefaultVisibility`. It is retained on the read projection,
 * because a client needs the resolved hint, and is absent from both writes because
 * no write could persist it. A shape that carried it would be refused with `400`
 * naming the member: the API's request deserialiser rejects an undeclared member
 * rather than discarding it, so an editor offering the value would collect a choice
 * the server then refuses the whole request over.
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
 * The body of `POST /api/v1/profile-definitions`.
 *
 * Mirrors `CreateProfilePropertyDefinitionRequest`: the nine shared members plus the
 * module association, which only a create may decide.
 *
 * MIGRATION: THE TWO VERBS BIND TWO DISTINCT CONTRACTS, and one shared client shape
 * cannot satisfy both. The terminal stored procedures genuinely disagree -
 * `AddPropertyDefinition` (`04.06.00.SqlDataProvider:L1101`) declares
 * `@ModuleDefId` and `UpdatePropertyDefinition` (`04.05.00.SqlDataProvider:L1685`)
 * neither declares it nor names the column in its `SET` list - so a module
 * association can be established when a property is declared and never afterwards.
 * That is why this shape is separate from {@link UpdateProfilePropertyDefinitionRequest}
 * rather than the two sharing one declaration: sending `moduleDefId` on the replace
 * verb would be refused, and omitting it here would silently discard the only member
 * the create verb accepts and the replace verb does not.
 *
 * `propertyDefinitionId` is absent because the store issues it, and `portalId` is
 * absent because the API resolves the tenant before dispatching the request. A
 * screen reports whether it means a create or a replace by raising a different
 * event, never by the presence or absence of an identifier, so an accidental
 * omission cannot turn an edit into a duplicate.
 */
export interface CreateProfilePropertyDefinitionRequest
  extends ProfilePropertyDefinitionWriteMembers {
  /**
   * The module definition to scope the declaration to, or `null` for none.
   *
   * Nullable rather than optional, because the server declares it as an optional
   * integer with no validation rule and treats an absent value as "no module
   * association" - and `null` is what a screen offering no module picker has to
   * send. `0` would be wrong: `dbo.ModuleDefinitions.ModuleDefID` is
   * `IDENTITY(1, 1)`, so zero names no row.
   */
  readonly moduleDefId: number | null;
}

/**
 * The body of `PUT /api/v1/profile-definitions/{propertyDefinitionId}`.
 *
 * Mirrors `UpdateProfilePropertyDefinitionRequest`, which is exactly the nine shared
 * members: the identifier arrives from the route, the tenant is resolved by the API,
 * and the module association cannot be changed after the declaration exists - see
 * the note on {@link CreateProfilePropertyDefinitionRequest} for the procedure-level
 * reason.
 *
 * An alias rather than an empty extension, because a distinct interface declaring
 * nothing of its own would invite a member to be added to it and thereby to diverge
 * from the shared set for no reason the server could honour.
 */
export type UpdateProfilePropertyDefinitionRequest = ProfilePropertyDefinitionWriteMembers;

/**
 * Decodes one profile property declaration.
 *
 * `visibility` and `dataType` are decoded as plain integers rather than closed code tables.
 * The three visibility codes the legacy screens offered are published as named constants
 * above for templates to use, but they are NOT the closed set: the column is an ordinary
 * integer, `dataType` refers to a list row the server owns, and closing either set here
 * would refuse a declaration a later release adds.
 *
 * `length`, `viewOrder` and `propertyCategory` are required. A declaration always carries
 * all three, and a `viewOrder` reaching a sorted render as `undefined` would silently move
 * the field to one end of the form.
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

/**
 * Decodes one stored profile value together with the declaration that describes it.
 *
 * ⚠ `propertyValue` IS A PLAIN STRING AND THE EMPTY STRING PASSES. A value the person
 * cleared is empty rather than absent — `Library/Components/Shared/Null.vb:L71-L75` returns
 * `""` literally — so refusing it would refuse a conforming profile, and coalescing it to
 * null would make a cleared field indistinguishable from one never filled in.
 *
 * The nested declaration is decoded in full rather than trusted, because the profile screen
 * renders each field FROM it: the label, the required flag and the validation expression all
 * come from the nested object, so a malformed one produces a field that looks legitimate and
 * validates against nothing.
 */
export const decodeUserProfileValue: Decoder<UserProfileValue> = objectOf<UserProfileValue>({
  propertyDefinitionId: decodeInteger,
  propertyValue: decodeString,
  visibility: decodeInteger,
  lastUpdatedDate: nullable(decodeDateString),
  definition: decodeProfilePropertyDefinition,
});

/**
 * Decodes one account's whole profile.
 *
 * `properties` is required. A person who has filled in nothing has an EMPTY array, so an
 * absent member is contract drift rather than an empty profile and is refused as such —
 * which is the distinction that stops a drifted response from rendering as a profile the
 * operator believes they have already cleared.
 */
export const decodeUserProfile: Decoder<UserProfile> = objectOf<UserProfile>({
  userId: decodeInteger,
  properties: arrayOf(decodeUserProfileValue),
});
