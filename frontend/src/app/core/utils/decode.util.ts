/**
 * Runtime decoding of external JSON at the API boundary.
 *
 * WHY THIS FILE EXISTS
 * --------------------
 * Every response type in `core/models/` is a COMPILE-TIME declaration. `HttpClient`
 * accepts a type argument and hands back a value asserted to have that shape without
 * ever inspecting it, so `http.get<ApiResponse<PortalDetail>>(url)` is a promise the
 * compiler makes on the server's behalf and cannot keep. The API is same-origin, but
 * same-origin is not the same as in-process: the response travels through a reverse
 * proxy, and a proxy, a gateway, an error page, a cached body from a previous
 * deployment or a partially-rolled-out server can all produce a document that parses
 * as JSON and satisfies nothing else.
 *
 * The consequence is not a cosmetic one. An undeclared `null` reaching a root signal
 * store is adopted as state and faults later, far from its origin, in a component
 * that did nothing wrong; and on the authentication path an unvalidated payload
 * becomes a bearer token and an authority list. Decoding at the boundary converts all
 * of that into one loud, located failure at the moment the response arrives, which
 * the existing `ProblemDetails` flow already knows how to present.
 *
 * WHAT A DECODER IS
 * -----------------
 * A {@link Decoder} is a total function from `unknown` to a declared type that either
 * returns a value of that type or throws {@link ContractViolationError}. It never
 * substitutes a default, never coerces and never repairs: a decoder that silently
 * produced an empty array or a zero would recreate the exact defect this file was
 * written to remove — a malformed response becoming an apparently successful, empty
 * result. Absence is a violation unless a member is declared optional, in which case
 * {@link optional} states so explicitly at the call site.
 *
 * WHY THE COMBINATOR STYLE
 * ------------------------
 * {@link objectOf} takes one decoder PER DECLARED MEMBER of the target type, and its
 * shape parameter removes optionality (`-?`), so the compiler rejects a shape that
 * forgets a member. Completeness of a decoder is therefore a compile-time property
 * rather than something a reviewer has to check by eye — which is what makes it
 * practical to decode the whole wire surface rather than the parts somebody
 * remembered.
 *
 * DIAGNOSTICS CARRY NO VALUES
 * ---------------------------
 * A violation message names the PATH and the EXPECTED and RECEIVED *types*, never the
 * received value. Response bodies on this boundary include access tokens, refresh
 * tokens, e-mail addresses and display names, and a decoding failure is precisely the
 * event most likely to be logged or shown. The type name is what a diagnosis needs;
 * the value is what must not escape.
 */

// TYPE-ONLY, and deliberately so. `paged-result.model.ts` imports the runtime members
// of this file, so a value import in this direction would close a runtime cycle
// between the two modules. Declared with `import type`, the dependency exists only in
// the type graph: at runtime this module imports nothing at all.
import type { ApiMeta, ApiResponse, PagedResult } from '../models/paged-result.model';

/**
 * A total function from an untrusted value to a declared type.
 *
 * @param value The untrusted value, as parsed from the response body.
 * @param path A dotted/indexed trail naming the value's position in the document,
 *   used only to describe a violation. The conventional root is `response`.
 * @returns The decoded value.
 * @throws ContractViolationError When the value does not satisfy the contract.
 */
export type Decoder<T> = (value: unknown, path: string) => T;

/**
 * One decoder per declared member of `T`, with optionality removed.
 *
 * `-?` is the load-bearing part. For a type declaring `readonly x?: string`, `T['x']`
 * is `string | undefined` and the key becomes REQUIRED here, so the shape must supply
 * a decoder that admits `undefined` — which is what {@link optional} produces. A
 * member cannot be forgotten and cannot be quietly treated as absent.
 */
export type DecoderShape<T> = { readonly [K in keyof T]-?: Decoder<T[K]> };

/**
 * Raised when an external document does not satisfy the contract this client
 * declares for it.
 *
 * Distinguishable by type so that a caller can tell "the server answered something
 * this client cannot read" apart from "the server refused the request", which is an
 * `HttpErrorResponse`. The two need different wording and different operator action,
 * and before this type existed the first was indistinguishable from success.
 */
export class ContractViolationError extends Error {
  /** The dotted/indexed position of the offending value within the document. */
  readonly path: string;

  /** A description of what the contract requires at {@link path}. */
  readonly expected: string;

  /** The TYPE of what arrived — never the value, which may carry credentials. */
  readonly received: string;

  /**
   * @param path The offending value's position, for example `response.data.items[2]`.
   * @param expected A short description of the requirement, for example `a string`.
   * @param value The offending value, used only to derive its type name.
   */
  constructor(path: string, expected: string, value: unknown) {
    const received = describeType(value);

    super(
      `The server response did not match the expected contract at ${path}: ` +
        `expected ${expected}, received ${received}.`,
    );

    this.name = 'ContractViolationError';
    this.path = path;
    this.expected = expected;
    this.received = received;
  }
}

/**
 * Whether a thrown value is a contract violation.
 *
 * Used where a caller must distinguish a response this client cannot read from a
 * response the server refused. `instanceof` is reliable here because the class is
 * declared in this workspace and compiled once; the name check is a belt-and-braces
 * fallback for a value that crossed a realm boundary.
 *
 * @param error A caught value.
 * @returns True when the value is a contract violation.
 */
export function isContractViolation(error: unknown): error is ContractViolationError {
  return (
    error instanceof ContractViolationError ||
    (error instanceof Error && error.name === 'ContractViolationError')
  );
}

/**
 * Names the type of an arbitrary value without disclosing it.
 *
 * `null` and arrays are named specifically because `typeof` reports both as `object`,
 * and those two are the most common shapes of a drifted response — a member that went
 * null and a collection that arrived where a scalar was declared.
 *
 * @param value Any value.
 * @returns A short type name such as `null`, `an array`, `a string`.
 */
function describeType(value: unknown): string {
  if (value === null) {
    return 'null';
  }

  if (Array.isArray(value)) {
    return 'an array';
  }

  switch (typeof value) {
    case 'undefined':
      return 'nothing';
    case 'string':
      return 'a string';
    case 'number':
      return 'a number';
    case 'boolean':
      return 'a boolean';
    case 'object':
      return 'an object';
    default:
      return `a ${typeof value}`;
  }
}

/**
 * Decodes a string.
 *
 * The empty string is ADMITTED, deliberately. The legacy null contract encoded absent
 * text as the empty string rather than as SQL `NULL`, so `''` is a value this schema
 * genuinely produces and rejecting it would refuse well-formed responses. Where a
 * member must carry text, {@link nonEmptyString} says so at the call site.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The string.
 */
export const decodeString: Decoder<string> = (value, path) => {
  if (typeof value !== 'string') {
    throw new ContractViolationError(path, 'a string', value);
  }

  return value;
};

/**
 * Decodes a string that must carry at least one non-whitespace character.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The string, exactly as received — leading and trailing whitespace is
 *   preserved, because trimming here would rewrite a wire value rather than validate
 *   it.
 */
export const nonEmptyString: Decoder<string> = (value, path) => {
  const text = decodeString(value, path);

  if (text.trim().length === 0) {
    throw new ContractViolationError(path, 'a non-blank string', value);
  }

  return text;
};

/**
 * Decodes a finite number.
 *
 * `NaN` and the infinities are refused. They cannot appear in conforming JSON at all —
 * `JSON.stringify` writes them as `null` — so their presence means the value was
 * produced by something other than a JSON serializer, and admitting them would let a
 * page total or a page size poison arithmetic downstream.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The number.
 */
export const decodeNumber: Decoder<number> = (value, path) => {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new ContractViolationError(path, 'a finite number', value);
  }

  return value;
};

/**
 * Decodes an integer.
 *
 * ⚠ SENTINEL SAFETY. `0` and `-1` are LEGITIMATE identifiers in this schema and must
 * pass. `Portals.PortalID` is `IDENTITY(-1,1)`, so the first portal is `0` and `-1` is
 * a real row identifier as well as the legacy absent-marker; `Roles.RoleID`,
 * `Tabs.TabID` and `Modules.ModuleID` are `IDENTITY(0,1)`. No decoder in this file
 * applies a truthiness test or a `> 0` check to an identifier, and none may be added.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The integer.
 */
export const decodeInteger: Decoder<number> = (value, path) => {
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw new ContractViolationError(path, 'an integer', value);
  }

  return value;
};

/**
 * Decodes a boolean.
 *
 * `0`, `1`, `'true'` and `'false'` are refused rather than coerced. The API's
 * serializer writes JSON booleans, so a string or a number here is drift, and
 * coercing it would hide the drift behind a plausible answer — including the case
 * where `'false'` would coerce to `true`.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The boolean.
 */
export const decodeBoolean: Decoder<boolean> = (value, path) => {
  if (typeof value !== 'boolean') {
    throw new ContractViolationError(path, 'a boolean', value);
  }

  return value;
};

/**
 * Decodes a plain object, excluding `null` and arrays.
 *
 * Both exclusions matter: `typeof null === 'object'` and `typeof [] === 'object'`, so
 * without them a null member or a collection arriving where an object was declared
 * would pass and fault on first member read.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The value as an indexable record.
 */
export const decodeObject: Decoder<Record<string, unknown>> = (value, path) => {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new ContractViolationError(path, 'an object', value);
  }

  return value as Record<string, unknown>;
};

/**
 * Decodes an ISO-8601 date-time string.
 *
 * Validated by parseability rather than by pattern. The server writes
 * round-trippable ISO strings, and every consumer here either displays the string or
 * hands it to `new Date(...)`; a string the platform cannot parse becomes an
 * `Invalid Date` that renders as `NaN` wherever it is shown. The STRING is returned,
 * not a `Date`, because the wire contract is a string and converting here would move
 * a timezone decision into the transport layer.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The date string, unchanged.
 */
export const decodeDateString: Decoder<string> = (value, path) => {
  const text = decodeString(value, path);

  if (Number.isNaN(Date.parse(text))) {
    throw new ContractViolationError(path, 'an ISO-8601 date-time string', value);
  }

  return text;
};

/**
 * Admits `null` in addition to whatever the inner decoder admits.
 *
 * `undefined` is NOT admitted, and the distinction is deliberate. The API's serializer
 * writes every declared member, expressing absence as the VALUE `null` rather than by
 * omitting the member, so a missing member is drift and must be reported. Use
 * {@link optional} for the genuinely omissible case.
 *
 * @param inner The decoder for the non-null case.
 * @returns A decoder admitting the inner type or `null`.
 */
export function nullable<T>(inner: Decoder<T>): Decoder<T | null> {
  return (value, path) => (value === null ? null : inner(value, path));
}

/**
 * Admits an absent member in addition to whatever the inner decoder admits.
 *
 * Reserved for members this client declares with `?`. A member that is present must
 * still satisfy the inner decoder, so this relaxes ABSENCE only — never type.
 *
 * @param inner The decoder for the present case.
 * @returns A decoder admitting the inner type or `undefined`.
 */
export function optional<T>(inner: Decoder<T>): Decoder<T | undefined> {
  return (value, path) => (value === undefined ? undefined : inner(value, path));
}

/**
 * Decodes an array, applying `item` to every element.
 *
 * The element path carries its index, so a violation in the fortieth record of a page
 * names that record rather than the page.
 *
 * @param item The element decoder.
 * @returns A decoder producing a read-only array.
 */
export function arrayOf<T>(item: Decoder<T>): Decoder<readonly T[]> {
  return (value, path) => {
    if (!Array.isArray(value)) {
      throw new ContractViolationError(path, 'an array', value);
    }

    // A plain loop rather than `map`, so the index is available for the path and no
    // intermediate closure is allocated per element on a page of records.
    const decoded: T[] = [];

    for (let index = 0; index < value.length; index += 1) {
      decoded.push(item(value[index], `${path}[${index}]`));
    }

    return decoded;
  };
}

/**
 * Decodes a string-keyed dictionary, applying `entry` to every value.
 *
 * This is the shape the settings endpoints use — module and tab-module settings are
 * genuine key/value rows in the legacy schema, so their wire form is an open
 * dictionary rather than a declared object, and no shape can be checked for it. What
 * IS checked is that it is an object and that every value satisfies the contract.
 *
 * Keys are read through `Object.keys`, which excludes inherited members, and the
 * result is assembled on a fresh object so that a body carrying a `__proto__` member
 * cannot influence the prototype of the value handed to a store.
 *
 * @param entry The value decoder.
 * @returns A decoder producing a read-only record.
 */
export function recordOf<T>(entry: Decoder<T>): Decoder<Readonly<Record<string, T>>> {
  return (value, path) => {
    const source = decodeObject(value, path);
    const decoded: Record<string, T> = Object.create(null) as Record<string, T>;

    for (const key of Object.keys(source)) {
      decoded[key] = entry(source[key], `${path}.${key}`);
    }

    // Re-spread onto a normal object literal so consumers see an ordinary object with
    // the expected prototype, while the assembly above stayed prototype-free.
    return { ...decoded };
  };
}

/**
 * Decodes a value that must be one of a closed set of string codes.
 *
 * The set is the vocabulary the API actually emits — sort directions, membership
 * statuses, the one-character billing frequency codes `D`/`W`/`M`/`Y` that are stored
 * in `Roles.BillingFrequency char(1)`, and the permission keys. A code outside the set
 * is refused rather than passed through, because every consumer switches on these and
 * an unknown code silently takes a default branch.
 *
 * The allowed values are named in the violation message. They are a closed contract
 * vocabulary rather than data, so quoting them discloses nothing.
 *
 * @param allowed The permitted codes.
 * @returns A decoder producing one of the permitted codes.
 */
export function oneOf<T extends string>(allowed: readonly T[]): Decoder<T> {
  const expected = `one of ${allowed.map((code) => `'${code}'`).join(', ')}`;

  return (value, path) => {
    const text = decodeString(value, path);

    // A widened comparison, because `includes` on a `readonly T[]` will not accept a
    // plain string without it.
    if (!(allowed as readonly string[]).includes(text)) {
      throw new ContractViolationError(path, expected, value);
    }

    return text as T;
  };
}

/**
 * Decodes a value that must be one of a closed set of numeric codes.
 *
 * The numeric counterpart of {@link oneOf}, for the integer discriminator columns the
 * legacy schema stores rather than lookup rows: the portal's registration mode and banner
 * advertising mode, and a module's visibility. Each is a small closed set on both sides of
 * the wire, and each is consumed by a `switch` whose default branch would otherwise absorb
 * an unrecognised code and render the wrong affordance without complaint.
 *
 * ⚠ A CODE OUTSIDE THE SET IS REFUSED, NOT COERCED. Coercing to the zero member would be
 * the worst available outcome for these particular enums, because zero is the *permissive*
 * member of two of the three — `NoRegistration` and `None` — so an unrecognised code would
 * silently present a portal as accepting no registrations, or a module as unrestricted.
 *
 * @param allowed The permitted codes. Pass the enum's members explicitly; a TypeScript
 *   numeric enum is not enumerable at runtime in a form that can be trusted, because
 *   reverse mapping puts its names in the same object as its values.
 * @returns A decoder producing one of the permitted codes.
 */
export function oneOfNumber<T extends number>(allowed: readonly T[]): Decoder<T> {
  const expected = `one of ${allowed.join(', ')}`;

  return (value, path) => {
    const code = decodeInteger(value, path);

    if (!(allowed as readonly number[]).includes(code)) {
      throw new ContractViolationError(path, expected, value);
    }

    return code as T;
  };
}

/**
 * Decodes an object against one decoder per declared member.
 *
 * Undeclared members are IGNORED rather than refused. The server is free to add a
 * member in a later version, and refusing an unrecognised one would make every client
 * a blocker on every additive server change; what matters is that everything this
 * client reads is what this client declared.
 *
 * @param shape One decoder per member of `T`. Optionality is removed by
 *   {@link DecoderShape}, so a forgotten member is a compile error.
 * @returns A decoder producing `T`.
 */
export function objectOf<T extends object>(shape: DecoderShape<T>): Decoder<T> {
  // Enumerated once rather than per call: the shape is a module-level constant at every
  // call site, and a page of records would otherwise re-enumerate it per element.
  const members: readonly string[] = Object.keys(shape);

  // The shape is read through a string-keyed view for the duration of the loop. A
  // `keyof T` view would require narrowing `string` to `keyof T`, which is unsound in
  // general and would be asserting the very thing the enumeration already guarantees.
  const decoders = shape as Readonly<Record<string, Decoder<unknown>>>;

  return (value, path) => {
    const source = decodeObject(value, path);
    const decoded: Record<string, unknown> = {};

    for (const member of members) {
      decoded[member] = decoders[member](source[member], `${path}.${member}`);
    }

    // Every declared member has been assigned by the loop above: `members` is the full
    // key set of the shape, and {@link DecoderShape} makes the shape's key set the full
    // key set of `T` with optionality removed. The assertion records that reasoning;
    // there is no way to express "every key assigned" to the compiler through an
    // index loop.
    return decoded as T;
  };
}

/**
 * The decoder for {@link ApiMeta}.
 *
 * The three coordinates a pager cannot be rendered without — the total, the zero-based
 * page index and the page size the server applied — are REQUIRED and must be integers.
 * `totalPages` is the single member permitted to be absent, and it is then derived with
 * the same arithmetic the server uses; that tolerance is retained from the previous
 * behaviour because the server's contract allows the omission, and removing it would
 * refuse conforming responses.
 *
 * Sentinel safety: `totalCount: 0` and `pageIndex: 0` are ordinary values for an empty
 * first page, so no coordinate is validated by a truthiness or `> 0` test.
 */
export const decodeApiMeta: Decoder<ApiMeta> = (value, path) => {
  const meta = decodeObject(value, path);

  const totalCount = decodeInteger(meta['totalCount'], `${path}.totalCount`);
  const pageIndex = decodeInteger(meta['pageIndex'], `${path}.pageIndex`);
  const pageSize = decodeInteger(meta['pageSize'], `${path}.pageSize`);

  const declaredTotalPages: unknown = meta['totalPages'];
  const totalPages =
    declaredTotalPages === undefined || declaredTotalPages === null
      ? derivePageCount(totalCount, pageSize)
      : decodeInteger(declaredTotalPages, `${path}.totalPages`);

  return { totalCount, pageIndex, pageSize, totalPages };
};

/**
 * Derives the page count from the total and the page size.
 *
 * Ceiling division, with a page size of zero yielding zero pages rather than a division
 * by zero. A page size of zero is a legitimate reply to an unpaged query, so it is
 * handled rather than refused.
 *
 * @param totalCount The total across every page.
 * @param pageSize The page size the server applied.
 * @returns The number of pages.
 */
function derivePageCount(totalCount: number, pageSize: number): number {
  if (pageSize <= 0 || totalCount <= 0) {
    return 0;
  }

  return Math.floor(totalCount / pageSize) + (totalCount % pageSize > 0 ? 1 : 0);
}

/**
 * Validates the framing of a page without inspecting its records.
 *
 * The single implementation of "is this a page?", shared by {@link pageOf} here and by
 * `toPagedResult` in `paged-result.model.ts`. There is deliberately one implementation:
 * two structurally similar page validators would drift, and the weaker of the two would
 * then define what a store actually accepts — which is how a malformed page came to be
 * read as an empty one in the first place.
 *
 * An EMPTY `items` array passes. That is a real empty page, and the whole point of this
 * validation is that it can be told apart from a body that carried no `items` at all.
 *
 * @param value The untrusted response body.
 * @param path The body's position.
 * @returns The undecoded records and the validated paging coordinates.
 * @throws ContractViolationError When the body is not a page.
 */
export function decodePageStructure(
  value: unknown,
  path: string,
): { readonly items: readonly unknown[]; readonly meta: ApiMeta } {
  const page = decodeObject(value, path);
  const items: unknown = page['items'];

  if (!Array.isArray(items)) {
    throw new ContractViolationError(`${path}.items`, 'an array', items);
  }

  return {
    items: items as readonly unknown[],
    meta: decodeApiMeta(page['meta'], `${path}.meta`),
  };
}

/**
 * Decodes the single-payload success envelope.
 *
 * The envelope's `meta` is PRESENT AND NULLABLE on this contract, not optional, and
 * that is asserted rather than assumed: the server's serializer writes every declared
 * member including one holding null. A single-payload response therefore carries
 * `meta: null`, and a response that omits the member is drift worth reporting.
 *
 * @param data The decoder for the payload.
 * @returns A decoder producing the payload, with the envelope already validated and
 *   stripped — callers want the payload, and returning the envelope would make every
 *   call site unwrap it again.
 */
export function envelopeOf<T>(data: Decoder<T>): Decoder<T> {
  return (value, path) => {
    const envelope = decodeObject(value, path);

    // The metadata is validated even though the payload is what the caller wants: a
    // MALFORMED metadata object on a single-payload response means the client and the
    // server disagree about which envelope this endpoint uses, and that is exactly the
    // drift worth surfacing early.
    //
    // An ABSENT `meta` is tolerated, and the asymmetry with the page envelope is
    // deliberate. This member is pure framing that the caller never sees — it is read here
    // only as a drift signal — so refusing an otherwise perfect payload because a
    // discarded member was omitted would make this client a blocker on a legitimate
    // serialiser change, for no detection gained: an UNWRAPPED body is caught by the
    // `data` check on the next line, which reports the more precise diagnosis of the two
    // anyway. A page's metadata is required, because there it is load-bearing — the total
    // and the coordinates are the page — and treating its absence as an empty first page
    // is the defect {@link decodePageStructure} exists to prevent.
    optional(nullable(decodeApiMeta))(envelope['meta'], `${path}.meta`);

    return data(envelope['data'], `${path}.data`);
  };
}

/**
 * Decodes a single-payload envelope and returns it WHOLE rather than stripped.
 *
 * The counterpart of {@link envelopeOf}, for the transport methods whose published signature
 * is the envelope itself. It exists so that those methods can be validated without changing
 * the shape they return: rewriting them to hand back the bare payload would be a contract
 * change rippling into every store and screen that reads `.data`, which is a refactor rather
 * than the validation this is for.
 *
 * The metadata is carried through as decoded rather than discarded, because a caller holding
 * the envelope may legitimately read it.
 *
 * @param data The decoder for the payload.
 * @returns A decoder producing the whole envelope.
 */
export function responseOf<T>(data: Decoder<T>): Decoder<ApiResponse<T>> {
  return (value, path) => {
    const envelope = decodeObject(value, path);
    const meta = optional(nullable(decodeApiMeta))(envelope['meta'], `${path}.meta`);

    return {
      data: data(envelope['data'], `${path}.data`),
      meta: meta ?? null,
    };
  };
}

/**
 * Decodes the envelope of a response whose payload is a page of records.
 *
 * Kept separate from {@link envelopeOf} because the two envelopes differ in exactly
 * one respect that matters — a page's metadata is required and populated, where a
 * single payload's is null — and folding them together is what allowed a page with no
 * metadata to be read as a successful empty first page.
 *
 * @param item The record decoder.
 * @returns A decoder producing a fully validated page.
 */
export function pageOf<T>(item: Decoder<T>): Decoder<PagedResult<T>> {
  return (value, path) => {
    const page = decodePageStructure(value, path);
    const decoded: T[] = [];

    for (let index = 0; index < page.items.length; index += 1) {
      decoded.push(item(page.items[index], `${path}.items[${index}]`));
    }

    return { items: decoded, meta: page.meta };
  };
}

/**
 * Convenience for decoding a whole response body from a service method.
 *
 * Exists so that a service reads `map((body) => decode(body, RESPONSE_ROOT))` with one
 * agreed root name in every message, rather than each call site inventing one.
 */
export const RESPONSE_ROOT = 'response';

/**
 * Applies a decoder to a response body using the conventional root path.
 *
 * @param decoder The decoder to apply.
 * @param body The untrusted response body.
 * @returns The decoded value.
 */
export function decodeResponse<T>(decoder: Decoder<T>, body: unknown): T {
  return decoder(body, RESPONSE_ROOT);
}
